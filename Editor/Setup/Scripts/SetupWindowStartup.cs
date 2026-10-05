using UnityEditor;

using UnityEngine;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// Opens the setup window when the project is opened, unless the creator turned it off with
    /// the toggle in the window's header.
    ///
    /// [InitializeOnLoad] runs on every domain reload — each script change, each package
    /// resolve — so a session flag limits it to the first one: the project opening, or this
    /// package arriving in a project that was already open.
    /// </summary>
    [InitializeOnLoad]
    internal static class SetupWindowStartup
    {
        private const string opened_this_session_key = "Virtuademy.SDK.Environments.Setup.OpenedThisSession";

        static SetupWindowStartup()
        {
            // Command-line builds and the Env-Test CLI run in batch mode: there is nobody to show
            // a window to.
            if (Application.isBatchMode || SessionState.GetBool(opened_this_session_key, false))
            {
                return;
            }

            SessionState.SetBool(opened_this_session_key, true);

            if (!CreatorKitSetupWindow.ShowOnStartup)
            {
                return;
            }

            // Deferred: on project open this runs before the editor has restored its layout.
            EditorApplication.delayCall += CreatorKitSetupWindow.ShowWindow;
        }
    }
}
