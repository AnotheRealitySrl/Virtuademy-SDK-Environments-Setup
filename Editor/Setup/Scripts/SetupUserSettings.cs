using Newtonsoft.Json.Linq;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UnityEngine;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// The setup package's choices that belong to one person in one project: they live in
    /// <c>UserSettings/Virtuademy/setup.json</c>, next to the project. <c>UserSettings/</c> is
    /// per user and not versioned (Unity's own default ignore files list it), so nothing here is
    /// shared with the team, and the choice does not follow the person to another project, which
    /// <c>EditorPrefs</c> would do for one machine's every project.
    ///
    /// The file is read and rewritten as a whole, keeping the keys this class does not know, so
    /// that a later key written by another version of the package survives.
    ///
    /// What it holds: whether the Readme and the wizard's introduction are shown when they would
    /// open by themselves, the connection action that
    /// waits for the SDK's connection support to be installed, and the packages that action
    /// installed (see <see cref="PreparedPackages"/>).
    /// </summary>
    internal static class SetupUserSettings
    {
        private const string hide_readme_key = "hideReadmeOnStartup";
        private const string hide_introduction_key = "hideIntroduction";
        private const string pending_connection_key = "pendingConnection";
        private const string prepared_packages_key = "preparedPackages";

        /// <summary>A connection action the Connection page could not do yet, because the SDK's connection support
        /// (SDK-Core) was not installed: <see cref="Action"/> is <c>choose-file</c> (ask for the configuration file, then
        /// import it) or <c>login</c> (the connection <see cref="Key"/>). It goes on after the domain reload that installing
        /// SDK-Core causes, and expires, so a forgotten one never opens a browser by surprise.</summary>
        internal sealed class PendingConnection
        {
            public string Action;
            public string Key;
            public string CreatedUtc;

            internal DateTime Created => DateTime.TryParse(CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed) ? parsed : DateTime.MinValue;
        }

        internal static string FilePath => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName, "UserSettings", "Virtuademy", "setup.json");

        /// <summary>Whether the person asked not to see the Readme when the project opens. Off
        /// (the Readme shows) when the file is missing or cannot be read.</summary>
        internal static bool HideReadmeOnStartup
        {
            get
            {
                // Only a JSON true counts: a hand-edited value ("abc", null, 1) means "not set".
                JToken token = Read(forWrite: false)[hide_readme_key];
                return token != null && token.Type == JTokenType.Boolean && token.Value<bool>();
            }
            set
            {
                JObject settings = Read(forWrite: true);
                settings[hide_readme_key] = value;
                Write(settings);
            }
        }

        /// <summary>Whether the person asked not to see the wizard's introduction when a project that is not set up
        /// opens the window. Off (shown) when the file is missing or cannot be read.</summary>
        internal static bool HideIntroduction
        {
            get
            {
                JToken token = Read(forWrite: false)[hide_introduction_key];
                return token != null && token.Type == JTokenType.Boolean && token.Value<bool>();
            }
            set
            {
                JObject settings = Read(forWrite: true);
                settings[hide_introduction_key] = value;
                Write(settings);
            }
        }

        /// <summary>The connection action waiting for the SDK's connection support, or null.</summary>
        internal static PendingConnection Pending
        {
            get
            {
                try
                {
                    return Read(forWrite: false)[pending_connection_key]?.ToObject<PendingConnection>();
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    return null;
                }
            }
            set
            {
                JObject settings = Read(forWrite: true);
                if (value == null)
                    settings.Remove(pending_connection_key);
                else
                    settings[pending_connection_key] = JObject.FromObject(value);
                Write(settings);
            }
        }

        /// <summary>
        /// The packages the Connection page installed to get the SDK's connection support before the SDK itself
        /// (SDK-Core and what it needs). The setup run does not take them for a project at another version: without
        /// the list, <c>sdk</c> would refuse a project that "already has Virtuademy packages".
        /// </summary>
        internal static IReadOnlyCollection<string> PreparedPackages
        {
            get
            {
                JArray names = Read(forWrite: false)[prepared_packages_key] as JArray;
                return names == null ? new string[0] : names.Select(name => (string)name).Where(name => !string.IsNullOrEmpty(name)).ToArray();
            }
            set
            {
                JObject settings = Read(forWrite: true);
                if (value == null || value.Count == 0)
                    settings.Remove(prepared_packages_key);
                else
                    settings[prepared_packages_key] = new JArray(value);
                Write(settings);
            }
        }

        private static JObject Read(bool forWrite)
        {
            try
            {
                return File.Exists(FilePath) ? JObject.Parse(File.ReadAllText(FilePath)) : new JObject();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
            {
                UnityEngine.Debug.LogWarning($"[Setup] Could not read {FilePath}: {ex.Message}. " +
                                             (forWrite ? "The file will be replaced by the setting you just changed." : "Its settings are ignored."));
                return new JObject();
            }
        }

        private static void Write(JObject settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, settings.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                UnityEngine.Debug.LogWarning($"[Setup] Could not write {FilePath}: {ex.Message}.");
            }
        }
    }
}
