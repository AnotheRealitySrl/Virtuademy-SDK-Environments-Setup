using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

using Unity.Properties;

using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

using UnityEditorInternal;

using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    public class CreatorKitSetupWindow : EditorWindow
    {
        [Serializable]
        public class ProjectConfiguration
        {
            [CreateProperty] public bool IsGitInstalled { get; set; }
            [CreateProperty] public bool IsHybridCLRInstalled { get; set; }
            [CreateProperty] public bool HybridCLRAssemblyReady { get; set; }
            [CreateProperty] public bool InterpreterReady => IsHybridCLRInstalled && HybridCLRAssemblyReady;

            /// <summary>Why the interpreter is not ready, verbatim from HotUpdateSetupper, or
            /// empty when it is. A red icon alone does not tell the author what to fix.</summary>
            [CreateProperty] public string InterpreterIssue { get; set; } = string.Empty;
            [CreateProperty] public string GitVersion { get; set; }

            [CreateProperty] public bool EditorConfigurationOk => UnityVersionIsMatching && AllEditorModulesInstalled;
            [CreateProperty] public bool UnityVersionIsMatching { get; set; }
            [CreateProperty] public bool AllEditorModulesInstalled { get; set; }

            [CreateProperty]
            public Dictionary<string, bool> InstalledModules = new()
            {
                { "Android", true },
                { "WebGL", true },
                { "Windows", true }
            };


            [CreateProperty] public bool ProjectSettingsOk => RenderPipelineURP && PlayerSettings && MaxTextureSizeOverride;
            [CreateProperty] public bool RenderPipelineURP { get; set; }
            [CreateProperty] public bool PlayerSettings { get; set; }
            [CreateProperty] public bool MaxTextureSizeOverride { get; set; }
        }

        [SerializeField] private VisualTreeAsset m_VisualTreeAsset = default;
        [SerializeField] private VisualTreeAsset packageItemAsset = default;
        [SerializeField] private VisualTreeAsset packageDependencyAsset = default;

        [SerializeField] private RenderPipelineGlobalSettings renderPipelineGlobalSettings;
        [SerializeField] private RenderPipelineAsset renderPipelineAsset;

        private VisualElement root;

        private readonly ProjectConfiguration projectConfig = new();
        private PackageManagerConfiguration packageManagerConfig;

        private const string utilities_folder_path = "Assets/Virtuademy/Editor/Scripts";
        private const string settings_folder_path = "Assets/Virtuademy/Editor/Settings";
        private const string setup_configuration_path = "SetupConfiguration.asset";

        // Every prefix a package of ours has ever shipped under, because a project can hold any
        // of them: reflectis-* predates the brand rename, virtuademy-* came with it, and spacs-*
        // is what the pieces carrying no platform are called — SPACS-Utility first, on
        // 2026-09-10.
        //
        // Missing one is not a cosmetic bug. Everything below reasons about "our" packages
        // through this list: with only the old prefix, GetInstalledPackages matched nothing in a
        // renamed project and the window went blind to the very packages it had just installed,
        // so its InstalledPackages list could never be confirmed against reality or pruned, and
        // version-switching and uninstall reasoned from a list nothing maintained. A package
        // missing here is also never unpinned in packages-lock, so it stays frozen at the commit
        // it first resolved to while everything around it moves.
        private static readonly string[] package_prefixes =
        {
            "com.anotherealitysrl.virtuademy",
            "com.anotherealitysrl.reflectis",
            "com.anotherealitysrl.spacs",
        };

        // The installer excludes itself. Listed under both names for the same reason as above:
        // a project installed before the rename still carries the old id.
        private readonly List<string> packages_to_exclude = new()
        {
            "com.anotherealitysrl.virtuademy-sdk-environments-setup",
            "com.anotherealitysrl.reflectis-creatorkit-worlds-setup",
        };

        private static bool IsOurPackage(string packageName)
            => package_prefixes.Any(prefix => packageName.StartsWith(prefix, StringComparison.Ordinal));

        private const string hybridclr_package_url = "https://github.com/focus-creative-games/hybridclr_unity.git";

        // Must stay in sync with HotUpdateSetupper.PENDING_SETUP_KEY: the setupper lives in another
        // package and this assembly cannot reference it, so the key is duplicated on purpose.
        private const string pending_hybridclr_setup_key = "PENDING_HYBRIDCLR_SETUP";

        private ListRequest _listRequest;
        private AddRequest _addRequest;

        #region Editor window setup

        private static bool isSetupping = false;
        private static bool setupCompleted = false;

        #endregion

        #region Project configuration

        private string UnityVersion => InternalEditorUtility.GetFullUnityVersion().Split(' ')[0];

        #endregion

        #region Package manager

        private const string package_registry_path = "https://spacsglobal.dfs.core.windows.net/reflectis2023-public/PackageManager/PackageRegistry.json";
        private const string breaking_changes_solver_path = "https://spacsglobal.dfs.core.windows.net/reflectis2023-public/PackageManager/BreakingChangesSolverIndex.json";

        private static Dictionary<(string, string), string> breakingChangesSolverDictionary;

        private string previousInstallationVersion;

        #endregion

        // Resolves by package name whether the package is a git dependency or an embedded folder.
        private const string package_root = "Packages/com.anotherealitysrl.virtuademy-sdk-environments-setup";

        private const string sdk_environments_package_name = "com.anotherealitysrl.virtuademy-sdk-environments";

        // Editor/Setup/ProjectSettings/DefaultRendererPipelineAsset.asset — the same asset the
        // window receives as renderPipelineAsset through its script's default references, which a
        // static check has no instance to read from.
        private const string render_pipeline_asset_guid = "a5c68f2b48f576544bba74d7a79c8d3c";

        // Per project (productGUID) and per machine: whether the window may open with the project
        // is a personal preference, not something to commit for the whole team.
        private static string ShowOnStartupPrefKey => "Virtuademy.SDK.Environments.Setup.ShowOnStartup." + PlayerSettings.productGUID;

        /// <summary>Whether the startup check may open the window. It still opens only when
        /// <see cref="FindStartupIssue"/> finds something.</summary>
        internal static bool ShowOnStartup
        {
            get => EditorPrefs.GetBool(ShowOnStartupPrefKey, true);
            set => EditorPrefs.SetBool(ShowOnStartupPrefKey, value);
        }

        private bool dataBindingsAdded;

        /// <summary>
        /// Why the project needs this window, or null when it does not: SDK-Environments missing,
        /// or one of the window's local checks failing. Left to the window: the Unity version,
        /// which is compared against the registry and so needs the network, and the interpreter,
        /// which a project may legitimately not have set up yet.
        /// </summary>
        internal static string FindStartupIssue()
        {
            UnityEditor.PackageManager.PackageInfo[] registered = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();

            if (!registered.Any(p => p.name == sdk_environments_package_name))
                return "Virtuademy-SDK-Environments is not installed.";

            if (!TryGetGitVersion(out _))
                return "git could not be run from the editor.";

            if (GetInstalledModules().ContainsValue(false))
                return "some editor modules (Android, WebGL, Windows) are missing.";

            RenderPipelineAsset renderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(render_pipeline_asset_guid));
            if (!IsRenderPipelineConfigured(renderPipeline) || !GetProjectSettingsStatus() || !GetMaxTextureSizeOverride())
                return "the project settings are not configured.";

            return null;
        }

        [MenuItem("Virtuademy/Setup/Setup project")]
        public static void ShowWindow()
        {
            CreatorKitSetupWindow wnd = GetWindow<CreatorKitSetupWindow>();
            wnd.titleContent = new GUIContent("Setup project");
        }

        public void CreateGUI()
        {
            // Each editor window contains a root VisualElement object
            root = rootVisualElement;

            // The UI below is new, so it has no bindings yet. The flag must be reset here: Unity
            // carries an EditorWindow's private fields across a domain reload, and every package
            // action ends in one — a surviving `true` left the rebuilt UI unbound, showing its
            // placeholders and every warning icon.
            dataBindingsAdded = false;

            // Instantiate UXML
            VisualElement labelFromUXML = m_VisualTreeAsset.Instantiate();
            // Fills the window, so the footer sits at the bottom rather than under the content.
            labelFromUXML.style.flexGrow = 1;
            root.Add(labelFromUXML);

            SetupHeader();

            Toggle showOnStartupToggle = root.Q<Toggle>("show-on-startup-toggle");
            showOnStartupToggle.SetValueWithoutNotify(ShowOnStartup);
            showOnStartupToggle.RegisterValueChangedCallback(evt => ShowOnStartup = evt.newValue);
            root.Q<Button>("load-error-retry-button").clicked += InitializeWindow;

            InitializeWindow();
        }

        /// <summary>The header needs no data, so it is wired before — and regardless of — the
        /// registry download.</summary>
        private void SetupHeader()
        {
            // Light lettering on the dark skin, dark lettering on the light one.
            string logoVariant = EditorGUIUtility.isProSkin ? "light" : "dark";
            Texture2D logo = AssetDatabase.LoadAssetAtPath<Texture2D>($"{package_root}/Editor/Setup/Icons/virtuademy-logo-{logoVariant}.png");
            VisualElement headerLogo = root.Q<VisualElement>("header-logo");
            if (logo != null)
            {
                headerLogo.style.backgroundImage = logo;
            }
            else
            {
                headerLogo.style.display = DisplayStyle.None;
            }
        }

        private void OnApplicationQuit()
        {
            SaveAsset(packageManagerConfig);
        }

        private void OnDestroy()
        {
            SaveAsset(packageManagerConfig);
        }

        private async void InitializeWindow()
        {
            if (!await LoadData())
            {
                return;
            }

            // Once per window: the bindings also subscribe click handlers, and a second pass
            // after a Retry or a Refresh would run every button's action twice.
            if (!dataBindingsAdded)
            {
                AddDataBindings();
                dataBindingsAdded = true;
            }
        }

        /// <summary>
        /// Loads the registry and runs the project checks. Returns false, with the reason shown
        /// in the window, when the version list cannot be obtained — everything below it is
        /// computed against that list, so there is nothing meaningful to show without it.
        /// </summary>
        private async Task<bool> LoadData()
        {
            isSetupping = true;
            try
            {
                string packageManagerAssetGuid = AssetDatabase.FindAssets("t:" + typeof(PackageManagerConfiguration).Name).ToList().FirstOrDefault();
                packageManagerConfig = AssetDatabase.LoadAssetAtPath<PackageManagerConfiguration>(AssetDatabase.GUIDToAssetPath(packageManagerAssetGuid));

                if (packageManagerConfig == null)
                {
                    EnsureFolderExists(settings_folder_path);

                    packageManagerConfig = CreateInstance<PackageManagerConfiguration>();
                    string settingsAssetPath = $"{settings_folder_path}/{setup_configuration_path}";
                    AssetDatabase.CreateAsset(packageManagerConfig, settingsAssetPath);
                    AssetDatabase.SaveAssets();
                }

                using HttpClient client = new();

                // The window used to call EnsureSuccessStatusCode here with nothing around it: an
                // offline editor got an exception inside an async void and a window frozen
                // half-built, with nothing on screen to say why.
                PackageRegistry[] registry;
                try
                {
                    string responseBody = await client.GetStringAsync(package_registry_path);
                    registry = JsonConvert.DeserializeObject<PackageRegistry[]>(responseBody);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    ShowLoadError("Could not download the list of Virtuademy versions: " + ex.Message +
                                  "\nCheck the internet connection (proxy and firewall included), then press Retry.");
                    UnityEngine.Debug.LogError($"[Setup] Could not load {package_registry_path}: {ex}");
                    return false;
                }

                if (registry == null || registry.Length == 0)
                {
                    ShowLoadError("The list of Virtuademy versions was downloaded but is empty. Please report it to the Virtuademy team.");
                    UnityEngine.Debug.LogError($"[Setup] {package_registry_path} holds no entries.");
                    return false;
                }

                packageManagerConfig.AllVersionsPackageRegistry = registry;

                if (packageManagerConfig.AvailableVersions.Count == 0)
                {
                    ShowLoadError("The list of Virtuademy versions holds no released version. Please report it to the Virtuademy team.");
                    UnityEngine.Debug.LogError($"[Setup] {package_registry_path} holds only prerelease entries.");
                    return false;
                }

                // Optional: the index only matters when an update runs with automatic resolution
                // on, so failing to get it must not take the rest of the window down with it.
                breakingChangesSolverDictionary = await LoadBreakingChangesIndex(client);

                packageManagerConfig.OnDisplayedVersionChanged.AddListener(InstantiatePackagesInPackageList);

                UpdateAvailableVersions();

                // A project with no recorded version is new: it starts on the default entry.
                //
                // A recorded version is NOT replaced when the list does not show it. It used to be
                // overwritten with the last visible entry whenever that happened — a prerelease
                // hidden by the toggle, or an entry since removed from the registry — so the
                // window claimed a version the project did not have, the installed and displayed
                // versions matched, and the update button that would have fixed it stayed off.
                if (string.IsNullOrEmpty(packageManagerConfig.CurrentInstallationVersion))
                {
                    packageManagerConfig.CurrentInstallationVersion = packageManagerConfig.AvailableVersions[^1];
                }
                else if (packageManagerConfig.CurrentVersion == null)
                {
                    UnityEngine.Debug.LogWarning($"[Setup] This project records version '{packageManagerConfig.CurrentInstallationVersion}', " +
                                                 "which the registry no longer lists. Select a version and press " +
                                                 "\"Update packages to selected version\" to move to it.");
                }

                if (string.IsNullOrEmpty(packageManagerConfig.DisplayedReflectisVersion) || !packageManagerConfig.AvailableVersions.Contains(packageManagerConfig.DisplayedReflectisVersion))
                {
                    packageManagerConfig.DisplayedReflectisVersion = packageManagerConfig.AvailableVersions.Contains(packageManagerConfig.CurrentInstallationVersion)
                        ? packageManagerConfig.CurrentInstallationVersion
                        : packageManagerConfig.AvailableVersions[^1];
                }
                previousInstallationVersion = packageManagerConfig.CurrentInstallationVersion;

                // Against the installed entry, or the selected one when the installed version is
                // no longer listed: that is the engine the update will need.
                PackageRegistry versionForEngineCheck = packageManagerConfig.CurrentVersion ?? packageManagerConfig.SelectedVersion;
                projectConfig.UnityVersionIsMatching = UnityVersion == versionForEngineCheck?.RequiredUnityVersion;
                packageManagerConfig.LastRefreshTime = DateTime.Now;

                CheckGitInstallation();
                CheckEditorModulesInstallation();
                CheckProjectSettings();
                CheckHybridCLRInstallation();
                CheckHybridCLRAssembly();

                GetInstalledPackages();
                SaveAsset(packageManagerConfig);

                HideLoadError();
                setupCompleted = true;
                return true;
            }
            finally
            {
                isSetupping = false;
            }
        }

        /// <summary>
        /// Downloads BreakingChangesSolverIndex.json. Its keys are written as
        /// <c>("2025.3", "2025.4")</c>; they become tuples of the two minor versions. Returns an
        /// empty index, with a warning, when the file cannot be read.
        /// </summary>
        private static async Task<Dictionary<(string, string), string>> LoadBreakingChangesIndex(HttpClient client)
        {
            Dictionary<(string, string), string> index = new();
            try
            {
                string body = await client.GetStringAsync(breaking_changes_solver_path);
                var dictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(body) ?? new();
                foreach (var kvp in dictionary)
                {
                    var key = kvp.Key.Trim('(', ')').Split(", ");
                    if (key.Length != 2)
                    {
                        UnityEngine.Debug.LogWarning($"[Setup] Ignoring malformed key '{kvp.Key}' in {breaking_changes_solver_path}.");
                        continue;
                    }
                    index[(key[0].Trim('"'), key[1].Trim('"'))] = kvp.Value;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                UnityEngine.Debug.LogWarning($"[Setup] Could not load {breaking_changes_solver_path}: {ex.Message}. " +
                                             "Automatic breaking-change resolution is unavailable until the window is refreshed.");
            }
            return index;
        }

        private void ShowLoadError(string message)
        {
            root.Q<Label>("load-error-text").text = message;
            root.Q<VisualElement>("load-error").style.display = DisplayStyle.Flex;
            // Both sections are computed against the registry: without it they would show
            // placeholders and buttons that act on an empty list.
            root.Q<VisualElement>("project-settings").style.display = DisplayStyle.None;
            root.Q<VisualElement>("package-manager").style.display = DisplayStyle.None;
        }

        private void HideLoadError()
        {
            root.Q<VisualElement>("load-error").style.display = DisplayStyle.None;
            root.Q<VisualElement>("project-settings").style.display = DisplayStyle.Flex;
            root.Q<VisualElement>("package-manager").style.display = DisplayStyle.Flex;
        }


        private void AddDataBindings()
        {
            #region Project settings section

            VisualElement projectSettingsSection = root.Q<VisualElement>("project-settings");
            projectSettingsSection.dataSource = projectConfig;

            List<(string, string)> settingIcons = new()
            {
                { ("project-settings-git-version-check", nameof(projectConfig.IsGitInstalled)) },
                { ("project-settings-unity-version-check", nameof(projectConfig.UnityVersionIsMatching)) },
                { ("project-settings-editor-modules-check", nameof(projectConfig.AllEditorModulesInstalled)) },
                { ("project-settings-urp-check", nameof(projectConfig.RenderPipelineURP)) },
                { ("project-settings-configuration-check", nameof(projectConfig.PlayerSettings)) },
                { ("project-settings-max-texture-size-check", nameof(projectConfig.MaxTextureSizeOverride)) },
                { ("Interpreter-settings-instance-check", nameof(projectConfig.IsHybridCLRInstalled)) },
                { ("Interpreter-settings-folder-check", nameof(projectConfig.HybridCLRAssemblyReady)) },
            };
            foreach (var entry in settingIcons)
            {
                VisualElement projectSettingsItemIcon = projectSettingsSection.Q<VisualElement>(entry.Item1);
                DataBinding styleBinding = new() { dataSourcePath = PropertyPath.FromName(entry.Item2) };
                styleBinding.sourceToUiConverters.AddConverter((ref bool value) =>
                {
                    projectSettingsItemIcon.RemoveFromClassList("settings-item-green-icon");
                    projectSettingsItemIcon.RemoveFromClassList("settings-item-red-icon");
                    projectSettingsItemIcon.AddToClassList(value ? "settings-item-green-icon" : "settings-item-red-icon");
                    return true;
                });
                // Find the binding that changes directly the class
                projectSettingsItemIcon.SetBinding(nameof(projectSettingsItemIcon.visible), styleBinding);
            }

            List<(string, string, Foldout)> warningIcons = new()
            {
                { ("git-installation-warning", nameof(projectConfig.IsGitInstalled), projectSettingsSection.Q<Foldout>("git-installation-foldout")) },
                { ("editor-configuration-warning", nameof(projectConfig.EditorConfigurationOk), projectSettingsSection.Q<Foldout>("editor-configuration-foldout")) },
                { ("project-settings-warning", nameof(projectConfig.ProjectSettingsOk), projectSettingsSection.Q<Foldout>("project-settings-foldout")) },
                // No entry for the interpreter: it is optional, so a project without it is not
                // flagged. Its two rows still say whether it is installed and ready.
            };
            foreach (var entry in warningIcons)
            {
                VisualElement warningIcon = projectSettingsSection.Q<VisualElement>(entry.Item1);
                DataBinding warningIconVisibilityBinding = new()
                {
                    dataSourcePath = PropertyPath.FromName(entry.Item2),
                    bindingMode = BindingMode.ToTarget
                };
                warningIconVisibilityBinding.sourceToUiConverters.AddConverter((ref bool value) => !value && !entry.Item3.value);
                warningIcon.SetBinding(nameof(warningIcon.visible), warningIconVisibilityBinding);
            }

            Label gitVersionLabel = projectSettingsSection.Q<Label>("git-version-label");
            DataBinding gitVersionLabelBinding = new() { dataSourcePath = PropertyPath.FromName(nameof(projectConfig.IsGitInstalled)), bindingMode = BindingMode.ToTarget };
            gitVersionLabelBinding.sourceToUiConverters.AddConverter((ref bool value) =>
                value ?
                    "Installed Git version: " :
                    "Git is not installed! Click \"Download\" button to download it from the official website.");
            gitVersionLabel.SetBinding(nameof(gitVersionLabel.text), gitVersionLabelBinding);

            Label gitVersionLabelValue = projectSettingsSection.Q<Label>("git-version-label-value");
            gitVersionLabelValue.SetBinding(nameof(gitVersionLabelValue.text), new DataBinding() { dataSourcePath = PropertyPath.FromName(nameof(projectConfig.GitVersion)) });

            Button gitDownloadButton = projectSettingsSection.Q<Button>("git-download-button");
            gitDownloadButton.clicked += () => Application.OpenURL("https://git-scm.com/downloads");
            DataBinding gitDownloadBinding = new() { dataSourcePath = PropertyPath.FromName(nameof(projectConfig.IsGitInstalled)) };
            gitDownloadBinding.sourceToUiConverters.AddConverter((ref bool value) => !value);
            gitDownloadButton.SetBinding(nameof(gitDownloadButton.enabledSelf), gitDownloadBinding);

            Label currentUnityVersionValue = projectSettingsSection.Q<Label>("editor-settings-unity-version-value");
            currentUnityVersionValue.text = UnityVersion;

            Label installedModules = projectSettingsSection.Q<Label>("installed-modules-label");
            DataBinding installedModulesBinding = new() { dataSourcePath = PropertyPath.FromName(nameof(projectConfig.InstalledModules)) };
            installedModulesBinding.sourceToUiConverters.AddConverter((ref Dictionary<string, bool> value) =>
                !value.Values.Contains(false) ?
                    "All editor modules are installed properly" :
                    $"The following modules are missing: {string.Join(", ", value.Where(x => !x.Value).Select(x => x.Key))}. Install them from Unity Hub."
            );
            installedModules.SetBinding(nameof(installedModules.text), installedModulesBinding);

            Button configureProjectSettingsButton = projectSettingsSection.Q<Button>("configure-project-settings-button");
            configureProjectSettingsButton.clicked += ConfigureProjectSettings;
            DataBinding configureProjectSettingsButtonBinding = new() { dataSourcePath = PropertyPath.FromName(nameof(projectConfig.ProjectSettingsOk)) };
            configureProjectSettingsButtonBinding.sourceToUiConverters.AddConverter((ref bool value) => !value);
            configureProjectSettingsButton.SetBinding(nameof(gitDownloadButton.enabledSelf), configureProjectSettingsButtonBinding);


            Button hybridCLRDownloadButton = projectSettingsSection.Q<Button>("configure-Interpreter-settings-button");
            hybridCLRDownloadButton.clicked += ConfigureInterpreterSettings;
            DataBinding interpreterDownloadBinding = new() { dataSourcePath = PropertyPath.FromName(nameof(projectConfig.InterpreterReady)) };
            interpreterDownloadBinding.sourceToUiConverters.AddConverter((ref bool value) => !value);
            hybridCLRDownloadButton.SetBinding(nameof(hybridCLRDownloadButton.enabledSelf), interpreterDownloadBinding);

            // Spell out what is missing. The row icons say "not ready", which is not actionable
            // on its own — the setupper already computes the reason and how to fix it.
            Label interpreterIssueLabel = projectSettingsSection.Q<Label>("Interpreter-issue-label");
            interpreterIssueLabel.SetBinding(nameof(interpreterIssueLabel.text), new DataBinding()
            {
                dataSourcePath = PropertyPath.FromName(nameof(projectConfig.InterpreterIssue)),
                bindingMode = BindingMode.ToTarget
            });


            #endregion

            #region Package manager section

            VisualElement packageManagerSection = root.Q<VisualElement>("package-manager");
            packageManagerSection.dataSource = packageManagerConfig;

            Label lastRefreshDateTimeLabel = packageManagerSection.Q<Label>("last-refresh-date-time");
            DataBinding lastRefreshDateTimeDataBinding = new()
            {
                dataSourcePath = PropertyPath.FromName(nameof(packageManagerConfig.LastRefreshTime)),
                bindingMode = BindingMode.ToTarget
            };
            lastRefreshDateTimeDataBinding.sourceToUiConverters.AddConverter((ref DateTime value) => value.ToString("MMM dd, HH:mm", CultureInfo.InvariantCulture));
            lastRefreshDateTimeLabel.SetBinding(nameof(lastRefreshDateTimeLabel.text), lastRefreshDateTimeDataBinding);

            Label currentReflectisVersionValue = packageManagerSection.Q<Label>("current-reflectis-version-value");
            currentReflectisVersionValue.SetBinding(nameof(currentReflectisVersionValue.text), new DataBinding()
            {
                dataSourcePath = PropertyPath.FromName(nameof(packageManagerConfig.CurrentInstallationVersionLabel)),
                bindingMode = BindingMode.ToTarget
            });

            Button refreshPackagesButton = packageManagerSection.Q<Button>("refresh-packages-button");
            refreshPackagesButton.clicked += SetupWindowData;

            Button reResolvePackagesButton = packageManagerSection.Q<Button>("reresolve-packages-button");
            reResolvePackagesButton.clicked += ReResolvePackages;

            InstantiatePackagesInPackageList();

            Button updatePackagesButton = packageManagerSection.Q<Button>("update-packages-button");
            updatePackagesButton.SetBinding(nameof(updatePackagesButton.enabledSelf), new DataBinding()
            {
                dataSourcePath = PropertyPath.FromName(nameof(packageManagerConfig.DisplayedAndInstalledVersionsAreDifferent)),
                bindingMode = BindingMode.TwoWay
            });
            updatePackagesButton.clicked += () => ShowAlertDialog("Warning", "Packages will be updated to the desired version", UpdatePackagesToSelectedVersion);

            DropdownField dropdown = packageManagerSection.Q<DropdownField>("reflectis-version-dropdown");
            dropdown.SetBinding(nameof(dropdown.choices), new DataBinding()
            {
                dataSourcePath = PropertyPath.FromName(nameof(packageManagerConfig.AvailableVersions)),
                bindingMode = BindingMode.TwoWay
            });
            dropdown.SetBinding(nameof(dropdown.value), new DataBinding()
            {
                dataSourcePath = PropertyPath.FromName(nameof(packageManagerConfig.DisplayedReflectisVersion)),
                bindingMode = BindingMode.TwoWay
            });

            Toggle showPrereleaseToggle = packageManagerSection.Q<Toggle>("show-prereleases-toggle");
            showPrereleaseToggle.SetBinding(nameof(showPrereleaseToggle.value), new DataBinding()
            {
                dataSourcePath = PropertyPath.FromName(nameof(packageManagerConfig.ShowPrereleases)),
                bindingMode = BindingMode.TwoWay
            });
            showPrereleaseToggle.RegisterValueChangedCallback(evt =>
            {
                // Written here as well as by the binding, so the list is filtered on the new value
                // whichever of the two runs first.
                packageManagerConfig.ShowPrereleases = evt.newValue;
                UpdateAvailableVersions();
            });

            Toggle resolveBreakingChangesAutomatically = packageManagerSection.Q<Toggle>("resolve-breaking-changes-toggle");
            resolveBreakingChangesAutomatically.SetBinding(nameof(resolveBreakingChangesAutomatically.value), new DataBinding()
            {
                dataSourcePath = PropertyPath.FromName(nameof(packageManagerConfig.ResolveBreakingChangesAutomatically)),
                bindingMode = BindingMode.TwoWay
            });

            VisualElement resolveBreakingChangesAutomaticallyWarning = packageManagerSection.Q<VisualElement>("resolve-breaking-changes-warning");
            resolveBreakingChangesAutomaticallyWarning.SetBinding(nameof(resolveBreakingChangesAutomaticallyWarning.visible), new DataBinding()
            {
                dataSourcePath = PropertyPath.FromName(nameof(packageManagerConfig.ResolveBreakingChangesAutomatically)),
                bindingMode = BindingMode.ToTarget
            });

            #endregion
        }

        private void InstantiatePackagesInPackageList()
        {
            ScrollView packagesListScroll = root.Q<ListView>("packages-list-view").Q<ScrollView>();
            packagesListScroll.Clear();

            for (int i = 0; i < packageManagerConfig.SelectedVersionVisiblePackages.Count(); i++)
            {
                VisualElement packageItem = packageItemAsset.Instantiate();
                packagesListScroll.Add(packageItem);
                packagesListScroll[i].dataSourcePath = PropertyPath.FromIndex(i);

                Foldout packageName = packagesListScroll[i].Q<Foldout>("package-item");
                packageName.text = $"<b>{packageManagerConfig.SelectedVersionVisiblePackages[i].DisplayName}</b> - {packageManagerConfig.SelectedVersionVisiblePackages[i].Version}";

                Label packageDescription = packagesListScroll[i].Q<Label>("package-description");
                packageDescription.text = packageManagerConfig.SelectedVersionVisiblePackages[i].Description;

                Label packageVersion = packagesListScroll[i].Q<Label>("package-url");
                packageVersion.text = $"<i><a href=\"{packageManagerConfig.SelectedVersionVisiblePackages[i].Url}\">{packageManagerConfig.SelectedVersionVisiblePackages[i].Url}</a></i>";
                packageVersion.RegisterCallback<ClickEvent>(evt => Application.OpenURL(packageManagerConfig.SelectedVersionVisiblePackages[i].Url));


                VisualElement dependenciesList = packagesListScroll[i].Q<VisualElement>("package-dependencies");
                dependenciesList.dataSource = packageManagerConfig.SelectedVersionDependenciesFullOrdered[i];

                for (int j = 0; j < packageManagerConfig.SelectedVersionDependenciesFullOrdered[i].Count(); j++)
                {
                    VisualElement packageDependency = packageDependencyAsset.Instantiate();
                    dependenciesList.Add(packageDependency);
                    packageDependency.dataSourcePath = PropertyPath.FromIndex(j);

                    Label dependencyText = packageDependency.Q<Label>("package-dependency-label");
                    dependencyText.SetBinding(nameof(dependencyText.text), new DataBinding()
                    {
                        dataSourcePath = PropertyPath.FromName(nameof(PackageDefinition.DisplayName)),
                        bindingMode = BindingMode.ToTarget
                    });

                    Label dependencyVersion = packageDependency.Q<Label>("package-dependency-version");
                    dependencyVersion.SetBinding(nameof(dependencyVersion.text), new DataBinding()
                    {
                        dataSourcePath = PropertyPath.FromName(nameof(PackageDefinition.Version)),
                        bindingMode = BindingMode.ToTarget
                    });
                }

                Button installPackageButton = packagesListScroll[i].Q<Button>("install-package-button");

                DataBinding installPackageButtonBinding = new() { bindingMode = BindingMode.ToTarget };
                installPackageButtonBinding.sourceToUiConverters.AddConverter((ref PackageDefinition package) =>
                {
                    string name = package.Name;
                    PackageDefinition installedPackage = packageManagerConfig.InstalledPackages.FirstOrDefault(x => x.Name == name);
                    return installedPackage != null ? (installedPackage.InstallationSource == EInstallationSource.Submodule ? "Embedded" : "Uninstall") : "Install";
                });
                installPackageButton.SetBinding(nameof(installPackageButton.text), installPackageButtonBinding);

                DataBinding installPackageButtonVisibilityBinding = new() { bindingMode = BindingMode.ToTarget };
                installPackageButtonVisibilityBinding.sourceToUiConverters.AddConverter((ref PackageDefinition package) =>
                {
                    string name = package.Name;
                    PackageDefinition installedPackage = packageManagerConfig.InstalledPackages.FirstOrDefault(x => x.Name == name);
                    return !((installedPackage != null && installedPackage.InstallationSource == EInstallationSource.Submodule)
                            || IsPackageInstalledAsDependency(package) || packageManagerConfig.DisplayedAndInstalledVersionsAreDifferent);
                });
                installPackageButton.SetBinding(nameof(installPackageButton.enabledSelf), installPackageButtonVisibilityBinding);

                PackageDefinition package = packageManagerConfig.SelectedVersionVisiblePackages[i];
                installPackageButton.clicked += () =>
                {
                    if (packageManagerConfig.InstalledPackages.Select(x => x.Name).Contains(package.Name))
                        UninstallPackageWithDependencies(package);
                    else
                        InstallPackageWithDependencies(package);
                };
            }
        }

        private async void SetupWindowData() => await LoadData();

        /// <summary>
        /// Moves the selection off a version the list no longer shows. This used to test for the
        /// literal name "develop", from before entries could declare <c>prerelease</c>: any other
        /// prerelease stayed selected after "Show pre-releases" was turned off. Asking the list
        /// covers every entry the toggle hides, whatever its name.
        /// </summary>
        private void UpdateAvailableVersions()
        {
            List<string> availableVersions = packageManagerConfig.AvailableVersions;
            if (availableVersions.Count == 0 || availableVersions.Contains(packageManagerConfig.DisplayedReflectisVersion))
            {
                return;
            }

            // Back to what the project has installed when the list still shows it, as LoadData does.
            packageManagerConfig.DisplayedReflectisVersion = availableVersions.Contains(packageManagerConfig.CurrentInstallationVersion)
                ? packageManagerConfig.CurrentInstallationVersion
                : availableVersions[^1];
        }

        #region Project settings

        private void CheckGitInstallation()
        {
            projectConfig.IsGitInstalled = TryGetGitVersion(out string gitVersion);
            if (projectConfig.IsGitInstalled)
            {
                projectConfig.GitVersion = gitVersion;
            }
        }

        private static bool TryGetGitVersion(out string version)
        {
            version = null;
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = "git",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using Process process = new() { StartInfo = startInfo };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    return false;
                }

                // Trimmed: `git --version` ends with a newline, and a Label holding one is two
                // lines tall. With align-items: center on the row, the visible line then sits
                // above the centre and the value reads as misaligned against its own caption.
                version = output.Trim();
                return true;
            }
            catch (Exception ex)
            {
                // Two things were wrong here. The flag was never assigned, so the UI reported
                // whatever it happened to hold — default(bool), i.e. "not installed" — for a value
                // nobody had computed. And a modal dialog on a check that runs when the window
                // opens blocks the editor to say something the author cannot act on.
                //
                // The distinction that matters: Process.Start throws Win32Exception when `git` is
                // not on THIS PROCESS's PATH, which is not the same as git being absent — an editor
                // launched from Unity Hub inherits an environment that a shell does not. Same red
                // icon, different fix, so say which one it is.
                UnityEngine.Debug.LogWarning($"[Setup] Could not run `git --version`: {ex.GetType().Name} - {ex.Message}. " +
                                             "If git works in a terminal, it is missing from the PATH this editor " +
                                             "process inherited — relaunch the editor from a shell where git resolves.");
                return false;
            }
        }

        private void CheckEditorModulesInstallation()
        {
            projectConfig.InstalledModules = GetInstalledModules();
            projectConfig.AllEditorModulesInstalled = !projectConfig.InstalledModules.Values.Contains(false);
        }

        private static Dictionary<string, bool> GetInstalledModules()
        {
            // Each target is asked about with its OWN group. The previous version looped over every
            // BuildTargetGroup and ASSIGNED inside the loop, so all three entries ended up holding
            // the answer for whichever group Enum.GetValues yielded last — paired with targets that
            // do not belong to it. IsBuildTargetSupported is false for every such pair, so the
            // check reported "editor modules missing" regardless of what was actually installed.
            return new()
            {
                { "Android", BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android) },
                { "WebGL", BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL) },
                { "Windows", BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows) },
            };
        }

        private void CheckProjectSettings()
        {
            projectConfig.RenderPipelineURP = GetURPConfigurationStatus();
            projectConfig.PlayerSettings = GetProjectSettingsStatus();
            projectConfig.MaxTextureSizeOverride = GetMaxTextureSizeOverride();
        }

        private void CheckHybridCLRInstallation()
        {
            // Avvia la richiesta (asincrona)
            _listRequest = Client.List(offlineMode: true, includeIndirectDependencies: false);
            EditorApplication.update += OnPackageListProgress;
        }

        private void OnPackageListProgress()
        {
            if (!_listRequest.IsCompleted) return;

            EditorApplication.update -= OnPackageListProgress;

            if (_listRequest.Status == StatusCode.Success)
            {
                bool trovato = false;
                foreach (var package in _listRequest.Result)
                {
                    if (package.name == "com.code-philosophy.hybridclr")
                    {
                        trovato = true;
                        break;
                    }
                }
                projectConfig.IsHybridCLRInstalled = trovato;
            }
            else
            {
                projectConfig.IsHybridCLRInstalled = false;
            }

            // The two flags feed the same InterpreterReady property, so re-evaluate the assembly
            // side now that the package side has an answer.
            CheckHybridCLRAssembly();
        }

        /// <summary>
        /// Asks HotUpdateSetupper whether this project's hot-update assembly is set up. The
        /// setupper owns the naming rule, so the window must not hardcode an assembly name: doing
        /// that is how the generic "HotUpdate" kept being approved, and two worlds carrying that
        /// same name shadow each other once the player loads both.
        /// </summary>
        private void CheckHybridCLRAssembly()
        {
            projectConfig.HybridCLRAssemblyReady = false;

            Type setupperType = FindSetupperType();
            if (setupperType == null)
            {
                // HybridCLR not installed: the setupper's assembly does not exist yet, so the
                // package row above is the one that explains it.
                projectConfig.InterpreterIssue = "The interpreter package is not installed yet.";
                return;
            }

            MethodInfo getIssue = setupperType.GetMethod("GetSetupIssue", BindingFlags.Public | BindingFlags.Static);
            if (getIssue == null)
            {
                projectConfig.InterpreterIssue =
                    "The Virtuademy-SDK-Environments package is older than this setup window (HotUpdateSetupper.GetSetupIssue is missing).";
                UnityEngine.Debug.LogWarning("[Setup] " + projectConfig.InterpreterIssue);
                return;
            }

            string issue = getIssue.Invoke(null, null) as string;

            projectConfig.HybridCLRAssemblyReady = issue == null;
            // Shown verbatim under the checks: the icon says "not ready", this says what to fix.
            projectConfig.InterpreterIssue = issue ?? string.Empty;
        }

        private bool GetURPConfigurationStatus() => IsRenderPipelineConfigured(renderPipelineAsset);

        private static bool IsRenderPipelineConfigured(RenderPipelineAsset asset)
        {
            return asset != null && GraphicsSettings.defaultRenderPipeline == asset && QualitySettings.renderPipeline == asset;
        }

        private static bool GetProjectSettingsStatus()
        {
            return PlayerSettings.GetApiCompatibilityLevel(NamedBuildTarget.Standalone) == ApiCompatibilityLevel.NET_Unity_4_8;
        }

        private static bool GetMaxTextureSizeOverride()
        {
            return EditorUserBuildSettings.overrideMaxTextureSize == 1024;
        }

        private void ConfigureProjectSettings()
        {
            // URP configuration
            GraphicsSettings.defaultRenderPipeline = renderPipelineAsset;
            QualitySettings.renderPipeline = renderPipelineAsset;

            // Project settings configuration
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Unity_4_8);

            // Max texture size override
            EditorUserBuildSettings.overrideMaxTextureSize = 1024;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            CheckProjectSettings();
        }

        private void ConfigureInterpreterSettings()
        {
            // Ground truth for "HybridCLR is usable" is the setupper type itself: it only exists
            // once the package is installed AND the assembly gated behind HYBRIDCLR_INSTALLED has
            // compiled. Branching on IsHybridCLRInstalled instead would race with the async
            // package listing on the first click after the window opens.
            if (FindSetupperType() != null)
            {
                // Configure right away, then refresh what the window shows. This is also the
                // recovery path when the pending flag was lost (editor restarted mid-install) or
                // when the project was configured before per-project assembly naming existed.
                InvokeSetupperViaReflection();
                CheckHybridCLRAssembly();
                return;
            }

            if (projectConfig.IsHybridCLRInstalled)
            {
                UnityEngine.Debug.LogError("[Setup] HybridCLR is installed but HotUpdateSetupper did not " +
                                           "compile. Fix the compilation errors in the Virtuademy-SDK-Environments " +
                                           "package and press the button again.");
                return;
            }

            // The setupper's assembly does not exist yet, so it cannot be called here: raise the
            // flag and let it pick the job up on the domain reload that follows the import.
            SessionState.SetBool(pending_hybridclr_setup_key, true);

            _addRequest = Client.Add(hybridclr_package_url);
            EditorApplication.update += OnAddProgress;
        }

        private void OnAddProgress()
        {
            if (!_addRequest.IsCompleted) return;
            EditorApplication.update -= OnAddProgress;

            if (_addRequest.Status == StatusCode.Success)
            {
                UnityEngine.Debug.Log("[Setup] HybridCLR installed. Configuring automatically after the recompilation...");
                return;
            }

            // Nothing is going to recompile, so the pending flag would linger for the rest of the
            // session and fire a setup on an unrelated domain reload.
            SessionState.SetBool(pending_hybridclr_setup_key, false);
            UnityEngine.Debug.LogError($"[Setup] Install failed: {_addRequest.Error?.message}");
        }

        private void InvokeSetupperViaReflection()
        {
            Type setupperType = FindSetupperType();
            if (setupperType == null)
            {
                UnityEngine.Debug.LogError("[Setup] HotUpdateSetupper not found (HybridCLR not ready?).");
                return;
            }

            setupperType.GetMethod("Setup", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }

        /// <summary>The setupper ships with the Virtuademy-SDK-Environments package but only
        /// compiles once HybridCLR is installed, so it can only be reached by reflection.</summary>
        private static Type FindSetupperType()
            => AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .FirstOrDefault(t => t.Name == "HotUpdateSetupper");

        #endregion

        #region Package management

        private void InstallPackageWithDependencies(PackageDefinition package)
        {
            string[] dependenciesToInstall = packageManagerConfig.SelectedVersion.FullDependencies[package.Name];

            foreach (var dependency in dependenciesToInstall.Select(x => packageManagerConfig.SelectedVersionPackageDictionary[x]).Append(package))
            {
                if (!packageManagerConfig.InstalledPackages.Contains(dependency))
                {
                    packageManagerConfig.InstalledPackages.Add(dependency);
                }
            }

            EditorUtility.SetDirty(packageManagerConfig);
            AssetDatabase.SaveAssetIfDirty(packageManagerConfig);

            InstallPackages(dependenciesToInstall.Append(package.Name).Select(x => packageManagerConfig.SelectedVersionPackageDictionary[x]).ToList());

            Client.Resolve();
        }

        /// <summary>
        /// Drops this project's Virtuademy git packages from <c>packages-lock.json</c> and asks UPM
        /// to resolve again, so a branch that has moved is actually picked up.
        ///
        /// The lock pins each git dependency to a resolved COMMIT, not to the ref the manifest
        /// asked for. Nothing else moves it: not the refresh button, which re-reads the registry;
        /// not <c>Client.Resolve</c> on its own, which honours the lock; not deleting
        /// Library/PackageCache, which re-clones the same locked hash. Dropping the entry is what
        /// lets UPM look the branch up again.
        ///
        /// Only entries whose source is git are touched — a registry package's pin is a version,
        /// and re-resolving those is not what anyone pressing this wants.
        /// </summary>
        private void ReResolvePackages()
        {
            string lockFilePath = Path.Combine(Application.dataPath, "../Packages/packages-lock.json");
            if (!File.Exists(lockFilePath))
            {
                UnityEngine.Debug.LogWarning("[Setup] There is no packages-lock.json to re-resolve from.");
                return;
            }

            JObject lockObj = JObject.Parse(File.ReadAllText(lockFilePath));
            if (lockObj["dependencies"] is not JObject dependencies)
            {
                UnityEngine.Debug.LogWarning("[Setup] packages-lock.json has no dependencies section.");
                return;
            }

            List<string> unpinned = new();
            foreach (JProperty entry in dependencies.Properties().ToList())
            {
                if (!IsOurPackage(entry.Name) || (string)entry.Value["source"] != "git")
                {
                    continue;
                }

                entry.Remove();
                unpinned.Add(entry.Name);
            }

            if (unpinned.Count == 0)
            {
                UnityEngine.Debug.Log("[Setup] No Virtuademy git packages are pinned — nothing to re-resolve.");
                return;
            }

            File.WriteAllText(lockFilePath, lockObj.ToString());
            UnityEngine.Debug.Log($"[Setup] Unpinned {unpinned.Count} git package(s), re-resolving. " +
                                  "Each one is re-cloned, so give it a moment:\n  " +
                                  string.Join("\n  ", unpinned));
            Client.Resolve();
        }

        private void InstallPackages(List<PackageDefinition> toInstall)
        {
            string manifestFilePath = Path.Combine(Application.dataPath, "../Packages/manifest.json");
            string manifestJson = File.ReadAllText(manifestFilePath);
            JObject manifestObj = JObject.Parse(manifestJson);

            JObject dependencies = (JObject)manifestObj["dependencies"];
            foreach (PackageDefinition p in toInstall)
            {
                dependencies[p.Name] = $"{p.Url}#{p.Version}";
            }

            File.WriteAllText(manifestFilePath, manifestObj.ToString());
        }

        private void UninstallPackageWithDependencies(PackageDefinition toUninstall)
        {
            packageManagerConfig.InstalledPackages.Remove(packageManagerConfig.InstalledPackages.FirstOrDefault(x => x.Name == toUninstall.Name));
            EditorUtility.SetDirty(packageManagerConfig);
            AssetDatabase.SaveAssetIfDirty(packageManagerConfig);

            List<PackageDefinition> packagesToRemove = new() { toUninstall };

            foreach (var hiddenPackage in packageManagerConfig.InstalledPackages.Where(x => x.Visibility == EPackageVisibility.Hidden))
            {
                if (packageManagerConfig.SelectedVersion.ReverseDependencies[hiddenPackage.Name].Intersect(packageManagerConfig.InstalledPackages.Select(x => x.Name)).Count() == 0)
                {
                    packagesToRemove.Add(packageManagerConfig.SelectedVersionPackageDictionary[hiddenPackage.Name]);
                }
            }

            foreach (PackageDefinition package in packagesToRemove)
            {
                packageManagerConfig.InstalledPackages.Remove(package);
            }

            UninstallPackages(packagesToRemove.Select(x => x.Name).ToList());

            Client.Resolve();
        }

        private void UninstallPackages(List<string> toRemove)
        {
            string manifestFilePath = Path.Combine(Application.dataPath, "../Packages/manifest.json");
            string manifestJson = File.ReadAllText(manifestFilePath);
            JObject manifestObj = JObject.Parse(manifestJson);

            JObject dependencies = (JObject)manifestObj["dependencies"];

            foreach (string pName in toRemove)
            {
                dependencies.Remove(pName);
            }

            File.WriteAllText(manifestFilePath, manifestObj.ToString());
        }

        private async void GetInstalledPackages()
        {
            // offlineMode, because the question is local: what did this project resolve. Answering
            // it used to require the registry, which made a local question fail whenever the
            // network did. Everything read below — name, version, source — is available offline.
            ListRequest listRequest = Client.List(offlineMode: true, includeIndirectDependencies: false);
            while (!listRequest.IsCompleted)
                await Task.Yield();

            if (listRequest.Status != StatusCode.Success)
            {
                // Result is null on anything other than Success. Observed 2026-09-03: the network
                // dropped, the request failed, and dereferencing Result here threw — and because
                // this is async void nothing could catch it. The exception went to the
                // synchronisation context and took the window's state with it, leaving a window
                // that looked fine and behaved as though the project were empty.
                // Qualified because this file has `using System.Diagnostics`, which makes a bare
                // Debug ambiguous — the rest of the file qualifies it for the same reason.
                UnityEngine.Debug.LogWarning("[Setup] Could not read the installed package list: " +
                                 $"{listRequest.Error?.message ?? listRequest.Status.ToString()}. " +
                                 "Reopen the window to retry — the installed-packages view is stale until then.");
                return;
            }

            List<PackageDefinition> installedPackages = new();

            foreach (var package in listRequest.Result)
            {
                if (IsOurPackage(package.name) && !packages_to_exclude.Contains(package.name))
                {
                    PackageDefinition installedPackage = new()
                    {
                        Name = package.name,
                        Version = package.version,
                        InstallationSource = package.source == PackageSource.Git ? EInstallationSource.Git : EInstallationSource.Submodule
                    };
                    installedPackages.Add(installedPackage);
                }
            }

            foreach (var package in installedPackages)
            {
                if (!packageManagerConfig.InstalledPackages.Select(x => x.Name).Contains(package.Name))
                {
                    packageManagerConfig.InstalledPackages.Add(package);

                    if (packageManagerConfig.SelectedVersionPackageDictionary.TryGetValue(package.Name, out PackageDefinition packageInfo))
                    {
                        package.DisplayName = packageInfo.DisplayName;
                        package.Description = packageInfo.Description;
                        package.Url = packageInfo.Url;
                        package.Visibility = packageInfo.Visibility;
                    }
                }
            }
        }

        private JObject ReadPackagesFromFile(string path)
        {
            string filePath = Path.Combine(Application.dataPath, path);
            string packagesJson = File.ReadAllText(filePath);
            JObject packagesObj = JObject.Parse(packagesJson);

            JObject packagesDependencies = (JObject)packagesObj["dependencies"];
            return packagesDependencies;
        }

        private bool IsPackageInstalledAsDependency(PackageDefinition package)
        {
            bool isDependency = false;

            // The installed version's graph, or the selected one's when the installed version is
            // no longer listed — this used to dereference a null CurrentVersion in that case.
            PackageRegistry graph = packageManagerConfig.CurrentVersion ?? packageManagerConfig.SelectedVersion;

            if (graph != null && graph.ReverseDependencies.TryGetValue(package.Name, out List<string> deps))
            {
                foreach (string dep in deps)
                {
                    if (packageManagerConfig.InstalledPackages.Select(x => x.Name).Contains(dep))
                    {
                        isDependency = true;
                        break;
                    }
                }
            }
            else
            {
                isDependency = false;
            }

            return isDependency;
        }

        private async void UpdatePackagesToSelectedVersion()
        {
            if (packageManagerConfig.CurrentInstallationVersion != packageManagerConfig.DisplayedReflectisVersion)
            {
                previousInstallationVersion = packageManagerConfig.CurrentInstallationVersion;

                // Nothing below reads the installed version's registry entry: the update works
                // from what is installed and what the target lists, so it also moves a project
                // off a version the registry no longer has. (An unused lookup of that entry used
                // to sit here and threw NullReferenceException in exactly that case.)
                List<string> removedPackages = new();

                List<PackageDefinition> installedPackagesCopy = new(packageManagerConfig.InstalledPackages);

                IEnumerable<PackageDefinition> newPackages = packageManagerConfig.SelectedVersionPackageDictionary.Values.Where(x => !installedPackagesCopy.Exists(y => y.Name == x.Name));
                //add new packages
                foreach (PackageDefinition package in newPackages)
                {
                    //if (package.InstallationSource == EInstallationSource.Submodule)
                    //{
                    //    UnityEngine.Debug.LogWarning($"Skipping submodule package {package.Name}. Align the submodule to the correct commit.");
                    //    continue;
                    //}
                    packageManagerConfig.InstalledPackages.Add(packageManagerConfig.SelectedVersionPackageDictionary[package.Name]);
                    InstallPackages(new() { packageManagerConfig.SelectedVersionPackageDictionary[package.Name] });
                }

                foreach (PackageDefinition package in installedPackagesCopy)
                {
                    //if (package.InstallationSource == EInstallationSource.Submodule)
                    //{
                    //    UnityEngine.Debug.LogWarning($"Skipping submodule package {package.Name}. Align the submodule to the correct commit.");
                    //    continue;
                    //}
                    //overlapping packages
                    if (packageManagerConfig.SelectedVersionPackageDictionary.TryGetValue(package.Name, out PackageDefinition target))
                    {
                        // The URL counts too: the same id can move repository across versions
                        // (the 2026-09 renames did), and the manifest must follow it.
                        if (package.Version != target.Version || package.Url != target.Url)
                        {
                            packageManagerConfig.InstalledPackages.Remove(package);
                            UninstallPackages(new() { package.Name });

                            packageManagerConfig.InstalledPackages.Add(packageManagerConfig.SelectedVersionPackageDictionary[package.Name]);
                            InstallPackages(new() { packageManagerConfig.SelectedVersionPackageDictionary[package.Name] });
                            //UnityEngine.Debug.Log($"Updated package {package.Name} from version {package.Version} to version {packageManagerConfig.SelectedVersionPackageDictionary[package.Name].Version}");
                        }
                    }
                    else
                    {
                        //old packages
                        removedPackages.Add(package.Name);

                        packageManagerConfig.InstalledPackages.Remove(package);
                        UninstallPackages(new() { package.Name });
                    }
                }

                // One dialog for all of them. It used to be one per package, each overwriting the
                // last in the same popup and adding its own handler to the same button.
                if (removedPackages.Count > 0)
                {
                    UnityEngine.Debug.Log("[Setup] Removed, not part of the selected version:\n  " + string.Join("\n  ", removedPackages));
                    ShowAlertDialog("Warning", $"{removedPackages.Count} package(s) are not part of the selected Virtuademy version " +
                                               "and have been uninstalled. The Console lists them.", null);
                }


                if (packageManagerConfig.ResolveBreakingChangesAutomatically)
                {
                    ResolveBreakingChanges();
                }
                packageManagerConfig.CurrentInstallationVersion = packageManagerConfig.DisplayedReflectisVersion;

                Client.Resolve();
                await LoadData();
            }
        }

        private async void ResolveBreakingChanges()
        {
            EnsureFolderExists(utilities_folder_path);

            string prev = FilterPatch(packageManagerConfig.CurrentInstallationVersion);
            string cur = FilterPatch(packageManagerConfig.DisplayedReflectisVersion);
            (string, string) routineKey = (prev, cur);

            // Indexing the dictionary directly threw KeyNotFoundException for every step with no
            // published script — inside an async void, after the manifest had been rewritten.
            // A step without a script is the normal case (only 2025.3 -> 2025.4 has one); the
            // later ones ship as menu entries under Virtuademy/Update routines.
            if (breakingChangesSolverDictionary == null || !breakingChangesSolverDictionary.TryGetValue(routineKey, out string routinePath))
            {
                UnityEngine.Debug.LogWarning($"[Setup] No breaking-change script is published for {prev} -> {cur}, so none was downloaded. " +
                                             "If the release notes name an update routine, run it from Virtuademy > Update routines.");
                return;
            }

            string responseBody;
            try
            {
                using HttpClient client = new();
                responseBody = await client.GetStringAsync(routinePath);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                UnityEngine.Debug.LogError($"[Setup] Could not download the breaking-change script for {prev} -> {cur} from {routinePath}: {ex.Message}");
                return;
            }

            string assetPath = $"{utilities_folder_path}/{routinePath.Split('/').Last()}";
            StreamWriter writer = new(assetPath, false);
            writer.WriteLine(responseBody);
            writer.Close();
            AssetDatabase.ImportAsset(assetPath);
        }

        public static void ResolveBreakingChangesCallback(string type)
        {
            Type myClassType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(assembly => assembly.GetTypes())
                    .FirstOrDefault(t => t.Name == type);

            try
            {
                myClassType.GetMethod("SolveBreakingChanges", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            }
            catch
            {
                UnityEngine.Debug.LogError($"Failed to load breaking changes solver: {myClassType}");
            }
        }

        private string FilterPatch(string input)
        {
            int index = input.LastIndexOf('.');
            if (index != -1)
                return input[..index];

            return input;
        }

        private void SaveAsset(UnityEngine.Object asset)
        {
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
        }

        #endregion

        private void ShowAlertDialog(string title, string message, UnityAction callback)
        {
            var dialog = root.Q<VisualElement>("popup-dialog-container");

            var titleLabel = dialog.Q<Label>("dialog-title");
            titleLabel.text = title;

            var messageLabel = dialog.Q<Label>("dialog-message");
            messageLabel.text = message;

            var confirmButton = dialog.Q<Button>("dialog-confirm-button");

            Action onClick = null;
            onClick = () =>
            {
                dialog.style.display = DisplayStyle.None;
                callback?.Invoke();
                confirmButton.clicked -= onClick;
            };

            confirmButton.clicked += onClick;

            var backButton = dialog.Q<Button>("dialog-back-button");

            Action onBackClick = null;
            onBackClick = () =>
            {
                dialog.style.display = DisplayStyle.None;
                backButton.clicked -= onBackClick;
            };
            backButton.clicked += onBackClick;

            if (callback == null)
            {
                backButton.style.display = DisplayStyle.None;
                confirmButton.text = "OK";
            }
            else
            {
                backButton.style.display = DisplayStyle.Flex;
                confirmButton.text = "Continue";
            }

            dialog.style.display = DisplayStyle.Flex;
        }

        private void EnsureFolderExists(string folderPath)
        {
            string[] folders = folderPath.Split('/');
            string currentPath = "";

            foreach (string folder in folders)
            {
                currentPath = Path.Combine(currentPath, folder);
                if (!AssetDatabase.IsValidFolder(currentPath))
                {
                    AssetDatabase.CreateFolder(Path.GetDirectoryName(currentPath), Path.GetFileName(currentPath));
                }
            }
        }
    }

}