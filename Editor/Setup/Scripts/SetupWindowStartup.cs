using UnityEditor;

using UnityEngine;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// Opens a window when the project is opened. When the project needs setup:
    /// Virtuademy-SDK-Environments is not installed, or one of the window's local checks fails
    /// (see <see cref="CreatorKitSetupWindow.FindStartupIssue"/>), it is the setup window: a project
    /// that is not set up opens its Introduction (or the Connection page, when the person turned the
    /// Introduction off), a project that is set up but has something to fix opens its status page.
    /// The toggle at the foot of the window turns that off. When nothing is wrong it is the
    /// <see cref="ReadmeWindow"/>, unless the person
    /// asked not to see it in this project (<see cref="SetupUserSettings.HideReadmeOnStartup"/>).
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
            // Nothing could open, so do not run the checks: they start git, and a person who turned
            // both windows off would still get its warning when git is missing.
            if (!CreatorKitSetupWindow.ShowOnStartup && SetupUserSettings.HideReadmeOnStartup)
            {
                return;
            }

            string issue = CreatorKitSetupWindow.FindStartupIssue();
            if (issue == null)
            {
                // Nothing to set up: orient the creator instead, unless they said not to.
                if (!SetupUserSettings.HideReadmeOnStartup)
                {
                    ReadmeWindow.ShowWindow();
                }

                return;
            }

            // The footer toggle of the setup window turns its startup opening off. The Readme is
            // not shown instead: a project that needs setup is not ready to be read about.
            if (!CreatorKitSetupWindow.ShowOnStartup)
            {
                return;
            }

            Debug.Log($"[Setup] Opening the setup window: {issue}");
            CreatorKitSetupWindow.ShowWindow();
        }
    }
}
