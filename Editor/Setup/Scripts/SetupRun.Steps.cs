using Newtonsoft.Json.Linq;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

using UnityEngine;

using static Virtuademy.SDK.Environments.Setup.Editor.SetupSteps;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// The steps of <see cref="SetupRun"/>: how each is driven from the editor's update loop. A step's code is in
    /// <see cref="SetupSteps"/>; what is here is the waiting around it, which the command line does by exiting and being
    /// launched again: after a round that changed the project the engine resolves the packages, waits until they are
    /// resolved and the editor has compiled, and calls the step again. Every driver is called on every update until it
    /// returns something other than <see cref="StepTickKind.InProgress"/>, and holds nothing in memory that matters: what
    /// it must remember across a domain reload is in <c>record.Data</c>.
    /// </summary>
    public static partial class SetupRun
    {
        private enum StepTickKind { InProgress, Done, Skipped, Failed, Hold }

        private readonly struct StepTick
        {
            private StepTick(StepTickKind kind, string detail)
            {
                Kind = kind;
                Detail = detail;
            }

            internal StepTickKind Kind { get; }
            internal string Detail { get; }

            internal static StepTick InProgress => new(StepTickKind.InProgress, null);
            internal static StepTick Done(string detail) => new(StepTickKind.Done, Sentence(detail));
            internal static StepTick Skipped(string reason) => new(StepTickKind.Skipped, Sentence(reason));
            internal static StepTick Failed(string reason) => new(StepTickKind.Failed, Sentence(reason));
            internal static StepTick Hold(string detail) => new(StepTickKind.Hold, Sentence(detail));
        }

        // How long the editor must have been neither compiling nor importing before a step is taken as settled: Unity
        // starts the compilation of what a resolve brought a moment after it, and a check in that gap would see "idle".
        private const double settle_seconds = 2.5;

        // The longest a round of package resolution is waited for: git packages are cloned, which can be slow.
        private const double resolve_timeout_seconds = 15 * 60;

        // The longest the import of the TextMesh Pro essentials is waited for.
        private const double import_timeout_seconds = 3 * 60;

        private const int max_rounds = 8;
        private const int max_settle_retries = 30;

        // A wait that asks the Package Manager, reads the manifest and the lock and scans the assets is done this often, not on every editor tick.
        private const double wait_check_seconds = 1;

        // In memory only: a domain reload loses them, and the step starts its wait again.
        private static double idleSince = -1;
        private static ListRequest resolveRequest;
        private static Task<PackageRegistry[]> registryTask;
        private static bool compileRefreshed;
        private static double nextWaitCheck;

        private static string Sentence(string text)
            => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        private static void ResetStepMemory()
        {
            idleSince = -1;
            resolveRequest = null;
            registryTask = null;
            compileRefreshed = false;
            nextWaitCheck = 0;
        }

        private static bool WaitCheckDue()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextWaitCheck)
                return false;

            nextWaitCheck = now + wait_check_seconds;
            return true;
        }

        private static StepTick RunStep(string id)
        {
            switch (id)
            {
                case SetupStepIds.ProjectSettings: return RunProjectSettings();
                case SetupStepIds.Packages: return RunPackages();
                case SetupStepIds.Compile: return RunCompile();
                case SetupStepIds.UpdateRoutine: return RunUpdateRoutine();
                case SetupStepIds.Interpreter: return RunInterpreter();
                case SetupStepIds.Addressables: return RunAddressables();
                case SetupStepIds.VisualScriptingNodes: return RunVisualScriptingNodes();
                case SetupStepIds.TmpEssentials: return RunTmpEssentials();
                case SetupStepIds.Connections: return RunConnections();
                case SetupStepIds.Restart: return RunRestart();
                default: return StepTick.Failed("Unknown step.");
            }
        }

        #region Scratch and waiting

        private static int GetInt(string key) => record.Data.TryGetValue(key, out string value) && int.TryParse(value, out int number) ? number : 0;

        private static void SetValue(string key, string value)
        {
            record.Data[key] = value;
            dirty = true;
        }

        private static int Increment(string key)
        {
            int next = GetInt(key) + 1;
            SetValue(key, next.ToString());
            return next;
        }

        /// <summary>True once <paramref name="seconds"/> have passed since the first call for <paramref name="key"/>, which
        /// is remembered across reloads (<see cref="EditorApplication.timeSinceStartup"/> only restarts with the editor).</summary>
        private static bool TimedOut(string key, double seconds)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!record.Data.TryGetValue(key, out string started) || !double.TryParse(started, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double since) || since > now)
            {
                SetValue(key, now.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return false;
            }

            return now - since > seconds;
        }

        /// <summary>The editor has been neither compiling nor importing for <see cref="settle_seconds"/>.</summary>
        private static bool Settled()
        {
            double now = EditorApplication.timeSinceStartup;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                idleSince = -1;
                return false;
            }

            if (idleSince < 0)
                idleSince = now;

            return now - idleSince >= settle_seconds;
        }

        /// <summary>Asks the Package Manager to resolve, and keeps the request that answers once it has: it queues behind the resolve.</summary>
        private static void StartResolve()
        {
            Client.Resolve();
            resolveRequest = Client.List(offlineMode: true, includeIndirectDependencies: false);
            idleSince = -1;
        }

        private static bool ResolveInFlight
        {
            get
            {
                if (resolveRequest == null)
                    return false;

                if (resolveRequest.IsCompleted)
                {
                    resolveRequest = null;
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Whether the git packages of the manifest that are ours (and HybridCLR) are the ones <c>packages-lock.json</c>
        /// records, and none of the lock's direct git dependencies of ours is gone from the manifest: the Package Manager
        /// writes the lock when it has resolved, so until then the project is still in the middle of the change.
        /// </summary>
        private static bool ManifestResolved()
        {
            try
            {
                string manifestPath = Path.Combine(ProjectRoot, "Packages", "manifest.json");
                string lockPath = PackageLock.LockFilePath;
                if (!File.Exists(manifestPath) || !File.Exists(lockPath))
                    return true;

                JObject manifest = (JObject)JObject.Parse(File.ReadAllText(manifestPath))["dependencies"];
                JObject locked = (JObject)JObject.Parse(File.ReadAllText(lockPath))["dependencies"];
                if (manifest == null || locked == null)
                    return true;

                bool Ours(string name) => CreatorKitSetupWindow.IsOurPackage(name) || name == CreatorKitSetupWindow.hybridclr_package_name;

                foreach (KeyValuePair<string, JToken> dependency in manifest)
                {
                    string value = (string)dependency.Value;
                    if (!Ours(dependency.Key) || value == null || !value.Contains(".git"))
                        continue;

                    if ((string)locked[dependency.Key]?["version"] != value)
                        return false;
                }

                foreach (KeyValuePair<string, JToken> entry in locked)
                {
                    if (Ours(entry.Key) && (string)entry.Value["source"] == "git" && (int?)entry.Value["depth"] == 0 && manifest[entry.Key] == null)
                        return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException or InvalidCastException)
            {
                return true;
            }
        }

        /// <summary>The packages are resolved and the editor has compiled what they brought: the step can be called again.</summary>
        private static bool PackagesSettled() => !ResolveInFlight && ManifestResolved() && Settled();

        /// <summary>
        /// A step that does one round per call, as the command line's do (<see cref="SetupCli.ExitRelaunch"/>: the project
        /// changed, call again after the editor has resolved and compiled). After a round that changed the project it
        /// resolves, waits until <see cref="PackagesSettled"/>, and calls the step again, up to <see cref="max_rounds"/> times.
        /// </summary>
        private static StepTick RunRounds(Func<StepResult> step)
        {
            int rounds = GetInt("rounds");
            if (rounds > 0)
            {
                if (!WaitCheckDue())
                    return StepTick.InProgress;

                if (!PackagesSettled())
                    return TimedOut("roundStarted", resolve_timeout_seconds)
                        ? StepTick.Failed("The Package Manager did not finish resolving in time. Look at the Console and the Package Manager window, then run the setup again.")
                        : StepTick.InProgress;
            }
            else if (!Settled())
            {
                return StepTick.InProgress;
            }

            StepResult result = step();
            if (result.NeedsRelaunch)
            {
                if (rounds >= max_rounds)
                    return StepTick.Failed($"This step needed more rounds than expected ({max_rounds}): {result.Message}");

                Increment("rounds");
                record.Data.Remove("roundStarted");
                StartResolve();
                return StepTick.InProgress;
            }

            if (!result.IsDone)
            {
                // Right after a round the SDK or HybridCLR may not have compiled yet: the step says "missing" or "did not
                // compile", which is true for a moment. A compile that failed is a real answer.
                bool maybeSettling = rounds > 0 && !EditorUtility.scriptCompilationFailed
                                     && (result.Code == SetupCli.ExitSdkMissing || result.Code == SetupCli.ExitInterpreterFailed);
                if (maybeSettling && Increment("retries") <= max_settle_retries)
                {
                    idleSince = -1;
                    return StepTick.InProgress;
                }

                return StepTick.Failed(result.Message);
            }

            return result.Changed || rounds > 0 ? StepTick.Done(result.Message) : StepTick.Skipped(result.Message);
        }

        #endregion

        #region project-settings

        private static StepTick RunProjectSettings()
        {
            // A changed Active Input Handling is applied when the editor starts again, which the restart step, the last of the run, offers:
            // the change is noted for the editor session (CreatorKitSetupWindow.InputHandlingChangedThisSession), not for this run.
            StepResult result = Configure(out _);
            if (!result.IsDone)
                return StepTick.Failed(result.Message);

            return result.Changed ? StepTick.Done(result.Message) : StepTick.Skipped(result.Message);
        }

        #endregion

        #region packages

        private static StepTick RunPackages()
        {
            if (!WaitCheckDue())
                return StepTick.InProgress;

            string version = record.Version;
            string recorded = FindRecordedVersion();
            bool registered = IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name);
            int rounds = GetInt("rounds");

            if (registered && SameVersion(recorded, version))
            {
                // The manifest was written in an earlier round: done when the Package Manager has resolved it.
                if (rounds == 0)
                {
                    ClearPreparedPackages();
                    return StepTick.Skipped($"Virtuademy {recorded} is already installed.");
                }

                if (!PackagesSettled())
                    return TimedOut("roundStarted", resolve_timeout_seconds)
                        ? StepTick.Failed("The Package Manager did not finish resolving in time. Look at the Console and the Package Manager window, then run the setup again.")
                        : StepTick.InProgress;

                ClearPreparedPackages();
                return StepTick.Done($"Virtuademy {recorded} is installed.");
            }

            if (rounds > 0)
            {
                // Written, and not registered yet. Asked again once; after that the Package Manager's errors are in the Console.
                if (!PackagesSettled())
                    return TimedOut("roundStarted", resolve_timeout_seconds)
                        ? StepTick.Failed("The Package Manager did not finish resolving in time. Look at the Console and the Package Manager window, then run the setup again.")
                        : StepTick.InProgress;

                int waits = Increment("waits");
                if (waits > 3)
                    return StepTick.Failed("Virtuademy-SDK-Environments did not resolve. The Package Manager's errors are in the Console.");

                StartResolve();
                return StepTick.InProgress;
            }

            // The first round: the choices are checked again, because a step before this one is the only thing between Start and here.
            if (!Settled())
                return StepTick.InProgress;

            if (!string.IsNullOrEmpty(recorded) && !SameVersion(recorded, version) && !record.AllowDowngrade && CompareVersions(version, recorded) < 0)
                return StepTick.Failed($"Virtuademy {version} is older than the installed {recorded}. Confirm the downgrade to go on.");

            if (CachedRegistry == null)
            {
                // Read off the main thread: the download can take as long as the network does.
                registryTask ??= Task.Run(() => ReadRegistry(RegistrySource));
                if (!registryTask.IsCompleted)
                    return StepTick.InProgress;

                Task<PackageRegistry[]> read = registryTask;
                registryTask = null;
                if (read.IsFaulted || read.IsCanceled)
                {
                    string why = read.Exception?.GetBaseException().Message ?? "cancelled";
                    return StepTick.Failed($"Could not read the list of Virtuademy versions: {why}. Check the internet connection (proxy and firewall included), then run the setup again.");
                }

                if (read.Result == null || read.Result.Length == 0)
                    return StepTick.Failed("The list of Virtuademy versions was downloaded but is empty. Please report it to the Virtuademy team.");

                UseRegistry(read.Result);
            }

            // A project that records the version but lost its packages installs them again; one with another version moves.
            StepResult result = string.IsNullOrEmpty(recorded) || SameVersion(recorded, version)
                ? InstallSdk(version)
                : UpdateVersion(version, trackOrigin: false);

            if (result.NeedsRelaunch)
            {
                Increment("rounds");
                StartResolve();
                return StepTick.InProgress;
            }

            if (!result.IsDone)
                return StepTick.Failed(result.Message);

            if (IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
                ClearPreparedPackages();

            return result.Changed ? StepTick.Done(result.Message) : StepTick.Skipped(result.Message);
        }

        // The SDK is in: what the Connection page installed before it is part of the project now, and needs no special treatment.
        private static void ClearPreparedPackages()
        {
            if (SetupUserSettings.PreparedPackages.Count > 0)
                SetupUserSettings.PreparedPackages = null;
        }

        #endregion

        #region compile

        private static StepTick RunCompile()
        {
            // The packages and the files a step wrote are imported and compiled now, not when the editor next takes focus.
            if (!compileRefreshed)
            {
                compileRefreshed = true;
                AssetDatabase.Refresh();
                idleSince = -1;
                return StepTick.InProgress;
            }

            if (!Settled())
                return StepTick.InProgress;

            if (!EditorUtility.scriptCompilationFailed)
                return StepTick.Done("The scripts compile.");

            // After an update the project's own scripts often do not compile until the update routines have renamed what
            // the new SDK renamed: they are what repairs it, and the compile is checked again after them.
            SetupStepStatus routines = steps.FirstOrDefault(step => step.Id == SetupStepIds.UpdateRoutine);
            if (routines != null && routines.State == SetupStepState.Pending)
                return StepTick.Done("The scripts do not compile yet. The update routines run next, and the compile is checked again after them.");

            return StepTick.Failed("Unity could not compile the project's scripts. Open the Console (Window > General > Console), fix the errors, then run the setup again.");
        }

        #endregion

        #region update-routine

        private static StepTick RunUpdateRoutine()
        {
            if (!Settled())
                return StepTick.InProgress;

            string from = record.FromVersion;
            string to = FindRecordedVersion() ?? record.Version;

            if (!record.Data.ContainsKey("routines"))
            {
                // Which routines the new SDK has: its menu items, which only exist once it has compiled.
                List<string> menu = ListUpdateRoutines();
                RoutineScope scope = MatchRoutines(from, to, menu, true, out List<string> crossed);
                if (scope == RoutineScope.NotASeries)
                    return SkippedUnlessBroken($"{from} to {to} is not a step between released versions.");
                if (scope == RoutineScope.NotForward)
                    return SkippedUnlessBroken("Update routines only run when the version goes forward.");
                if (crossed.Count == 0)
                    return SkippedUnlessBroken($"The SDK has no update routine between {from} and {to}.");

                // The routines close the open scenes without saving them: the person chooses first, and nothing is run when they stop.
                if (!ResolveUnsavedScenes())
                    return StepTick.Failed("The setup stopped before the update routines, because the open scenes have changes that are not saved. Save or discard them, then run the setup again.");

                SetValue("routines", string.Join("|", crossed));
                SetValue("index", "0");
                SetValue("summary", string.Empty);
            }

            string[] names = record.Data["routines"].Split('|');
            int index = GetInt("index");
            if (index >= names.Length)
            {
                // What the routines repair is the compile: it is checked here, after them.
                if (!Settled())
                    return StepTick.InProgress;

                if (EditorUtility.scriptCompilationFailed)
                    return StepTick.Failed("The update routines ran, but Unity still cannot compile the project's scripts. Open the Console (Window > General > Console) and fix the errors, then run the setup again.");

                string summary = record.Data.TryGetValue("summary", out string text) ? text.Trim() : string.Empty;
                return StepTick.Done(summary.Length > 0 ? summary : "The update routines ran.");
            }

            string name = names[index];
            if (!SetupUpdateRoutines.TryGetKind(name, out SetupUpdateRoutines.Kind kind))
            {
                // A routine this installer has no way to drive: said, and left to the menu.
                AppendSummary($"Not run: \"{name}\" has to be started from the menu Virtuademy > Update routines.");
                SetValue("index", (index + 1).ToString());
                ClearRoutineScratch();
                return StepTick.InProgress;
            }

            SetupUpdateRoutines.RoutineTick tick = SetupUpdateRoutines.Run(kind, record.Data, () => { Save(); });
            dirty = true;

            if (tick.Status == SetupUpdateRoutines.Status.Failed)
                return StepTick.Failed($"\"{name}\": {tick.Message}");

            if (tick.Status == SetupUpdateRoutines.Status.Done)
            {
                AppendSummary($"\"{name}\": {tick.Message}");
                SetValue("index", (index + 1).ToString());
                ClearRoutineScratch();
            }

            return StepTick.InProgress;
        }

        // The compile step leaves a failure to these routines (RunCompile): skipping them must not let it through.
        private static StepTick SkippedUnlessBroken(string reason)
            => EditorUtility.scriptCompilationFailed
                ? StepTick.Failed($"{reason} Unity still cannot compile the project's scripts, though. Open the Console (Window > General > Console), fix the errors, then run the setup again.")
                : StepTick.Skipped(reason);

        private static void AppendSummary(string text)
        {
            string summary = record.Data.TryGetValue("summary", out string existing) ? existing : string.Empty;
            SetValue("summary", (summary + " " + Sentence(text)).Trim());
        }

        private static void ClearRoutineScratch()
        {
            foreach (string key in new[] { "phase", "applyRounds", "failed" })
                record.Data.Remove(key);
        }

        /// <summary>
        /// When open scenes have changes that are not saved, the person chooses: save them and continue, continue and lose the
        /// changes (the scenes are reopened as they are on disk, an untitled one is closed), or stop. False when they stop, or
        /// when a scene could not be saved.
        /// </summary>
        private static bool ResolveUnsavedScenes()
        {
            List<UnityEngine.SceneManagement.Scene> open = Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                .Select(UnityEngine.SceneManagement.SceneManager.GetSceneAt).ToList();
            List<UnityEngine.SceneManagement.Scene> modified = open.Where(scene => scene.isDirty).ToList();
            if (modified.Count == 0 || Application.isBatchMode)
                return true;

            string names = string.Join("\n", modified.Select(scene => "- " + (string.IsNullOrEmpty(scene.path) ? "Untitled (never saved)" : scene.path)));
            int choice = EditorUtility.DisplayDialogComplex("Unsaved changes in the open scenes",
                "The update routines close the open scenes. These have changes that are not saved:\n\n" + names +
                "\n\nSave them before the routines run, or continue without saving: the changes are then lost for good, and the scenes " +
                "are reopened as they are on disk. Stopping leaves the project as it is now.",
                "Save and continue", "Stop the setup", "Continue without saving");

            switch (choice)
            {
                case 0:
                    return UnityEditor.SceneManagement.EditorSceneManager.SaveScenes(modified.ToArray());

                case 2:
                    // Captured before the first open, which invalidates the scenes it closes.
                    List<(string Path, bool Loaded)> onDisk = open.Where(scene => !string.IsNullOrEmpty(scene.path))
                        .Select(scene => (scene.path, scene.isLoaded)).ToList();
                    if (onDisk.Count == 0)
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
                        return true;
                    }

                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(onDisk[0].Path, UnityEditor.SceneManagement.OpenSceneMode.Single);
                    foreach ((string path, bool loaded) in onDisk.Skip(1))
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, loaded
                            ? UnityEditor.SceneManagement.OpenSceneMode.Additive
                            : UnityEditor.SceneManagement.OpenSceneMode.AdditiveWithoutLoading);
                    }

                    return true;

                default:
                    return false;
            }
        }

        #endregion

        #region interpreter, addressables, nodes, essentials

        // With C# scripts the interpreter is installed; with Visual Scripting only, a project that has one loses it (the Summary warns first).
        private static StepTick RunInterpreter()
            => RunRounds(record.Scripting == SetupScripting.VisualScriptingAndCSharp ? InstallInterpreter : RemoveInterpreter);

        private static StepTick RunAddressables()
        {
            if (!Settled())
                return StepTick.InProgress;

            StepResult result = ConfigureAddressables();
            if (!result.IsDone)
                return StepTick.Failed(result.Message);

            return result.Changed ? StepTick.Done(result.Message) : StepTick.Skipped(result.Message);
        }

        private static StepTick RunVisualScriptingNodes()
        {
            if (!Settled())
                return StepTick.InProgress;

            // It only adds, so there is nothing to do when the library has the SDK's types already (a run again, or an attempt a reload cut after it was done).
            if (ReadVisualScriptingLibraryCurrent() == true)
                return StepTick.Skipped("The Visual Scripting library already has the SDK's types.");

            // A reload during it ends the step in the middle: asked again, but not for ever. The count is written before the call, which a reload may not let end.
            if (Increment("attempts") > 2)
                return StepTick.Failed("The editor reloaded its scripts three times during the update of the Visual Scripting library. " + UpdateLibraryByHand);

            Save();

            StepResult result = UpdateVisualScriptingLibrary();
            if (!result.IsDone)
                return StepTick.Failed(result.Message);

            return result.Changed ? StepTick.Done(result.Message) : StepTick.Skipped(result.Message);
        }

        private static StepTick RunTmpEssentials()
        {
            if (!IsTmpPresent())
                return StepTick.Skipped("TextMesh Pro is not in the project.");

            if (!record.Data.ContainsKey("imported"))
            {
                if (!Settled())
                    return StepTick.InProgress;

                if (!TmpEssentialsMissing())
                    return StepTick.Skipped("The TextMesh Pro Essential Resources are already in the project.");

                StepResult result = ImportTmpEssentials();
                if (!result.IsDone)
                    return StepTick.Failed(result.Message);

                SetValue("imported", "1");
                TimedOut("importStarted", import_timeout_seconds);
                return StepTick.InProgress;
            }

            // The import is asynchronous: done when the settings asset is there and the editor is quiet.
            if (!TmpEssentialsMissing() && Settled())
                return StepTick.Done("The TextMesh Pro Essential Resources were imported.");

            return TimedOut("importStarted", import_timeout_seconds)
                ? StepTick.Failed("The TextMesh Pro Essential Resources were not imported in time. Import them from Window > TextMeshPro > Import TMP Essential Resources.")
                : StepTick.InProgress;
        }

        #endregion

        #region connections, restart

        private static StepTick RunConnections()
        {
            ConnectionSummary connections = ReadConnections(record.Version);

            // Never fails the run: the connection can be made later, and the page says what is missing.
            if (connections.Error != null)
                return StepTick.Done($"The connections could not be read: {connections.Error}");

            if (connections.Count == 0)
                return StepTick.Done("This computer has no connection yet.");

            string count = connections.Count == 1 ? "1 connection" : $"{connections.Count} connections";
            return StepTick.Done(connections.RunsVersion
                ? $"{count} on this computer; one runs platform version {record.Version}."
                : $"{count} on this computer; none runs platform version {record.Version}.");
        }

        private static StepTick RunRestart()
        {
            // Not this run's business alone: a change made earlier in this editor session waits for the restart too.
            if (!RestartPending)
                return StepTick.Skipped("Active Input Handling did not change in this editor session, so the editor does not have to restart.");

            return StepTick.Hold("Restart the editor to apply Active Input Handling.");
        }

        #endregion
    }
}
