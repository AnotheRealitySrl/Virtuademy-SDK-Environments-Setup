using System;

using Unity.Properties;

using UnityEngine;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    public enum EPackageVisibility
    {
        Visible,
        Hidden
    }

    public enum EInstallationSource
    {
        Git,
        Submodule
    }

    [Serializable]
    [Newtonsoft.Json.JsonObject(Newtonsoft.Json.MemberSerialization.Fields)]
    public class PackageDefinition : IEquatable<PackageDefinition>
    {
        [SerializeField] private string name;
        [SerializeField] private string displayName;
        [SerializeField] private string description;
        [SerializeField] private string version;
        [SerializeField] private string url;
        [SerializeField] private EPackageVisibility visibility;
        [SerializeField] private EInstallationSource installationSource = EInstallationSource.Git;

        public string Name { get => name; set => name = value; }
        [CreateProperty] public string DisplayName { get => displayName; set => displayName = value; }
        [CreateProperty] public string Description { get => description; set => description = value; }
        [CreateProperty] public string Version { get => version; set => version = value; }
        [CreateProperty] public string Url { get => url; set => url = value; }
        public EPackageVisibility Visibility { get => visibility; set => visibility = value; }
        public EInstallationSource InstallationSource { get => installationSource; set => installationSource = value; }

        public bool Equals(PackageDefinition other)
        {
            if (other == null) return false;

            bool namesEqual = string.IsNullOrEmpty(name) ? string.IsNullOrEmpty(other.name) : name.Equals(other.name, StringComparison.Ordinal);
            bool versionsEqual = string.IsNullOrEmpty(version) ? string.IsNullOrEmpty(other.version) : version.Equals(other.version, StringComparison.Ordinal);

            return namesEqual && versionsEqual;
        }

        public override bool Equals(object obj)
        {
            if (obj is PackageDefinition other)
            {
                return Equals(other);
            }
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(name, version);
        }
    }
}
