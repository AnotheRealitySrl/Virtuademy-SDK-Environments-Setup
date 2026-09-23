using System;
using System.Collections.Generic;
using System.Linq;

using Unity.Properties;

using UnityEngine;
using UnityEngine.Events;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    [CreateAssetMenu(fileName = "CreatorKitSetupConfiguration", menuName = "Virtuademy/Setup/SetupConfiguration")]
    public class PackageManagerConfiguration : ScriptableObject
    {
        public PackageRegistry[] AllVersionsPackageRegistry { get; set; } = new PackageRegistry[0];

        public Dictionary<string, PackageDefinition> SelectedVersionPackageDictionary => SelectedVersion.PackageDictionary;

        [CreateProperty] public PackageDefinition[] SelectedVersionVisiblePackages => SelectedVersion.Packages.Where(x => x.Visibility == EPackageVisibility.Visible).ToArray();

        [CreateProperty]
        public List<string> AvailableVersions => AllVersionsPackageRegistry
                .Where(x => ShowPrereleases || !x.Prerelease)
                .Select(x => x.ReflectisVersion)
                .ToList();

        [CreateProperty] public List<PackageDefinition[]> SelectedVersionDependenciesFullOrdered => SelectedVersion.FullDependencies.Select(x => x.Value.Select(x => SelectedVersionPackageDictionary[x]).ToArray()).ToList();

        public PackageRegistry SelectedVersion => AllVersionsPackageRegistry.FirstOrDefault(x => x.ReflectisVersion == DisplayedReflectisVersion);


        [SerializeField] private List<PackageDefinition> installedPackages = new();
        [CreateProperty] public List<PackageDefinition> InstalledPackages { get => installedPackages; set => installedPackages = value; }

        [SerializeField] private string currentInstallationVersion;
        [CreateProperty]
        public string CurrentInstallationVersion
        {
            get => currentInstallationVersion;
            set
            {
                currentInstallationVersion = value;
            }
        }

        public PackageRegistry CurrentVersion => AllVersionsPackageRegistry.FirstOrDefault(x => x.ReflectisVersion == CurrentInstallationVersion);

        public UnityEvent OnDisplayedVersionChanged { get; } = new();

        private string displayedReflectisVersion;
        [CreateProperty]
        public string DisplayedReflectisVersion
        {
            get => displayedReflectisVersion;
            set
            {
                displayedReflectisVersion = value;
                OnDisplayedVersionChanged.Invoke();
            }
        }

        [CreateProperty] public bool DisplayedAndInstalledVersionsAreDifferent => CurrentInstallationVersion != DisplayedReflectisVersion;


        [SerializeField] private bool resolveBreakingChangesAutomatically;
        [CreateProperty] public bool ResolveBreakingChangesAutomatically { get => resolveBreakingChangesAutomatically; set => resolveBreakingChangesAutomatically = value; }

        [SerializeField] private bool showPrereleases;
        [CreateProperty] public bool ShowPrereleases { get => showPrereleases; set => showPrereleases = value; }

        [CreateProperty] public DateTime LastRefreshTime { get; set; }

    }

}
