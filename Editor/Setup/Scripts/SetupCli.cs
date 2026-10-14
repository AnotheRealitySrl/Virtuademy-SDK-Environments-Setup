using Newtonsoft.Json;

using System;
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
    /// Runs what <b>Virtuademy ▸ Setup project</b> does, from the command line, for a machine with
    /// nobody at the window: a fresh clone, CI, the Env-Test harness. The steps, in the order they
    /// run:
    /// <list type="bullet">
    /// <item><c>configure</c>: the Configure button.</item>
    /// <item><c>sdk</c>: Install next to Virtuademy SDK Environments, at a given registry version.</item>
    /// <item><c>update</c>: "Update packages to selected version", to the registry version given.
    /// <b>Never a default step</b>, and not combinable with <c>sdk</c>.</item>
    /// <item><c>resolve</c>: re-resolves the git packages whose tag or branch moved on the remote
    /// (a smarter "Re-resolve packages from git"); a no-op when none moved.</item>
    /// <item><c>interpreter</c>: Install interpreter.</item>
    /// <item><c>addressables</c>: the Addressables management window's "Fix Addressables
    /// configurations" button.</item>
    /// <item><c>vslibrary</c>: the SDK's <b>Virtuademy ▸ Update VS Library</b> menu. <b>Opt-in</b>,
    /// not a default step.</item>
    /// <item><c>check</c>: changes nothing, reports.</item>
    /// </list>
    /// The default steps, without <c>-vdSetupSteps</c>, are <c>configure</c>, <c>sdk</c>,
    /// <c>resolve</c>, <c>interpreter</c> and <c>addressables</c>.
    /// <code>
    /// Unity -batchmode -nographics -projectPath &lt;project&gt; -logFile -
    ///       -executeMethod Virtuademy.SDK.Environments.Setup.Editor.SetupCli.Run
    ///       -vdSetupVersion develop
    ///       [-vdSetupSteps configure,sdk,update,resolve,interpreter,addressables,vslibrary | check]
    ///       [-vdSetupRegistry &lt;url or file&gt;]
    /// </code>
    /// Arguments are strict: any <c>-vd...</c> argument (with a <c>-</c>, <c>--</c> or <c>/</c> prefix, in
    /// any case) that is not exactly one of these three is refused with <see cref="ExitBadArguments"/>, as is
    /// an option given twice and a value that no option claims (<c>-vdSetupSteps sdk update</c>): a typo must
    /// never run the default steps nobody asked for. Unity's own arguments are not touched.
    /// <para>
    /// The <c>interpreter</c> step installs the HybridCLR that the project's recorded version
    /// declares in the registry (<see cref="PackageRegistry.Interpreter"/>), and switches to it when
    /// the manifest asks for another one: that is a repair, not a version update. With
    /// <c>-vdSetupRegistry</c> the registry is read from another URL or from a local file, to try a
    /// registry change before it is published.
    /// </para>
    /// <para>
    /// The <c>addressables</c> step calls <c>AddressablesConfiguration.Apply()</c> of
    /// Virtuademy-SDK-Environments, by reflection like the interpreter's setupper, since this
    /// package installs the SDK and cannot reference it; an SDK version that predates the class
    /// is skipped with a warning, so the default steps still install it. A project that is not
    /// configured gets the SDK's profile and its <b>Addressables groups reset to the single
    /// default group</b>: every other group and every entry are removed, as the window's button
    /// does. A configured project is left as it is. Without it, the first "Build &amp; Deploy"
    /// would build every platform for minutes and fail at the end, with the bundles in
    /// <c>Library</c> instead of <c>ServerData</c>.
    /// </para>
    /// <para>
    /// The <c>resolve</c> step compares, for every git package of ours in
    /// <c>Packages/packages-lock.json</c>, the commit it pins with what the manifest's ref (a tag,
    /// a branch, or none for the default branch) points to on the remote now, through
    /// <c>git ls-remote</c>; a tag is peeled, so an annotated one gives its commit, and a commit
    /// ref is up to date by definition. It unpins the ones that moved and exits
    /// <see cref="ExitRelaunch"/>: the next launch resolves them again and finds none moved. The
    /// installer itself is never touched. It is all or nothing: when the remote of any package
    /// could not be checked (it is "unknown"), <b>nothing is unpinned</b>, not even the packages
    /// known to have moved, so a timeout cannot leave the project with a mix of new and old
    /// packages. That is a warning and <see cref="ExitDone"/>: the network being down never fails
    /// the step. It is part of the default steps because it does nothing when nothing moved. The
    /// window's button, by contrast, unpins every one of them.
    /// </para>
    /// <para>
    /// The <c>update</c> step moves a project that has another registry version installed (what
    /// <c>sdk</c> refuses with <see cref="ExitOtherVersionInstalled"/>) to the version of
    /// <c>-vdSetupVersion</c>, with the window's own code: what the new version adds is installed,
    /// what it moves is moved, <b>what it does not list is removed</b> and HybridCLR follows the
    /// registry. It writes the manifest and <c>SetupConfiguration.asset</c> and exits
    /// <see cref="ExitRelaunch"/>; the next launch finds the project at the target. Unity must be
    /// the version the target requires (<see cref="ExitUnityVersionMismatch"/>). When the move
    /// crosses a version step that has an update routine in the new SDK (a menu item under
    /// <c>Virtuademy/Update routines</c>, such as "v2026.5 -> v2026.6"), that launch logs a warning
    /// with the routine's menu name and its command line (<c>UpdateRoutinesCli</c>, <c>-vdRoutine 2026.6</c>):
    /// it must run next, because SetupCli does not run it. Not a default step: it removes packages.
    /// </para>
    /// <para>
    /// The <c>vslibrary</c> step calls <c>VisualScriptingSetupEditor.Setup()</c> of
    /// Virtuademy-SDK-Environments, by reflection: it adds the SDK's Visual Scripting types to the
    /// node library and rebuilds the units. It is opt-in because it is slow (about 75 to 80 s on a
    /// scratch project, 2026-10-14) and cannot tell it is already done: it rewrites the unit
    /// options (a file of tens of MB) on every run. An SDK without the method is skipped with a
    /// warning.
    /// </para>
    /// <para>
    /// <b>One launch does one round, not the whole setup.</b> Every package change ends in a
    /// resolve and a domain reload, and a reload cuts short the method that is running. So a launch
    /// applies what it can, stops right after the first change that needs the editor to start
    /// again, and exits with <see cref="ExitRelaunch"/>; the caller launches again until the code
    /// is <see cref="ExitDone"/>. A new project takes three launches: Configure and the SDK
    /// packages, then HybridCLR, then the interpreter and the Addressables configuration (a
    /// fourth when the interpreter setup needs a recompilation first). An update takes two (the
    /// manifest, then the resolve and the routines list), and a moved tag one more per round of
    /// <c>resolve</c>. Every step is idempotent, so a project that is already set up costs one
    /// launch and is left as it is.
    /// </para>
    /// <para>
    /// <c>-vdSetupSteps check</c> changes nothing: it reports and exits with <see cref="ExitDone"/>
    /// when the project is set up, <see cref="ExitNotReady"/> otherwise. The Addressables
    /// configuration counts once the SDK is installed (<c>addressablesConfigured</c> in the report
    /// is null before that). So does a git package that moved on the remote
    /// (<c>stalePackages</c>: name, ref, locked commit, remote commit). A remote that cannot be
    /// asked is not a failure: <c>stalePackagesChecked</c> is then false and the check goes on.
    /// </para>
    /// <para>
    /// The method exits the editor itself, with one of the codes below, so <c>-quit</c> is not
    /// needed and does not change the outcome. Every launch ends with a <c>[SetupCli] REPORT</c>
    /// line: one JSON object with every check.
    /// </para>
    /// <para>
    /// The <c>sdk</c> step refuses a project that already has packages of another version
    /// (<see cref="ExitOtherVersionInstalled"/>): moving it is the <c>update</c> step.
    /// </para>
    /// </summary>
    public static class SetupCli
    {
        /// <summary>Every requested step is done.</summary>
        public const int ExitDone = 0;
        /// <summary>An unexpected exception: the log has the stack trace.</summary>
        public const int ExitUnexpected = 1;
        /// <summary>A step changed the project: launch again to continue.</summary>
        public const int ExitRelaunch = 2;
        /// <summary>The <c>check</c> step found something not set up.</summary>
        public const int ExitNotReady = 3;
        public const int ExitBadArguments = 10;
        public const int ExitGitMissing = 11;
        public const int ExitModulesMissing = 12;
        public const int ExitRegistryUnavailable = 13;
        public const int ExitUnityVersionMismatch = 14;
        public const int ExitOtherVersionInstalled = 15;
        public const int ExitSdkMissing = 16;
        public const int ExitInterpreterFailed = 17;
        public const int ExitProjectSettingsFailed = 18;
        public const int ExitAddressablesFailed = 19;
        /// <summary>The <c>vslibrary</c> step failed: <c>VisualScriptingSetupEditor.Setup()</c> threw.</summary>
        public const int ExitVisualScriptingFailed = 20;

        private const string log_prefix = "[SetupCli] ";

        // As many as HotUpdateSetupper allows itself when the window drives it across reloads.
        private const int max_interpreter_attempts = 3;

        // Counted across launches, so it cannot live in SessionState, which dies with the editor.
        // Library/ is per machine and never committed.
        private const string interpreter_attempts_file = "Library/VirtuademySetupCli.interpreter-attempts";

        // Where the "Virtuademy/Update routines/..." menu items say they are applied from, kept
        // across the launches of an update: the launch that moves the packages cannot list the
        // routines of the new SDK, which only exists after the resolve.
        private const string update_origin_file = "Library/VirtuademySetupCli.updated-from";

        // Where the SDK keeps its editor code. Named, not referenced: this package installs the
        // SDK, so it cannot compile against it.
        private const string sdk_editor_assembly = "Virtuademy.SDK.Environments.Editor";
        private const string addressables_configuration_type = sdk_editor_assembly + ".AddressablesConfiguration";
        private const string visual_scripting_setup_type = "Virtuademy.SDK.Environments.VisualScripting.Editor.VisualScriptingSetupEditor";
        private const string update_routines_menu = "Virtuademy/Update routines/";

        // The command line of the same routines, in the SDK's BreakingChangeSolvers: named, not referenced, like the menu.
        private const string update_routines_cli = "Virtuademy.BreakingChangeSolvers.UpdateRoutinesCli.Run";

        private enum Step { Configure, Sdk, Update, Resolve, Interpreter, Addressables, VisualScriptingLibrary, Check }

        private static readonly Dictionary<string, Step> step_names = new()
        {
            { "configure", Step.Configure },
            { "sdk", Step.Sdk },
            { "update", Step.Update },
            { "resolve", Step.Resolve },
            { "interpreter", Step.Interpreter },
            { "addressables", Step.Addressables },
            { "vslibrary", Step.VisualScriptingLibrary },
            { "check", Step.Check },
        };

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        // Where the registry is read from in this launch, and what was read: one download per
        // launch, shared by the steps and the report.
        private static string registrySource = CreatorKitSetupWindow.package_registry_path;
        private static PackageRegistry[] registryCache;

        /// <summary>The <c>-executeMethod</c> entry point. It does not return: it exits the editor.</summary>
        public static void Run()
        {
            int code;
            try
            {
                code = Execute(Environment.GetCommandLineArgs());
            }
            catch (Exception ex)
            {
                Debug.LogError(log_prefix + "Unexpected failure: " + ex);
                code = ExitUnexpected;
            }

            Debug.Log($"{log_prefix}EXIT {code} ({Describe(code)})");
            EditorApplication.Exit(code);
        }

        private static int Execute(string[] args)
        {
            if (!TryParseArguments(args, out HashSet<Step> steps, out string version, out string argumentError))
            {
                Debug.LogError(log_prefix + argumentError);
                return ExitBadArguments;
            }

            registrySource = GetArgument(args, "-vdSetupRegistry") ?? CreatorKitSetupWindow.package_registry_path;
            registryCache = null;
            if (registrySource != CreatorKitSetupWindow.package_registry_path)
                Debug.Log($"{log_prefix}Registry read from {registrySource} instead of the published one.");

            // What the window shows in red, and what no step can do without: git clones every
            // package and the interpreter, and interpreted scripts are compiled for all four
            // targets.
            if (!CreatorKitSetupWindow.TryGetGitVersion(out _))
            {
                Debug.LogError(log_prefix + "git cannot be run from this editor process. Install git, or launch " +
                               "the editor from a shell where git is on PATH.");
                return ExitGitMissing;
            }

            List<string> missingModules = CreatorKitSetupWindow.GetInstalledModules()
                .Where(module => !module.Value)
                .Select(module => module.Key)
                .ToList();
            if (missingModules.Count > 0)
            {
                Debug.LogError($"{log_prefix}Editor modules missing: {string.Join(", ", missingModules)}. Install them from Unity Hub.");
                return ExitModulesMissing;
            }

            if (steps.Contains(Step.Check))
            {
                return LogReport() ? ExitDone : ExitNotReady;
            }

            int result = ExitDone;
            if (steps.Contains(Step.Configure))
                result = Configure();
            if (result == ExitDone && steps.Contains(Step.Sdk))
                result = InstallSdk(version);
            if (result == ExitDone && steps.Contains(Step.Update))
                result = UpdateVersion(version);
            if (result == ExitDone && steps.Contains(Step.Resolve))
                result = ResolveMovedPackages();
            if (result == ExitDone && steps.Contains(Step.Interpreter))
                result = InstallInterpreter();
            if (result == ExitDone && steps.Contains(Step.Addressables))
                result = ConfigureAddressables();
            if (result == ExitDone && steps.Contains(Step.VisualScriptingLibrary))
                result = UpdateVisualScriptingLibrary();

            // Informational after the steps: a report that throws must not replace their result.
            try
            {
                LogReport();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(log_prefix + "The report could not be produced: " + ex.Message);
            }

            return result;
        }

        private static readonly string[] value_options = { "-vdSetupVersion", "-vdSetupSteps", "-vdSetupRegistry" };

        // Unity's own options that take a value: the token after one is Unity's, not a stray value of ours.
        private static readonly string[] unity_value_options = { "-projectPath", "-logFile", "-executeMethod", "-buildTarget", "-activeBuildProfile" };

        // What an option of ours looks like, in any prefix and case: anything matching it has to be one exactly.
        private static readonly Regex vd_like = new("^[-/]+vd", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Checks every <c>-vd</c> argument of the command line: one that is not exactly a known option
        /// (<c>--vdSetupSteps</c>, <c>-vdsetupsteps</c> are not <c>-vdSetupSteps</c>), one given twice, an option with
        /// no value, and a token that no option claims after an option's value are all refused. Unity's own
        /// arguments (and the value of the ones that take one) are left alone.
        /// </summary>
        private static bool TryCheckArguments(string[] args, out string error)
        {
            error = null;
            HashSet<string> seen = new();

            // Index 0 is the editor's own path.
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                if (unity_value_options.Contains(arg, StringComparer.OrdinalIgnoreCase))
                {
                    i++;
                    continue;
                }

                if (!vd_like.IsMatch(arg))
                    continue;

                if (!value_options.Contains(arg))
                {
                    string near = value_options.FirstOrDefault(o => string.Equals(o, "-" + arg.TrimStart('-', '/'), StringComparison.OrdinalIgnoreCase));
                    error = $"'{arg}' is not an option of this tool." +
                            (near != null ? $" Did you mean {near}? Options are exact, with a single leading dash." : $" Options: {string.Join(", ", value_options)}.");
                    return false;
                }

                if (!seen.Add(arg))
                {
                    error = $"{arg} is given twice.";
                    return false;
                }

                if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal) || vd_like.IsMatch(args[i + 1]))
                {
                    error = $"{arg} needs a value.";
                    return false;
                }

                i++;

                // The one place a stray value can hide: right behind an option's value. Whatever Unity's
                // options carry comes after an option of theirs, which starts with a dash.
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal) && !vd_like.IsMatch(args[i + 1]))
                {
                    error = $"'{args[i + 1]}' follows {arg} and its value and belongs to no option (a list takes commas, as in -vdSetupSteps sdk,resolve).";
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseArguments(string[] args, out HashSet<Step> steps, out string version, out string error)
        {
            steps = new HashSet<Step> { Step.Configure, Step.Sdk, Step.Resolve, Step.Interpreter, Step.Addressables };
            version = null;

            if (!TryCheckArguments(args, out error))
                return false;

            version = GetArgument(args, "-vdSetupVersion");

            string stepList = GetArgument(args, "-vdSetupSteps");
            if (stepList != null)
            {
                steps = new HashSet<Step>();
                foreach (string name in stepList.Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0))
                {
                    if (!step_names.TryGetValue(name, out Step step))
                    {
                        error = $"Unknown step '{name}' in -vdSetupSteps. Steps: {string.Join(", ", step_names.Keys)}.";
                        return false;
                    }
                    steps.Add(step);
                }

                if (steps.Count == 0)
                {
                    error = "-vdSetupSteps names no step.";
                    return false;
                }

                if (steps.Contains(Step.Check) && steps.Count > 1)
                {
                    error = "The check step changes nothing, so it cannot be combined with the others.";
                    return false;
                }
            }

            // Both are about the same manifest line, and the sdk step refuses a project that has
            // another version, which is the update's starting point.
            if (steps.Contains(Step.Sdk) && steps.Contains(Step.Update))
            {
                error = "The sdk step installs and the update step moves a project that has another version: they cannot be combined.";
                return false;
            }

            if ((steps.Contains(Step.Sdk) || steps.Contains(Step.Update)) && string.IsNullOrWhiteSpace(version))
            {
                error = "The sdk and update steps need the registry version, e.g. -vdSetupVersion develop.";
                return false;
            }

            return true;
        }

        private static string GetArgument(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                return null;

            return args[index + 1];
        }

        #region Steps

        private static int Configure()
        {
            RenderPipelineAsset asset = CreatorKitSetupWindow.LoadDefaultRenderPipelineAsset();
            if (asset == null)
            {
                Debug.LogError(log_prefix + "configure: the installer's render pipeline asset could not be loaded. " +
                               "The installer package looks incomplete.");
                return ExitProjectSettingsFailed;
            }

            if (ProjectSettingsConfigured(asset))
            {
                Debug.Log(log_prefix + "configure: the project settings are already configured.");
                return ExitDone;
            }

            CreatorKitSetupWindow.ApplyProjectSettings(asset);

            if (!ProjectSettingsConfigured(asset))
            {
                Debug.LogError(log_prefix + "configure: the settings were applied, but the checks still fail.");
                return ExitProjectSettingsFailed;
            }

            Debug.Log(log_prefix + "configure: URP, API compatibility .NET Framework and max texture size 1024 applied.");
            return ExitDone;
        }

        private static int InstallSdk(string version)
        {
            int registryResult = LoadRegistry("sdk", out PackageRegistry[] registry);
            if (registryResult != ExitDone)
                return registryResult;

            int targetResult = FindTarget("sdk", registry, ref version, out PackageRegistry target);
            if (targetResult != ExitDone)
                return targetResult;

            if (!target.PackageDictionary.TryGetValue(CreatorKitSetupWindow.sdk_environments_package_name, out PackageDefinition sdk))
            {
                // Entries before 2026.6 ship the authoring package under its earlier ids, which
                // this step does not install: the window still can.
                Debug.LogError($"{log_prefix}sdk: registry entry '{version}' has no {CreatorKitSetupWindow.sdk_environments_package_name}: " +
                               "the command line installs 2026.6 and later. Use the window for an earlier version.");
                return ExitBadArguments;
            }

            string wanted = $"{sdk.Url}#{sdk.Version}";
            string inManifest = CreatorKitSetupWindow.ReadManifestDependency(CreatorKitSetupWindow.sdk_environments_package_name);

            if (inManifest == wanted)
            {
                if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
                {
                    Debug.LogError($"{log_prefix}sdk: {wanted} is in Packages/manifest.json but did not resolve. " +
                                   "The package manager errors are earlier in the log.");
                    return ExitSdkMissing;
                }

                RecordVersion(PrepareConfiguration(registry, version), target);
                Debug.Log($"{log_prefix}sdk: Virtuademy-SDK-Environments {version} is installed.");
                return ExitDone;
            }

            List<string> ours = RegisteredPackagesOfOurs(registry);
            if (inManifest != null || ours.Count > 0)
            {
                string found = inManifest != null
                    ? $"Packages/manifest.json asks for {inManifest}, while registry entry '{version}' is {wanted}"
                    : $"this project already has Virtuademy packages: {string.Join(", ", ours)}";
                Debug.LogError($"{log_prefix}sdk: {found}. Moving a project to another version is an update, which the " +
                               "sdk step does not do. Use the update step (-vdSetupSteps update -vdSetupVersion <version>) or " +
                               "\"Update packages to selected version\" in Virtuademy > Setup project: both also remove what " +
                               "the new version drops.");
                return ExitOtherVersionInstalled;
            }

            PackageManagerConfiguration config = PrepareConfiguration(registry, version);
            RecordVersion(config, target);
            CreatorKitSetupWindow.AddPackageWithDependencies(config, sdk);

            Debug.Log($"{log_prefix}sdk: Virtuademy-SDK-Environments {version} and its dependencies written to " +
                      "Packages/manifest.json. Launch again to resolve them.");
            return ExitRelaunch;
        }

        private static int InstallInterpreter()
        {
            if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
            {
                Debug.LogError(log_prefix + "interpreter: Virtuademy-SDK-Environments is not installed, and the interpreter " +
                               "setup ships with it. Run the sdk step first.");
                return ExitSdkMissing;
            }

            // The HybridCLR to install is the one the project's recorded version declares.
            PackageManagerConfiguration config = FindConfiguration();
            string recorded = config != null ? config.CurrentInstallationVersion : null;
            if (string.IsNullOrEmpty(recorded))
            {
                Debug.LogError(log_prefix + "interpreter: the project records no Virtuademy version, so which HybridCLR to " +
                               "install is unknown. Run the sdk step, or open Virtuademy > Setup project once.");
                return ExitSdkMissing;
            }

            int registryResult = LoadRegistry("interpreter", out PackageRegistry[] registry);
            if (registryResult != ExitDone)
                return registryResult;

            PackageRegistry entry = registry.FirstOrDefault(candidate => candidate.ReflectisVersion == recorded);
            if (entry == null)
            {
                Debug.LogError($"{log_prefix}interpreter: the project records Virtuademy {recorded}, which the registry no longer lists.");
                return ExitRegistryUnavailable;
            }

            if (!CreatorKitSetupWindow.TryGetDeclaredInterpreter(entry, out PackageDefinition declared))
                return ExitRegistryUnavailable;

            if (declared == null)
            {
                Debug.LogError($"{log_prefix}interpreter: Virtuademy {recorded} declares no interpreter in the registry, so no " +
                               "HybridCLR is known to work with it. Versions before 2026.6 have no interpreter; a later " +
                               "version without one is missing its \"interpreter\" entry in the registry.");
                return ExitInterpreterFailed;
            }

            // Missing or another version: written as the window's InstallPackages writes it, and
            // resolved at the start of the next launch, so nothing has to survive a domain reload.
            string wanted = CreatorKitSetupWindow.InterpreterReference(declared);
            string inManifest = CreatorKitSetupWindow.ReadManifestDependency(CreatorKitSetupWindow.hybridclr_package_name);
            if (inManifest != wanted)
            {
                CreatorKitSetupWindow.InstallPackages(new() { declared });
                Debug.Log(inManifest == null
                    ? $"{log_prefix}interpreter: HybridCLR {declared.Version} written to Packages/manifest.json. Launch again to resolve and compile it."
                    : $"{log_prefix}interpreter: HybridCLR switched from {inManifest} to {wanted}. Launch again to resolve and compile it.");
                return ExitRelaunch;
            }

            if (!IsRegistered(CreatorKitSetupWindow.hybridclr_package_name))
            {
                Debug.LogError($"{log_prefix}interpreter: {wanted} is in Packages/manifest.json but did not resolve. " +
                               "The package manager errors are earlier in the log.");
                return ExitInterpreterFailed;
            }

            if (CreatorKitSetupWindow.FindSetupperType() == null)
            {
                Debug.LogError(log_prefix + "interpreter: HybridCLR is installed but HotUpdateSetupper did not compile. " +
                               "Look for compilation errors earlier in the log.");
                return ExitInterpreterFailed;
            }

            if (CreatorKitSetupWindow.IsInterpreterReady(entry, out string issue))
            {
                ClearInterpreterAttempts();
                Debug.Log(log_prefix + "interpreter: ready.");
                return ExitDone;
            }

            int attempt = NextInterpreterAttempt();
            if (attempt > max_interpreter_attempts)
            {
                ClearInterpreterAttempts();
                Debug.LogError($"{log_prefix}interpreter: still not ready after {max_interpreter_attempts} configurations: {issue}");
                return ExitInterpreterFailed;
            }

            // Synchronous. When this editor's IL2CPP has no interpreter yet, it clones hybridclr and
            // il2cpp_plus first, which takes a few minutes.
            Debug.Log($"{log_prefix}interpreter: configuring, attempt {attempt} of {max_interpreter_attempts} ({issue})");
            CreatorKitSetupWindow.InvokeSetupperViaReflection();

            if (CreatorKitSetupWindow.IsInterpreterReady(entry, out issue))
            {
                ClearInterpreterAttempts();
                Debug.Log(log_prefix + "interpreter: configured.");
                return ExitDone;
            }

            // Some of the setup only takes effect after a recompilation, which is why the window's
            // path retries on the domain reload. Here the next launch does.
            Debug.Log($"{log_prefix}interpreter: continues in the next launch ({issue})");
            return ExitRelaunch;
        }

        private static int ConfigureAddressables()
        {
            if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
            {
                Debug.LogError(log_prefix + "addressables: Virtuademy-SDK-Environments is not installed, and the Addressables " +
                               "configuration ships with it. Run the sdk step first.");
                return ExitSdkMissing;
            }

            Type configuration = FindAddressablesConfigurationType();
            if (configuration == null)
            {
                if (EditorUtility.scriptCompilationFailed)
                {
                    Debug.LogError(log_prefix + "addressables: Virtuademy-SDK-Environments is installed but its editor assembly " +
                                   "did not compile. Look for compilation errors earlier in the log.");
                    return ExitSdkMissing;
                }

                // A registry version older than the class: the default steps must keep installing it.
                Debug.LogWarning(log_prefix + "addressables: this Virtuademy-SDK-Environments has no AddressablesConfiguration " +
                                 "(it predates it), so there is nothing to apply. Use its Addressables management window.");
                return ExitDone;
            }

            try
            {
                if (ReadAddressablesConfigured(configuration) == true)
                {
                    Debug.Log(log_prefix + "addressables: already configured.");
                    return ExitDone;
                }

                // The same reset the window's Fix Addressables configurations button does.
                Debug.Log(log_prefix + "addressables: applying the SDK's Addressables profile and resetting the Addressables " +
                          "groups to the single default group (every other group and every entry is removed).");
                configuration.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);

                if (ReadAddressablesConfigured(configuration) != true)
                {
                    Debug.LogError(log_prefix + "addressables: the configuration was applied, but the checks still fail.");
                    return ExitAddressablesFailed;
                }
            }
            catch (TargetInvocationException ex)
            {
                Debug.LogError(log_prefix + "addressables: " + (ex.InnerException ?? ex));
                return ExitAddressablesFailed;
            }

            Debug.Log(log_prefix + "addressables: configured.");
            return ExitDone;
        }

        /// <summary>
        /// Re-resolves the git packages whose tag or branch moved on the remote, which is what the
        /// window's <b>Re-resolve packages from git</b> does for every package, moved or not. It
        /// compares, per package, the commit <c>packages-lock.json</c> pins with what the manifest's
        /// ref points to now (<see cref="PackageLock.FindStale"/>), and unpins the stale ones only:
        /// the next launch resolves them again, which is why this step exits
        /// <see cref="ExitRelaunch"/>, and finds none stale. A remote that cannot be reached leaves
        /// the package "unknown": a warning, nothing unpinned, exit <see cref="ExitDone"/>.
        /// </summary>
        private static int ResolveMovedPackages()
        {
            int registryResult = LoadRegistry("resolve", out PackageRegistry[] registry);
            if (registryResult != ExitDone)
                return registryResult;

            PackageLock.StaleReport stale = PackageLock.FindStale(registry);
            if (!stale.Complete)
                Debug.LogWarning($"{log_prefix}resolve: could not tell whether these moved on the remote (no network, no access, or the ref " +
                                 $"is gone): {string.Join(", ", stale.Unknown)}.");

            if (stale.Stale.Count == 0)
            {
                if (stale.Complete)
                    Debug.Log($"{log_prefix}resolve: {(stale.Compared == 0 ? "no pinned git package to compare" : $"{stale.Compared} pinned git package(s) compared, none moved")}.");
                return ExitDone;
            }

            foreach (PackageLock.StalePackage package in stale.Stale)
                Debug.Log($"{log_prefix}resolve: {package.Name} is pinned to {ShortCommit(package.LockedCommit)}, " +
                          $"{package.Reference ?? "the default branch"} is at {ShortCommit(package.RemoteCommit)} on the remote.");

            // All or nothing: unpinning only the packages whose remote answered would leave the project with
            // some packages at the new commit and the others at the old one, a combination nobody tested.
            if (!stale.Complete)
            {
                Debug.LogWarning($"{log_prefix}resolve: nothing was unpinned, because {stale.Unknown.Count} package(s) could not be checked and a " +
                                 $"partial update would mix new and old packages. Still pinned to an older commit: " +
                                 $"{string.Join(", ", stale.Stale.Select(package => package.Name))}. Launch again when the remotes can be reached.");
                return ExitDone;
            }

            List<string> unpinned = PackageLock.Unpin(registry, stale.Stale.Select(package => package.Name));
            Debug.Log($"{log_prefix}resolve: unpinned {unpinned.Count} package(s) from packages-lock.json. Launch again to resolve " +
                      "them; each is cloned again, so give it a moment.");
            return ExitRelaunch;
        }

        /// <summary>
        /// The window's <b>Update packages to selected version</b>, to the registry version given with
        /// <c>-vdSetupVersion</c>, for a project that has another one installed (what the <c>sdk</c>
        /// step refuses with <see cref="ExitOtherVersionInstalled"/>). The semantics are the window's,
        /// through the same code (<see cref="CreatorKitSetupWindow.ApplyVersionUpdate"/>): what the new
        /// version adds is installed, what it moves is moved, <b>what it does not list is removed</b>,
        /// and HybridCLR follows the registry. The manifest and <c>SetupConfiguration.asset</c> are
        /// written and the step exits <see cref="ExitRelaunch"/>; the next launch resolves the new
        /// packages, finds the project at the target and exits <see cref="ExitDone"/>.
        /// <para>
        /// A version step may have an update routine in the SDK, a menu item under
        /// <c>Virtuademy/Update routines</c> (for example "v2026.5 -> v2026.6"). They are in the new
        /// SDK, so the launch that finds the project at the target lists the ones the move crosses and
        /// logs that they must run next. This class does not run them.
        /// </para>
        /// </summary>
        private static int UpdateVersion(string version)
        {
            int registryResult = LoadRegistry("update", out PackageRegistry[] registry);
            if (registryResult != ExitDone)
                return registryResult;

            int targetResult = FindTarget("update", registry, ref version, out PackageRegistry target);
            if (targetResult != ExitDone)
                return targetResult;

            // What the window shows as the installed version. Without one the project was never set
            // up by this installer, and there is nothing to move from: that is the sdk step.
            PackageManagerConfiguration existing = FindConfiguration();
            string recorded = existing != null ? existing.CurrentInstallationVersion : null;
            if (string.IsNullOrEmpty(recorded))
            {
                Debug.LogError(log_prefix + "update: the project records no Virtuademy version, so there is nothing to update from. " +
                               "Run the sdk step to install " + version + ".");
                return ExitSdkMissing;
            }

            if (recorded == version)
            {
                string origin = ReadUpdateOrigin();
                if (origin != null)
                {
                    if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
                    {
                        Debug.LogError($"{log_prefix}update: the packages of Virtuademy {version} are in Packages/manifest.json but " +
                                       "Virtuademy-SDK-Environments did not resolve. The package manager errors are earlier in the log.");
                        return ExitSdkMissing;
                    }

                    LogUpdateRoutines(origin, version);
                    ClearUpdateOrigin();
                }

                Debug.Log($"{log_prefix}update: the project is at Virtuademy {version}.");
                return ExitDone;
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
                Debug.Log($"{log_prefix}update: removed, not part of Virtuademy {version}:\n  " + string.Join("\n  ", removedPackages));
            if (interpreterMoved)
                Debug.Log(log_prefix + "update: HybridCLR moved with the version; the interpreter step configures it after the resolve.");
            if (config.ResolveBreakingChangesAutomatically)
                Debug.LogWarning(log_prefix + "update: \"Resolve breaking changes automatically\" is on in the project, but the command line " +
                                 "does not download the old conversion script (the window does, for 2025.3 -> 2025.4 only).");

            RecordVersion(config, target);
            WriteUpdateOrigin(recorded);

            Debug.Log($"{log_prefix}update: Virtuademy {recorded} -> {version} written to Packages/manifest.json. Launch again to resolve the packages.");
            return ExitRelaunch;
        }

        /// <summary>
        /// The registry entry for <paramref name="version"/>, which is respelled as the registry spells
        /// it from here on (the window matches versions exactly), and the check that this editor is
        /// the one the entry requires. Stricter than the window, which only shows the mismatch in
        /// red: a batch run has nobody to see it, and installing anyway produces a project that does
        /// not compile.
        /// </summary>
        private static int FindTarget(string step, PackageRegistry[] registry, ref string version, out PackageRegistry target)
        {
            string asked = version;
            target = registry.FirstOrDefault(entry => string.Equals(entry.ReflectisVersion, asked, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                string listed = string.Join(", ", registry.Select(entry => entry.Prerelease ? entry.ReflectisVersion + " (prerelease)" : entry.ReflectisVersion));
                Debug.LogError($"{log_prefix}{step}: version '{version}' is not in the registry. Listed: {listed}.");
                return ExitBadArguments;
            }

            version = target.ReflectisVersion;

            if (CreatorKitSetupWindow.UnityVersion != target.RequiredUnityVersion)
            {
                Debug.LogError($"{log_prefix}{step}: Virtuademy {version} requires Unity {target.RequiredUnityVersion}; " +
                               $"this editor is {CreatorKitSetupWindow.UnityVersion}.");
                return ExitUnityVersionMismatch;
            }

            return ExitDone;
        }

        /// <summary>
        /// The SDK's <b>Virtuademy ▸ Update VS Library</b> menu item: <c>VisualScriptingSetupEditor.Setup()</c>,
        /// by reflection like <see cref="ConfigureAddressables"/>. It marks Visual Scripting as used,
        /// resets Visual Scripting's type options to its defaults, adds every type that the project's
        /// <c>VisualScriptingCustomTypeCollector</c> assets list (and their namespaces to the assembly
        /// options), saves the Visual Scripting settings and rebuilds the units. Without it the
        /// SDK's nodes are not in the node library of a project set up from the command line.
        /// <para>
        /// <b>Not a default step.</b> It cannot tell it is already done: it does everything again,
        /// every time, ending with a unit rebuild, and the only way to know what it would add is the
        /// SDK's private collector. So it runs when asked (<c>-vdSetupSteps vslibrary</c>), after the
        /// others in a launch. An SDK without the method is skipped with a warning.
        /// </para>
        /// </summary>
        private static int UpdateVisualScriptingLibrary()
        {
            if (!IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name))
            {
                Debug.LogError(log_prefix + "vslibrary: Virtuademy-SDK-Environments is not installed, and the Visual Scripting " +
                               "library setup ships with it. Run the sdk step first.");
                return ExitSdkMissing;
            }

            Type setup = FindSdkType(visual_scripting_setup_type);
            MethodInfo method = setup?.GetMethod("Setup", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (method == null)
            {
                if (EditorUtility.scriptCompilationFailed && setup == null)
                {
                    Debug.LogError(log_prefix + "vslibrary: Virtuademy-SDK-Environments is installed but its editor assembly " +
                                   "did not compile. Look for compilation errors earlier in the log.");
                    return ExitSdkMissing;
                }

                Debug.LogWarning(log_prefix + "vslibrary: this Virtuademy-SDK-Environments has no VisualScriptingSetupEditor.Setup " +
                                 "(it predates it), so there is nothing to apply.");
                return ExitDone;
            }

            Debug.Log(log_prefix + "vslibrary: updating the Visual Scripting library (VisualScriptingSetupEditor.Setup).");
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                method.Invoke(null, null);
            }
            catch (TargetInvocationException ex)
            {
                Debug.LogError(log_prefix + "vslibrary: " + (ex.InnerException ?? ex));
                return ExitVisualScriptingFailed;
            }

            Debug.Log($"{log_prefix}vslibrary: updated in {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s.");
            return ExitDone;
        }

        #endregion

        #region Helpers

        private static string ShortCommit(string commit) => commit.Length > 8 ? commit[..8] : commit;

        /// <summary>A type of the SDK's editor assembly, which only exists once the package has
        /// resolved and compiled, so it can only be reached by reflection. Null before that.</summary>
        private static Type FindSdkType(string fullName)
            => AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name == sdk_editor_assembly)
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);

        /// <summary>
        /// The SDK's <c>AddressablesConfiguration</c>, which only exists once the package has
        /// resolved and compiled, so it can only be reached by reflection. Null before that.
        /// </summary>
        private static Type FindAddressablesConfigurationType() => FindSdkType(addressables_configuration_type);

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
        private static bool TryParseSeries(string text, out (int year, int minor) series)
        {
            Match match = Regex.Match(text ?? string.Empty, @"v?(\d+)\.(\d+)");
            series = match.Success ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : default;
            return match.Success;
        }

        /// <summary>
        /// Logs the SDK's update routines that the move from <paramref name="from"/> to
        /// <paramref name="to"/> crosses, each with its menu name: the menu items of the new SDK
        /// under <c>Virtuademy/Update routines</c>, named "vA -> vB", whose step lies between the two
        /// versions. They change the project's assets, so the log says they must run next, and that
        /// this class does not run them. A pre-release or a downgrade has no numbered step to match,
        /// so the log points at the menu instead.
        /// </summary>
        private static void LogUpdateRoutines(string from, string to)
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

            if (!TryParseSeries(from, out var fromSeries) || !TryParseSeries(to, out var toSeries))
            {
                Debug.Log($"{log_prefix}update: {from} -> {to} is not a step between released versions, so no update routine is matched. " +
                          "Look under Virtuademy > Update routines" +
                          (routines.Count > 0 ? ", which has: " + string.Join(", ", routines) : ", which this SDK does not have") + ".");
                return;
            }

            if (toSeries.CompareTo(fromSeries) <= 0)
            {
                Debug.Log($"{log_prefix}update: {from} -> {to} does not go to a later series, and update routines only run forward.");
                return;
            }

            List<string> crossed = new();
            foreach (string name in routines.OrderByDescending(name => name, StringComparer.Ordinal))
            {
                string[] sides = name.Split(new[] { "->" }, StringSplitOptions.None);
                if (sides.Length == 2 && TryParseSeries(sides[0], out var routineFrom) && TryParseSeries(sides[1], out var routineTo)
                    && routineFrom.CompareTo(fromSeries) >= 0 && routineTo.CompareTo(toSeries) <= 0 && routineFrom.CompareTo(routineTo) < 0)
                    crossed.Add(name);
            }

            if (crossed.Count == 0)
            {
                Debug.Log($"{log_prefix}update: this SDK has no update routine for {from} -> {to} (Virtuademy > Update routines).");
                return;
            }

            // Newest first: that is the order to run them when a project needs several (2026.6 before 2026.5, because
            // the 2026.5 routine saves graphs that only the 2026.6 one makes readable). The name is the routine's
            // target series, which is how UpdateRoutinesCli's -vdRoutine names it.
            foreach (string name in crossed)
            {
                string target = name.Split(new[] { "->" }, StringSplitOptions.None)[1];
                TryParseSeries(target, out var routineTo);
                Debug.LogWarning($"{log_prefix}update: the update routine \"{name}\" must run next, from the menu Virtuademy > Update routines > {name}, " +
                                 $"or from the command line: -executeMethod {update_routines_cli} -vdRoutine {routineTo.year}.{routineTo.minor} " +
                                 "(launch it again while it exits 2). It changes the project's assets (back them up first); SetupCli does not run it.");
            }

            if (crossed.Count > 1)
                Debug.LogWarning($"{log_prefix}update: run the routines in the order listed, newest first (-vdRoutine 2026.6 before 2026.5): " +
                                 "the 2026.5 routine refuses to start while the 2026.6 one still has graphs to migrate.");
        }

        /// <summary>
        /// Whether the project's Addressables are configured as the SDK expects; null when the SDK's
        /// <c>AddressablesConfiguration</c> is not there to ask. Does not change anything.
        /// </summary>
        private static bool? ReadAddressablesConfigured(Type configuration = null)
        {
            configuration ??= FindAddressablesConfigurationType();
            if (configuration == null)
                return null;

            return (bool?)configuration.GetProperty("IsConfigured", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }

        private static bool ProjectSettingsConfigured(RenderPipelineAsset asset)
            => CreatorKitSetupWindow.IsRenderPipelineConfigured(asset)
               && CreatorKitSetupWindow.GetProjectSettingsStatus()
               && CreatorKitSetupWindow.GetMaxTextureSizeOverride();

        /// <summary>
        /// The registry, read once per launch from <see cref="registrySource"/>: the published URL,
        /// or what <c>-vdSetupRegistry</c> names. Returns <see cref="ExitDone"/>, or the exit code
        /// for a registry that cannot be read or holds no entries.
        /// </summary>
        private static int LoadRegistry(string step, out PackageRegistry[] registry)
        {
            registry = registryCache;
            if (registry != null)
                return ExitDone;

            try
            {
                registry = ReadRegistry(registrySource);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
            {
                Debug.LogError($"{log_prefix}{step}: could not read the registry {registrySource}: {ex.Message}");
                return ExitRegistryUnavailable;
            }

            if (registry == null || registry.Length == 0)
            {
                Debug.LogError($"{log_prefix}{step}: the registry {registrySource} holds no entries.");
                return ExitRegistryUnavailable;
            }

            registryCache = registry;
            return ExitDone;
        }

        private static PackageRegistry[] ReadRegistry(string source)
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

        /// <summary>The window's state when the project has one; never created here.</summary>
        private static PackageManagerConfiguration FindConfiguration()
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

        private static bool IsRegistered(string packageName)
            => UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(package => package.name == packageName);

        // Managed packages only, as the window counts them: a package of ours that no registry entry
        // lists (one a creator added by hand) does not make the project "already installed".
        private static List<string> RegisteredPackagesOfOurs(PackageRegistry[] registry)
            => UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Select(package => package.name)
                .Where(name => CreatorKitSetupWindow.IsManagedPackage(name, registry) && !CreatorKitSetupWindow.packages_to_exclude.Contains(name))
                .ToList();

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

        /// <summary>
        /// Logs one JSON line with every check, and returns whether the project is set up. The
        /// interpreter counts the way the window counts it: optional, so a project without HybridCLR
        /// is set up, and one with HybridCLR is only set up when the interpreter is ready, including
        /// the HybridCLR version the recorded Virtuademy version declares.
        /// </summary>
        private static bool LogReport()
        {
            CreatorKitSetupWindow.TryGetGitVersion(out string git);
            RenderPipelineAsset asset = CreatorKitSetupWindow.LoadDefaultRenderPipelineAsset();
            bool hybridClr = IsRegistered(CreatorKitSetupWindow.hybridclr_package_name);

            // Found, never created: the check step must not change the project.
            PackageManagerConfiguration config = FindConfiguration();
            string recorded = config != null ? config.CurrentInstallationVersion : null;

            // The declared HybridCLR, and which git packages are ours, need the registry. Without it
            // the report still says everything else, and says that these parts were not checked.
            PackageRegistry entry = null;
            bool registryRead = false;
            PackageRegistry[] registry = null;
            if ((hybridClr && !string.IsNullOrEmpty(recorded)) || File.Exists(PackageLock.LockFilePath))
            {
                registryRead = LoadRegistry("report", out registry) == ExitDone;
                entry = registryRead ? registry.FirstOrDefault(candidate => candidate.ReflectisVersion == recorded) : null;
            }

            // Whether the remote has moved a tag or branch since the lock pinned it. Network errors
            // make it unknown, never stale.
            PackageLock.StaleReport stale = !File.Exists(PackageLock.LockFilePath) ? new PackageLock.StaleReport()
                : registryRead ? PackageLock.FindStale(registry) : null;

            // Null while the SDK is not there to ask: its own row says so, and this one does not
            // fail the check a second time.
            bool? addressablesConfigured = ReadAddressablesConfigured();

            bool interpreterReady = CreatorKitSetupWindow.IsInterpreterReady(entry, out string interpreterIssue);
            CreatorKitSetupWindow.TryGetDeclaredInterpreter(entry, out PackageDefinition declared);
            if (hybridClr && declared == null)
                Debug.LogWarning(log_prefix + "The HybridCLR version was not checked against the registry: " +
                                 (registryRead ? $"Virtuademy {recorded} declares no valid interpreter." : "the registry could not be read or no version is recorded."));

            var report = new
            {
                unityVersion = CreatorKitSetupWindow.UnityVersion,
                git,
                modules = CreatorKitSetupWindow.GetInstalledModules(),
                renderPipelineUrp = CreatorKitSetupWindow.IsRenderPipelineConfigured(asset),
                apiCompatibilityNetFramework = CreatorKitSetupWindow.GetProjectSettingsStatus(),
                maxTextureSize1024 = CreatorKitSetupWindow.GetMaxTextureSizeOverride(),
                recordedVersion = recorded,
                sdkEnvironments = IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name),
                scriptsCompile = !EditorUtility.scriptCompilationFailed,
                hybridClr,
                hybridClrInManifest = CreatorKitSetupWindow.ReadManifestDependency(CreatorKitSetupWindow.hybridclr_package_name),
                hybridClrDeclared = declared != null ? CreatorKitSetupWindow.InterpreterReference(declared) : null,
                hybridClrVersionChecked = declared != null,
                interpreterReady,
                interpreterIssue,
                addressablesConfigured,
                stalePackages = stale?.Stale.Select(package => new
                {
                    name = package.Name,
                    reference = package.Reference,
                    lockedCommit = package.LockedCommit,
                    remoteCommit = package.RemoteCommit,
                }).ToList(),
                stalePackagesChecked = stale != null && stale.Complete,
            };
            Debug.Log(log_prefix + "REPORT " + JsonConvert.SerializeObject(report));

            return report.renderPipelineUrp
                   && report.apiCompatibilityNetFramework
                   && report.maxTextureSize1024
                   && report.sdkEnvironments
                   && report.scriptsCompile
                   && (!hybridClr || interpreterReady)
                   && addressablesConfigured != false
                   && (stale == null || stale.Stale.Count == 0);
        }

        private static string Describe(int code) => code switch
        {
            ExitDone => "done",
            ExitUnexpected => "unexpected failure",
            ExitRelaunch => "launch again to continue",
            ExitNotReady => "not set up",
            ExitBadArguments => "bad arguments",
            ExitGitMissing => "git missing",
            ExitModulesMissing => "editor modules missing",
            ExitRegistryUnavailable => "registry unavailable",
            ExitUnityVersionMismatch => "Unity version does not match the registry",
            ExitOtherVersionInstalled => "another Virtuademy version is installed",
            ExitSdkMissing => "Virtuademy-SDK-Environments missing",
            ExitInterpreterFailed => "interpreter setup failed",
            ExitProjectSettingsFailed => "project settings failed",
            ExitAddressablesFailed => "addressables configuration failed",
            ExitVisualScriptingFailed => "visual scripting library update failed",
            _ => "unknown",
        };

        #endregion
    }
}
