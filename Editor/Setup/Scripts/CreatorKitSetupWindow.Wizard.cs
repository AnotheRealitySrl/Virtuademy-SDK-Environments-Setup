using System.Collections.Generic;
using System.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.UIElements;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// The window as a wizard: one step at a time, over the sections the window already has.
    ///
    /// Nothing here re-implements a check or an action. Every step shows a group of the long page
    /// (the checks, the buttons and their bindings are the window's own) and the wizard only
    /// decides which group is visible and whether Next is enabled, from the same
    /// <see cref="projectConfig"/> and <see cref="packageManagerConfig"/> the page reads. The two
    /// steps without a group on the page, Connection and Done, are built here. The long page stays
    /// one toggle away (<c>Show all checks</c>, at the foot), unchanged.
    /// </summary>
    public partial class CreatorKitSetupWindow
    {
        private const int step_computer = 0;
        private const int step_project = 1;
        private const int step_sdk = 2;
        private const int step_scripts = 3;
        private const int step_connection = 4;
        private const int step_done = 5;

        private static readonly (string caption, string title, string description)[] wizard_steps =
        {
            ("Computer", "Your computer",
                "Git and the editor's build modules are required: fix what is red, then press Check again. The Unity version is only a warning."),
            ("Project", "Your project",
                "The render pipeline (URP) and the project settings the SDK expects. Configure changes project settings, so back the project up first; when it changes the input handling, it offers to restart the editor."),
            ("SDK", "The SDK",
                "Choose the Virtuademy version and install it. The other Virtuademy packages come with the SDK."),
            ("Scripts", "C# scripts (optional)",
                "The interpreter runs the C# you write for your environments."),
            ("Connection", "Connection",
                "Import the configuration file from the Back Office and log in, in the SDK's own window."),
            ("Done", "Done", string.Empty),
        };

        // Once per editor session the wizard opens on the first step that fails, whatever step a
        // window restored from the layout was on. Within the session the step is kept, because every
        // package action ends in a domain reload that rebuilds the window.
        private const string wizard_step_chosen_key = "Virtuademy.SDK.Environments.Setup.WizardStepChosenThisSession";

        // Serialized: an EditorWindow keeps these across a domain reload.
        [SerializeField] private int wizardStep = -1;
        [SerializeField] private bool showAllChecks;
        [SerializeField] private bool connectionConfirmed;

        private bool dataLoaded;
        private bool loadFailed;
        private bool wizardBuilt;
        private bool? lastViewWasWizard;

        // Elements of the long page that the wizard shows one by one.
        private VisualElement settingsSection;
        private VisualElement settingsHeader;
        private VisualElement gitGroup;
        private VisualElement editorGroup;
        private VisualElement projectGroup;
        private VisualElement interpreterGroup;
        private VisualElement packageSection;
        private readonly List<VisualElement> pageSeparators = new();
        private readonly List<Foldout> pageFoldouts = new();

        // Elements the wizard adds.
        private VisualElement wizardBar;
        private VisualElement wizardIntro;
        private Label wizardLoading;
        private VisualElement connectionPanel;
        private VisualElement donePanel;
        private VisualElement wizardNav;
        private readonly List<(VisualElement item, Label circle)> stepItems = new();
        private Label introTitle;
        private Label introDescription;
        private Label introHint;
        private VisualElement sdkPanel;
        private VisualElement scriptsPanel;
        private Label connectionSteps;
        private Label connectionNote;
        private Button openConnectionButton;
        private Label doneHeadline;
        private Label doneComputer;
        private Label doneProject;
        private Label doneSdk;
        private Label doneScripts;
        private Label doneConnection;
        private Button donePublishButton;
        private Button backButton;
        private Button checkAgainButton;
        private Button nextButton;
        private Label navHint;

        private void SetupWizard()
        {
            StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>($"{package_root}/Editor/Setup/Scripts/wizard.uss");
            if (sheet != null)
                root.styleSheets.Add(sheet);

            // The colours of wizard.uss are for the dark skin; the light skin has its own, darker, set.
            root.AddToClassList(EditorGUIUtility.isProSkin ? "vd-skin-dark" : "vd-skin-light");

            ScrollView scroll = root.Q<ScrollView>("ScrollView");
            VisualElement content = scroll.contentContainer;

            settingsSection = root.Q<VisualElement>("project-settings");
            packageSection = root.Q<VisualElement>("package-manager");
            // The outer group and the inner one share the name "project-settings".
            settingsHeader = settingsSection.Q<Label>("Header");
            gitGroup = root.Q<VisualElement>("git-installation");
            editorGroup = root.Q<VisualElement>("editor-configuration");
            projectGroup = FirstAncestorWithClass(root.Q<VisualElement>("project-settings-content"), "settings-group");
            interpreterGroup = root.Q<VisualElement>("Interpreter-settings");

            pageSeparators.AddRange(content.Children().Where(child => child.name == "separator"));
            foreach (string foldoutName in new[] { "git-installation-foldout", "editor-configuration-foldout", "project-settings-foldout", "Interpreter-settings-foldout" })
                pageFoldouts.Add(root.Q<Foldout>(foldoutName));

            VisualElement header = root.Q<VisualElement>("window-header");
            int at = content.IndexOf(header) + 1;

            wizardBar = BuildStepBar();
            content.Insert(at++, wizardBar);

            wizardIntro = BuildIntro();
            content.Insert(at++, wizardIntro);

            wizardLoading = new Label("Checking your project...");
            wizardLoading.AddToClassList("wizard-loading");
            content.Insert(at++, wizardLoading);

            sdkPanel = BuildSdkPanel();
            content.Insert(at++, sdkPanel);

            scriptsPanel = BuildScriptsPanel();
            content.Insert(at++, scriptsPanel);

            connectionPanel = BuildConnectionPanel();
            content.Insert(at++, connectionPanel);

            donePanel = BuildDonePanel();
            content.Insert(at++, donePanel);

            VisualElement footer = root.Q<VisualElement>("window-footer");
            wizardNav = BuildNavigation();
            footer.parent.Insert(footer.parent.IndexOf(footer), wizardNav);

            Toggle showAllToggle = new("Show all checks");
            showAllToggle.AddToClassList("footer-startup-toggle");
            showAllToggle.tooltip = "Shows the whole setup on one page, instead of one step at a time.";
            showAllToggle.SetValueWithoutNotify(showAllChecks);
            showAllToggle.RegisterValueChangedCallback(evt =>
            {
                showAllChecks = evt.newValue;
                // The long page has its own version dropdown: take the choice from it again on the way back.
                sdkChoiceMode = -1;
                ApplyView();
                RefreshWizard();
            });
            footer.Add(showAllToggle);

            wizardBuilt = true;
            ApplyView();

            // The checks the wizard gates on are read from memory, but several of them change
            // behind the window's back (a package resolves, a Check call finishes): a light poll
            // keeps the indicator and Next honest without hooking every one.
            root.schedule.Execute(RefreshWizard).Every(500);
        }

        #region Building the wizard

        private static VisualElement FirstAncestorWithClass(VisualElement element, string className)
        {
            for (VisualElement ancestor = element?.parent; ancestor != null; ancestor = ancestor.parent)
            {
                if (ancestor.ClassListContains(className))
                    return ancestor;
            }

            return null;
        }

        private VisualElement BuildStepBar()
        {
            VisualElement bar = new() { name = "wizard-bar" };
            bar.AddToClassList("wizard-bar");

            for (int i = 0; i < wizard_steps.Length; i++)
            {
                VisualElement item = new();
                item.AddToClassList("wizard-step");

                Label circle = new((i + 1).ToString());
                circle.AddToClassList("wizard-step__circle");
                item.Add(circle);

                Label caption = new(wizard_steps[i].caption);
                caption.AddToClassList("wizard-step__caption");
                item.Add(caption);

                bar.Add(item);
                stepItems.Add((item, circle));

                if (i < wizard_steps.Length - 1)
                {
                    VisualElement connector = new();
                    connector.AddToClassList("wizard-step__connector");
                    bar.Add(connector);
                }
            }

            return bar;
        }

        private VisualElement BuildIntro()
        {
            VisualElement intro = new() { name = "wizard-intro" };
            intro.AddToClassList("wizard-intro");

            introTitle = new Label();
            introTitle.AddToClassList("wizard-intro__title");
            intro.Add(introTitle);

            introDescription = new Label();
            introDescription.AddToClassList("wizard-intro__description");
            intro.Add(introDescription);

            introHint = new Label();
            introHint.AddToClassList("wizard-intro__hint");
            intro.Add(introHint);
            return intro;
        }

        private VisualElement BuildConnectionPanel()
        {
            VisualElement panel = new() { name = "wizard-connection" };
            panel.AddToClassList("wizard-panel");

            connectionSteps = new Label();
            connectionSteps.AddToClassList("wizard-panel__text");
            panel.Add(connectionSteps);

            openConnectionButton = new Button(() => SdkWindowLinks.Open(SdkWindowLinks.FindConnectionWindow()));
            openConnectionButton.AddToClassList("wizard-panel__action");
            panel.Add(openConnectionButton);

            connectionNote = new Label("This window cannot see whether you are logged in, so this step never blocks you.");
            connectionNote.AddToClassList("wizard-panel__note");
            panel.Add(connectionNote);

            Toggle confirmed = new() { text = "I imported the file and logged in" };
            confirmed.AddToClassList("wizard-check");
            confirmed.SetValueWithoutNotify(connectionConfirmed);
            confirmed.RegisterValueChangedCallback(evt =>
            {
                connectionConfirmed = evt.newValue;
                RefreshWizard();
            });
            panel.Add(confirmed);
            return panel;
        }

        private VisualElement BuildDonePanel()
        {
            VisualElement panel = new() { name = "wizard-done" };
            panel.AddToClassList("wizard-panel");

            doneHeadline = new Label();
            doneHeadline.AddToClassList("wizard-panel__text");
            panel.Add(doneHeadline);

            doneComputer = SummaryRow(panel, wizard_steps[step_computer].caption);
            doneProject = SummaryRow(panel, wizard_steps[step_project].caption);
            doneSdk = SummaryRow(panel, wizard_steps[step_sdk].caption);
            doneScripts = SummaryRow(panel, "C# scripts (optional)");
            doneConnection = SummaryRow(panel, wizard_steps[step_connection].caption);

            VisualElement buttons = new();
            buttons.AddToClassList("wizard-panel__buttons");

            Button readme = new(ReadmeWindow.ShowWindow) { text = "Open the Readme" };
            buttons.Add(readme);

            Button documentation = new(() => Application.OpenURL(SdkWindowLinks.documentation_root_url))
            {
                text = "Open the documentation",
                tooltip = SdkWindowLinks.documentation_root_url
            };
            buttons.Add(documentation);

            donePublishButton = new Button(() => SdkWindowLinks.Open(SdkWindowLinks.FindPublishWindow())) { text = "Open the publish window" };
            buttons.Add(donePublishButton);

            panel.Add(buttons);
            return panel;
        }

        private static Label SummaryRow(VisualElement panel, string name)
        {
            VisualElement row = new();
            row.AddToClassList("wizard-summary-row");

            Label nameLabel = new(name);
            nameLabel.AddToClassList("wizard-summary-row__name");
            row.Add(nameLabel);

            Label status = new();
            status.AddToClassList("wizard-summary-row__status");
            row.Add(status);

            panel.Add(row);
            return status;
        }

        private VisualElement BuildNavigation()
        {
            VisualElement nav = new() { name = "wizard-nav" };
            nav.AddToClassList("wizard-nav");

            backButton = new Button(() => GoToStep(wizardStep - 1)) { text = "Back" };
            nav.Add(backButton);

            navHint = new Label();
            navHint.AddToClassList("wizard-nav__hint");
            nav.Add(navHint);

            checkAgainButton = new Button(() =>
            {
                if (!isSetupping)
                    SetupWindowData();
            })
            {
                text = "Check again",
                tooltip = "Reads the computer and the project again, as the refresh button of the Package manager does."
            };
            nav.Add(checkAgainButton);

            nextButton = new Button(() => GoToStep(wizardStep + 1)) { text = "Next" };
            nextButton.AddToClassList("wizard-nav__next");
            nav.Add(nextButton);
            return nav;
        }

        #endregion

        #region State

        /// <summary>
        /// Whether the checks of a step are green. Connection and Done have none, and Scripts is
        /// optional, so a step the person may leave with nothing to show is always "ok" here.
        /// </summary>
        private bool StepOk(int step) => step switch
        {
            // The Unity version is not part of the gate: it is an exact compare with the registry's, so
            // any other patch release would block Next on a step whose fix (another version) is on a
            // later one. It is a warning on the step (see RefreshWizard), not a red item.
            step_computer => projectConfig.IsGitInstalled && projectConfig.AllEditorModulesInstalled,
            step_project => projectConfig.ProjectSettingsOk,
            // Installed at the chosen version AND the project compiles: with the SDK in but a compile error,
            // nothing after this step can work, and a green step would hide it.
            step_sdk => sdkRegisteredSnapshot && !packageManagerConfig.DisplayedAndInstalledVersionsAreDifferent && ProjectCompiles,
            _ => true,
        };

        // Read once per refresh (and per load): asking Package Manager several times per tick is waste.
        private bool sdkRegisteredSnapshot;

        private bool compileFailed;
        private bool compiling;
        private bool importing;

        /// <summary>Scripts compiled, and nothing is compiling right now.</summary>
        private bool ProjectCompiles => !compileFailed && !compiling;

        private void UpdateSdkSnapshot()
        {
            sdkRegisteredSnapshot = IsSdkRegistered();
            // Polled with the rest, which also covers "after each compilation": the poll sees it end.
            compileFailed = EditorUtility.scriptCompilationFailed;
            compiling = EditorApplication.isCompiling;
            importing = EditorApplication.isUpdating;
        }

        private static bool IsSdkRegistered()
            => UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(p => p.name == sdk_environments_package_name);

        /// <summary>The first of the three required steps that fails, or Done when none does. The
        /// interpreter is left out on purpose, as in the startup check: it is optional, and whether it
        /// is installed is read asynchronously, so at the first load it is not known yet.</summary>
        private int FirstFailingStep()
        {
            for (int step = step_computer; step <= step_sdk; step++)
            {
                if (!StepOk(step))
                    return step;
            }

            return step_done;
        }

        /// <summary>Called after every successful load: the checks have values now.</summary>
        private void OnWizardDataLoaded()
        {
            dataLoaded = true;
            loadFailed = false;
            UpdateSdkSnapshot();
            InitializeSdkChoice();

            if (wizardStep < 0 || wizardStep >= wizard_steps.Length || !SessionState.GetBool(wizard_step_chosen_key, false))
            {
                wizardStep = FirstFailingStep();
                SessionState.SetBool(wizard_step_chosen_key, true);
            }

            ApplyView();
            RefreshWizard();
        }

        private void GoToStep(int step)
        {
            wizardStep = Mathf.Clamp(step, 0, wizard_steps.Length - 1);
            showUpdateRoutineNote = false;
            root.Q<ScrollView>("ScrollView").scrollOffset = Vector2.zero;
            ApplyView();
            RefreshWizard();
        }

        private void InstallSdkFromWizard()
        {
            SelectedPackage(sdk_environments_package_name, out PackageDefinition sdk);
            if (sdk != null)
                InstallPackageWithDependencies(sdk);
        }

        private void SelectedPackage(string name, out PackageDefinition package)
        {
            package = null;
            packageManagerConfig?.SelectedVersion?.PackageDictionary.TryGetValue(name, out package);
        }

        #endregion

        #region View

        // Flex, not StyleKeyword.Null: on 6000.3 an element hidden once and then given back to its
        // stylesheet with Null kept resolving to None, so going Back to a step showed nothing.
        private static void SetShown(VisualElement element, bool shown)
        {
            if (element != null)
                element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Which parts of the window are visible. In "show all checks" mode this is the page as it
        /// always was: both sections, minus what the load error hides. In wizard mode it is the
        /// bar, the intro, the navigation and the one group (or panel) of the current step.
        /// </summary>
        private void ApplyView()
        {
            if (!wizardBuilt)
                return;

            bool ready = dataLoaded && !loadFailed;
            bool wizard = !showAllChecks;

            SetShown(wizardBar, wizard && !loadFailed);
            SetShown(wizardIntro, wizard && ready);
            SetShown(wizardLoading, wizard && !ready && !loadFailed);
            SetShown(sdkPanel, wizard && ready && wizardStep == step_sdk);
            SetShown(scriptsPanel, wizard && ready && wizardStep == step_scripts);
            SetShown(connectionPanel, wizard && ready && wizardStep == step_connection);
            SetShown(donePanel, wizard && ready && wizardStep == step_done);
            SetShown(wizardNav, wizard && ready);

            if (!wizard)
            {
                SetShown(settingsSection, !loadFailed);
                SetShown(packageSection, !loadFailed);
                SetShown(settingsHeader, true);
                SetShown(gitGroup, true);
                SetShown(editorGroup, true);
                SetShown(projectGroup, true);
                SetShown(interpreterGroup, true);
                pageSeparators.ForEach(separator => SetShown(separator, true));

                // Back to how the page opens: groups collapsed, their warning icons showing.
                if (lastViewWasWizard == true)
                    pageFoldouts.ForEach(foldout => foldout.value = false);
            }
            else
            {
                // The SDK and Scripts steps are panels of their own (WizardSteps.cs): the Package manager block and
                // the interpreter group stay on the long page.
                SetShown(settingsSection, ready && (wizardStep == step_computer || wizardStep == step_project));
                SetShown(packageSection, false);
                // The step has its own title.
                SetShown(settingsHeader, false);
                SetShown(gitGroup, wizardStep == step_computer);
                SetShown(editorGroup, wizardStep == step_computer);
                SetShown(projectGroup, wizardStep == step_project);
                SetShown(interpreterGroup, false);
                pageSeparators.ForEach(separator => SetShown(separator, false));

                // A step shows its checks, not a collapsed title.
                if (lastViewWasWizard != true)
                    pageFoldouts.ForEach(foldout => foldout.value = true);
            }

            lastViewWasWizard = wizard;
        }

        /// <summary>Brings the indicator, the intro, Next and the two panels in line with the checks.</summary>
        private void RefreshWizard()
        {
            if (!wizardBuilt || showAllChecks || !dataLoaded || loadFailed)
                return;

            UpdateSdkSnapshot();
            bool sdkRegistered = sdkRegisteredSnapshot;
            bool versionsDiffer = packageManagerConfig.DisplayedAndInstalledVersionsAreDifferent;

            for (int i = 0; i < stepItems.Count; i++)
            {
                VisualElement item = stepItems[i].item;
                bool current = i == wizardStep;
                bool passed = i < wizardStep;
                item.EnableInClassList("wizard-step--current", current);
                item.EnableInClassList("wizard-step--done", passed && StepOk(i));
                item.EnableInClassList("wizard-step--failed", passed && !StepOk(i));
            }

            introTitle.text = $"Step {wizardStep + 1} of {wizard_steps.Length}: {wizard_steps[wizardStep].title}";
            introDescription.text = wizard_steps[wizardStep].description;
            SetShown(introDescription, !string.IsNullOrEmpty(introDescription.text));

            string hint = string.Empty;
            if (wizardStep == step_computer && !projectConfig.UnityVersionIsMatching)
            {
                // A warning, never a gate: see StepOk.
                PackageRegistry entry = packageManagerConfig.CurrentVersion ?? packageManagerConfig.SelectedVersion;
                hint = string.IsNullOrEmpty(entry?.RequiredUnityVersion)
                    ? $"This is Unity {UnityVersion}, not the version this Virtuademy version requires. Install the required one from Unity Hub: a build made with another version may not match the player."
                    : $"This is Unity {UnityVersion}, but Virtuademy {entry.ReflectisVersion} requires {entry.RequiredUnityVersion}. Install it from Unity Hub: a build made with another version may not match the player.";
            }

            introHint.text = hint;
            SetShown(introHint, hint.Length > 0);

            bool stepOk = StepOk(wizardStep);
            backButton.SetEnabled(wizardStep > step_computer);
            SetShown(checkAgainButton, wizardStep <= step_scripts);
            // One load at a time: a second click while one runs would start another.
            checkAgainButton.SetEnabled(!isSetupping);
            SetShown(nextButton, wizardStep < step_done);
            nextButton.SetEnabled(stepOk);
            nextButton.text = wizardStep == step_scripts && !projectConfig.InterpreterReady ? "Skip" : "Next";
            navHint.text = stepOk
                ? string.Empty
                : wizardStep == step_sdk
                    ? "Install the SDK at the chosen version and let Unity compile it."
                    : "Fix the red items, then press Check again.";

            if (wizardStep == step_sdk)
                RefreshSdkPanel(sdkRegistered);
            else if (wizardStep == step_scripts)
                RefreshScriptsPanel(sdkRegistered);
            else if (wizardStep == step_connection)
                RefreshConnectionPanel(sdkRegistered);
            else if (wizardStep == step_done)
                RefreshDonePanel(sdkRegistered, versionsDiffer);
        }

        private void RefreshConnectionPanel(bool sdkRegistered)
        {
            SdkWindowLink window = sdkRegistered ? SdkWindowLinks.FindConnectionWindow() : null;

            if (window != null)
            {
                connectionSteps.text = window.Steps;
                openConnectionButton.text = $"Open {window.Name}";
            }
            else
            {
                connectionSteps.text = sdkRegistered
                    ? "The installed SDK has no window for connections yet. If Unity is still compiling, wait for it and look again."
                    : "The SDK is not installed yet: install it in step 3 first.";
                openConnectionButton.text = "Open the SDK window";
            }

            openConnectionButton.SetEnabled(window != null);
        }

        private void RefreshDonePanel(bool sdkRegistered, bool versionsDiffer)
        {
            bool computer = StepOk(step_computer);
            bool project = StepOk(step_project);
            bool sdk = StepOk(step_sdk);

            doneHeadline.text = computer && project && sdk
                ? "Your project is set up. The Readme has the first steps; when a scene is ready, publish it."
                : "Some steps still need attention: go back to the ones marked in red.";

            if (!computer)
                SetStatus(doneComputer, "Needs attention", false);
            else if (!projectConfig.UnityVersionIsMatching)
                SetStatus(doneComputer, "Ready, but Unity is not the required version", null, warning: true);
            else
                SetStatus(doneComputer, "Ready", true);
            SetStatus(doneProject, project ? "Ready" : "Needs attention", project);

            string sdkText = !sdkRegistered
                ? "Not installed"
                : versionsDiffer
                    ? "Installed, another version is selected"
                    : !ProjectCompiles
                        ? (compileFailed ? "Installed, but the project does not compile" : "Installed, Unity is compiling")
                        : $"Installed, Virtuademy {packageManagerConfig.CurrentInstallationVersion}";
            SetStatus(doneSdk, sdkText, sdk);

            if (projectConfig.InterpreterReady)
                SetStatus(doneScripts, "Ready", true);
            else if (projectConfig.InterpreterNeedsFix)
                SetStatus(doneScripts, "Installed, not ready to build", false);
            else
                SetStatus(doneScripts, "Not installed (optional)", null);

            SetStatus(doneConnection, connectionConfirmed ? "Done, as you confirmed" : "Not confirmed", connectionConfirmed ? true : null);

            donePublishButton.SetEnabled(sdkRegistered && SdkWindowLinks.FindPublishWindow() != null);
        }

        private static void SetStatus(Label label, string text, bool? ok, bool warning = false)
        {
            label.text = text;
            label.EnableInClassList("wizard-ok", ok == true);
            label.EnableInClassList("wizard-bad", ok == false);
            label.EnableInClassList("wizard-warn", warning);
            label.EnableInClassList("wizard-muted", ok == null && !warning);
        }

        #endregion
    }
}
