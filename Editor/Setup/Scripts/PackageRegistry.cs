using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    [Serializable]
    [Newtonsoft.Json.JsonObject(Newtonsoft.Json.MemberSerialization.Fields)]
    public class PackageRegistry
    {
        [SerializeField] private string reflectisVersion;
        [SerializeField] private string requiredUnityVersion;
        [SerializeField] private bool prerelease;
        [SerializeField] private PackageDefinition[] packages;
        [SerializeField] private Dictionary<string, string[]> dependencies;

        public string ReflectisVersion => reflectisVersion;
        public string RequiredUnityVersion => requiredUnityVersion;
        public PackageDefinition[] Packages => packages;
        public Dictionary<string, string[]> Dependencies => dependencies ??= new();

        /// <summary>
        /// Kept out of the version list unless the window is showing prereleases, so an entry can
        /// point a tester at an in-flight branch without appearing to creators.
        ///
        /// The fallback on the name preserves the behaviour that was hardcoded in the window
        /// before this field existed: registry files written without it keep hiding "develop"
        /// without having to declare anything.
        /// </summary>
        public bool Prerelease => prerelease || reflectisVersion == "develop";

        public Dictionary<string, PackageDefinition> PackageDictionary => Packages.ToDictionary(x => x.Name);

        /// <summary>
        /// Keyed on EVERY package, not only on the ones that declare dependencies. The window
        /// indexes this by the package the user picked, so a leaf package — one that needs nothing
        /// — used to throw <see cref="KeyNotFoundException"/> the moment it was selected, which
        /// made it impossible to install.
        /// </summary>
        public Dictionary<string, string[]> FullDependencies => Packages.ToDictionary(
                package => package.Name,
                package => FindAllDependencies(package).ToArray()
            );

        public List<string> FindAllDependencies(PackageDefinition package)
        {
            List<string> resolved = new();
            Collect(package, PackageDictionary, resolved, new HashSet<string>());
            return resolved;
        }

        /// <summary>
        /// Depth-first, so a dependency is listed before whoever needs it — the window installs in
        /// this order. Each package is expanded once: without the visited set, the first cycle in
        /// the registry recurses until the stack gives out, and the registry is a hand-edited file
        /// with no review to catch one.
        /// </summary>
        private void Collect(PackageDefinition package, Dictionary<string, PackageDefinition> byName,
                             List<string> resolved, HashSet<string> visited)
        {
            if (!visited.Add(package.Name))
            {
                return;
            }

            if (!Dependencies.TryGetValue(package.Name, out string[] packageDependencies))
            {
                return;
            }

            foreach (string dependency in packageDependencies)
            {
                if (!byName.TryGetValue(dependency, out PackageDefinition dependencyPackage))
                {
                    // A typo in the registry is a realistic failure: one hand-edited file, no
                    // review, no schema. Name both sides and keep going — an install the creator
                    // can see is short beats an exception thrown inside the setup window.
                    Debug.LogError($"[Setup] Package '{package.Name}' declares a dependency on " +
                                   $"'{dependency}', which is not in the package list of registry entry " +
                                   $"'{reflectisVersion}'. Skipping it — the install will be incomplete.");
                    continue;
                }

                Collect(dependencyPackage, byName, resolved, visited);

                if (!resolved.Contains(dependency))
                {
                    resolved.Add(dependency);
                }
            }
        }


        public Dictionary<string, List<string>> ReverseDependencies => InvertDictionary(FullDependencies);



        private Dictionary<string, List<string>> InvertDictionary(Dictionary<string, string[]> dictionary)
        {
            // Seeded with every package so the map answers for all of them. The uninstall path
            // asks "who still needs this?" about each installed hidden package, and one that
            // nothing depends on has no key of its own to be found under.
            var invertedDictionary = Packages.ToDictionary(x => x.Name, _ => new List<string>());

            foreach (var kvp in dictionary)
            {
                foreach (var value in kvp.Value)
                {
                    if (!invertedDictionary.ContainsKey(value))
                    {
                        invertedDictionary[value] = new List<string>();
                    }
                    invertedDictionary[value].Add(kvp.Key);
                }
            }

            return invertedDictionary;
        }
    }
}
