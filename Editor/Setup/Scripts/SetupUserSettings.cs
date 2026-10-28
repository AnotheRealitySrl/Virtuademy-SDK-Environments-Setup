using Newtonsoft.Json.Linq;

using System;
using System.IO;

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
    /// </summary>
    internal static class SetupUserSettings
    {
        private const string hide_readme_key = "hideReadmeOnStartup";

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
