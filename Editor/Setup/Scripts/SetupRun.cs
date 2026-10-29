using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using UnityEditor;

using UnityEngine;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>How the person writes the scripts of their environments.</summary>
    public enum SetupScripting
    {
        /// <summary>Visual Scripting only: no interpreter.</summary>
        VisualScriptingOnly,
        /// <summary>Visual Scripting and C#: the interpreter (HybridCLR) is installed and configured.</summary>
        VisualScriptingAndCSharp,
    }

    /// <summary>What the Choices page collects.</summary>
    public sealed class SetupChoices
    {
        /// <summary>A registry entry's <c>ReflectisVersion</c> (<c>2026.6.0</c>, <c>develop</c>).</summary>
        public string Version;
        public SetupScripting Scripting;
        /// <summary>The confirmation tick of a downgrade: without it, a version older than the installed one is refused.</summary>
        public bool AllowDowngrade;
        /// <summary>The confirmation that C# scripts stop running: without it, <see cref="SetupRun.Start"/> refuses to remove the interpreter
        /// from a project that has C# scripts (<see cref="SetupSteps.CountEnvironmentScripts"/>). <see cref="SetupRun.Plan"/> shows the removal
        /// either way: it is what the run would do.</summary>
        public bool AllowScriptRemoval;
    }

    public enum SetupStepState { Pending, Running, Done, Skipped, Failed }

    /// <summary>One step of a run, as the pages show it.</summary>
    public sealed class SetupStepStatus
    {
        /// <summary>One of <see cref="SetupStepIds"/>.</summary>
        public string Id;
        public string Title;
        public SetupStepState State;
        /// <summary>What it did, why it was skipped, or what failed; null before it has anything to say.</summary>
        public string Detail;
        /// <summary>How much of the run's time the step takes, relative to the others (see <see cref="SetupRun.Progress"/>).</summary>
        public float Weight;
    }

    /// <summary>The ids of the steps, in the order a run does them.</summary>
    public static class SetupStepIds
    {
        /// <summary>URP, API compatibility level, Active Input Handling (Both), max texture size. Never restarts the editor.</summary>
        public const string ProjectSettings = "project-settings";
        /// <summary>Installs the SDK at the chosen version, or updates or changes the project to it.</summary>
        public const string Packages = "packages";
        /// <summary>Waits until the scripts compile; fails when they do not.</summary>
        public const string Compile = "compile";
        /// <summary>The SDK's update routines between the old version and the new one, when it has any.</summary>
        public const string UpdateRoutine = "update-routine";
        /// <summary>The interpreter (HybridCLR), only with C# scripts.</summary>
        public const string Interpreter = "interpreter";
        /// <summary>The single default Addressables group and the SDK's profile.</summary>
        public const string Addressables = "addressables";
        /// <summary>The SDK's types in the Visual Scripting node library.</summary>
        public const string VisualScriptingNodes = "vs-nodes";
        /// <summary>TextMesh Pro's Essential Resources, when missing.</summary>
        public const string TmpEssentials = "tmp-essentials";
        /// <summary>Reads the computer's connections; changes nothing.</summary>
        public const string Connections = "connections";
        /// <summary>The editor restart that applies Active Input Handling, waiting for the person's click.</summary>
        public const string Restart = "restart";
    }

    /// <summary>
    /// The engine of the setup wizard: it runs every step of the setup, one at a time, by itself, inside the open editor,
    /// and keeps going across the domain reloads that package installs and recompilations cause.
    /// <list type="bullet">
    /// <item><b>The steps</b> are the ones of <see cref="SetupStepIds"/>, in that order; what each does is in
    /// <see cref="SetupSteps"/>, the code the command line (<see cref="SetupCli"/>) runs too. A step that has nothing to
    /// do is <see cref="SetupStepState.Skipped"/> with the reason in its detail.</item>
    /// <item><b>The state</b> is <c>UserSettings/Virtuademy/setup-run.json</c> (per user and project, not versioned): the
    /// choices, every step's state and detail, and the scratch of the step in progress. The <c>[InitializeOnLoad]</c> of
    /// this class reads it after each domain reload and goes on with the step that was running. A step that was
    /// interrupted in the middle runs again from its start, which every step allows (each does only what is left).</item>
    /// <item><b>A new editor session</b> does not resume: a run that was still going when the editor closed is shown as
    /// failed at the step it was in ("the editor was closed"), and a restart that was waiting is shown as done. The
    /// session is told apart by a <c>SessionState</c> flag, which survives reloads but not the editor.</item>
    /// <item><b>Progress</b> comes from the steps' weights: a long step (<c>packages</c>, <c>interpreter</c>) weighs more.
    /// Inside a step there is no percentage, because Unity gives none for package resolution.</item>
    /// <item><b>Nothing here draws.</b> <see cref="Changed"/> is raised on the main thread on every change of state,
    /// and the pages read <see cref="Steps"/>, <see cref="Progress"/>, <see cref="IsRunning"/> and
    /// <see cref="RestartPending"/>.</item>
    /// </list>
    /// A step that blocks the editor (the interpreter's setup clones repositories; the Visual Scripting library takes
    /// over a minute) does so for its whole duration, as the same buttons of the window always did: the step is shown as
    /// <see cref="SetupStepState.Running"/> before it starts.
    /// </summary>
    [InitializeOnLoad]
    public static partial class SetupRun
    {
        private sealed class StepDefinition
        {
            internal string Id;
            internal string Title;
            internal float Weight;
        }

        // The order is the order of the run. Weights are relative: the long ones are the package resolution, the
        // interpreter setup (it can clone repositories) and the Visual Scripting library (73 to 80 s on a scratch project).
        private static readonly StepDefinition[] definitions =
        {
            new() { Id = SetupStepIds.ProjectSettings, Title = "Project settings", Weight = 1f },
            new() { Id = SetupStepIds.Packages, Title = "Virtuademy SDK packages", Weight = 6f },
            new() { Id = SetupStepIds.Compile, Title = "Compile the scripts", Weight = 2f },
            new() { Id = SetupStepIds.UpdateRoutine, Title = "Update routines", Weight = 4f },
            new() { Id = SetupStepIds.Interpreter, Title = "Interpreter for C# scripts", Weight = 6f },
            new() { Id = SetupStepIds.Addressables, Title = "Addressables", Weight = 1f },
            new() { Id = SetupStepIds.VisualScriptingNodes, Title = "Visual Scripting nodes", Weight = 3f },
            new() { Id = SetupStepIds.TmpEssentials, Title = "TextMesh Pro essentials", Weight = 1f },
            new() { Id = SetupStepIds.Connections, Title = "Connections", Weight = 0.5f },
            new() { Id = SetupStepIds.Restart, Title = "Restart the editor", Weight = 0.5f },
        };

        // Set when a run starts and never cleared: it lives as long as the editor process, through domain reloads.
        private const string session_key = "Virtuademy.SDK.Environments.Setup.RunSession";

        private const string state_file_name = "setup-run.json";

        // The layout of setup-run.json: a file of another one is not a run this code can resume or show.
        private const int state_format = 1;

        /// <summary>What is saved to <c>setup-run.json</c>.</summary>
        internal sealed class RunRecord
        {
            public int Format = state_format;
            public string Version;
            [JsonConverter(typeof(StringEnumConverter))] public SetupScripting Scripting;
            public bool AllowDowngrade;

            /// <summary>The version the project recorded when the run started; null for a project without the SDK.</summary>
            public string FromVersion;
            public string StartedUtc;
            public bool Running;
            public bool StopRequested;

            /// <summary>The run ended with the restart step waiting for the person. Whether a restart is pending now is not
            /// this run's to say (<see cref="SetupRun.RestartPending"/>): this is how a new editor session shows the step as done.</summary>
            public bool RestartPending;

            /// <summary>The index in <see cref="Steps"/> of the step being run.</summary>
            public int Current;
            public List<StepRecord> Steps = new();

            /// <summary>The scratch of the step in progress (rounds waited, phase of a routine...). Cleared between steps.</summary>
            public Dictionary<string, string> Data = new();
        }

        internal sealed class StepRecord
        {
            public string Id;
            [JsonConverter(typeof(StringEnumConverter))] public SetupStepState State;
            public string Detail;
        }

        private static RunRecord record;
        private static List<SetupStepStatus> steps = new();
        private static ReadOnlyCollection<SetupStepStatus> stepsView = new(new List<SetupStepStatus>());
        private static bool dirty;

        /// <summary>Whether a run is going: the steps advance by themselves until it ends, fails or is stopped.</summary>
        public static bool IsRunning => record != null && record.Running;

        /// <summary>The steps of the current run, or of the last one (also across editor sessions); empty before any run.</summary>
        public static IReadOnlyList<SetupStepStatus> Steps => stepsView;

        /// <summary>
        /// 0 to 1, by the steps' weights: the share of the weight of the steps that did something that is done. A step
        /// that was skipped counts for nothing, on either side, so the bar does not start half full when most of the
        /// project is set up. The step in progress counts for nothing until it is done. When the last step is the editor
        /// restart waiting for the person, the run is complete: 1.
        /// </summary>
        public static float Progress
        {
            get
            {
                float total = 0f;
                float done = 0f;
                foreach (SetupStepStatus step in steps)
                {
                    if (step.State == SetupStepState.Skipped)
                        continue;

                    total += step.Weight;
                    if (step.State == SetupStepState.Done || (step.Id == SetupStepIds.Restart && RestartPending))
                        done += step.Weight;
                }

                return total <= 0f ? 0f : Mathf.Clamp01(done / total);
            }
        }

        /// <summary>The editor has to restart: Active Input Handling was changed in this editor session and applies only when it
        /// starts again. It does not belong to a run (any later run, "Try again" or Fix leaves it set) and ends with the session;
        /// <see cref="RestartEditorNow"/> does the restart. Never automatic.</summary>
        public static bool RestartPending => CreatorKitSetupWindow.InputHandlingChangedThisSession;

        /// <summary>Raised on the main thread whenever a step changes state or detail, and when a run starts or ends.</summary>
        public static event Action Changed;

        static SetupRun()
        {
            // Unity's asset import workers are editor processes of their own that load this assembly too, with no session flag:
            // one would take the run for an old session's and rewrite its file.
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            Load();

            // After the editor has settled: at the first reload of a session the asset database is not ready.
            EditorApplication.delayCall += Resume;
        }

        #region Public API

        /// <summary>
        /// What <see cref="Start"/> would do for <paramref name="choices"/>, and nothing run: every step with the state it
        /// has now (<see cref="SetupStepState.Pending"/>, or <see cref="SetupStepState.Skipped"/> with the reason when the
        /// project already has what the step makes), and a <see cref="SetupStepState.Failed"/> step, with the reason, for
        /// a choice that <see cref="Start"/> would refuse (no version, a downgrade without confirmation, a version for another
        /// Unity). Reads only: no network, no file is changed. It knows the Unity version of the chosen registry entry only
        /// when the registry was read before.
        /// </summary>
        public static IReadOnlyList<SetupStepStatus> Plan(SetupChoices choices)
        {
            if (choices == null)
                throw new ArgumentNullException(nameof(choices));

            return BuildSteps(choices, FindRecordedVersion(), evaluateProject: true);
        }

        /// <summary>
        /// Starts a run and returns at once: the steps advance by themselves, one at a time, and <see cref="Changed"/> tells
        /// each change. Refused, with nothing run, while a run is going, when <see cref="Plan"/> has a failed step, and when the
        /// run would remove the interpreter from a project that has C# scripts without <see cref="SetupChoices.AllowScriptRemoval"/>:
        /// the steps are then the plan, with the reason on the one that failed, and <see cref="IsRunning"/> stays false.
        /// With <paramref name="onlySteps"/> (step ids) the run does those steps only, as the status page's "Fix" does: the others are
        /// <see cref="SetupStepState.Skipped"/> with <see cref="NotPartOfRun"/>.
        /// </summary>
        public static void Start(SetupChoices choices, IReadOnlyCollection<string> onlySteps = null)
        {
            if (choices == null)
                throw new ArgumentNullException(nameof(choices));

            if (IsRunning)
            {
                UnityEngine.Debug.LogWarning("[Setup] A setup run is already going: it was not started again.");
                return;
            }

            string recorded = FindRecordedVersion();

            // The plan the run starts from holds the skips that follow from the choices alone. What depends on the state of
            // the project is decided by each step when it comes, because an earlier step may change it.
            List<SetupStepStatus> plan = BuildSteps(choices, recorded, evaluateProject: false);

            // A run for one check of the status page ("Fix"): the other steps are not part of it, whatever they would have done.
            if (onlySteps != null)
            {
                foreach (SetupStepStatus step in plan.Where(step => !onlySteps.Contains(step.Id)))
                {
                    step.State = SetupStepState.Skipped;
                    step.Detail = NotPartOfRun;
                }
            }

            // Removing the interpreter from a project that has C# scripts waits for the confirmation (the Summary's tick, -vdAllowScriptRemoval).
            SetupStepStatus removal = plan.FirstOrDefault(step => step.Id == SetupStepIds.Interpreter && step.State == SetupStepState.Pending
                                                                  && step.Detail == RemovesInterpreterDetail);
            int scripts = removal != null ? SetupSteps.CountEnvironmentScripts() : 0;
            if (scripts > 0 && !choices.AllowScriptRemoval)
            {
                removal.State = SetupStepState.Failed;
                removal.Detail = $"The {scripts} C# script(s) in {SetupSteps.environment_scripts_folder} would no longer run. Confirm the removal of the interpreter to go on.";
            }

            record = new RunRecord
            {
                Version = choices.Version,
                Scripting = choices.Scripting,
                AllowDowngrade = choices.AllowDowngrade,
                FromVersion = recorded,
                StartedUtc = DateTime.UtcNow.ToString("o"),
            };
            steps = plan;
            stepsView = steps.AsReadOnly();

            SetupStepStatus refused = steps.FirstOrDefault(step => step.State == SetupStepState.Failed);
            if (refused != null)
            {
                UnityEngine.Debug.LogError($"[Setup] The setup was not started: {refused.Detail}");
                Save();
                RaiseChanged();
                return;
            }

            SessionState.SetBool(session_key, true);
            record.Running = true;
            record.Current = NextRunnable(0);
            ResetStepMemory();
            Hook();

            if (record.Current >= steps.Count)
            {
                End();
                return;
            }

            Save();
            RaiseChanged();
        }

        /// <summary>The detail of a step that a run for some steps only (<see cref="Start"/> with <c>onlySteps</c>) leaves out.</summary>
        public const string NotPartOfRun = "Not part of this run.";

        /// <summary>The interpreter step's detail in a plan that removes it (Visual Scripting only, on a project that has it).</summary>
        public const string RemovesInterpreterDetail = "Removes the interpreter for C# scripts.";

        /// <summary>The run ends when the step in progress is done: the steps after it stay pending.</summary>
        public static void StopAfterCurrentStep()
        {
            if (!IsRunning)
                return;

            record.StopRequested = true;
            Save();
        }

        /// <summary>Reopens the project in a new editor session, which applies Active Input Handling. Only when
        /// <see cref="RestartPending"/>; asks to save the open scenes first, and does nothing if the person cancels.</summary>
        public static void RestartEditorNow()
        {
            if (!RestartPending)
            {
                UnityEngine.Debug.LogWarning("[Setup] No restart is waiting.");
                return;
            }

            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            AssetDatabase.SaveAssets();
            EditorApplication.OpenProject(ProjectRoot);
        }

        #endregion

        #region Plan

        /// <summary>
        /// The steps in order, with the state they start from. A step is skipped here only for a reason that does not change
        /// during the run; with <paramref name="evaluateProject"/> also for what the project has now (for <see cref="Plan"/>,
        /// which shows what would happen if the run started now).
        /// </summary>
        private static List<SetupStepStatus> BuildSteps(SetupChoices choices, string recorded, bool evaluateProject)
        {
            List<SetupStepStatus> list = definitions
                .Select(definition => new SetupStepStatus { Id = definition.Id, Title = definition.Title, Weight = definition.Weight, State = SetupStepState.Pending })
                .ToList();

            SetupStepStatus Of(string id) => list.First(step => step.Id == id);

            void Skip(string id, string reason)
            {
                SetupStepStatus step = Of(id);
                step.State = SetupStepState.Skipped;
                step.Detail = reason;
            }

            void Fail(string id, string reason)
            {
                SetupStepStatus step = Of(id);
                step.State = SetupStepState.Failed;
                step.Detail = reason;
            }

            // project-settings: whether it is configured is the state of the project.
            if (evaluateProject)
            {
                UnityEngine.Rendering.RenderPipelineAsset asset = CreatorKitSetupWindow.LoadDefaultRenderPipelineAsset();
                if (asset != null && SetupSteps.ProjectSettingsConfigured(asset))
                    Skip(SetupStepIds.ProjectSettings, "The project settings are already configured.");
            }

            // packages
            string version = choices.Version?.Trim();
            bool sdkInstalled = SetupSteps.IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name);
            bool sameVersion = SameVersion(recorded, version);
            List<string> oldPackages = sdkInstalled ? new List<string>() : SetupSteps.OtherPackagesOfOurs();
            if (oldPackages.Count > 0)
            {
                // Before any step runs: the first ones would half change a project the rest cannot finish.
                Fail(SetupStepIds.Packages, $"This project has Virtuademy packages ({string.Join(", ", oldPackages)}) and not " +
                                            $"{CreatorKitSetupWindow.sdk_environments_package_name}: it comes from a version before 2026.6, or was set up by hand. " +
                                            "The setup cannot move it: see \"Updating from an older platform version\" in the documentation.");
            }
            else if (string.IsNullOrEmpty(version))
            {
                Fail(SetupStepIds.Packages, "No Virtuademy version is chosen.");
            }
            else if (sdkInstalled && sameVersion)
            {
                Skip(SetupStepIds.Packages, $"Virtuademy {recorded} is already installed.");
            }
            else
            {
                int? order = string.IsNullOrEmpty(recorded) ? null : CompareVersions(version, recorded);
                PackageRegistry[] registry = SetupSteps.CachedRegistry;
                PackageRegistry entry = registry?.FirstOrDefault(candidate => string.Equals(candidate.ReflectisVersion, version, StringComparison.OrdinalIgnoreCase));

                if (order < 0 && !choices.AllowDowngrade)
                    Fail(SetupStepIds.Packages, $"Virtuademy {version} is older than the installed {recorded}. Confirm the downgrade to go on.");
                else if (entry != null && entry.RequiredUnityVersion != CreatorKitSetupWindow.UnityVersion)
                    Fail(SetupStepIds.Packages, $"Virtuademy {version} requires Unity {entry.RequiredUnityVersion}; this editor is {CreatorKitSetupWindow.UnityVersion}.");
                else
                    Of(SetupStepIds.Packages).Detail = string.IsNullOrEmpty(recorded) || sameVersion
                        ? $"Install Virtuademy {version}."
                        : order < 0
                            ? $"Downgrade Virtuademy {recorded} to {version}."
                            : $"Change Virtuademy {recorded} to {version}.";
            }

            // update-routine: follows from the two versions alone.
            if (string.IsNullOrEmpty(recorded))
            {
                Skip(SetupStepIds.UpdateRoutine, "A new project has nothing to update from.");
            }
            else if (!string.IsNullOrEmpty(version) && !sameVersion)
            {
                SetupSteps.RoutineScope scope = SetupSteps.MatchRoutines(recorded, version, Enumerable.Empty<string>(), true, out _);
                if (scope == SetupSteps.RoutineScope.NotASeries)
                    Skip(SetupStepIds.UpdateRoutine, $"{recorded} to {version} is not a step between released versions.");
                else if (scope == SetupSteps.RoutineScope.NotForward)
                    Skip(SetupStepIds.UpdateRoutine, "Update routines only run when the version goes forward.");
                else
                    Of(SetupStepIds.UpdateRoutine).Detail = $"Runs the update routines of the new SDK between {recorded} and {version}, if it has any.";
            }
            else
            {
                Skip(SetupStepIds.UpdateRoutine, "The version does not change.");
            }

            // interpreter: installed with C# scripts; with Visual Scripting only, removed from a project that has it.
            if (choices.Scripting != SetupScripting.VisualScriptingAndCSharp)
            {
                if (SetupSteps.InterpreterInManifest())
                    Of(SetupStepIds.Interpreter).Detail = RemovesInterpreterDetail;
                else
                    Skip(SetupStepIds.Interpreter, "Visual Scripting only: no interpreter.");
            }

            // addressables: a full run resets them only when the project is not configured, and the person confirms it (DestructiveActions).
            if (evaluateProject && SetupSteps.ReadAddressablesConfigured() == true)
                Skip(SetupStepIds.Addressables, "The Addressables are already configured.");

            // vs-nodes: only adds, so it has nothing to do when the library has the SDK's types already.
            if (evaluateProject && SetupSteps.ReadVisualScriptingLibraryCurrent() == true)
                Skip(SetupStepIds.VisualScriptingNodes, "The Visual Scripting library already has the SDK's types.");

            // tmp-essentials
            if (evaluateProject && !SetupSteps.TmpEssentialsMissing())
                Skip(SetupStepIds.TmpEssentials, SetupSteps.IsTmpPresent()
                    ? "The TextMesh Pro Essential Resources are already in the project."
                    : "TextMesh Pro is not in the project.");

            // restart: whether Active Input Handling changes is the state of the project, and a change made earlier in this
            // editor session (by a run that is over, or the window) still waits for the restart.
            if (evaluateProject && CreatorKitSetupWindow.GetInputHandlingStatus() && !RestartPending)
                Skip(SetupStepIds.Restart, "Active Input Handling is already Both.");

            return list;
        }

        /// <summary>
        /// What the steps of <paramref name="plan"/> (from <see cref="Plan"/>) that will run do to the project and cannot be undone by
        /// the run, in plain words, for the one confirmation before a run starts. Empty when the plan has nothing of the kind.
        /// </summary>
        internal static List<string> DestructiveActions(SetupChoices choices, IReadOnlyList<SetupStepStatus> plan)
        {
            List<string> actions = new();
            SetupStepStatus Pending(string id) => plan.FirstOrDefault(step => step.Id == id && step.State == SetupStepState.Pending);

            string recorded = FindRecordedVersion();
            string version = choices.Version?.Trim();
            if (Pending(SetupStepIds.Packages) != null && !string.IsNullOrEmpty(recorded) && !SameVersion(recorded, version))
            {
                actions.Add($"Move Virtuademy {recorded} to {version}: the Virtuademy packages that {version} does not list are removed from the project" +
                            (CompareVersions(version, recorded) < 0 ? ", and an older version can break it." : "."));
            }

            if (Pending(SetupStepIds.Interpreter) != null && choices.Scripting != SetupScripting.VisualScriptingAndCSharp)
            {
                int scripts = SetupSteps.CountEnvironmentScripts();
                actions.Add("Remove the interpreter for C# scripts" + (scripts > 0
                    ? $": the {scripts} C# script(s) in {SetupSteps.environment_scripts_folder} stay in the project but no longer run in Virtuademy."
                    : ". C# scripts you add later will not run until you add it again."));
            }

            if (Pending(SetupStepIds.UpdateRoutine) != null)
                actions.Add("Run the update routines of the new version: they rename and rewrite scripts, graphs and assets of the project, and close the open scenes. If a scene has unsaved changes, you choose then: save them, lose them, or stop.");

            // Without the SDK its Addressables cannot be asked: the setup resets them then, but there is something to lose only when the project has some.
            if (Pending(SetupStepIds.Addressables) != null
                && (SetupSteps.ReadAddressablesConfigured() == false || Directory.Exists(Path.Combine(ProjectRoot, "Assets", "AddressableAssetsData"))))
                actions.Add("Reset the Addressables: every group other than the SDK's single default group, and every entry in the groups, is removed.");

            return actions;
        }

        /// <summary>The version the project has installed: the recorded one, when the SDK is registered; null otherwise.</summary>
        internal static string FindRecordedVersion()
        {
            // The window records a version for a project that has no SDK yet (its default, the latest release): that is not what is
            // installed, and taking it for it would make any older choice a "downgrade".
            if (!SetupSteps.IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
                return null;

            string recorded = SetupSteps.FindConfiguration()?.CurrentInstallationVersion;
            return string.IsNullOrEmpty(recorded) ? null : recorded;
        }

        private static bool SameVersion(string a, string b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        private static readonly Regex version_numbers = new(@"^v?(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant);

        /// <summary>
        /// Which of two versions is later: negative when <paramref name="a"/> is older than <paramref name="b"/>. The
        /// development version is later than every release. Null when one of them is neither (a test entry): nothing then
        /// says that it is a downgrade.
        /// </summary>
        internal static int? CompareVersions(string a, string b)
        {
            long? Rank(string version)
            {
                if (string.Equals(version?.Trim(), "develop", StringComparison.OrdinalIgnoreCase))
                    return long.MaxValue;

                Match match = version_numbers.Match(version?.Trim() ?? string.Empty);
                if (!match.Success)
                    return null;

                long patch = match.Groups[3].Success ? long.Parse(match.Groups[3].Value) : 0;
                return long.Parse(match.Groups[1].Value) * 1_000_000_000L + long.Parse(match.Groups[2].Value) * 1_000_000L + patch;
            }

            long? rankA = Rank(a);
            long? rankB = Rank(b);
            return rankA.HasValue && rankB.HasValue ? rankA.Value.CompareTo(rankB.Value) : null;
        }

        #endregion

        #region State file

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        private static string StatePath => Path.Combine(Path.GetDirectoryName(SetupUserSettings.FilePath), state_file_name);

        private static readonly JsonSerializerSettings json_settings = new() { Formatting = Formatting.Indented, NullValueHandling = NullValueHandling.Ignore };

        private static void Save()
        {
            dirty = false;
            if (record == null)
                return;

            record.Steps = steps.Select(step => new StepRecord { Id = step.Id, State = step.State, Detail = step.Detail }).ToList();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, JsonConvert.SerializeObject(record, json_settings));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                UnityEngine.Debug.LogWarning($"[Setup] Could not write {StatePath}: {ex.Message}. The run goes on, but would not survive a domain reload.");
            }
        }

        /// <summary>Reads the last run, for the pages and to resume it. A new editor session ends what the old one left going.</summary>
        private static void Load()
        {
            try
            {
                if (!File.Exists(StatePath))
                    return;

                record = JsonConvert.DeserializeObject<RunRecord>(File.ReadAllText(StatePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                UnityEngine.Debug.LogWarning($"[Setup] Could not read {StatePath}: {ex.Message}. The last setup run is ignored.");
                record = null;
                return;
            }

            if (record == null || record.Format != state_format || record.Steps == null || record.Steps.Count == 0)
            {
                record = null;
                return;
            }

            steps = record.Steps.Select(saved =>
            {
                StepDefinition definition = definitions.FirstOrDefault(candidate => candidate.Id == saved.Id);
                return new SetupStepStatus
                {
                    Id = saved.Id,
                    Title = definition?.Title ?? saved.Id,
                    Weight = definition?.Weight ?? 1f,
                    State = saved.State,
                    Detail = saved.Detail,
                };
            }).ToList();
            stepsView = steps.AsReadOnly();

            if (SessionState.GetBool(session_key, false))
                return;

            // The editor was restarted since the file was written.
            bool changed = false;
            if (record.Running)
            {
                SetupStepStatus interrupted = record.Current >= 0 && record.Current < steps.Count ? steps[record.Current] : null;
                if (interrupted != null)
                {
                    interrupted.State = SetupStepState.Failed;
                    interrupted.Detail = "The editor was closed during this step. Run the setup again: it does only what is left.";
                }

                record.Running = false;
                record.StopRequested = false;
                changed = true;
            }

            if (record.RestartPending)
            {
                SetupStepStatus restart = steps.FirstOrDefault(step => step.Id == SetupStepIds.Restart);
                if (restart != null)
                {
                    restart.State = SetupStepState.Done;
                    restart.Detail = "The editor was restarted: Active Input Handling applies.";
                }

                record.RestartPending = false;
                changed = true;
            }

            if (changed)
                Save();
        }

        #endregion

        #region Loop

        private static void Resume()
        {
            if (!IsRunning)
            {
                RaiseChanged();
                return;
            }

            ResetStepMemory();
            Hook();
            RaiseChanged();
        }

        private static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void RaiseChanged()
        {
            Action handlers = Changed;
            if (handlers == null)
                return;

            foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                }
            }
        }

        private static void Tick()
        {
            if (!IsRunning)
            {
                EditorApplication.update -= Tick;
                return;
            }

            try
            {
                Advance();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                Finish(CurrentStep, SetupStepState.Failed, "Unexpected failure: " + ex.Message + " The Console has the details.");
            }

            if (dirty && IsRunning)
                Save();
        }

        private static SetupStepStatus CurrentStep => record != null && record.Current >= 0 && record.Current < steps.Count ? steps[record.Current] : null;

        private static void Advance()
        {
            SetupStepStatus step = CurrentStep;
            if (step == null)
            {
                End();
                return;
            }

            if (step.State == SetupStepState.Pending)
            {
                if (record.StopRequested)
                {
                    UnityEngine.Debug.Log("[Setup] The setup run was stopped before the step '" + step.Title + "'.");
                    End();
                    return;
                }

                // Shown as running one tick before it starts, so a step that blocks the editor is on the page already.
                step.State = SetupStepState.Running;
                step.Detail = null;
                record.Data.Clear();
                ResetStepMemory();
                Save();
                RaiseChanged();
                return;
            }

            StepTick tick = RunStep(step.Id);
            switch (tick.Kind)
            {
                case StepTickKind.InProgress:
                    break;
                case StepTickKind.Done:
                    Finish(step, SetupStepState.Done, tick.Detail);
                    break;
                case StepTickKind.Skipped:
                    Finish(step, SetupStepState.Skipped, tick.Detail);
                    break;
                case StepTickKind.Failed:
                    Finish(step, SetupStepState.Failed, tick.Detail);
                    break;
                case StepTickKind.Hold:
                    // The run is complete but for the restart, which waits for the person.
                    step.State = SetupStepState.Pending;
                    step.Detail = tick.Detail;
                    record.RestartPending = true;
                    End();
                    break;
            }
        }

        private static void Finish(SetupStepStatus step, SetupStepState state, string detail)
        {
            if (step == null)
            {
                End();
                return;
            }

            step.State = state;
            step.Detail = detail;
            record.Data.Clear();

            if (state == SetupStepState.Failed)
            {
                UnityEngine.Debug.LogError($"[Setup] {step.Title}: {detail}");
                End();
                return;
            }

            UnityEngine.Debug.Log($"[Setup] {step.Title}: {(state == SetupStepState.Skipped ? "skipped. " : "done. ")}{detail}");

            record.Current = NextRunnable(record.Current + 1);
            ResetStepMemory();

            if (record.Current >= steps.Count || record.StopRequested)
            {
                End();
                return;
            }

            Save();
            RaiseChanged();
        }

        // The steps that are Pending from here on: one that the plan skipped is not run.
        private static int NextRunnable(int from)
        {
            int index = from;
            while (index < steps.Count && steps[index].State != SetupStepState.Pending)
                index++;

            return index;
        }

        private static void End()
        {
            record.Running = false;
            record.StopRequested = false;
            EditorApplication.update -= Tick;
            Save();
            RaiseChanged();
        }

        #endregion
    }
}
