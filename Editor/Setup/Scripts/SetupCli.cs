using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using UnityEditor;

using UnityEngine;
using UnityEngine.Rendering;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// Runs what <b>Virtuademy ▸ Setup project</b> does, from the command line, for a machine with
    /// nobody at the window: a fresh clone, CI, the Env-Test harness. Three steps, the window's own:
    /// <c>configure</c> (the Configure button), <c>sdk</c> (Install next to Virtuademy SDK
    /// Environments, at a given registry version) and <c>interpreter</c> (Install interpreter).
    /// <code>
    /// Unity -batchmode -nographics -projectPath &lt;project&gt; -logFile -
    ///       -executeMethod Virtuademy.SDK.Environments.Setup.Editor.SetupCli.Run
    ///       -vdSetupVersion develop [-vdSetupSteps configure,sdk,interpreter]
    ///       [-vdSetupRegistry &lt;url or file&gt;]
    /// </code>
    /// <para>
    /// The <c>interpreter</c> step installs the HybridCLR that the project's recorded version
    /// declares in the registry (<see cref="PackageRegistry.Interpreter"/>), and switches to it when
    /// the manifest asks for another one: that is a repair, not a version update. With
    /// <c>-vdSetupRegistry</c> the registry is read from another URL or from a local file, to try a
    /// registry change before it is published.
    /// </para>
    /// <para>
    /// <b>One launch does one round, not the whole setup.</b> Every package change ends in a
    /// resolve and a domain reload, and a reload cuts short the method that is running. So a launch
    /// applies what it can, stops right after the first change that needs the editor to start
    /// again, and exits with <see cref="ExitRelaunch"/>; the caller launches again until the code
    /// is <see cref="ExitDone"/>. A new project takes three launches: Configure and the SDK
    /// packages, then HybridCLR, then the interpreter (a fourth when the interpreter setup needs a
    /// recompilation first). Every step is idempotent, so a project that is already set up costs
    /// one launch and is left as it is.
    /// </para>
    /// <para>
    /// <c>-vdSetupSteps check</c> changes nothing: it reports and exits with <see cref="ExitDone"/>
    /// when the project is set up, <see cref="ExitNotReady"/> otherwise.
    /// </para>
    /// <para>
    /// The method exits the editor itself, with one of the codes below, so <c>-quit</c> is not
    /// needed and does not change the outcome. Every launch ends with a <c>[SetupCli] REPORT</c>
    /// line: one JSON object with every check.
    /// </para>
    /// <para>
    /// Moving a project from one Virtuademy version to another is not done here: that is the
    /// window's "Update packages to selected version", which removes what the new version drops
    /// and leads to the update routines. The <c>sdk</c> step refuses a project that already has
    /// packages of another version (<see cref="ExitOtherVersionInstalled"/>).
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

        private const string log_prefix = "[SetupCli] ";

        // As many as HotUpdateSetupper allows itself when the window drives it across reloads.
        private const int max_interpreter_attempts = 3;

        // Counted across launches, so it cannot live in SessionState, which dies with the editor.
        // Library/ is per machine and never committed.
        private const string interpreter_attempts_file = "Library/VirtuademySetupCli.interpreter-attempts";

        private enum Step { Configure, Sdk, Interpreter, Check }

        private static readonly Dictionary<string, Step> step_names = new()
        {
            { "configure", Step.Configure },
            { "sdk", Step.Sdk },
            { "interpreter", Step.Interpreter },
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
            if (result == ExitDone && steps.Contains(Step.Interpreter))
                result = InstallInterpreter();

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

        private static bool TryParseArguments(string[] args, out HashSet<Step> steps, out string version, out string error)
        {
            steps = new HashSet<Step> { Step.Configure, Step.Sdk, Step.Interpreter };
            version = GetArgument(args, "-vdSetupVersion");
            error = null;

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

            if (steps.Contains(Step.Sdk) && string.IsNullOrWhiteSpace(version))
            {
                error = "The sdk step needs the registry version to install, e.g. -vdSetupVersion develop.";
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

            PackageRegistry target = registry.FirstOrDefault(entry => string.Equals(entry.ReflectisVersion, version, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                string listed = string.Join(", ", registry.Select(entry => entry.Prerelease ? entry.ReflectisVersion + " (prerelease)" : entry.ReflectisVersion));
                Debug.LogError($"{log_prefix}sdk: version '{version}' is not in the registry. Listed: {listed}.");
                return ExitBadArguments;
            }

            // The registry's own spelling from here on: the window matches versions exactly.
            version = target.ReflectisVersion;

            // Stricter than the window, which only shows the mismatch in red: a batch run has
            // nobody to see it, and installing anyway produces a project that does not compile.
            if (CreatorKitSetupWindow.UnityVersion != target.RequiredUnityVersion)
            {
                Debug.LogError($"{log_prefix}sdk: Virtuademy {version} requires Unity {target.RequiredUnityVersion}; " +
                               $"this editor is {CreatorKitSetupWindow.UnityVersion}.");
                return ExitUnityVersionMismatch;
            }

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

            List<string> ours = RegisteredPackagesOfOurs();
            if (inManifest != null || ours.Count > 0)
            {
                string found = inManifest != null
                    ? $"Packages/manifest.json asks for {inManifest}, while registry entry '{version}' is {wanted}"
                    : $"this project already has Virtuademy packages: {string.Join(", ", ours)}";
                Debug.LogError($"{log_prefix}sdk: {found}. Moving a project to another version is an update, which the " +
                               "command line does not do. Use \"Update packages to selected version\" in Virtuademy > Setup " +
                               "project: it also removes what the new version drops.");
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

        #endregion

        #region Helpers

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

        private static List<string> RegisteredPackagesOfOurs()
            => UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Select(package => package.name)
                .Where(name => CreatorKitSetupWindow.IsOurPackage(name) && !CreatorKitSetupWindow.packages_to_exclude.Contains(name))
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

            // The declared HybridCLR needs the registry. Without it the report still says
            // everything else, and says that this part was not checked.
            PackageRegistry entry = null;
            bool registryRead = false;
            if (hybridClr && !string.IsNullOrEmpty(recorded))
            {
                registryRead = LoadRegistry("report", out PackageRegistry[] registry) == ExitDone;
                entry = registryRead ? registry.FirstOrDefault(candidate => candidate.ReflectisVersion == recorded) : null;
            }

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
            };
            Debug.Log(log_prefix + "REPORT " + JsonConvert.SerializeObject(report));

            return report.renderPipelineUrp
                   && report.apiCompatibilityNetFramework
                   && report.maxTextureSize1024
                   && report.sdkEnvironments
                   && report.scriptsCompile
                   && (!hybridClr || interpreterReady);
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
            _ => "unknown",
        };

        #endregion
    }
}
