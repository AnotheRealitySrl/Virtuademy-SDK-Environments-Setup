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

            // Instantiate UXML
            VisualElement labelFromUXML = m_VisualTreeAsset.Instantiate();
            root.Add(labelFromUXML);

            InitializeWindow();
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
            await LoadData();
            AddDataBindings();
        }

        private async Task LoadData()
        {
            isSetupping = true;

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
            HttpResponseMessage response = await client.GetAsync(package_registry_path);
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();
            packageManagerConfig.AllVersionsPackageRegistry = JsonConvert.DeserializeObject<PackageRegistry[]>(responseBody);

            HttpResponseMessage routineResponse = await client.GetAsync(breaking_changes_solver_path);
            routineResponse.EnsureSuccessStatusCode();
            string routineResponseBody = await routineResponse.Content.ReadAsStringAsync();

            // Deserialize into a Dictionary<string, string>
            var dictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(routineResponseBody);
            // Convert the keys into tuples
            breakingChangesSolverDictionary = new Dictionary<(string, string), string>();
            foreach (var kvp in dictionary)
            {
                // Parse the key into a tuple
                var key = kvp.Key.Trim('(', ')').Split(", ");
                var tupleKey = (key[0].Trim('"'), key[1].Trim('"'));
                breakingChangesSolverDictionary[tupleKey] = kvp.Value;
            }

            packageManagerConfig.OnDisplayedVersionChanged.AddListener(InstantiatePackagesInPackageList);

            UpdateAvailableVersions();

            //Get reflectis version and update list of packages
            if (string.IsNullOrEmpty(packageManagerConfig.CurrentInstallationVersion) || !packageManagerConfig.AvailableVersions.Contains(packageManagerConfig.CurrentInstallationVersion))
            {
                packageManagerConfig.CurrentInstallationVersion = packageManagerConfig.AvailableVersions[^1];
            }
            if (string.IsNullOrEmpty(packageManagerConfig.DisplayedReflectisVersion) || !packageManagerConfig.AvailableVersions.Contains(packageManagerConfig.DisplayedReflectisVersion))
            {
                packageManagerConfig.DisplayedReflectisVersion = !string.IsNullOrEmpty(packageManagerConfig.CurrentInstallationVersion) ? packageManagerConfig.CurrentInstallationVersion : packageManagerConfig.AvailableVersions[^1];
            }
            previousInstallationVersion = packageManagerConfig.CurrentInstallationVersion;


            projectConfig.UnityVersionIsMatching = UnityVersion == packageManagerConfig.AllVersionsPackageRegistry.FirstOrDefault(x => x.ReflectisVersion == packageManagerConfig.CurrentInstallationVersion).RequiredUnityVersion;
            packageManagerConfig.LastRefreshTime = DateTime.Now;

            CheckGitInstallation();
            CheckEditorModulesInstallation();
            CheckProjectSettings();
            CheckHybridCLRInstallation();
            CheckHybridCLRAssembly();

            GetInstalledPackages();
            SaveAsset(packageManagerConfig);

            setupCompleted = true;
            isSetupping = false;
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
                { ("Interpreter-settings-warning", nameof(projectConfig.InterpreterReady), projectSettingsSection.Q<Foldout>("Interpreter-settings-foldout")) }
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
                dataSourcePath = PropertyPath.FromName(nameof(packageManagerConfig.CurrentInstallationVersion)),
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
            showPrereleaseToggle.RegisterValueChangedCallback(evt => UpdateAvailableVersions());

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

        private void UpdateAvailableVersions()
        {
            if (packageManagerConfig.DisplayedReflectisVersion == "develop" && !packageManagerConfig.ShowPrereleases)
            {
                packageManagerConfig.DisplayedReflectisVersion = packageManagerConfig.AvailableVersions[^1];
            }
        }

        #region Project settings

        private void CheckGitInstallation()
        {
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

                if (process.ExitCode == 0)
                {
                    // Trimmed: `git --version` ends with a newline, and a Label holding one is two
                    // lines tall. With align-items: center on the row, the visible line then sits
                    // above the centre and the value reads as misaligned against its own caption.
                    projectConfig.GitVersion = output.Trim();
                    projectConfig.IsGitInstalled = true;
                }
                else
                {
                    projectConfig.IsGitInstalled = false;
                }
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
                projectConfig.IsGitInstalled = false;
                UnityEngine.Debug.LogWarning($"[Setup] Could not run `git --version`: {ex.GetType().Name} - {ex.Message}. " +
                                             "If git works in a terminal, it is missing from the PATH this editor " +
                                             "process inherited — relaunch the editor from a shell where git resolves.");
            }
        }

        private void CheckEditorModulesInstallation()
        {
            // Each target is asked about with its OWN group. The previous version looped over every
            // BuildTargetGroup and ASSIGNED inside the loop, so all three entries ended up holding
            // the answer for whichever group Enum.GetValues yielded last — paired with targets that
            // do not belong to it. IsBuildTargetSupported is false for every such pair, so the
            // check reported "editor modules missing" regardless of what was actually installed.
            projectConfig.InstalledModules["Android"] = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);
            projectConfig.InstalledModules["Windows"] = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows);
            projectConfig.InstalledModules["WebGL"] = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL);
            projectConfig.AllEditorModulesInstalled = !projectConfig.InstalledModules.Values.Contains(false);
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

        private bool GetURPConfigurationStatus()
        {
            return GraphicsSettings.defaultRenderPipeline == renderPipelineAsset && QualitySettings.renderPipeline == renderPipelineAsset;
        }

        private bool GetProjectSettingsStatus()
        {
            return PlayerSettings.GetApiCompatibilityLevel(NamedBuildTarget.Standalone) == ApiCompatibilityLevel.NET_Unity_4_8;
        }

        private bool GetMaxTextureSizeOverride()
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

            if (packageManagerConfig.CurrentVersion.ReverseDependencies.TryGetValue(package.Name, out List<string> deps))
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

                Dictionary<string, PackageDefinition> packages = packageManagerConfig.AllVersionsPackageRegistry
                    .FirstOrDefault(x => x.ReflectisVersion == packageManagerConfig.CurrentInstallationVersion).Packages
                    .ToDictionary(x => x.Name, y => y);

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
                    if (packageManagerConfig.SelectedVersionPackageDictionary.ContainsKey(package.Name))
                    {
                        if (package.Version != packageManagerConfig.SelectedVersionPackageDictionary[package.Name].Version)
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
                        if (!IsPackageInstalledAsDependency(package))
                        {
                            ShowAlertDialog("Warning", $"The package \n{package.Name}\n is not available in the selected Virtuademy version\n and has been uninstalled.", null);
                        }

                        packageManagerConfig.InstalledPackages.Remove(package);
                        UninstallPackages(new() { package.Name });
                        //If the package is not available in the selected version and is not a dependency, show a warning
                    }
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

            string routinePath = breakingChangesSolverDictionary[routineKey];

            using HttpClient client = new();
            HttpResponseMessage response = await client.GetAsync(routinePath);
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();

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