using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using UnityEditor;

using UnityEngine;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>One way the SDK offers a window, as a menu path, with what to do there.</summary>
    internal sealed class SdkWindowLink
    {
        internal string MenuPath { get; }
        internal string Name { get; }
        /// <summary>What the person does in that window; empty when the link is only for opening it.</summary>
        internal string Steps { get; }

        internal SdkWindowLink(string menuPath, string name, string steps = "")
        {
            MenuPath = menuPath;
            Name = name;
            Steps = steps;
        }
    }

    /// <summary>
    /// The SDK's own windows, reached the only way this package can: by menu path. This package
    /// installs the SDK, so it cannot reference it, and an SDK that is not installed yet (or not
    /// compiled yet) has no menu at all. Each window has had a different menu path in the SDK
    /// versions the installer still serves, so the candidates are listed newest first and the first
    /// one that exists wins.
    /// </summary>
    internal static class SdkWindowLinks
    {
        /// <summary>The documentation site. Version-independent, so it does not go stale.</summary>
        internal const string documentation_root_url = "https://docs.virtuademy.io";

        /// <summary>
        /// The SDK section's first page. The site publishes one tree per version
        /// (<c>/docs/&lt;version&gt;/...</c>) and has no address for "the latest", so this names the
        /// newest version whose pages were checked; a frozen version stays on the site, so the link
        /// keeps working, and it is to be raised when a newer one is published.
        /// </summary>
        internal const string documentation_sdk_url = "https://docs.virtuademy.io/docs/2026.6/SDK/intro";

        private const string back_office_file_steps =
            "In the Back Office, open a World you may upload environments to and press Download SDK Environments Config. ";

        // Where a person imports the configuration file from the Back Office and logs in.
        //  - 2026.7.0 on: Publish Environments, one connection at a time.
        //  - before it: Available Tenants (named Show available tenants before 2026-10-07).
        //  - before that: the Addressables management window, under three roots over time.
        private static readonly SdkWindowLink[] connection_windows =
        {
            new("Virtuademy/Publish Environments", "Publish Environments",
                back_office_file_steps + "Then, in Publish Environments, press Import configuration file... and choose that file, " +
                "and press Log in on each connection you will publish to."),
            new("Virtuademy/Available Tenants", "Available Tenants",
                "Import the configuration file you downloaded from the Back Office, then log in."),
            new("Virtuademy/Show available tenants", "Available Tenants",
                "Import the configuration file you downloaded from the Back Office, then log in."),
            new("Virtuademy/Addressables management", "Addressables management",
                "Log in from this window, with the configuration file you downloaded from the Back Office."),
            new("Virtuademy Worlds/Creator Kit/Core/Addressables management", "Addressables management",
                "Log in from this window, with the configuration file you downloaded from the Back Office."),
            new("Reflectis Worlds/Creator Kit/Core/Addressables management", "Addressables management",
                "Log in from this window, with the configuration file you downloaded from the Back Office."),
        };

        // Where a person builds the scenes and deploys them to a world.
        private static readonly SdkWindowLink[] publish_windows =
        {
            new("Virtuademy/Publish Environments", "Publish Environments"),
            new("Virtuademy/Addressables management", "Addressables management"),
            new("Virtuademy Worlds/Creator Kit/Core/Addressables management", "Addressables management"),
            new("Reflectis Worlds/Creator Kit/Core/Addressables management", "Addressables management"),
        };

        private static HashSet<string> menuPaths;

        /// <summary>The window to import the configuration file and log in, or null when the
        /// installed SDK (if any) has none.</summary>
        internal static SdkWindowLink FindConnectionWindow() => connection_windows.FirstOrDefault(Exists);

        /// <summary>The window to build and publish, or null when the installed SDK (if any) has none.</summary>
        internal static SdkWindowLink FindPublishWindow() => publish_windows.FirstOrDefault(Exists);

        /// <summary>Opens <paramref name="link"/>; false, with a warning, when the menu entry
        /// does not run (it exists but is disabled, or is gone).</summary>
        internal static bool Open(SdkWindowLink link)
        {
            if (link != null && EditorApplication.ExecuteMenuItem(link.MenuPath))
                return true;

            UnityEngine.Debug.LogWarning($"[Setup] Could not open {link?.Name ?? "the window"}: " +
                                         $"the menu entry {link?.MenuPath} is missing or disabled.");
            return false;
        }

        private static bool Exists(SdkWindowLink link) => MenuPaths().Contains(link.MenuPath);

        // Every menu entry the editor's assemblies declare with [MenuItem]. Built once per domain:
        // a package that arrives later brings a domain reload, which resets it. An entry added
        // by Menu.AddMenuItem at runtime is not seen, and the SDK declares its own with the attribute.
        private static HashSet<string> MenuPaths()
        {
            if (menuPaths != null)
                return menuPaths;

            menuPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<MenuItem>())
            {
                foreach (MenuItem attribute in method.GetCustomAttributes<MenuItem>(false))
                {
                    if (!attribute.validate)
                        menuPaths.Add(WithoutShortcut(attribute.menuItem));
                }
            }

            return menuPaths;
        }

        // "Virtuademy/Thing %g": the shortcut after the last space is not part of the path.
        private static string WithoutShortcut(string menuItem)
        {
            int space = menuItem.LastIndexOf(' ');
            return space > 0 && space < menuItem.Length - 1 && "%#&_".IndexOf(menuItem[space + 1]) >= 0
                ? menuItem[..space]
                : menuItem;
        }
    }
}
