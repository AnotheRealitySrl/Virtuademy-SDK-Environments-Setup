using System.Collections.Generic;
using System.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.UIElements;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// The two wizard steps that are panels of their own instead of a group of the long page: the
    /// SDK (which version, one button) and the C# scripts (the interpreter's state and action).
    ///
    /// They show a choice and a state, and do the work through the window's own methods:
    /// <see cref="InstallPackageWithDependencies"/>, <see cref="UpdatePackagesToSelectedVersion"/>
    /// and <see cref="ConfigureInterpreterSettings"/>. The version the person picks is written to
    /// <see cref="PackageManagerConfiguration.DisplayedReflectisVersion"/>, as the long page's
    /// dropdown does, so the two views never disagree about what "selected" means.
    /// </summary>
    public partial class CreatorKitSetupWindow
    {
        private const int choice_latest = 0;
        private const int choice_previous = 1;
        private const int choice_development = 2;

        private const string console_menu_path = "Window/General/Console";

        // Serialized, like the step: a package action ends in a domain reload.
        [SerializeField] private int sdkChoiceMode = -1;
        [SerializeField] private string previousChoice;
        [SerializeField] private bool showUpdateRoutineNote;

        private RadioButton radioLatest;
        private RadioButton radioPrevious;
        private RadioButton radioDevelopment;
        private DropdownField previousDropdown;
        private Toggle developmentToggle;
        private Label sdkUnityLabel;
        private Label sdkNote;
        private Button sdkActionButton;
        private Label sdkStatus;
        private Label sdkRoutineNote;
        private Button sdkConsoleButton;

        private Label scriptsStatus;
        private Label scriptsIssue;
        private Button scriptsActionButton;
        private Button scriptsConsoleButton;

        #region Building

        private VisualElement BuildSdkPanel()
        {
            VisualElement panel = new() { name = "wizard-sdk" };
            panel.AddToClassList("wizard-panel");

            Label question = new("Which version of the SDK?");
            question.AddToClassList("wizard-panel__text");
            panel.Add(question);

            // A GroupBox makes the radio buttons inside it exclusive; its own look is reset in wizard.uss.
            GroupBox choices = new();
            choices.AddToClassList("wizard-choices");

            radioLatest = new RadioButton();
            radioLatest.RegisterValueChangedCallback(evt => { if (evt.newValue) OnSdkChoice(choice_latest); });
            choices.Add(radioLatest);

            radioPrevious = new RadioButton { text = "A previous release" };
            radioPrevious.RegisterValueChangedCallback(evt => { if (evt.newValue) OnSdkChoice(choice_previous); });
            choices.Add(radioPrevious);

            previousDropdown = new DropdownField();
            previousDropdown.AddToClassList("wizard-dropdown");
            previousDropdown.RegisterValueChangedCallback(evt =>
            {
                previousChoice = evt.newValue;
                OnSdkChoice(choice_previous);
            });
            choices.Add(previousDropdown);

            radioDevelopment = new RadioButton();
            radioDevelopment.RegisterValueChangedCallback(evt => { if (evt.newValue) OnSdkChoice(choice_development); });
            choices.Add(radioDevelopment);

            panel.Add(choices);

            // The same setting as "Show pre-releases" of the long page, so the two stay one.
            developmentToggle = new Toggle { text = "Show the development version (unreleased, for internal testing)" };
            developmentToggle.AddToClassList("wizard-check");
            developmentToggle.RegisterValueChangedCallback(evt =>
            {
                packageManagerConfig.ShowPrereleases = evt.newValue;
                UpdateAvailableVersions();
                if (!evt.newValue && sdkChoiceMode == choice_development)
                    sdkChoiceMode = choice_latest;
                ApplySdkChoice();
                RefreshWizard();
            });
            panel.Add(developmentToggle);

            sdkUnityLabel = new Label();
            sdkUnityLabel.AddToClassList("wizard-panel__text");
            panel.Add(sdkUnityLabel);

            sdkNote = new Label();
            sdkNote.AddToClassList("wizard-panel__text");
            sdkNote.AddToClassList("wizard-warn");
            panel.Add(sdkNote);

            sdkActionButton = new Button(OnSdkActionClicked);
            sdkActionButton.AddToClassList("wizard-panel__primary");
            panel.Add(sdkActionButton);

            sdkStatus = new Label();
            sdkStatus.AddToClassList("wizard-panel__text");
            panel.Add(sdkStatus);

            sdkConsoleButton = new Button(OpenConsole) { text = "Open the Console" };
            sdkConsoleButton.AddToClassList("wizard-panel__action");
            panel.Add(sdkConsoleButton);

            sdkRoutineNote = new Label("If the release notes name an update routine, run it from Virtuademy > Update routines.");
            sdkRoutineNote.AddToClassList("wizard-panel__note");
            panel.Add(sdkRoutineNote);
            return panel;
        }

        private VisualElement BuildScriptsPanel()
        {
            VisualElement panel = new() { name = "wizard-scripts" };
            panel.AddToClassList("wizard-panel");

            scriptsStatus = new Label();
            scriptsStatus.AddToClassList("wizard-panel__text");
            panel.Add(scriptsStatus);

            scriptsIssue = new Label();
            scriptsIssue.AddToClassList("wizard-panel__note");
            panel.Add(scriptsIssue);

            scriptsActionButton = new Button(ConfigureInterpreterSettings);
            scriptsActionButton.AddToClassList("wizard-panel__primary");
            panel.Add(scriptsActionButton);

            scriptsConsoleButton = new Button(OpenConsole) { text = "Open the Console" };
            scriptsConsoleButton.AddToClassList("wizard-panel__action");
            panel.Add(scriptsConsoleButton);

            Label note = new("Optional: skip it if you only use Visual Scripting. Installing it clones two repositories and patches a " +
                             "project-local copy of il2cpp, so it takes a while and needs git.");
            note.AddToClassList("wizard-panel__note");
            panel.Add(note);
            return panel;
        }

        private static void OpenConsole() => EditorApplication.ExecuteMenuItem(console_menu_path);

        #endregion

        #region SDK choice

        private PackageRegistry[] Registry => packageManagerConfig.AllVersionsPackageRegistry ?? new PackageRegistry[0];

        /// <summary>The newest release: the registry lists entries oldest first, and prereleases do not count.</summary>
        private PackageRegistry LatestRelease => Registry.LastOrDefault(entry => !entry.Prerelease);

        /// <summary>The releases before the latest one, newest first.</summary>
        private List<PackageRegistry> PreviousReleases()
        {
            List<PackageRegistry> releases = Registry.Where(entry => !entry.Prerelease).ToList();
            if (releases.Count > 0)
                releases.RemoveAt(releases.Count - 1);
            releases.Reverse();
            return releases;
        }

        private PackageRegistry DevelopmentEntry
            => Registry.FirstOrDefault(entry => entry.ReflectisVersion == "develop") ?? Registry.LastOrDefault(entry => entry.Prerelease);

        private PackageRegistry ChosenEntry
        {
            get
            {
                switch (sdkChoiceMode)
                {
                    case choice_previous:
                        List<PackageRegistry> previous = PreviousReleases();
                        return previous.FirstOrDefault(entry => entry.ReflectisVersion == previousChoice) ?? previous.FirstOrDefault();
                    case choice_development:
                        return DevelopmentEntry;
                    default:
                        return LatestRelease;
                }
            }
        }

        /// <summary>Takes the choice from what the window has selected, once per window and again after
        /// the long page was used; a choice kept across a reload is only checked and applied.</summary>
        private void InitializeSdkChoice()
        {
            if (packageManagerConfig == null)
                return;

            if (sdkChoiceMode < choice_latest || sdkChoiceMode > choice_development)
            {
                PackageRegistry selected = packageManagerConfig.SelectedVersion;
                if (selected == null || selected == LatestRelease)
                {
                    sdkChoiceMode = choice_latest;
                }
                else if (selected.Prerelease)
                {
                    sdkChoiceMode = packageManagerConfig.ShowPrereleases ? choice_development : choice_latest;
                }
                else
                {
                    sdkChoiceMode = choice_previous;
                    previousChoice = selected.ReflectisVersion;
                }
            }

            ValidateSdkChoice();
            ApplySdkChoice();
        }

        /// <summary>A choice the registry no longer offers falls back to the latest release.</summary>
        private void ValidateSdkChoice()
        {
            if (sdkChoiceMode == choice_previous && PreviousReleases().Count == 0)
                sdkChoiceMode = choice_latest;
            if (sdkChoiceMode == choice_development && (!packageManagerConfig.ShowPrereleases || DevelopmentEntry == null))
                sdkChoiceMode = choice_latest;
        }

        /// <summary>Writes the choice to the window's selected version.</summary>
        private void ApplySdkChoice()
        {
            PackageRegistry chosen = ChosenEntry;
            if (chosen != null && packageManagerConfig.DisplayedReflectisVersion != chosen.ReflectisVersion)
                packageManagerConfig.DisplayedReflectisVersion = chosen.ReflectisVersion;
        }

        private void OnSdkChoice(int mode)
        {
            sdkChoiceMode = mode;
            ValidateSdkChoice();
            ApplySdkChoice();
            RefreshWizard();
        }

        private int IndexOf(PackageRegistry entry)
            => entry == null ? -1 : System.Array.FindIndex(Registry, candidate => candidate == entry);

        /// <summary>What the button will do to the project, as the person reads it.</summary>
        private enum SdkAction { Install, Update, Change }

        private SdkAction DescribeAction(PackageRegistry chosen, bool sdkRegistered, out bool older)
        {
            older = false;
            if (!sdkRegistered)
                return SdkAction.Install;

            PackageRegistry current = packageManagerConfig.CurrentVersion;
            int currentIndex = IndexOf(current);
            int chosenIndex = IndexOf(chosen);
            if (current == null || current.Prerelease || chosen.Prerelease)
                return SdkAction.Change;

            // The registry lists releases oldest first: that is the order the window itself relies on.
            older = chosenIndex < currentIndex;
            return older ? SdkAction.Change : SdkAction.Update;
        }

        // The window moves a project to an older release without refusing it (a project may break: the
        // long page's button warns), so the wizard does the same and says it, rather than adding a rule.
        private void OnSdkActionClicked()
        {
            if (ChosenEntry == null)
                return;

            ApplySdkChoice();
            bool registered = IsSdkRegistered();

            // A new project, at the version the window recorded: the package's own install.
            if (!registered && !packageManagerConfig.DisplayedAndInstalledVersionsAreDifferent)
            {
                InstallSdkFromWizard();
                return;
            }

            // Another version than the recorded one (or none yet): the long page's update, which installs
            // what the version adds, moves what changed and removes what it no longer lists.
            if (registered)
                showUpdateRoutineNote = true;
            UpdatePackagesToSelectedVersion();
        }

        #endregion

        #region Refreshing

        private void RefreshSdkPanel(bool sdkRegistered)
        {
            ValidateSdkChoice();

            PackageRegistry latest = LatestRelease;
            List<PackageRegistry> previous = PreviousReleases();
            PackageRegistry development = DevelopmentEntry;
            bool showDevelopment = packageManagerConfig.ShowPrereleases && development != null;

            radioLatest.text = latest != null ? $"Latest release ({latest.ReflectisVersion})" : "Latest release (none is listed)";
            radioLatest.SetEnabled(latest != null);

            SetShown(radioPrevious, previous.Count > 0);
            SetShown(previousDropdown, previous.Count > 0);
            List<string> previousVersions = previous.Select(entry => entry.ReflectisVersion).ToList();
            if (!previousDropdown.choices.SequenceEqual(previousVersions))
                previousDropdown.choices = previousVersions;
            string previousShown = ChosenEntry != null && sdkChoiceMode == choice_previous ? ChosenEntry.ReflectisVersion : previousChoice;
            if (string.IsNullOrEmpty(previousShown) || !previousVersions.Contains(previousShown))
                previousShown = previousVersions.FirstOrDefault();
            previousDropdown.SetValueWithoutNotify(previousShown);
            previousDropdown.SetEnabled(sdkChoiceMode == choice_previous);

            SetShown(radioDevelopment, showDevelopment);
            if (showDevelopment)
                radioDevelopment.text = $"Development version ({development.ReflectisVersion})";

            radioLatest.SetValueWithoutNotify(sdkChoiceMode == choice_latest);
            radioPrevious.SetValueWithoutNotify(sdkChoiceMode == choice_previous);
            radioDevelopment.SetValueWithoutNotify(sdkChoiceMode == choice_development);
            developmentToggle.SetValueWithoutNotify(packageManagerConfig.ShowPrereleases);

            PackageRegistry chosen = ChosenEntry;
            string noteText = string.Empty;
            string buttonText = "Install";
            bool buttonEnabled = false;

            if (chosen == null)
            {
                sdkUnityLabel.text = "No version is available.";
            }
            else
            {
                string required = chosen.RequiredUnityVersion;
                bool unityMatches = !string.IsNullOrEmpty(required) && required == UnityVersion;
                sdkUnityLabel.text = string.IsNullOrEmpty(required)
                    ? $"Virtuademy {chosen.ReflectisVersion} does not name a Unity version."
                    : $"Virtuademy {chosen.ReflectisVersion} needs Unity {required}" + (unityMatches ? " (this editor)." : ".");

                bool installedChoice = sdkRegistered && packageManagerConfig.CurrentInstallationVersion == chosen.ReflectisVersion;
                SdkAction action = DescribeAction(chosen, sdkRegistered, out bool older);

                if (installedChoice)
                {
                    buttonText = "Installed";
                }
                else
                {
                    buttonText = action switch
                    {
                        SdkAction.Install => $"Install {chosen.ReflectisVersion}",
                        SdkAction.Update => $"Update to {chosen.ReflectisVersion}",
                        _ => $"Change to {chosen.ReflectisVersion}",
                    };

                    if (!string.IsNullOrEmpty(required) && !unityMatches)
                    {
                        // Installing it here would not work: the packages of that version are built for that editor.
                        noteText = $"This editor is Unity {UnityVersion}. Virtuademy {chosen.ReflectisVersion} needs Unity {required}: " +
                                   "open the project with that editor, or choose another version.";
                    }
                    else
                    {
                        buttonEnabled = !compiling;
                        if (older)
                            noteText = "An older release than the installed one may break the project. Back it up first.";
                    }
                }
            }

            sdkNote.text = noteText;
            SetShown(sdkNote, noteText.Length > 0);
            sdkActionButton.text = buttonText;
            sdkActionButton.SetEnabled(buttonEnabled);

            string status;
            bool statusOk = false;
            if (compileFailed)
            {
                status = "The project does not compile after the install: open the Console (Window > General > Console) for the errors.";
            }
            else if (compiling)
            {
                status = "Unity is compiling the scripts...";
            }
            else if (importing)
            {
                status = "Unity is importing the packages...";
            }
            else if (!sdkRegistered && ReadManifestDependency(sdk_environments_package_name) != null)
            {
                status = "Installing: Unity is fetching the packages. This can take a few minutes.";
            }
            else if (sdkRegistered && StepOk(step_sdk))
            {
                status = "The SDK is installed and the project compiles.";
                statusOk = true;
            }
            else
            {
                status = string.Empty;
            }

            sdkStatus.text = status;
            SetShown(sdkStatus, status.Length > 0);
            sdkStatus.EnableInClassList("wizard-bad", compileFailed);
            sdkStatus.EnableInClassList("wizard-ok", statusOk);
            SetShown(sdkConsoleButton, compileFailed);
            SetShown(sdkRoutineNote, showUpdateRoutineNote);
        }

        private void RefreshScriptsPanel(bool sdkRegistered)
        {
            string status;
            string issue = string.Empty;
            string button = "Install interpreter";
            bool? ok = null;
            bool enabled = false;

            if (!sdkRegistered)
            {
                status = "Install the SDK first (step 3).";
            }
            else if (compileFailed)
            {
                status = "The project does not compile: fix the errors first.";
                ok = false;
            }
            else if (compiling || importing)
            {
                status = "Unity is compiling: wait for it to finish.";
            }
            else if (projectConfig.InterpreterReady)
            {
                status = "The interpreter is installed and ready to build.";
                button = "Ready";
                ok = true;
            }
            else if (projectConfig.IsHybridCLRInstalled)
            {
                status = "The interpreter is installed but not ready to build.";
                issue = projectConfig.InterpreterIssue;
                button = "Fix the interpreter";
                ok = false;
                enabled = true;
            }
            else
            {
                PackageRegistry entry = packageManagerConfig.CurrentVersion;
                TryGetDeclaredInterpreter(entry, out PackageDefinition declared);
                if (entry != null && declared == null)
                {
                    status = $"Virtuademy {entry.ReflectisVersion} has no interpreter (versions before 2026.6 have none).";
                }
                else
                {
                    status = "The interpreter is not installed.";
                    enabled = true;
                }
            }

            scriptsStatus.text = status;
            scriptsStatus.EnableInClassList("wizard-ok", ok == true);
            scriptsStatus.EnableInClassList("wizard-bad", ok == false);
            scriptsIssue.text = issue;
            SetShown(scriptsIssue, issue.Length > 0);
            scriptsActionButton.text = button;
            scriptsActionButton.SetEnabled(enabled);
            SetShown(scriptsConsoleButton, sdkRegistered && compileFailed);
        }

        #endregion
    }
}
