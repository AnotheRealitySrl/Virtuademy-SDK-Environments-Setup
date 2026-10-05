using UnityEditor;

using UnityEngine;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// Opens the setup window when the project is opened, but only when the project needs it:
    /// Virtuademy-SDK-Environments is not installed, or one of the window's local checks fails
    /// (see <see cref="CreatorKitSetupWindow.FindStartupIssue"/>). A configured project opens
    /// without it, and the toggle at the foot of the window turns the check off altogether.
    ///
    /// [InitializeOnLoad] runs on every domain reload — each script change, each package
    /// resolve — so a session flag limits it to the first one: the project opening, or this
    /// package arriving in a project that was already open. Closing the window therefore keeps
    /// it closed until the next editor session, even while something is still missing.
    /// </summary>
    [InitializeOnLoad]
    internal static class SetupWindowStartup
    {
        private const string checked_this_session_key = "Virtuademy.SDK.Environments.Setup.CheckedThisSession";

        static SetupWindowStartup()
        {
            // Command-line builds and the Env-Test CLI run in batch mode: there is nobody to show
            // a window to.
            if (Application.isBatchMode || SessionState.GetBool(checked_this_session_key, false))
            {
                return;
            }

            SessionState.SetBool(checked_this_session_key, true);

            // Deferred: on project open this runs before the editor has restored its layout and
            // before the asset database is fully available to the checks.
            EditorApplication.delayCall += OpenIfNeeded;
        }

        private static void OpenIfNeeded()
        {
            // The footer toggle of the window turns the whole startup check off.
            if (!CreatorKitSetupWindow.ShowOnStartup)
            {
                return;
            }

            string issue = CreatorKitSetupWindow.FindStartupIssue();
            if (issue == null)
            {
                return;
            }

            Debug.Log($"[Setup] Opening the setup window: {issue}");
            CreatorKitSetupWindow.ShowWindow();
        }
    }
}
