using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using UnityEditor;

using UnityEngine;
using UnityEngine.Rendering;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// Runs what <b>Virtuademy ▸ Setup project</b> does, from the command line, for a machine with
    /// nobody at the window: a fresh clone, CI, the Env-Test harness. It has two ways of working.
    /// <para>
    /// <b>The whole setup in one launch</b>: <c>-vdSetupSteps run</c> does what the wizard does after
    /// its Summary page, with the wizard's own engine (<see cref="SetupRun"/>): the project settings,
    /// the packages of the version, the compile, the update routines, the interpreter (installed, or
    /// removed with Visual Scripting only), the Addressables, the Visual Scripting nodes and the
    /// TextMesh Pro essentials, each skipped when the project has it already. The editor stays open
    /// across the domain reloads the run causes and exits when the run ends, so one launch is enough
    /// and <c>-quit</c> is refused. <c>-vdSetupSteps plan</c> changes nothing: it logs what the run
    /// would do as one <c>[SetupCli] PLAN</c> JSON line (each step with its state and detail, what
    /// cannot be undone, the confirmations missing) and exits with the code the run would start or
    /// stop with. Both need <c>-vdSetupVersion</c> and <c>-vdSetupScripting</c>, and the run asks the
    /// wizard's confirmations as options: <c>-vdAllowDowngrade</c> to go back to an older version,
    /// <c>-vdAllowScriptRemoval</c> to remove the interpreter from a project that has C# scripts in
    /// <c>Assets/VirtuademyEnvironmentScripts</c> (they stay, and no longer run). Without one of them
    /// it does not start (<see cref="ExitConfirmationNeeded"/>). The run ends with a
    /// <c>[SetupCli] RUN</c> JSON line: every step with its state and detail, the step that failed,
    /// and whether Active Input Handling changed (it applies from the next launch). Launch it with
    /// <c>-ignoreCompilerErrors</c>: after an update the project's scripts may not compile until the
    /// update routines have run, and batch mode stops on compile errors otherwise. A run that has not
    /// ended after <see cref="run_timeout_seconds"/> is ended with <see cref="ExitRunStepFailed"/>.
    /// </para>
    /// <para>
    /// <b>One step at a time</b>, one round per launch. The steps, in the order they run:
    /// </para>
    /// <list type="bullet">
    /// <item><c>configure</c>: the Configure button.</item>
    /// <item><c>sdk</c>: Install next to Virtuademy SDK Environments, at a given registry version.</item>
    /// <item><c>update</c>: "Update packages to selected version", to the registry version given.
    /// <b>Never a default step</b>, and not combinable with <c>sdk</c>. Going back to an older version needs
    /// <c>-vdAllowDowngrade</c>.</item>
    /// <item><c>resolve</c>: re-resolves the git packages whose tag or branch moved on the remote
    /// (a smarter "Re-resolve packages from git"); a no-op when none moved.</item>
    /// <item><c>interpreter</c>: Install interpreter; with <c>-vdSetupScripting vs</c>, removes it instead
    /// (with <c>-vdAllowScriptRemoval</c> when the project has C# scripts).</item>
    /// <item><c>addressables</c>: the Addressables management window's "Fix Addressables
    /// configurations" button.</item>
    /// <item><c>vslibrary</c>: the SDK's <b>Virtuademy ▸ Advanced ▸ Update Visual Scripting library</b> menu (<b>Update VS Library</b> before 2026.7),
    /// only when the library lacks the SDK's types. <b>Opt-in</b>, not a default step.</item>
    /// <item><c>tmp</c>: TextMesh Pro's Essential Resources, when they are missing. <b>Opt-in</b>. The import
    /// ends after the launch's method returns, so the editor stays open until it is done (no <c>-quit</c>).</item>
    /// <item><c>check</c>: changes nothing, reports.</item>
    /// </list>
    /// The default steps, without <c>-vdSetupSteps</c>, are <c>configure</c>, <c>sdk</c>,
    /// <c>resolve</c>, <c>interpreter</c> and <c>addressables</c>.
    /// <code>
    /// Unity -batchmode -nographics -projectPath &lt;project&gt; -logFile - -ignoreCompilerErrors
    ///       -executeMethod Virtuademy.SDK.Environments.Setup.Editor.SetupCli.Run
    ///       -vdSetupVersion develop
    ///       [-vdSetupSteps configure,sdk,update,resolve,interpreter,addressables,vslibrary,tmp | check | plan | run]
    ///       [-vdSetupScripting vs | csharp] [-vdAllowDowngrade] [-vdAllowScriptRemoval]
    ///       [-vdSetupRegistry &lt;url or file&gt;]
    /// </code>
    /// Arguments are strict: any <c>-vd...</c> argument (with a <c>-</c>, <c>--</c> or <c>/</c> prefix, in
    /// any case) that is not exactly one of these options is refused with <see cref="ExitBadArguments"/>, as is
    /// an option given twice, a value after an option that takes none, and a value that no option claims
    /// (<c>-vdSetupSteps sdk update</c>): a typo must never run the default steps nobody asked for. Unity's own
    /// arguments are not touched. <c>-vdSetupScripting</c> is read by <c>interpreter</c>, <c>plan</c> and
    /// <c>run</c> only, and refused with other steps.
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
    /// it must run next, because the step does not run it (<c>run</c> does). Not a default step: it removes packages.
    /// </para>
    /// <para>
    /// The <c>vslibrary</c> step calls <c>VisualScriptingSetupEditor.Setup()</c> of
    /// Virtuademy-SDK-Environments, by reflection: it adds the SDK's Visual Scripting types to the
    /// node library and rebuilds the units, keeping the types a creator added by hand. It is opt-in
    /// because it is slow (about 75 to 80 s on a scratch project, 2026-10-14) and rewrites the unit
    /// options (a file of tens of MB). A library that has every type of the SDK already is left
    /// alone. An SDK without the method is skipped with a warning.
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
    /// when the project is set up, <see cref="ExitNotReady"/> otherwise, counting what the wizard's
    /// status page counts. The Addressables configuration counts once the SDK is installed
    /// (<c>addressablesConfigured</c> in the report is null before that). So do a git package that
    /// moved on the remote (<c>stalePackages</c>: name, ref, locked commit, remote commit) and the
    /// TextMesh Pro essentials (<c>tmpEssentialsMissing</c>). A remote that cannot be asked is not a
    /// failure: <c>stalePackagesChecked</c> is then false and the check goes on.
    /// <c>vsLibraryCurrent</c> (null when it cannot be told) is reported, not counted.
    /// </para>
    /// <para>
    /// The method exits the editor itself, with one of the codes below, so <c>-quit</c> is not
    /// needed and does not change the outcome, except for <c>run</c> and <c>tmp</c>, which it would
    /// cut short. Every launch ends with a <c>[SetupCli] REPORT</c> line: one JSON object with every
    /// check.
    /// </para>
    /// <para>
    /// The <c>sdk</c> step refuses a project that already has packages of another version
    /// (<see cref="ExitOtherVersionInstalled"/>): moving it is the <c>update</c> step.
    /// </para>
    /// <para>
    /// This class is the command line: the arguments, the order of the steps, the report and the exit codes. What each
    /// step does is in <see cref="SetupSteps"/>, which the wizard's engine (<see cref="SetupRun"/>) runs too.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
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
        /// <summary>A confirmation the wizard asks for is missing: <c>-vdAllowDowngrade</c> or <c>-vdAllowScriptRemoval</c>. Nothing was changed.</summary>
        public const int ExitConfirmationNeeded = 21;
        /// <summary>The <c>tmp</c> step failed: the TextMesh Pro Essential Resources were not imported.</summary>
        public const int ExitTmpEssentialsFailed = 22;
        /// <summary>A step of <c>run</c> failed, or the run did not end in time: the <c>RUN</c> line names the step and why.</summary>
        public const int ExitRunStepFailed = 23;

        private const string log_prefix = "[SetupCli] ";

        // Not an exit code: Run leaves the editor open for an engine run, which exits it when it ends.
        private const int keep_running = -1;

        // The longest an engine run is watched: past it, the editor is closed with ExitRunStepFailed. The steps have their own
        // timeouts (a round of package resolution: 15 minutes); this is for anything that would keep a batch editor open for ever.
        private const double run_timeout_seconds = 90 * 60;

        // Which engine run this command line watches ("run" or "tmp"), and since when: they survive the domain reloads of the run.
        private const string watch_key = "Virtuademy.SDK.Environments.Setup.CliWatch";
        private const string watch_started_key = "Virtuademy.SDK.Environments.Setup.CliWatchStarted";
        private const string watch_run = "run";
        private const string watch_tmp = "tmp";

        private enum Step { Configure, Sdk, Update, Resolve, Interpreter, Addressables, VisualScriptingLibrary, TmpEssentials, Check, Plan, Run }

        private static readonly Dictionary<string, Step> step_names = new()
        {
            { "configure", Step.Configure },
            { "sdk", Step.Sdk },
            { "update", Step.Update },
            { "resolve", Step.Resolve },
            { "interpreter", Step.Interpreter },
            { "addressables", Step.Addressables },
            { "vslibrary", Step.VisualScriptingLibrary },
            { "tmp", Step.TmpEssentials },
            { "check", Step.Check },
            { "plan", Step.Plan },
            { "run", Step.Run },
        };

        // The steps that stand alone.
        private static readonly Step[] alone_steps = { Step.Check, Step.Plan, Step.Run };

        private static readonly Dictionary<string, SetupScripting> scripting_names = new()
        {
            { "vs", SetupScripting.VisualScriptingOnly },
            { "csharp", SetupScripting.VisualScriptingAndCSharp },
        };

        /// <summary>What the command line asked for.</summary>
        private sealed class Arguments
        {
            internal HashSet<Step> Steps;
            internal string Version;
            /// <summary>Null without <c>-vdSetupScripting</c>.</summary>
            internal SetupScripting? Scripting;
            internal bool AllowDowngrade;
            internal bool AllowScriptRemoval;
        }

        static SetupCli()
        {
            if (!Application.isBatchMode || AssetDatabase.IsAssetImportWorkerProcess())
                return;

            if (string.IsNullOrEmpty(SessionState.GetString(watch_key, string.Empty)))
                return;

            // A domain reload in the middle of a run this command line watches: what lives in static fields is set again from the
            // command line, which the process still has, and the watch goes on.
            SetupSteps.Prefix = log_prefix;
            string registrySource = GetArgument(Environment.GetCommandLineArgs(), "-vdSetupRegistry");
            if (registrySource != null)
                SetupSteps.UseRegistrySource(registrySource);
            Watch();
        }

        /// <summary>The <c>-executeMethod</c> entry point. It exits the editor, except for an engine run, which exits it when it ends.</summary>
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

            if (code == keep_running)
            {
                Debug.Log(log_prefix + "The editor stays open for the run, across its reloads, and exits when the run ends.");
                return;
            }

            Debug.Log($"{log_prefix}EXIT {code} ({Describe(code)})");
            EditorApplication.Exit(code);
        }

        private static int Execute(string[] args)
        {
            if (!TryParseArguments(args, out Arguments arguments, out string argumentError))
            {
                Debug.LogError(log_prefix + argumentError);
                return ExitBadArguments;
            }

            SetupSteps.Prefix = log_prefix;
            SetupSteps.Caller = SetupSteps.StepCaller.CommandLine;
            string registrySource = GetArgument(args, "-vdSetupRegistry") ?? CreatorKitSetupWindow.package_registry_path;
            SetupSteps.UseRegistrySource(registrySource);
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

            HashSet<Step> steps = arguments.Steps;
            if (steps.Contains(Step.Check))
            {
                return LogReport() ? ExitDone : ExitNotReady;
            }

            if (steps.Contains(Step.Plan) || steps.Contains(Step.Run))
            {
                int planned = PlanAndRun(arguments, steps.Contains(Step.Run));
                if (planned != keep_running)
                    TryLogReport();
                return planned;
            }

            int result = ExitDone;
            if (steps.Contains(Step.Configure))
                result = SetupSteps.Configure(out _).Code;
            if (result == ExitDone && steps.Contains(Step.Sdk))
                result = SetupSteps.InstallSdk(arguments.Version).Code;
            if (result == ExitDone && steps.Contains(Step.Update))
                result = RunUpdate(arguments);
            if (result == ExitDone && steps.Contains(Step.Resolve))
                result = SetupSteps.ResolveMovedPackages().Code;
            if (result == ExitDone && steps.Contains(Step.Interpreter))
                result = RunInterpreter(arguments);
            if (result == ExitDone && steps.Contains(Step.Addressables))
                result = SetupSteps.ConfigureAddressables().Code;
            if (result == ExitDone && steps.Contains(Step.VisualScriptingLibrary))
                result = SetupSteps.UpdateVisualScriptingLibrary(onlyWhenMissing: true).Code;
            if (result == ExitDone && steps.Contains(Step.TmpEssentials))
            {
                // The import ends after this method returns: the engine's tmp-essentials step imports and waits for it, alone.
                if (SetupSteps.TmpEssentialsMissing())
                    return StartWatchedRun(TmpChoices(), new[] { SetupStepIds.TmpEssentials }, watch_tmp);

                Debug.Log(log_prefix + "tmp: " + (SetupSteps.IsTmpPresent()
                    ? "the TextMesh Pro Essential Resources are already in the project."
                    : "TextMesh Pro is not in the project (com.unity.ugui), so there is nothing to import."));
            }

            TryLogReport();
            return result;
        }

        #region Steps

        private static int RunUpdate(Arguments arguments)
        {
            // The wizard's confirmation: an older version can break the project.
            string recorded = SetupSteps.FindConfiguration()?.CurrentInstallationVersion;
            if (!arguments.AllowDowngrade && !string.IsNullOrEmpty(recorded) && SetupRun.CompareVersions(arguments.Version, recorded) < 0)
            {
                Debug.LogError($"{log_prefix}update: Virtuademy {arguments.Version} is older than the installed {recorded}, and an older version " +
                               "can break the project. Commit or back it up, then launch again with -vdAllowDowngrade.");
                return ExitConfirmationNeeded;
            }

            return SetupSteps.UpdateVersion(arguments.Version).Code;
        }

        private static int RunInterpreter(Arguments arguments)
        {
            if (arguments.Scripting != SetupScripting.VisualScriptingOnly)
                return SetupSteps.InstallInterpreter().Code;

            // The wizard's red banner: the C# scripts stay in the project and no longer run.
            int scripts = SetupSteps.CountEnvironmentScripts();
            if (SetupSteps.InterpreterInManifest() && scripts > 0 && !arguments.AllowScriptRemoval)
            {
                Debug.LogError($"{log_prefix}interpreter: removing it leaves the {scripts} C# script(s) in {SetupSteps.environment_scripts_folder} " +
                               "in the project, and they no longer run in Virtuademy. Launch again with -vdAllowScriptRemoval to remove it, " +
                               "or with -vdSetupScripting csharp to keep it.");
                return ExitConfirmationNeeded;
            }

            return SetupSteps.RemoveInterpreter().Code;
        }

        // The tmp step's run: the installed version and scripting, so nothing else of the plan differs from the project.
        private static SetupChoices TmpChoices() => new()
        {
            Version = SetupRun.FindRecordedVersion(),
            Scripting = SetupSteps.InterpreterInManifest() ? SetupScripting.VisualScriptingAndCSharp : SetupScripting.VisualScriptingOnly,
        };

        #endregion

        #region Plan and run

        /// <summary>
        /// The <c>plan</c> and <c>run</c> steps: what the wizard's Summary page shows, as a <c>PLAN</c> line, and for <c>run</c> the start of
        /// the engine run, which this command line watches until it ends. Refused before anything is written: a version the registry does
        /// not list or another Unity needs, C# scripts on a version without an interpreter, a project the engine cannot move (packages
        /// from before 2026.6), and a confirmation that is missing.
        /// </summary>
        private static int PlanAndRun(Arguments arguments, bool run)
        {
            const string step = "plan";

            // The engine's wording: it has no launches or steps to point at.
            SetupSteps.Caller = SetupSteps.StepCaller.Wizard;

            SetupSteps.StepResult registryResult = SetupSteps.LoadRegistry(step, out PackageRegistry[] registry);
            if (!registryResult.IsDone)
                return registryResult.Code;

            string version = arguments.Version;
            SetupSteps.StepResult targetResult = SetupSteps.FindTarget(step, registry, ref version, out PackageRegistry target);
            if (!targetResult.IsDone)
                return targetResult.Code;

            // The versions the wizard offers: the run installs and moves to the SDK of 2026.6 and later only.
            if (target.Packages == null || target.Packages.All(package => package.Name != CreatorKitSetupWindow.sdk_environments_package_name))
            {
                Debug.LogError($"{log_prefix}{step}: Virtuademy {version} has no {CreatorKitSetupWindow.sdk_environments_package_name} (versions before 2026.6), " +
                               "so the run cannot set a project up with it. The update step can move a project to it.");
                return ExitBadArguments;
            }

            SetupScripting scripting = arguments.Scripting.Value;
            if (scripting == SetupScripting.VisualScriptingAndCSharp
                && (!CreatorKitSetupWindow.TryGetDeclaredInterpreter(target, out PackageDefinition declared) || declared == null))
            {
                Debug.LogError($"{log_prefix}{step}: Virtuademy {version} has no interpreter for C# scripts (versions before 2026.6 have none). " +
                               "Use -vdSetupScripting vs.");
                return ExitInterpreterFailed;
            }

            SetupChoices choices = new()
            {
                Version = version,
                Scripting = scripting,
                AllowDowngrade = arguments.AllowDowngrade,
                AllowScriptRemoval = arguments.AllowScriptRemoval,
            };

            // What the run does with every confirmation given; the ones missing are listed apart, by the option that gives each.
            IReadOnlyList<SetupStepStatus> plan = SetupRun.Plan(new SetupChoices { Version = version, Scripting = scripting, AllowDowngrade = true, AllowScriptRemoval = true });

            string recorded = SetupRun.FindRecordedVersion();
            List<string> confirmations = new();
            if (!arguments.AllowDowngrade && recorded != null && SetupRun.CompareVersions(version, recorded) < 0)
                confirmations.Add("-vdAllowDowngrade");

            bool removesInterpreter = plan.Any(planned => planned.Id == SetupStepIds.Interpreter && planned.State == SetupStepState.Pending
                                                          && planned.Detail == SetupRun.RemovesInterpreterDetail);
            if (!arguments.AllowScriptRemoval && removesInterpreter && SetupSteps.CountEnvironmentScripts() > 0)
                confirmations.Add("-vdAllowScriptRemoval");

            SetupStepStatus refused = plan.FirstOrDefault(planned => planned.State == SetupStepState.Failed);
            var planReport = new
            {
                version,
                fromVersion = recorded,
                scripting = scripting_names.First(name => name.Value == scripting).Key,
                steps = DescribeSteps(plan),
                irreversible = SetupRun.DestructiveActions(choices, plan),
                confirmationsNeeded = confirmations,
                startable = refused == null && confirmations.Count == 0,
            };
            Debug.Log(log_prefix + "PLAN " + JsonConvert.SerializeObject(planReport));

            if (refused != null)
            {
                Debug.LogError($"{log_prefix}{step}: {refused.Title}: {refused.Detail}");

                // The packages step is the only one the plan refuses here: the version and its Unity were checked above, the
                // downgrade is a confirmation, so what is left is a project with packages from before 2026.6.
                return refused.Id == SetupStepIds.Packages ? ExitOtherVersionInstalled : ExitRunStepFailed;
            }

            if (confirmations.Count > 0)
            {
                string message = $"{step}: the run needs {string.Join(" and ", confirmations)}: the PLAN line says what cannot be undone. " +
                                 $"Commit or back up the project, then launch again with {(confirmations.Count == 1 ? "it" : "them")}.";
                if (run)
                    Debug.LogError(log_prefix + message);
                else
                    Debug.LogWarning(log_prefix + message);
                return ExitConfirmationNeeded;
            }

            if (!run)
                return ExitDone;

            return StartWatchedRun(choices, null, watch_run);
        }

        /// <summary>
        /// Starts an engine run and watches it: the editor stays open, across the run's domain reloads, until the run ends, then the
        /// <c>RUN</c> line, the report and the exit. <paramref name="mode"/> is <c>run</c> or <c>tmp</c> (which only sets the exit code of a failure).
        /// </summary>
        private static int StartWatchedRun(SetupChoices choices, IReadOnlyCollection<string> onlySteps, string mode)
        {
            if (SetupRun.IsRunning)
            {
                Debug.LogError(log_prefix + "A setup run is already going in this editor.");
                return ExitUnexpected;
            }

            SessionState.SetString(watch_key, mode);
            SessionState.SetString(watch_started_key, EditorApplication.timeSinceStartup.ToString("R", CultureInfo.InvariantCulture));
            Watch();

            // A run that has nothing to do ends inside Start, and one Start refuses never begins: either way Changed has told the
            // watch, which exits once this launch's method has returned.
            SetupRun.Start(choices, onlySteps);
            return keep_running;
        }

        private static bool finishing;
        private static double watchStarted = -1;

        private static void Watch()
        {
            SetupRun.Changed -= OnRunChanged;
            SetupRun.Changed += OnRunChanged;
            EditorApplication.update -= CheckRunTimeout;
            EditorApplication.update += CheckRunTimeout;
        }

        private static void OnRunChanged()
        {
            if (SetupRun.IsRunning || finishing)
                return;

            // After the engine is out of its tick.
            finishing = true;
            EditorApplication.delayCall += () => FinishWatchedRun(timedOut: false);
        }

        private static void CheckRunTimeout()
        {
            if (finishing)
                return;

            if (watchStarted < 0 && !double.TryParse(SessionState.GetString(watch_started_key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out watchStarted))
                watchStarted = EditorApplication.timeSinceStartup;

            if (EditorApplication.timeSinceStartup - watchStarted < run_timeout_seconds)
                return;

            finishing = true;
            Debug.LogError($"{log_prefix}The setup run did not end within {run_timeout_seconds / 60:0} minutes: the editor is closed.");
            FinishWatchedRun(timedOut: true);
        }

        private static void FinishWatchedRun(bool timedOut)
        {
            string mode = SessionState.GetString(watch_key, watch_run);
            SessionState.EraseString(watch_key);
            SessionState.EraseString(watch_started_key);
            SetupRun.Changed -= OnRunChanged;
            EditorApplication.update -= CheckRunTimeout;

            IReadOnlyList<SetupStepStatus> steps = SetupRun.Steps;
            SetupStepStatus failed = steps.FirstOrDefault(step => step.State == SetupStepState.Failed);
            SetupStepStatus running = steps.FirstOrDefault(step => step.State == SetupStepState.Running);
            bool restart = SetupRun.RestartPending;

            // A step still pending at the end is one the run did not reach, but for the restart, which waits for the next launch only.
            bool complete = !timedOut && failed == null && running == null
                            && steps.All(step => step.State != SetupStepState.Pending || step.Id == SetupStepIds.Restart);

            var runReport = new
            {
                steps = DescribeSteps(steps),
                failedStep = failed?.Id ?? (timedOut ? running?.Id : null),
                timedOut,
                restartPending = restart,
            };
            Debug.Log(log_prefix + "RUN " + JsonConvert.SerializeObject(runReport));
            if (restart)
                Debug.Log(log_prefix + "Active Input Handling changed: it applies from the next launch of the editor.");

            TryLogReport();

            int code = complete ? ExitDone : mode == watch_tmp ? ExitTmpEssentialsFailed : ExitRunStepFailed;
            Debug.Log($"{log_prefix}EXIT {code} ({Describe(code)})");
            EditorApplication.Exit(code);
        }

        private static List<object> DescribeSteps(IEnumerable<SetupStepStatus> steps)
            => steps.Select(step => (object)new { id = step.Id, title = step.Title, state = step.State.ToString(), detail = step.Detail }).ToList();

        #endregion

        #region Arguments

        private static readonly string[] value_options = { "-vdSetupVersion", "-vdSetupSteps", "-vdSetupRegistry", "-vdSetupScripting" };

        // Options that take no value: present or not.
        private static readonly string[] flag_options = { "-vdAllowDowngrade", "-vdAllowScriptRemoval" };

        private static readonly string[] all_options = value_options.Concat(flag_options).ToArray();

        // Unity's own options that take a value: the token after one is Unity's, not a stray value of ours.
        private static readonly string[] unity_value_options = { "-projectPath", "-logFile", "-executeMethod", "-buildTarget", "-activeBuildProfile" };

        // What an option of ours looks like, in any prefix and case: anything matching it has to be one exactly.
        private static readonly Regex vd_like = new("^[-/]+vd", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Checks every <c>-vd</c> argument of the command line: one that is not exactly a known option
        /// (<c>--vdSetupSteps</c>, <c>-vdsetupsteps</c> are not <c>-vdSetupSteps</c>), one given twice, an option with
        /// no value, a value after an option that takes none, and a token that no option claims after an option's value
        /// are all refused. Unity's own arguments (and the value of the ones that take one) are left alone.
        /// </summary>
        private static bool TryCheckArguments(string[] args, out string error)
        {
            error = null;
            HashSet<string> seen = new();

            bool IsStray(int index) => index < args.Length && !args[index].StartsWith("-", StringComparison.Ordinal) && !vd_like.IsMatch(args[index]);

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

                if (!all_options.Contains(arg))
                {
                    string near = all_options.FirstOrDefault(o => string.Equals(o, "-" + arg.TrimStart('-', '/'), StringComparison.OrdinalIgnoreCase));
                    error = $"'{arg}' is not an option of this tool." +
                            (near != null ? $" Did you mean {near}? Options are exact, with a single leading dash." : $" Options: {string.Join(", ", all_options)}.");
                    return false;
                }

                if (!seen.Add(arg))
                {
                    error = $"{arg} is given twice.";
                    return false;
                }

                if (flag_options.Contains(arg))
                {
                    if (IsStray(i + 1))
                    {
                        error = $"{arg} takes no value: '{args[i + 1]}' belongs to no option.";
                        return false;
                    }

                    continue;
                }

                if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal) || vd_like.IsMatch(args[i + 1]))
                {
                    error = $"{arg} needs a value.";
                    return false;
                }

                i++;

                // The one place a stray value can hide: right behind an option's value. Whatever Unity's
                // options carry comes after an option of theirs, which starts with a dash.
                if (IsStray(i + 1))
                {
                    error = $"'{args[i + 1]}' follows {arg} and its value and belongs to no option (a list takes commas, as in -vdSetupSteps sdk,resolve).";
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseArguments(string[] args, out Arguments arguments, out string error)
        {
            arguments = new Arguments
            {
                Steps = new HashSet<Step> { Step.Configure, Step.Sdk, Step.Resolve, Step.Interpreter, Step.Addressables },
            };

            if (!TryCheckArguments(args, out error))
                return false;

            arguments.Version = GetArgument(args, "-vdSetupVersion");
            arguments.AllowDowngrade = args.Contains("-vdAllowDowngrade");
            arguments.AllowScriptRemoval = args.Contains("-vdAllowScriptRemoval");
            HashSet<Step> steps = arguments.Steps;

            string stepList = GetArgument(args, "-vdSetupSteps");
            if (stepList != null)
            {
                steps.Clear();
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

                Step[] alone = alone_steps.Where(steps.Contains).Take(1).ToArray();
                if (steps.Count > 1 && alone.Length > 0)
                {
                    error = alone[0] == Step.Check
                        ? "The check step changes nothing, so it cannot be combined with the others."
                        : $"The {step_names.First(name => name.Value == alone[0]).Key} step is the whole setup, so it cannot be combined with other steps.";
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

            if ((steps.Contains(Step.Sdk) || steps.Contains(Step.Update) || steps.Contains(Step.Plan) || steps.Contains(Step.Run)) && string.IsNullOrWhiteSpace(arguments.Version))
            {
                error = "The sdk, update, plan and run steps need the registry version, e.g. -vdSetupVersion develop.";
                return false;
            }

            string scripting = GetArgument(args, "-vdSetupScripting");
            if (scripting != null)
            {
                if (!scripting_names.TryGetValue(scripting.Trim().ToLowerInvariant(), out SetupScripting parsed))
                {
                    error = "-vdSetupScripting takes vs (Visual Scripting only) or csharp (Visual Scripting and C# scripts, with the interpreter).";
                    return false;
                }

                if (!steps.Contains(Step.Interpreter) && !steps.Contains(Step.Plan) && !steps.Contains(Step.Run))
                {
                    error = "-vdSetupScripting is read by the interpreter, plan and run steps only.";
                    return false;
                }

                arguments.Scripting = parsed;
            }

            // The run removes the interpreter or keeps it: a choice it never makes by default.
            if ((steps.Contains(Step.Plan) || steps.Contains(Step.Run)) && arguments.Scripting == null)
            {
                error = "The plan and run steps need -vdSetupScripting vs (Visual Scripting only) or csharp (Visual Scripting and C# scripts).";
                return false;
            }

            // Checked before any step runs: -quit closes the editor as soon as the launch's method returns, before these end.
            if ((steps.Contains(Step.Run) || steps.Contains(Step.TmpEssentials)) && args.Contains("-quit", StringComparer.OrdinalIgnoreCase))
            {
                error = "-quit would close the editor before the run or the tmp import ends: launch without it. The editor exits by itself when they end.";
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

        #endregion

        #region Report

        // Informational after the steps: a report that throws must not replace their result.
        private static void TryLogReport()
        {
            try
            {
                LogReport();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(log_prefix + "The report could not be produced: " + ex.Message);
            }
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
            bool hybridClr = SetupSteps.IsRegistered(CreatorKitSetupWindow.hybridclr_package_name);

            // Found, never created: the check step must not change the project.
            PackageManagerConfiguration config = SetupSteps.FindConfiguration();
            string recorded = config != null ? config.CurrentInstallationVersion : null;

            // The declared HybridCLR, and which git packages are ours, need the registry. Without it
            // the report still says everything else, and says that these parts were not checked.
            PackageRegistry entry = null;
            bool registryRead = false;
            PackageRegistry[] registry = null;
            if ((hybridClr && !string.IsNullOrEmpty(recorded)) || File.Exists(PackageLock.LockFilePath))
            {
                registryRead = SetupSteps.LoadRegistry("report", out registry).IsDone;
                entry = registryRead ? registry.FirstOrDefault(candidate => candidate.ReflectisVersion == recorded) : null;
            }

            // Whether the remote has moved a tag or branch since the lock pinned it. Network errors
            // make it unknown, never stale.
            PackageLock.StaleReport stale = !File.Exists(PackageLock.LockFilePath) ? new PackageLock.StaleReport()
                : registryRead ? PackageLock.FindStale(registry) : null;

            // Null while the SDK is not there to ask: its own row says so, and this one does not
            // fail the check a second time.
            bool? addressablesConfigured = SetupSteps.ReadAddressablesConfigured();

            bool interpreterReady = CreatorKitSetupWindow.IsInterpreterReady(entry, out string interpreterIssue);
            CreatorKitSetupWindow.TryGetDeclaredInterpreter(entry, out PackageDefinition declared);
            if (hybridClr && declared == null)
                Debug.LogWarning(log_prefix + "The HybridCLR version was not checked against the registry: " +
                                 (registryRead ? $"Virtuademy {recorded} declares no valid interpreter." : "the registry could not be read or no version is recorded."));

            // False when TextMesh Pro is not in the project: there is nothing to miss.
            bool tmpEssentialsMissing = SetupSteps.TmpEssentialsMissing();

            var report = new
            {
                unityVersion = CreatorKitSetupWindow.UnityVersion,
                git,
                modules = CreatorKitSetupWindow.GetInstalledModules(),
                renderPipelineUrp = CreatorKitSetupWindow.IsRenderPipelineConfigured(asset),
                apiCompatibilityNetFramework = CreatorKitSetupWindow.GetProjectSettingsStatus(),
                inputHandlingBoth = CreatorKitSetupWindow.GetInputHandlingStatus(),
                maxTextureSize1024 = CreatorKitSetupWindow.GetMaxTextureSizeOverride(),
                recordedVersion = recorded,
                sdkEnvironments = SetupSteps.IsRegistered(CreatorKitSetupWindow.sdk_environments_package_name),
                scriptsCompile = !EditorUtility.scriptCompilationFailed,
                scripting = SetupSteps.InterpreterInManifest() ? "csharp" : "vs",
                environmentScripts = SetupSteps.CountEnvironmentScripts(),
                hybridClr,
                hybridClrInManifest = CreatorKitSetupWindow.ReadManifestDependency(CreatorKitSetupWindow.hybridclr_package_name),
                hybridClrDeclared = declared != null ? CreatorKitSetupWindow.InterpreterReference(declared) : null,
                hybridClrVersionChecked = declared != null,
                interpreterReady,
                interpreterIssue,
                addressablesConfigured,
                vsLibraryCurrent = SetupSteps.ReadVisualScriptingLibraryCurrent(),
                tmpPresent = SetupSteps.IsTmpPresent(),
                tmpEssentialsMissing,
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
                   && report.inputHandlingBoth
                   && report.maxTextureSize1024
                   && report.sdkEnvironments
                   && report.scriptsCompile
                   && (!hybridClr || interpreterReady)
                   && addressablesConfigured != false
                   && !tmpEssentialsMissing
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
            ExitConfirmationNeeded => "a confirmation is missing",
            ExitTmpEssentialsFailed => "TextMesh Pro essentials import failed",
            ExitRunStepFailed => "a step of the run failed",
            _ => "unknown",
        };

        #endregion
    }
}
