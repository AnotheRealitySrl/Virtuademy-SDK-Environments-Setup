using System.Collections.Generic;
using System.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.UIElements;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// The Project status page: the result of a run, and the home of a project that is set up. It says whether the project is ready or how
    /// many things need attention. A card, laid out like the Summary's choices (the name in bold, the value beside it), shows the Virtuademy
    /// version (Change version), the packages (Reload packages), how scripts are written (Add C# scripts or Remove C# scripts, both confirmed
    /// on the Summary page) and the connections (Manage).
    /// Under it the checks, each with a Fix: only those that fail, unless development mode shows them all. A Fix is a run of <see cref="SetupRun"/>
    /// limited to the step that makes the thing (<see cref="SetupRun.Start"/> with <c>onlySteps</c>), so it is shown on the Run page like any other.
    /// When the run ended with the editor restart waiting, the page offers Restart now.
    ///
    /// Everything is read from the project when the page shows and every two seconds after; nothing is cached across them.
    /// </summary>
    public partial class CreatorKitSetupWindow
    {
        private const double status_refresh_seconds = 2;

        // Serialized: "Reload packages" ends in a domain reload when something moved, and the sentence has to survive it.
        [SerializeField] private string packagesNote;

        private Label statusHeadline;
        private VisualElement statusRestartBanner;
        private InfoLine statusVersionLine;
        private InfoLine statusPackagesLine;
        private InfoLine statusScriptingLine;
        private InfoLine statusConnectionsLine;
        private Label statusChecksHeading;
        private RowView statusComputerRow;
        private RowView statusSettingsRow;
        private RowView statusAddressablesRow;
        private RowView statusTextMeshRow;
        private RowView statusInterpreterRow;
        private RowView statusCompileRow;
        private double statusReadAt = -100;
        private bool packagesChecking;

        #region Building

        private VisualElement BuildStatusPage()
        {
            VisualElement page = NewPage("wizard-status");
            statusHeadline = MakeLabel(string.Empty, "wizard-title");
            page.Add(statusHeadline);

            statusRestartBanner = new VisualElement();
            statusRestartBanner.AddToClassList("wizard-banner");
            statusRestartBanner.Add(MakeLabel("Restart the editor once to turn on the input settings.", "wizard-text"));
            VisualElement restartButtons = new();
            restartButtons.AddToClassList("wizard-buttons");
            restartButtons.Add(MakeButton("Restart now", SetupRun.RestartEditorNow, "wizard-button", "wizard-primary"));
            statusRestartBanner.Add(restartButtons);
            page.Add(statusRestartBanner);

            page.Add(MakeLabel("Your project", "wizard-heading"));
            VisualElement card = new();
            card.AddToClassList("wizard-choice-card");
            statusVersionLine = new InfoLine("Virtuademy version");
            card.Add(statusVersionLine.Root);
            statusScriptingLine = new InfoLine("Scripting");
            card.Add(statusScriptingLine.Root);
            statusConnectionsLine = new InfoLine("Connections");
            card.Add(statusConnectionsLine.Root);
            statusPackagesLine = new InfoLine("Packages");
            card.Add(statusPackagesLine.Root);
            page.Add(card);

            statusChecksHeading = MakeLabel("Checks", "wizard-heading");
            page.Add(statusChecksHeading);
            statusComputerRow = AddRow(page, withIcon: true);
            statusSettingsRow = AddRow(page, withIcon: true);
            statusAddressablesRow = AddRow(page, withIcon: true);
            statusTextMeshRow = AddRow(page, withIcon: true);
            statusInterpreterRow = AddRow(page, withIcon: true);
            statusCompileRow = AddRow(page, withIcon: true);

            return page;
        }

        private static RowView AddRow(VisualElement page, bool withIcon)
        {
            RowView row = new(withIcon);
            page.Add(row.Root);
            return row;
        }

        /// <summary>One line of the status card, laid out like the Summary's choices: the name in bold; beside it the value, a grey note,
        /// and the line's action.</summary>
        private sealed class InfoLine
        {
            internal readonly VisualElement Root = new();
            private readonly Label value;
            private readonly Label note;
            private readonly VisualElement buttons = new();
            private readonly Button button = new();
            private System.Action onClick;

            internal InfoLine(string name)
            {
                Root.AddToClassList("wizard-choice");
                Root.Add(MakeLabel(name, "wizard-choice__name"));

                VisualElement body = new();
                body.AddToClassList("wizard-choice__body");
                value = MakeLabel(string.Empty, "wizard-choice__value");
                body.Add(value);
                note = MakeLabel(string.Empty, "wizard-choice__note");
                body.Add(note);

                buttons.AddToClassList("wizard-buttons");
                button.AddToClassList("wizard-button");
                button.clicked += () => onClick?.Invoke();
                buttons.Add(button);
                body.Add(buttons);
                Root.Add(body);
            }

            internal void Set(string valueText, RowState state, string noteText, string buttonText = null, bool buttonEnabled = true, System.Action click = null)
            {
                value.text = valueText ?? string.Empty;
                value.EnableInClassList("wizard-bad", state == RowState.Bad);
                value.EnableInClassList("wizard-warn", state == RowState.Warn);

                note.text = noteText ?? string.Empty;
                SetShown(note, !string.IsNullOrEmpty(noteText));
                note.EnableInClassList("wizard-warn", state == RowState.Warn);

                button.text = buttonText ?? string.Empty;
                button.SetEnabled(buttonEnabled);
                SetShown(buttons, !string.IsNullOrEmpty(buttonText));
                onClick = click;
            }
        }

        #endregion

        #region Refreshing

        private void RefreshStatusPage(bool force)
        {
            if (statusHeadline == null || packageManagerConfig == null || !dataLoaded)
                return;

            double now = EditorApplication.timeSinceStartup;
            if (!force && now - statusReadAt < status_refresh_seconds)
                return;

            statusReadAt = now;
            if (force || now - connectionsReadAt > connections_refresh_seconds)
                ReadConnections();

            UpdateSdkSnapshot();
            string installed = InstalledVersion();
            bool sdk = installed != null;
            bool busy = SetupRun.IsRunning || compiling || importing;
            int attention = 0;

            bool development = DevelopmentMode;

            // Virtuademy version.
            if (!sdk)
            {
                attention++;
                statusVersionLine.Set("Not installed", RowState.Bad, "The SDK is not in this project yet.", "Set up", !busy, StartSetupFromStatus);
            }
            else
            {
                PackageRegistry entry = packageManagerConfig.CurrentVersion;
                RowState state = RowState.None;
                string note = null;
                if (entry == null)
                {
                    state = RowState.Warn;
                    note = "No longer in the list of versions. Change to one that is.";
                }
                else if (entry.RequiredUnityVersion != UnityVersion)
                {
                    state = RowState.Warn;
                    note = $"Needs Unity {entry.RequiredUnityVersion}; this editor is {UnityVersion}.";
                }

                statusVersionLine.Set(installed + (entry != null && entry.Prerelease ? " (development)" : string.Empty), state, note, "Change version", !busy, ChangeVersion);
            }

            // Scripting: either way through the Summary, which says what the change does (and asks before C# scripts stop running).
            bool interpreterInstalled = SetupSteps.InterpreterInManifest();
            PackageRegistry installedEntry = packageManagerConfig.CurrentVersion;
            bool canAddInterpreter = sdk && !interpreterInstalled && installedEntry?.Interpreter != null;
            string scriptingButton = !sdk ? null : interpreterInstalled ? "Remove C# scripts" : canAddInterpreter ? "Add C# scripts" : null;
            statusScriptingLine.Set(interpreterInstalled ? scripting_choices[1] : scripting_choices[0], RowState.None,
                !interpreterInstalled && sdk && installedEntry != null && installedEntry.Interpreter == null ? $"Virtuademy {installed} has no interpreter for C#." : null,
                scriptingButton, !busy && (interpreterInstalled || projectConfig.IsGitInstalled), () => ChangeScripting(!interpreterInstalled));

            // Connections.
            int loggedIn = connections.Count(connection => connection.LoggedIn);
            statusConnectionsLine.Set(
                connections.Count == 0 ? "None yet" : connections.Count == loggedIn ? $"{connections.Count}, all logged in" : $"{connections.Count}, {loggedIn} logged in",
                RowState.None,
                connections.Count == 0 ? "Publishing needs one; you can connect later." : null,
                "Manage", true, ManageConnections);

            // Packages: what the last Reload packages found, else when they are updated.
            statusPackagesLine.Set(
                !string.IsNullOrEmpty(packagesNote) ? packagesNote : PackageAutoUpdate.Enabled ? "Updated when the project opens" : "Not updated automatically",
                RowState.None,
                "Reload packages looks for new commits now.",
                "Reload packages", sdk && !busy && !packagesChecking, ReloadPackages);

            // Checks: a creator sees only the ones that fail, each with its Fix; development mode shows them all.
            bool computer = projectConfig.IsGitInstalled && projectConfig.AllEditorModulesInstalled;
            if (!computer)
                attention++;
            SetShown(statusComputerRow.Root, development || !computer);
            statusComputerRow.Set(computer ? RowState.Ok : RowState.Bad, "This computer",
                computer
                    ? "Git and the editor build modules are there."
                    : !projectConfig.IsGitInstalled
                        ? "Git is not installed. Install it from git-scm.com, then press Check again on the Choices page."
                        : $"Missing build modules: {string.Join(", ", projectConfig.InstalledModules.Where(module => !module.Value).Select(module => module.Key))}. Install them from Unity Hub.");

            List<string> settingsOff = new();
            if (!IsRenderPipelineConfigured(LoadDefaultRenderPipelineAsset()))
                settingsOff.Add("URP");
            if (!GetProjectSettingsStatus())
                settingsOff.Add("API compatibility level");
            if (!GetInputHandlingStatus())
                settingsOff.Add("Active Input Handling");
            if (!GetMaxTextureSizeOverride())
                settingsOff.Add("max texture size");
            if (settingsOff.Count > 0)
                attention++;
            SetShown(statusSettingsRow.Root, development || settingsOff.Count > 0);
            statusSettingsRow.Set(settingsOff.Count == 0 ? RowState.Ok : RowState.Bad, "Project settings",
                settingsOff.Count == 0
                    ? "URP, .NET Framework 4.8, Active Input Handling Both, max texture size 1024."
                    : $"Not set: {string.Join(", ", settingsOff)}.",
                settingsOff.Count == 0 ? null : "Fix", sdk && !busy, FixProjectSettings);

            bool? addressables = SetupSteps.ReadAddressablesConfigured();
            if (addressables == false)
                attention++;
            SetShown(statusAddressablesRow.Root, development || addressables == false);
            statusAddressablesRow.Set(addressables == true ? RowState.Ok : addressables == false ? RowState.Bad : RowState.None, "Addressables",
                addressables == true
                    ? "The SDK's profile and a single default group."
                    : addressables == false
                        ? "Not configured: the SDK expects its profile and a single default group."
                        : "Nothing to check: this SDK version has no Addressables configuration.",
                addressables == false ? "Fix" : null, sdk && !busy, FixAddressables);

            bool tmpPresent = SetupSteps.IsTmpPresent();
            bool tmpMissing = SetupSteps.TmpEssentialsMissing();
            if (tmpMissing)
                attention++;
            SetShown(statusTextMeshRow.Root, tmpPresent && (development || tmpMissing));
            statusTextMeshRow.Set(tmpMissing ? RowState.Bad : RowState.Ok, "TextMesh Pro essentials",
                tmpMissing ? "The Essential Resources are missing: the SDK's text renders magenta." : "The Essential Resources are in the project.",
                tmpMissing ? "Fix" : null, sdk && !busy, FixTextMesh);

            bool interpreterBad = interpreterInstalled && projectConfig.InterpreterNeedsFix;
            if (interpreterBad)
                attention++;
            SetShown(statusInterpreterRow.Root, interpreterInstalled && (development || interpreterBad));
            statusInterpreterRow.Set(interpreterBad ? RowState.Bad : RowState.Ok, "Interpreter",
                interpreterBad ? (string.IsNullOrEmpty(projectConfig.InterpreterIssue) ? "Installed, but not ready to build." : projectConfig.InterpreterIssue) : "Ready to build.",
                interpreterBad ? "Fix" : null, sdk && !busy, FixInterpreter);

            bool compileFailed = EditorUtility.scriptCompilationFailed;
            if (compileFailed)
                attention++;
            SetShown(statusCompileRow.Root, compileFailed);
            statusCompileRow.Set(RowState.Bad, "Scripts", "Unity could not compile the project's scripts. The Console has the errors.", "Open the Console", true, OpenConsole);

            // The heading goes with the rows: "Checks" in development mode, "Needs attention" when only failing ones show.
            bool anyCheck = development || !computer || settingsOff.Count > 0 || addressables == false || tmpMissing || interpreterBad || compileFailed;
            statusChecksHeading.text = development ? "Checks" : "Needs attention";
            SetShown(statusChecksHeading, anyCheck);

            // Headline and restart.
            bool restart = SetupRun.RestartPending;
            SetShown(statusRestartBanner, restart);
            statusHeadline.text = attention > 0
                ? attention == 1 ? "1 thing needs attention" : $"{attention} things need attention"
                : restart ? "Your project is almost ready" : "Your project is ready";
            statusHeadline.EnableInClassList("wizard-bad", attention > 0);
            statusHeadline.EnableInClassList("wizard-ok", attention == 0 && !restart);
        }

        #endregion

        #region Actions

        private void StartSetupFromStatus()
        {
            choicesChangeMode = false;
            GoTo(WizardPage.Choices);
        }

        private void ChangeVersion()
        {
            choicesChangeMode = true;
            choiceVersion = null;
            choiceAllowDowngrade = false;
            choiceScripting = SetupSteps.InterpreterInManifest() ? 1 : 0;
            GoTo(WizardPage.Choices);
        }

        private void ManageConnections()
        {
            connectionFromStatus = true;
            connectionNote = null;
            GoTo(WizardPage.Connection);
        }

        private async void ReloadPackages()
        {
            if (packagesChecking)
                return;

            packagesChecking = true;
            packagesNote = "Checking the remotes...";
            RefreshStatusPage(true);

            try
            {
                packagesNote = await PackageAutoUpdate.CheckNowAsync();
            }
            finally
            {
                packagesChecking = false;
            }

            RefreshStatusPage(true);
        }

        private void RunSetupAgain()
        {
            string installed = InstalledVersion();
            if (installed == null)
            {
                StartSetupFromStatus();
                return;
            }

            // The installed version and scripting, confirmed on the Summary page like any other run.
            choiceVersion = installed;
            choiceScripting = SetupSteps.InterpreterInManifest() ? 1 : 0;
            choiceAllowDowngrade = false;
            OpenSummary(true);
        }

        /// <summary>Add C# scripts or Remove C# scripts: the installed version with the other scripting, confirmed on the Summary page, which
        /// says how long the interpreter takes, or warns, with a tick to give, when C# scripts would stop running.</summary>
        private void ChangeScripting(bool csharp)
        {
            string installed = InstalledVersion();
            if (installed == null)
                return;

            choiceVersion = installed;
            choiceScripting = csharp ? 1 : 0;
            choiceAllowDowngrade = false;
            OpenSummary(true);
        }

        private void FixProjectSettings() => StartFix(SetupSteps.InterpreterInManifest(), SetupStepIds.ProjectSettings, SetupStepIds.Restart);

        private void FixAddressables()
        {
            if (!EditorUtility.DisplayDialog("Fix the Addressables",
                    "This gives the project the SDK's Addressables profile and resets the groups to a single default group: every other group and every entry is removed.",
                    "Fix", "Cancel"))
                return;

            StartFix(SetupSteps.InterpreterInManifest(), SetupStepIds.Addressables);
        }

        private void FixTextMesh() => StartFix(SetupSteps.InterpreterInManifest(), SetupStepIds.TmpEssentials);

        private void FixInterpreter() => StartFix(true, SetupStepIds.Interpreter);

        private static SetupChoices InstalledChoices(string version, bool csharp) => new()
        {
            Version = version,
            Scripting = csharp ? SetupScripting.VisualScriptingAndCSharp : SetupScripting.VisualScriptingOnly,
        };

        /// <summary>A "Fix": a run of the installed version, limited to the step that makes the thing.</summary>
        private void StartFix(bool csharp, params string[] steps)
        {
            string installed = InstalledVersion();
            if (installed == null)
                return;

            StartRun(InstalledChoices(installed, csharp), steps, WizardPage.Status);
        }

        #endregion
    }
}
