using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using UnityEditor;

using UnityEngine;
using UnityEngine.Rendering;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// What one step of the setup <b>does</b>, in one place, for the two callers that run the steps:
    /// <see cref="SetupCli"/> (the command line, one launch per round, with exit codes) and
    /// <see cref="SetupRun"/> (the engine of the wizard, inside the open editor, across domain
    /// reloads). Neither has its own copy: a step is fixed here once. Each method is idempotent and does
    /// one round, as the command line always has: it applies what it can and returns a
    /// <see cref="StepResult"/> whose code is one of <see cref="SetupCli"/>'s exit codes, where
    /// <see cref="SetupCli.ExitRelaunch"/> means "the project changed and needs the editor to resolve,
    /// compile or reload before the step is called again".
    /// <para>
    /// The methods log as they always did, with <see cref="Prefix"/> in front: the command line sets
    /// it to <c>[SetupCli] </c>, which is what its logs and its REPORT have always carried; the engine
    /// leaves <c>[Setup] </c>.
    /// </para>
    /// </summary>
    internal static class SetupSteps
    {
        /// <summary>What a step returned: the command line's exit code, a short message without the
        /// step's name (the engine shows it as the step's detail), and whether the project changed.</summary>
        internal readonly struct StepResult
        {
            internal StepResult(int code, string message, bool changed)
            {
                Code = code;
                Message = message;
                Changed = changed;
            }

            /// <summary>One of <see cref="SetupCli"/>'s exit codes.</summary>
            internal int Code { get; }
            internal string Message { get; }

            /// <summary>The step wrote something. False for a step that found nothing to do.</summary>
            internal bool Changed { get; }

            internal bool IsDone => Code == SetupCli.ExitDone;
            internal bool NeedsRelaunch => Code == SetupCli.ExitRelaunch;
        }

        /// <summary>What <see cref="ReadConnections"/> found.</summary>
        internal readonly struct ConnectionSummary
        {
            internal ConnectionSummary(int count, bool runsVersion, string error)
            {
                Count = count;
                RunsVersion = runsVersion;
                Error = error;
            }

            /// <summary>How many connections the computer holds.</summary>
            internal int Count { get; }

            /// <summary>Whether one of them runs the platform version that was asked about.</summary>
            internal bool RunsVersion { get; }

            /// <summary>Why the file could not be read; null when it could, or when there is none.</summary>
            internal string Error { get; }
        }

        // Where the SDK keeps its editor code. Named, not referenced: this package installs the
        // SDK, so it cannot compile against it.
        private const string sdk_editor_assembly = "Virtuademy.SDK.Environments.Editor";
        private const string addressables_configuration_type = sdk_editor_assembly + ".AddressablesConfiguration";
        private const string visual_scripting_setup_type = "Virtuademy.SDK.Environments.VisualScripting.Editor.VisualScriptingSetupEditor";
        internal const string update_routines_menu = "Virtuademy/Update routines/";

        // The command line of the same routines, in the SDK's BreakingChangeSolvers: named, not referenced, like the menu.
        private const string update_routines_cli = "Virtuademy.BreakingChangeSolvers.UpdateRoutinesCli.Run";

        // As many as HotUpdateSetupper allows itself when the window drives it across reloads.
        private const int max_interpreter_attempts = 3;

        // Counted across launches (and rounds), so it cannot live in SessionState, which dies with the editor.
        // Library/ is per machine and never committed.
        private const string interpreter_attempts_file = "Library/VirtuademySetupCli.interpreter-attempts";

        // Where the "Virtuademy/Update routines/..." menu items say they are applied from, kept
        // across the launches of an update: the launch that moves the packages cannot list the
        // routines of the new SDK, which only exists after the resolve.
        private const string update_origin_file = "Library/VirtuademySetupCli.updated-from";

        // The SDK's connections, written by its login and read by its windows and command lines. No secret in it:
        // which tenant, app and platform version each connection is, and whether it is logged in.
        private const string connections_status_file = "status.json";

        /// <summary>What the logs start with: <c>[SetupCli] </c> for the command line, <c>[Setup] </c> otherwise.</summary>
        internal static string Prefix { get; set; } = "[Setup] ";

        /// <summary>Who runs the steps: it decides how the few messages that tell what to do next are worded.</summary>
        internal enum StepCaller { Wizard, CommandLine }

        /// <summary>The wizard unless the command line says so (<see cref="SetupCli"/>, together with <see cref="Prefix"/>).</summary>
        internal static StepCaller Caller { get; set; } = StepCaller.Wizard;

        // The command line's words are the ones it always had; the wizard has no launches, steps with names or arguments to point at.
        private static string For(string commandLine, string wizard) => Caller == StepCaller.CommandLine ? commandLine : wizard;

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        // Where the registry is read from, and what was read: one download per launch, shared by the steps and
        // the report. The engine puts the registry it fetched off the main thread here (UseRegistry).
        private static string registrySource = CreatorKitSetupWindow.package_registry_path;
        private static PackageRegistry[] registryCache;

        #region Results and logging

        private static StepResult Unchanged(string step, string message, bool warning = false)
        {
            Write(step, message, warning ? LogType.Warning : LogType.Log);
            return new StepResult(SetupCli.ExitDone, message, false);
        }

        private static StepResult Applied(string step, string message)
        {
            Write(step, message, LogType.Log);
            return new StepResult(SetupCli.ExitDone, message, true);
        }

        private static StepResult Relaunch(string step, string message)
        {
            Write(step, message, LogType.Log);
            return new StepResult(SetupCli.ExitRelaunch, message, true);
        }

        private static StepResult Fail(string step, int code, string message)
        {
            Write(step, message, LogType.Error);
            return new StepResult(code, message, false);
        }

        private static void Write(string step, string message, LogType type)
        {
            string line = $"{Prefix}{step}: {message}";
            switch (type)
            {
                case LogType.Error: Debug.LogError(line); break;
                case LogType.Warning: Debug.LogWarning(line); break;
                default: Debug.Log(line); break;
            }
        }

        #endregion

        #region Registry

        /// <summary>Reads the registry from another URL or a local file from now on (<c>-vdSetupRegistry</c>).</summary>
        internal static void UseRegistrySource(string source)
        {
            registrySource = source;
            registryCache = null;
        }

        /// <summary>Hands over a registry that was read already, so the steps do not read it again.</summary>
        internal static void UseRegistry(PackageRegistry[] registry) => registryCache = registry;

        /// <summary>What was read, or null before the first read. Never fetches.</summary>
        internal static PackageRegistry[] CachedRegistry => registryCache;

        /// <summary>
        /// The registry, read from <see cref="registrySource"/> once and kept: the published URL, or what
        /// <c>-vdSetupRegistry</c> names. Blocks until it is read. Returns <see cref="SetupCli.ExitDone"/>, or the
        /// exit code for a registry that cannot be read or holds no entries.
        /// </summary>
        internal static StepResult LoadRegistry(string step, out PackageRegistry[] registry)
        {
            registry = registryCache;
            if (registry != null)
                return default;

            try
            {
                registry = ReadRegistry(registrySource);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
            {
                return Fail(step, SetupCli.ExitRegistryUnavailable, $"could not read the registry {registrySource}: {ex.Message}");
            }

            if (registry == null || registry.Length == 0)
                return Fail(step, SetupCli.ExitRegistryUnavailable, $"the registry {registrySource} holds no entries.");

            registryCache = registry;
            return default;
        }

        /// <summary>Reads the registry named by <paramref name="source"/>. Touches no Unity API, so the engine runs it on the thread pool.</summary>
        internal static PackageRegistry[] ReadRegistry(string source)
        {
            // A local file, for testing a registry change before it is published.
            if (File.Exists(source))
                return JsonConvert.DeserializeObject<PackageRegistry[]>(File.ReadAllText(source));

            // Anything that is not a URL was meant as a file: say so, rather than let HttpClient
            // fail on a relative URI.
            if (!source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException($"No such file: {Path.GetFullPath(source)}");

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };

            // Waited for synchronously, so the whole round runs inside this call. The request is
            // started with Task.Run so its continuations run on the thread pool: blocking the main
            // thread on a task that needs the main thread to finish would deadlock.
            string body = Task.Run(() => client.GetStringAsync(source)).GetAwaiter().GetResult();
            return JsonConvert.DeserializeObject<PackageRegistry[]>(body);
        }

        /// <summary>The source the registry is read from now; what the engine's background read must use.</summary>
        internal static string RegistrySource => registrySource;

        #endregion

        #region Steps

        /// <summary>
        /// The Configure button: URP, API compatibility .NET Framework, Active Input Handling Both and the max texture
        /// size override. <paramref name="inputHandlingChanged"/> says whether Active Input Handling changed, which only
        /// takes effect when the editor starts again: the step never restarts it, the caller decides.
        /// </summary>
        internal static StepResult Configure(out bool inputHandlingChanged)
        {
            const string step = "configure";
            inputHandlingChanged = false;

            RenderPipelineAsset asset = CreatorKitSetupWindow.LoadDefaultRenderPipelineAsset();
            if (asset == null)
            {
                return Fail(step, SetupCli.ExitProjectSettingsFailed,
                    "the installer's render pipeline asset could not be loaded. The installer package looks incomplete.");
            }

            if (ProjectSettingsConfigured(asset))
                return Unchanged(step, "the project settings are already configured.");

            inputHandlingChanged = CreatorKitSetupWindow.ApplyProjectSettings(asset);

            if (!ProjectSettingsConfigured(asset))
                return Fail(step, SetupCli.ExitProjectSettingsFailed, "the settings were applied, but the checks still fail.");

            return Applied(step, "URP, API compatibility .NET Framework, Active Input Handling Both and max texture size 1024 applied." +
                                 (inputHandlingChanged ? For(" The input handling applies from the next editor launch.", " The input handling applies when the editor restarts.") : string.Empty));
        }

        internal static bool ProjectSettingsConfigured(RenderPipelineAsset asset)
            => CreatorKitSetupWindow.IsRenderPipelineConfigured(asset)
               && CreatorKitSetupWindow.GetProjectSettingsStatus()
               && CreatorKitSetupWindow.GetInputHandlingStatus()
               && CreatorKitSetupWindow.GetMaxTextureSizeOverride();

        internal static StepResult InstallSdk(string version)
        {
            const string step = "sdk";

            StepResult registryResult = LoadRegistry(step, out PackageRegistry[] registry);
            if (!registryResult.IsDone)
                return registryResult;

            StepResult targetResult = FindTarget(step, registry, ref version, out PackageRegistry target);
            if (!targetResult.IsDone)
                return targetResult;

            if (!target.PackageDictionary.TryGetValue(CreatorKitSetupWindow.sdk_environments_package_name, out PackageDefinition sdk))
            {
                // Entries before 2026.6 ship the authoring package under its earlier ids, which
                // this step does not install: the window still can.
                return Fail(step, SetupCli.ExitBadArguments,
                    $"registry entry '{version}' has no {CreatorKitSetupWindow.sdk_environments_package_name}: " +
                    For("the command line installs 2026.6 and later. Use the window for an earlier version.",
                        "the setup installs 2026.6 and later. Choose a later version."));
            }

            string wanted = $"{sdk.Url}#{sdk.Version}";
            string inManifest = CreatorKitSetupWindow.ReadManifestDependency(CreatorKitSetupWindow.sdk_environments_package_name);

            if (inManifest == wanted)
            {
                if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
                {
                    return Fail(step, SetupCli.ExitSdkMissing, $"{wanted} is in Packages/manifest.json but did not resolve. " +
                                                              "The package manager errors are earlier in the log.");
                }

                RecordVersion(PrepareConfiguration(registry, version), target);
                return Unchanged(step, $"Virtuademy-SDK-Environments {version} is installed.");
            }

            // What the Connection page installed to get the SDK's connection support first is the setup's own doing, not another version.
            IReadOnlyCollection<string> prepared = SetupUserSettings.PreparedPackages;
            List<string> ours = RegisteredPackagesOfOurs(registry).Where(name => !prepared.Contains(name)).ToList();
            if (inManifest != null || ours.Count > 0)
            {
                string found = inManifest != null
                    ? $"Packages/manifest.json asks for {inManifest}, while registry entry '{version}' is {wanted}"
                    : $"this project already has Virtuademy packages: {string.Join(", ", ours)}";
                return Fail(step, SetupCli.ExitOtherVersionInstalled, For(
                    $"{found}. Moving a project to another version is an update, which the " +
                    "sdk step does not do. Use the update step (-vdSetupSteps update -vdSetupVersion <version>) or " +
                    "\"Update packages to selected version\" in Virtuademy > Advanced > All setup checks: both also remove what " +
                    "the new version drops.",
                    $"{found}. Moving a project to another version is an update, which also removes what the new " +
                    "version drops: use Change version on the project status page."));
            }

            PackageManagerConfiguration config = PrepareConfiguration(registry, version);
            RecordVersion(config, target);
            CreatorKitSetupWindow.AddPackageWithDependencies(config, sdk);

            return Relaunch(step, $"Virtuademy-SDK-Environments {version} and its dependencies written to " +
                                  For("Packages/manifest.json. Launch again to resolve them.", "Packages/manifest.json. Unity resolves them next."));
        }

        /// <summary>The folder of the project's C# scripts, which the interpreter runs (HotUpdateSetupper's hot-update folder).</summary>
        internal const string environment_scripts_folder = "Assets/VirtuademyEnvironmentScripts";

        /// <summary>Where to update the Visual Scripting library by hand: the menu moved under Advanced in 2026.7.</summary>
        internal const string UpdateLibraryByHand =
            "Update the library by hand: Virtuademy > Advanced > Update Visual Scripting library (Virtuademy > Update VS Library before 2026.7).";

        /// <summary>Whether HybridCLR is in Packages/manifest.json: the interpreter for C# scripts is part of the project.</summary>
        internal static bool InterpreterInManifest()
            => CreatorKitSetupWindow.ReadManifestDependency(CreatorKitSetupWindow.hybridclr_package_name) != null;

        /// <summary>How many C# scripts the project has in <see cref="environment_scripts_folder"/>: the ones that stop running without the interpreter.</summary>
        internal static int CountEnvironmentScripts()
        {
            string folder = Path.Combine(ProjectRoot, environment_scripts_folder);
            return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Length : 0;
        }

        /// <summary>
        /// Takes the interpreter out of the project, for a setup with Visual Scripting only: HybridCLR leaves Packages/manifest.json, and with
        /// it the SDK's interpreter assembly, which compiles only while HybridCLR is there (HYBRIDCLR_INSTALLED). The C# scripts in
        /// <see cref="environment_scripts_folder"/> stay, and no longer run in Virtuademy; so do HybridCLR's files outside Assets
        /// (HybridCLRData/, ProjectSettings/HybridCLRSettings.asset), which nothing reads without it.
        /// </summary>
        internal static StepResult RemoveInterpreter()
        {
            const string step = "interpreter";

            if (InterpreterInManifest())
            {
                CreatorKitSetupWindow.UninstallPackages(new() { CreatorKitSetupWindow.hybridclr_package_name });
                ClearInterpreterAttempts();
                return Relaunch(step, "HybridCLR removed from Packages/manifest.json. " +
                                      For("Launch again to resolve the packages.", "Unity resolves the packages next."));
            }

            if (IsRegistered(CreatorKitSetupWindow.hybridclr_package_name))
            {
                return Fail(step, SetupCli.ExitInterpreterFailed, "HybridCLR is not in Packages/manifest.json but is still in the project " +
                                                                 "(an embedded package?). Remove it from the Package Manager window.");
            }

            return Unchanged(step, "The interpreter is not in the project.");
        }

        internal static StepResult InstallInterpreter()
        {
            const string step = "interpreter";

            if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
            {
                return Fail(step, SetupCli.ExitSdkMissing, "Virtuademy-SDK-Environments is not installed, and the interpreter " +
                                                          "setup ships with it. " + For("Run the sdk step first.", "The packages step has to finish first."));
            }

            // The HybridCLR to install is the one the project's recorded version declares.
            PackageManagerConfiguration config = FindConfiguration();
            string recorded = config != null ? config.CurrentInstallationVersion : null;
            if (string.IsNullOrEmpty(recorded))
            {
                return Fail(step, SetupCli.ExitSdkMissing, "the project records no Virtuademy version, so which HybridCLR to " +
                                                          "install is unknown. " + For("Run the sdk step, or open Virtuademy > Setup project once.", "Run the setup again."));
            }

            StepResult registryResult = LoadRegistry(step, out PackageRegistry[] registry);
            if (!registryResult.IsDone)
                return registryResult;

            PackageRegistry entry = registry.FirstOrDefault(candidate => candidate.ReflectisVersion == recorded);
            if (entry == null)
                return Fail(step, SetupCli.ExitRegistryUnavailable, $"the project records Virtuademy {recorded}, which the registry no longer lists.");

            if (!CreatorKitSetupWindow.TryGetDeclaredInterpreter(entry, out PackageDefinition declared))
                return new StepResult(SetupCli.ExitRegistryUnavailable, $"Virtuademy {recorded} declares a malformed interpreter in the registry.", false);

            if (declared == null)
            {
                return Fail(step, SetupCli.ExitInterpreterFailed,
                    $"Virtuademy {recorded} declares no interpreter in the registry, so no " +
                    "HybridCLR is known to work with it. Versions before 2026.6 have no interpreter; a later " +
                    "version without one is missing its \"interpreter\" entry in the registry.");
            }

            // Missing or another version: written as the window's InstallPackages writes it, and
            // resolved at the start of the next launch, so nothing has to survive a domain reload.
            string wanted = CreatorKitSetupWindow.InterpreterReference(declared);
            string inManifest = CreatorKitSetupWindow.ReadManifestDependency(CreatorKitSetupWindow.hybridclr_package_name);
            if (inManifest != wanted)
            {
                CreatorKitSetupWindow.InstallPackages(new() { declared });
                return Relaunch(step, inManifest == null
                    ? $"HybridCLR {declared.Version} written to Packages/manifest.json. " + For("Launch again to resolve and compile it.", "Unity resolves and compiles it next.")
                    : $"HybridCLR switched from {inManifest} to {wanted}. " + For("Launch again to resolve and compile it.", "Unity resolves and compiles it next."));
            }

            if (!IsRegistered(CreatorKitSetupWindow.hybridclr_package_name))
            {
                return Fail(step, SetupCli.ExitInterpreterFailed, $"{wanted} is in Packages/manifest.json but did not resolve. " +
                                                                 "The package manager errors are earlier in the log.");
            }

            if (CreatorKitSetupWindow.FindSetupperType() == null)
            {
                return Fail(step, SetupCli.ExitInterpreterFailed, "HybridCLR is installed but HotUpdateSetupper did not compile. " +
                                                                 "Look for compilation errors earlier in the log.");
            }

            if (CreatorKitSetupWindow.IsInterpreterReady(entry, out string issue))
            {
                ClearInterpreterAttempts();
                return Unchanged(step, "ready.");
            }

            int attempt = NextInterpreterAttempt();
            if (attempt > max_interpreter_attempts)
            {
                ClearInterpreterAttempts();
                return Fail(step, SetupCli.ExitInterpreterFailed, $"still not ready after {max_interpreter_attempts} configurations: {issue}");
            }

            // Synchronous. When this editor's IL2CPP has no interpreter yet, it clones hybridclr and
            // il2cpp_plus first, which takes a few minutes.
            Write(step, $"configuring, attempt {attempt} of {max_interpreter_attempts} ({issue})", LogType.Log);
            CreatorKitSetupWindow.InvokeSetupperViaReflection();

            if (CreatorKitSetupWindow.IsInterpreterReady(entry, out issue))
            {
                ClearInterpreterAttempts();
                return Applied(step, "configured.");
            }

            // Some of the setup only takes effect after a recompilation, which is why the window's
            // path retries on the domain reload. Here the next launch does.
            return Relaunch(step, $"continues in the next {For("launch", "round")} ({issue})");
        }

        internal static StepResult ConfigureAddressables()
        {
            const string step = "addressables";

            if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
            {
                return Fail(step, SetupCli.ExitSdkMissing, "Virtuademy-SDK-Environments is not installed, and the Addressables " +
                                                          "configuration ships with it. " + For("Run the sdk step first.", "The packages step has to finish first."));
            }

            Type configuration = FindAddressablesConfigurationType();
            if (configuration == null)
            {
                if (EditorUtility.scriptCompilationFailed)
                {
                    return Fail(step, SetupCli.ExitSdkMissing, "Virtuademy-SDK-Environments is installed but its editor assembly " +
                                                              "did not compile. Look for compilation errors earlier in the log.");
                }

                // A registry version older than the class: the default steps must keep installing it.
                return Unchanged(step, "this Virtuademy-SDK-Environments has no AddressablesConfiguration " +
                                       "(it predates it), so there is nothing to apply. Use its Addressables management window.", warning: true);
            }

            try
            {
                if (ReadAddressablesConfigured(configuration) == true)
                    return Unchanged(step, "already configured.");

                // The same reset the window's Fix Addressables configurations button does.
                Write(step, "applying the SDK's Addressables profile and resetting the Addressables " +
                            "groups to the single default group (every other group and every entry is removed).", LogType.Log);
                configuration.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);

                if (ReadAddressablesConfigured(configuration) != true)
                    return Fail(step, SetupCli.ExitAddressablesFailed, "the configuration was applied, but the checks still fail.");
            }
            catch (TargetInvocationException ex)
            {
                return Fail(step, SetupCli.ExitAddressablesFailed, (ex.InnerException ?? ex).ToString());
            }

            return Applied(step, "configured.");
        }

        /// <summary>
        /// Re-resolves the git packages whose tag or branch moved on the remote, which is what the
        /// window's <b>Re-resolve packages from git</b> does for every package, moved or not. It
        /// compares, per package, the commit <c>packages-lock.json</c> pins with what the manifest's
        /// ref points to now (<see cref="PackageLock.FindStale"/>), and unpins the stale ones only:
        /// the next launch resolves them again, which is why this step exits
        /// <see cref="SetupCli.ExitRelaunch"/>, and finds none stale. A remote that cannot be reached leaves
        /// the package "unknown": a warning, nothing unpinned, exit <see cref="SetupCli.ExitDone"/>.
        /// </summary>
        internal static StepResult ResolveMovedPackages()
        {
            const string step = "resolve";

            StepResult registryResult = LoadRegistry(step, out PackageRegistry[] registry);
            if (!registryResult.IsDone)
                return registryResult;

            PackageLock.StaleReport stale = PackageLock.FindStale(registry);
            if (!stale.Complete)
                Write(step, $"could not tell whether these moved on the remote (no network, no access, or the ref " +
                            $"is gone): {string.Join(", ", stale.Unknown)}.", LogType.Warning);

            if (stale.Stale.Count == 0)
            {
                return stale.Complete
                    ? Unchanged(step, stale.Compared == 0 ? "no pinned git package to compare." : $"{stale.Compared} pinned git package(s) compared, none moved.")
                    : new StepResult(SetupCli.ExitDone, "some remotes could not be asked.", false);
            }

            foreach (PackageLock.StalePackage package in stale.Stale)
                Write(step, $"{package.Name} is pinned to {ShortCommit(package.LockedCommit)}, " +
                            $"{package.Reference ?? "the default branch"} is at {ShortCommit(package.RemoteCommit)} on the remote.", LogType.Log);

            // All or nothing: unpinning only the packages whose remote answered would leave the project with
            // some packages at the new commit and the others at the old one, a combination nobody tested.
            if (!stale.Complete)
            {
                return Unchanged(step, $"nothing was unpinned, because {stale.Unknown.Count} package(s) could not be checked and a " +
                                       $"partial update would mix new and old packages. Still pinned to an older commit: " +
                                       $"{string.Join(", ", stale.Stale.Select(package => package.Name))}. Launch again when the remotes can be reached.", warning: true);
            }

            List<string> unpinned = PackageLock.Unpin(registry, stale.Stale.Select(package => package.Name));
            return Relaunch(step, $"unpinned {unpinned.Count} package(s) from packages-lock.json. Launch again to resolve " +
                                  "them; each is cloned again, so give it a moment.");
        }

        /// <summary>
        /// The window's <b>Update packages to selected version</b>, to the registry version given, for a project that
        /// has another one installed (what <see cref="InstallSdk"/> refuses with
        /// <see cref="SetupCli.ExitOtherVersionInstalled"/>). The semantics are the window's, through the same code
        /// (<see cref="CreatorKitSetupWindow.ApplyVersionUpdate"/>): what the new version adds is installed, what it
        /// moves is moved, <b>what it does not list is removed</b>, and HybridCLR follows the registry. The manifest and
        /// <c>SetupConfiguration.asset</c> are written and the step returns <see cref="SetupCli.ExitRelaunch"/>; the
        /// next call resolves the new packages, finds the project at the target and returns
        /// <see cref="SetupCli.ExitDone"/>.
        /// <para>
        /// A version step may have an update routine in the SDK, a menu item under
        /// <c>Virtuademy/Update routines</c> (for example "v2026.5 -> v2026.6"). They are in the new
        /// SDK, so with <paramref name="trackOrigin"/> (the command line) the launch that finds the project at the
        /// target lists the ones the move crosses and logs that they must run next; <see cref="SetupSteps"/> does not
        /// run them there. The engine passes false: it keeps where the run started itself and runs the routines in its
        /// <c>update-routine</c> step.
        /// </para>
        /// </summary>
        internal static StepResult UpdateVersion(string version, bool trackOrigin = true)
        {
            const string step = "update";

            StepResult registryResult = LoadRegistry(step, out PackageRegistry[] registry);
            if (!registryResult.IsDone)
                return registryResult;

            StepResult targetResult = FindTarget(step, registry, ref version, out PackageRegistry target);
            if (!targetResult.IsDone)
                return targetResult;

            // What the window shows as the installed version. Without one the project was never set
            // up by this installer, and there is nothing to move from: that is the sdk step.
            PackageManagerConfiguration existing = FindConfiguration();
            string recorded = existing != null ? existing.CurrentInstallationVersion : null;
            if (string.IsNullOrEmpty(recorded))
            {
                return Fail(step, SetupCli.ExitSdkMissing, "the project records no Virtuademy version, so there is nothing to update from. " +
                                                          For("Run the sdk step to install " + version + ".", "Run the setup to install " + version + "."));
            }

            if (recorded == version)
            {
                string origin = trackOrigin ? ReadUpdateOrigin() : null;
                if (origin != null)
                {
                    if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
                    {
                        return Fail(step, SetupCli.ExitSdkMissing,
                            $"the packages of Virtuademy {version} are in Packages/manifest.json but " +
                            "Virtuademy-SDK-Environments did not resolve. The package manager errors are earlier in the log.");
                    }

                    LogUpdateRoutines(origin, version);
                    ClearUpdateOrigin();
                }

                return Unchanged(step, $"the project is at Virtuademy {version}.");
            }

            // The installed list the way the window has it when it opens: the displayed version is
            // the installed one while it is listed, so the packages it adds get that entry's urls.
            PackageManagerConfiguration config = CreatorKitSetupWindow.LoadOrCreateConfiguration();
            config.AllVersionsPackageRegistry = registry;
            config.DisplayedReflectisVersion = registry.Any(entry => entry.ReflectisVersion == recorded) ? recorded : version;
            CreatorKitSetupWindow.DropUnmanagedInstalledPackages(config);
            CreatorKitSetupWindow.AddResolvedInstalledPackages(config, UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Where(package => package.isDirectDependency));

            config.DisplayedReflectisVersion = version;
            bool interpreterMoved = CreatorKitSetupWindow.ApplyVersionUpdate(config, out List<string> removedPackages);

            if (removedPackages.Count > 0)
                Write(step, $"removed, not part of Virtuademy {version}:\n  " + string.Join("\n  ", removedPackages), LogType.Log);
            if (interpreterMoved)
                Write(step, "HybridCLR moved with the version; the interpreter step configures it after the resolve.", LogType.Log);
            if (config.ResolveBreakingChangesAutomatically)
                Write(step, "\"Resolve breaking changes automatically\" is on in the project, but " + For("the command line", "the setup") + " " +
                            "does not download the old conversion script (the window does, for 2025.3 -> 2025.4 only).", LogType.Warning);

            RecordVersion(config, target);
            if (trackOrigin)
                WriteUpdateOrigin(recorded);

            return Relaunch(step, $"Virtuademy {recorded} -> {version} written to Packages/manifest.json. " + For("Launch again to resolve the packages.", "Unity resolves the packages next."));
        }

        /// <summary>
        /// The registry entry for <paramref name="version"/>, which is respelled as the registry spells
        /// it from here on (the window matches versions exactly), and the check that this editor is
        /// the one the entry requires. Stricter than the window, which only shows the mismatch in
        /// red: a batch run has nobody to see it, and installing anyway produces a project that does
        /// not compile.
        /// </summary>
        internal static StepResult FindTarget(string step, PackageRegistry[] registry, ref string version, out PackageRegistry target)
        {
            string asked = version;
            target = registry.FirstOrDefault(entry => string.Equals(entry.ReflectisVersion, asked, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                string listed = string.Join(", ", registry.Select(entry => entry.Prerelease ? entry.ReflectisVersion + " (prerelease)" : entry.ReflectisVersion));
                return Fail(step, SetupCli.ExitBadArguments, $"version '{version}' is not in the registry. Listed: {listed}.");
            }

            version = target.ReflectisVersion;

            if (CreatorKitSetupWindow.UnityVersion != target.RequiredUnityVersion)
            {
                return Fail(step, SetupCli.ExitUnityVersionMismatch,
                    $"Virtuademy {version} requires Unity {target.RequiredUnityVersion}; this editor is {CreatorKitSetupWindow.UnityVersion}.");
            }

            return default;
        }

        /// <summary>
        /// The SDK's <b>Virtuademy ▸ Advanced ▸ Update Visual Scripting library</b> menu item (<b>Virtuademy ▸ Update VS Library</b>
        /// before 2026.7): <c>VisualScriptingSetupEditor.Setup()</c>,
        /// by reflection like <see cref="ConfigureAddressables"/>. It marks Visual Scripting as used,
        /// resets Visual Scripting's type options to its defaults, adds every type that the project's
        /// <c>VisualScriptingCustomTypeCollector</c> assets list (and their namespaces to the assembly
        /// options), saves the Visual Scripting settings and rebuilds the units. Without it the
        /// SDK's nodes are not in the node library of a project set up from the command line.
        /// <para>
        /// Its reset would drop the types a creator added to the node library by hand, so they are read first and put back
        /// after it (a second unit rebuild, only when there were such types): the library only ever grows.
        /// <see cref="ReadVisualScriptingLibraryCurrent"/> says whether the SDK's types are all there already, which the
        /// engine uses to leave the library alone. The SDK's method itself cannot tell: it does everything again, every time,
        /// ending with a unit rebuild. With <paramref name="onlyWhenMissing"/> (the command line's <c>vslibrary</c>) this method
        /// asks first, once Visual Scripting has started, and leaves a library that has every type alone; the engine's
        /// <c>vs-nodes</c> asks before it calls. An SDK without the method is skipped with a warning.
        /// </para>
        /// </summary>
        internal static StepResult UpdateVisualScriptingLibrary(bool onlyWhenMissing = false)
        {
            const string step = "vslibrary";

            if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
            {
                return Fail(step, SetupCli.ExitSdkMissing, "Virtuademy-SDK-Environments is not installed, and the Visual Scripting " +
                                                          "library setup ships with it. " + For("Run the sdk step first.", "The packages step has to finish first."));
            }

            Type setup = FindSdkType(visual_scripting_setup_type);
            MethodInfo method = setup?.GetMethod("Setup", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (method == null)
            {
                if (EditorUtility.scriptCompilationFailed && setup == null)
                {
                    return Fail(step, SetupCli.ExitSdkMissing, "Virtuademy-SDK-Environments is installed but its editor assembly " +
                                                              "did not compile. Look for compilation errors earlier in the log.");
                }

                return Unchanged(step, "this Virtuademy-SDK-Environments has no VisualScriptingSetupEditor.Setup " +
                                       "(it predates it), so there is nothing to apply.", warning: true);
            }

            try
            {
                StartVisualScripting();
            }
            catch (TargetInvocationException ex)
            {
                return Fail(step, SetupCli.ExitVisualScriptingFailed, (ex.InnerException ?? ex).ToString());
            }

            // The command line's vslibrary: a library that has the SDK's types already is left alone, as the engine's vs-nodes leaves it.
            if (onlyWhenMissing && ReadVisualScriptingLibraryCurrent() == true)
                return Unchanged(step, "the Visual Scripting library already has the SDK's types.");

            // Refused rather than run blind: the reset below would lose what the person added, and nothing could bring it back.
            if (!TryReadVisualScriptingOptions(out _, out IList typeOptions, out _))
            {
                return Fail(step, SetupCli.ExitVisualScriptingFailed, "Visual Scripting's type options could not be read, so the types added by hand " +
                                                                    "cannot be kept. " + UpdateLibraryByHand);
            }

            List<Type> before = typeOptions.Cast<Type>().ToList();

            Write(step, "updating the Visual Scripting library (VisualScriptingSetupEditor.Setup).", LogType.Log);
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int kept;
            // The library is about to change: what was read of it no longer holds.
            visualScriptingLibraryCurrent = null;
            try
            {
                method.Invoke(null, null);
                kept = RestoreVisualScriptingTypes(before);
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or NullReferenceException)
            {
                return Fail(step, SetupCli.ExitVisualScriptingFailed, (ex.InnerException ?? ex).ToString());
            }

            return Applied(step, $"updated in {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s." +
                                 (kept > 0 ? $" Kept {kept} type(s) that were added by hand." : string.Empty));
        }

        #region Visual Scripting library

        private const string bolt_core_type = "Unity.VisualScripting.BoltCore";
        private const string codebase_type = "Unity.VisualScripting.Codebase";
        private const string unit_base_type = "Unity.VisualScripting.UnitBase";
        private const string custom_type_collector_type = "Virtuademy.SDK.Environments.VisualScripting.Editor.VisualScriptingCustomTypeCollector";
        private const string vs_usage_utility_type = "Unity.VisualScripting.VSUsageUtility";

        private static Type FindAnyType(string fullName)
            => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(type => type != null);

        /// <summary>
        /// Visual Scripting starts its plugins, and loads its configuration, only in a project it counts as using it (one with
        /// its generated folder), so a new project has nothing to read yet. Marks it used, as opening a graph does and as the
        /// SDK's library setup does first.
        /// </summary>
        private static void StartVisualScripting()
        {
            PropertyInfo used = FindAnyType(vs_usage_utility_type)?.GetProperty("isVisualScriptingUsed", BindingFlags.Public | BindingFlags.Static);
            if (used != null && used.GetValue(null) is false)
                used.SetValue(null, true);
        }

        /// <summary>
        /// Visual Scripting's own lists of the types and assemblies its node library offers (<c>BoltCore.Configuration</c>),
        /// by reflection: this package does not reference Visual Scripting. False before Visual Scripting has started.
        /// </summary>
        private static bool TryReadVisualScriptingOptions(out object configuration, out IList typeOptions, out IList assemblyOptions)
        {
            typeOptions = null;
            assemblyOptions = null;
            configuration = FindAnyType(bolt_core_type)?.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (configuration == null)
                return false;

            Type configurationType = configuration.GetType();
            typeOptions = configurationType.GetProperty("typeOptions")?.GetValue(configuration) as IList;
            assemblyOptions = configurationType.GetProperty("assemblyOptions")?.GetValue(configuration) as IList;
            return typeOptions != null && assemblyOptions != null;
        }

        private const string text_asset_extensions_type = "SPACS.Utilities.TextAssetExtensions";

        // Every type of the domain by full name, the first an assembly gives, as SPACS-Utility's GetTypeFromString finds it. Built once
        // per domain: the collector's GetCustomTypes scans every assembly once per type name, which took about 4 s in an environment
        // project, and the Summary and the Choices pages plan every few seconds.
        private static Dictionary<string, Type> typesByFullName;

        // ReadVisualScriptingLibraryCurrent's answer in this domain, once it could tell. The library changes through its own setup,
        // which clears it, or through a package change, which reloads the domain.
        private static bool? visualScriptingLibraryCurrent;

        private static Type ResolveTypeName(string fullName)
        {
            if (typesByFullName == null)
            {
                typesByFullName = new Dictionary<string, Type>();
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(type => type != null).ToArray();
                    }

                    foreach (Type type in types)
                    {
                        if (type.FullName != null && !typesByFullName.ContainsKey(type.FullName))
                            typesByFullName.Add(type.FullName, type);
                    }
                }
            }

            return !string.IsNullOrEmpty(fullName) && typesByFullName.TryGetValue(fullName, out Type found) ? found : null;
        }

        /// <summary>
        /// The types the SDK's <c>VisualScriptingCustomTypeCollector</c> assets list, which is what its library setup adds: the names the
        /// collector reads (its source files, through SPACS-Utility's <c>GetTextTypes</c>, and its plain names), resolved in one pass over
        /// the domain's types. A collector without those members is asked for its types, the slow way.
        /// </summary>
        private static List<Type> ReadSdkVisualScriptingTypes()
        {
            List<Type> types = new();
            Type collector = FindSdkType(custom_type_collector_type);
            if (collector == null)
                return types;

            PropertyInfo texts = collector.GetProperty("CustomTypeTexts", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo names = collector.GetProperty("CustomTypeNames", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo getTextTypes = FindAnyType(text_asset_extensions_type)?.GetMethod("GetTextTypes", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(TextAsset) }, null);
            MethodInfo getTypes = collector.GetMethod("GetCustomTypes", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            bool byName = texts != null && names != null && getTextTypes != null;
            if (!byName && getTypes == null)
                return types;

            foreach (string guid in AssetDatabase.FindAssets("t:" + collector.Name))
            {
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid), collector);
                if (asset == null)
                    continue;

                if (!byName)
                {
                    if (getTypes.Invoke(asset, null) is IEnumerable<Type> found)
                        types.AddRange(found.Where(type => type != null));
                    continue;
                }

                List<string> typeNames = new();
                foreach (TextAsset text in texts.GetValue(asset) as TextAsset[] ?? Array.Empty<TextAsset>())
                {
                    if (text != null && getTextTypes.Invoke(null, new object[] { text }) is IEnumerable<string> declared)
                        typeNames.AddRange(declared);
                }

                typeNames.AddRange(names.GetValue(asset) as string[] ?? Array.Empty<string>());
                types.AddRange(typeNames.Select(ResolveTypeName).Where(type => type != null));
            }

            return types;
        }

        /// <summary>
        /// Whether Visual Scripting's node library already has every type the SDK adds to it, and their namespaces among its assemblies:
        /// then its library setup has nothing to add. Null when that cannot be told (the SDK or Visual Scripting is not there, or the
        /// SDK lists no type). Reads only, and once per domain once it could tell (<see cref="visualScriptingLibraryCurrent"/>).
        /// </summary>
        internal static bool? ReadVisualScriptingLibraryCurrent()
        {
            if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
                return null;

            if (visualScriptingLibraryCurrent != null)
                return visualScriptingLibraryCurrent;

            try
            {
                if (!TryReadVisualScriptingOptions(out _, out IList typeOptions, out IList assemblyOptions))
                    return null;

                List<Type> wanted = ReadSdkVisualScriptingTypes();
                if (wanted.Count == 0)
                    return null;

                HashSet<Type> have = new(typeOptions.Cast<Type>());
                HashSet<string> assemblies = new(assemblyOptions.Cast<object>().Select(option => (string)option.GetType().GetField("name")?.GetValue(option)));

                visualScriptingLibraryCurrent = wanted.All(type => have.Contains(type) && (type.Namespace == null || assemblies.Contains(type.Namespace)));
                return visualScriptingLibraryCurrent;
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or NullReferenceException)
            {
                return null;
            }
        }

        /// <summary>
        /// Puts back the types of <paramref name="before"/> that the library setup's reset dropped, saves, and rebuilds the units as
        /// the setup does. Returns how many it put back; nothing is saved or rebuilt again when there were none.
        /// </summary>
        private static int RestoreVisualScriptingTypes(List<Type> before)
        {
            if (!TryReadVisualScriptingOptions(out object configuration, out IList typeOptions, out _))
                throw new InvalidOperationException("Visual Scripting's type options could not be read after the update, so the types added by hand were not checked.");

            List<Type> dropped = before.Where(type => !typeOptions.Contains(type)).ToList();
            if (dropped.Count == 0)
                return 0;

            foreach (Type type in dropped)
                typeOptions.Add(type);

            Type configurationType = configuration.GetType();
            object metadata = configurationType.GetMethod("GetMetadata", new[] { typeof(string) })?.Invoke(configuration, new object[] { "typeOptions" });
            metadata?.GetType().GetMethod("Save", Type.EmptyTypes)?.Invoke(metadata, null);
            configurationType.GetMethod("SaveProjectSettingsAsset", new[] { typeof(bool) })?.Invoke(configuration, new object[] { true });
            FindAnyType(codebase_type)?.GetMethod("UpdateSettings", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
            FindAnyType(unit_base_type)?.GetMethod("Rebuild", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
            return dropped.Count;
        }

        #endregion

        #endregion

        #region TextMesh Pro essentials

        private const string tmp_importer_type = "TMPro.TMP_PackageResourceImporter";
        private const string tmp_settings_type = "TMP_Settings";

        /// <summary>
        /// Whether the TextMesh Pro importer is in the project (<c>com.unity.ugui</c>) <b>and</b> its Essential Resources
        /// are not: the SDK's fonts use a TextMesh Pro shader that is one of them, and without it text renders magenta.
        /// The same test the importer's own window uses for its button: the settings asset it creates.
        /// </summary>
        internal static bool TmpEssentialsMissing()
            => FindTmpImporter() != null && AssetDatabase.FindAssets("t:" + tmp_settings_type).Length == 0;

        internal static bool IsTmpPresent() => FindTmpImporter() != null;

        private static MethodInfo FindTmpImporter()
            => AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(tmp_importer_type, false))
                .FirstOrDefault(type => type != null)
                ?.GetMethod("ImportResources", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool), typeof(bool), typeof(bool) }, null);

        /// <summary>
        /// Imports TextMesh Pro's Essential Resources without a dialog (<c>TMP_PackageResourceImporter.ImportResources(true,
        /// false, false)</c>, the call behind <b>Window ▸ TextMeshPro ▸ Import TMP Essential Resources</b>), by reflection:
        /// this package does not reference the TextMesh Pro assembly. The import finishes asynchronously, so the caller
        /// waits for <see cref="TmpEssentialsMissing"/> to turn false.
        /// </summary>
        internal static StepResult ImportTmpEssentials()
        {
            const string step = "tmp";

            MethodInfo import = FindTmpImporter();
            if (import == null)
                return Unchanged(step, "TextMesh Pro is not in the project (com.unity.ugui), so there is nothing to import.");

            if (!TmpEssentialsMissing())
                return Unchanged(step, "the TextMesh Pro Essential Resources are already in the project.");

            try
            {
                import.Invoke(null, new object[] { true, false, false });
            }
            catch (TargetInvocationException ex)
            {
                return Fail(step, SetupCli.ExitTmpEssentialsFailed, "the import failed: " + (ex.InnerException ?? ex).Message);
            }

            return Applied(step, "TextMesh Pro Essential Resources are being imported.");
        }

        #endregion

        #region Connections

        /// <summary>
        /// Reads, and changes nothing, how many connections this computer holds and whether one of them runs
        /// <paramref name="platformVersion"/>, from the SDK's status file in <c>%USERPROFILE%\.virtuademy\sdk</c>: it names
        /// tenant, app, platform version and login state of each connection, and holds no token or secret. It never
        /// throws: an unreadable file is reported in <see cref="ConnectionSummary.Error"/>.
        /// </summary>
        internal static ConnectionSummary ReadConnections(string platformVersion)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".virtuademy", "sdk", connections_status_file);
            if (!File.Exists(path))
                return new ConnectionSummary(0, false, null);

            try
            {
                JArray connections = JObject.Parse(File.ReadAllText(path))["connections"] as JArray;
                if (connections == null)
                    return new ConnectionSummary(0, false, null);

                string wanted = NormalizeVersion(platformVersion);
                bool runs = wanted.Length > 0 && connections.Any(connection => NormalizeVersion((string)connection["platformVersion"]) == wanted);
                return new ConnectionSummary(connections.Count, runs, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new ConnectionSummary(0, false, ex.Message);
            }
        }

        private static string NormalizeVersion(string version)
            => (version ?? string.Empty).Trim().TrimStart('v', 'V').ToLowerInvariant();

        #endregion

        #region Helpers

        internal static string ShortCommit(string commit) => commit.Length > 8 ? commit[..8] : commit;

        /// <summary>A type of the SDK's editor assembly, which only exists once the package has
        /// resolved and compiled, so it can only be reached by reflection. Null before that.</summary>
        internal static Type FindSdkType(string fullName)
            => AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name == sdk_editor_assembly)
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);

        /// <summary>
        /// The SDK's <c>AddressablesConfiguration</c>, which only exists once the package has
        /// resolved and compiled, so it can only be reached by reflection. Null before that.
        /// </summary>
        internal static Type FindAddressablesConfigurationType() => FindSdkType(addressables_configuration_type);

        // Per machine, like the interpreter attempts: Library/ is never committed.
        private static string UpdateOriginPath => Path.Combine(ProjectRoot, update_origin_file);

        private static string ReadUpdateOrigin()
            => File.Exists(UpdateOriginPath) ? File.ReadAllText(UpdateOriginPath).Trim() : null;

        private static void WriteUpdateOrigin(string version) => File.WriteAllText(UpdateOriginPath, version);

        private static void ClearUpdateOrigin()
        {
            if (File.Exists(UpdateOriginPath))
                File.Delete(UpdateOriginPath);
        }

        /// <summary>
        /// The first two numbers of a version as a series: "2026.5.1" and "v2026.5" are both
        /// (2026, 5). False for anything else, such as the pre-release "develop".
        /// </summary>
        internal static bool TryParseSeries(string text, out (int year, int minor) series)
        {
            Match match = Regex.Match(text ?? string.Empty, @"v?(\d+)\.(\d+)");
            series = match.Success ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : default;
            return match.Success;
        }

        /// <summary>The menu names of the SDK's update routines, without <see cref="update_routines_menu"/>:
        /// for example "v2026.5 -> v2026.6". Empty when the SDK has none (or is not installed).</summary>
        internal static List<string> ListUpdateRoutines()
        {
            List<string> routines = new();
            foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<MenuItem>())
            {
                foreach (MenuItem item in method.GetCustomAttributes<MenuItem>())
                {
                    if (item.menuItem.StartsWith(update_routines_menu, StringComparison.Ordinal))
                        routines.Add(item.menuItem.Substring(update_routines_menu.Length));
                }
            }

            return routines;
        }

        /// <summary>How a move from one version to another relates to the update routines.</summary>
        internal enum RoutineScope
        {
            /// <summary>One of the two is not a numbered release, so no step is matched.</summary>
            NotASeries,
            /// <summary>The target is not a later series: update routines only run forward.</summary>
            NotForward,
            /// <summary>A move to a later series: the routines in between apply.</summary>
            Forward,
        }

        /// <summary>
        /// Whether a move from <paramref name="from"/> to <paramref name="to"/> crosses a version step, and which of
        /// <paramref name="routines"/> (menu names, "vA -> vB") lie between the two, newest first. With
        /// <paramref name="prereleaseIsLatest"/> a target that is not a numbered release (the development version) counts as
        /// later than every release, which is what it is; the command line leaves it false and points at the menu instead.
        /// </summary>
        internal static RoutineScope MatchRoutines(string from, string to, IEnumerable<string> routines, bool prereleaseIsLatest, out List<string> crossed)
        {
            crossed = new List<string>();

            if (!TryParseSeries(from, out var fromSeries))
                return RoutineScope.NotASeries;

            (int year, int minor) toSeries;
            if (!TryParseSeries(to, out toSeries))
            {
                if (!prereleaseIsLatest)
                    return RoutineScope.NotASeries;
                toSeries = (int.MaxValue, int.MaxValue);
            }

            if (toSeries.CompareTo(fromSeries) <= 0)
                return RoutineScope.NotForward;

            foreach (string name in routines.OrderByDescending(name => name, StringComparer.Ordinal))
            {
                string[] sides = name.Split(new[] { "->" }, StringSplitOptions.None);
                if (sides.Length == 2 && TryParseSeries(sides[0], out var routineFrom) && TryParseSeries(sides[1], out var routineTo)
                    && routineFrom.CompareTo(fromSeries) >= 0 && routineTo.CompareTo(toSeries) <= 0 && routineFrom.CompareTo(routineTo) < 0)
                    crossed.Add(name);
            }

            return RoutineScope.Forward;
        }

        /// <summary>
        /// Logs the SDK's update routines that the move from <paramref name="from"/> to
        /// <paramref name="to"/> crosses, each with its menu name: the menu items of the new SDK
        /// under <c>Virtuademy/Update routines</c>, named "vA -> vB", whose step lies between the two
        /// versions. They change the project's assets, so the log says they must run next, and that
        /// the command line does not run them. A pre-release or a downgrade has no numbered step to match,
        /// so the log points at the menu instead.
        /// </summary>
        private static void LogUpdateRoutines(string from, string to)
        {
            const string step = "update";
            List<string> routines = ListUpdateRoutines();

            RoutineScope scope = MatchRoutines(from, to, routines, false, out List<string> crossed);
            if (scope == RoutineScope.NotASeries)
            {
                Write(step, $"{from} -> {to} is not a step between released versions, so no update routine is matched. " +
                            "Look under Virtuademy > Update routines" +
                            (routines.Count > 0 ? ", which has: " + string.Join(", ", routines) : ", which this SDK does not have") + ".", LogType.Log);
                return;
            }

            if (scope == RoutineScope.NotForward)
            {
                Write(step, $"{from} -> {to} does not go to a later series, and update routines only run forward.", LogType.Log);
                return;
            }

            if (crossed.Count == 0)
            {
                Write(step, $"this SDK has no update routine for {from} -> {to} (Virtuademy > Update routines).", LogType.Log);
                return;
            }

            // Newest first: that is the order to run them when a project needs several (2026.6 before 2026.5, because
            // the 2026.5 routine saves graphs that only the 2026.6 one makes readable). The name is the routine's
            // target series, which is how UpdateRoutinesCli's -vdRoutine names it.
            foreach (string name in crossed)
            {
                string target = name.Split(new[] { "->" }, StringSplitOptions.None)[1];
                TryParseSeries(target, out var routineTo);
                Write(step, $"the update routine \"{name}\" must run next, from the menu Virtuademy > Update routines > {name}, " +
                            $"or from the command line: -executeMethod {update_routines_cli} -vdRoutine {routineTo.year}.{routineTo.minor} " +
                            "(launch it again while it exits 2). It changes the project's assets (back them up first); SetupCli does not run it.", LogType.Warning);
            }

            if (crossed.Count > 1)
                Write(step, "run the routines in the order listed, newest first (-vdRoutine 2026.6 before 2026.5): " +
                            "the 2026.5 routine refuses to start while the 2026.6 one still has graphs to migrate.", LogType.Warning);
        }

        /// <summary>
        /// Whether the project's Addressables are configured as the SDK expects; null when the SDK's
        /// <c>AddressablesConfiguration</c> is not there to ask. Does not change anything.
        /// </summary>
        internal static bool? ReadAddressablesConfigured(Type configuration = null)
        {
            configuration ??= FindAddressablesConfigurationType();
            if (configuration == null)
                return null;

            return (bool?)configuration.GetProperty("IsConfigured", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }

        /// <summary>The window's state when the project has one; never created here.</summary>
        internal static PackageManagerConfiguration FindConfiguration()
        {
            string configGuid = AssetDatabase.FindAssets("t:" + nameof(PackageManagerConfiguration)).FirstOrDefault();
            return configGuid == null
                ? null
                : AssetDatabase.LoadAssetAtPath<PackageManagerConfiguration>(AssetDatabase.GUIDToAssetPath(configGuid));
        }

        /// <summary>
        /// The window's state, loaded or created only once the step is going to write it, so a
        /// refused run leaves no asset behind. The version is selected as the window's dropdown
        /// selects it: SelectedVersion, and with it the package list, follows it.
        /// </summary>
        private static PackageManagerConfiguration PrepareConfiguration(PackageRegistry[] registry, string version)
        {
            PackageManagerConfiguration config = CreatorKitSetupWindow.LoadOrCreateConfiguration();
            config.AllVersionsPackageRegistry = registry;
            config.DisplayedReflectisVersion = version;
            return config;
        }

        private static void RecordVersion(PackageManagerConfiguration config, PackageRegistry target)
        {
            config.CurrentInstallationVersion = target.ReflectisVersion;

            // The window lists a prerelease only with Show pre-releases on. Without it, the window
            // would open on a version its own list does not show.
            if (target.Prerelease)
                config.ShowPrereleases = true;

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssetIfDirty(config);
        }

        internal static bool IsRegistered(string packageName)
            => UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(package => package.name == packageName);

        // Managed packages only, as the window counts them: a package of ours that no registry entry
        // lists (one a creator added by hand) does not make the project "already installed".
        private static List<string> RegisteredPackagesOfOurs(PackageRegistry[] registry)
            => UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Select(package => package.name)
                .Where(name => CreatorKitSetupWindow.IsManagedPackage(name, registry) && !CreatorKitSetupWindow.packages_to_exclude.Contains(name))
                .ToList();

        /// <summary>
        /// The packages of ours the project has, other than the installer and what the Connection page installed to reach the
        /// connections (<see cref="SetupUserSettings.PreparedPackages"/>). A project that has some and not
        /// <c>virtuademy-sdk-environments</c> is from before 2026.6 (the packages had other ids), which the setup cannot move.
        /// Managed packages only (listed by a registry entry) once the registry was read; by the id's prefix before.
        /// </summary>
        internal static List<string> OtherPackagesOfOurs()
        {
            IReadOnlyCollection<string> prepared = SetupUserSettings.PreparedPackages;
            PackageRegistry[] registry = registryCache;

            return UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Select(package => package.name)
                .Where(name => !prepared.Contains(name)
                               && !CreatorKitSetupWindow.packages_to_exclude.Contains(name)
                               && (registry != null ? CreatorKitSetupWindow.IsManagedPackage(name, registry) : CreatorKitSetupWindow.IsOurPackage(name)))
                .ToList();
        }

        private static int NextInterpreterAttempt()
        {
            string path = Path.Combine(ProjectRoot, interpreter_attempts_file);
            int attempt = 1;
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int previous))
                attempt = previous + 1;

            File.WriteAllText(path, attempt.ToString());
            return attempt;
        }

        private static void ClearInterpreterAttempts()
        {
            string path = Path.Combine(ProjectRoot, interpreter_attempts_file);
            if (File.Exists(path))
                File.Delete(path);
        }

        #endregion
    }
}
