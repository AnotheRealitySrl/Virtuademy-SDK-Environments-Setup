using System.Collections.Generic;
using System.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.UIElements;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// The window as a wizard of five pages, over the long page: the frame, the navigation and the start page. The pages are in
    /// <c>CreatorKitSetupWindow.WizardConnection.cs</c> (Connection), <c>WizardChoices.cs</c> (Choices), <c>WizardRun.cs</c> (Run) and
    /// <c>WizardStatus.cs</c> (Project status); the Introduction is here.
    /// <list type="bullet">
    /// <item>Introduction, then Connection (it may be skipped: "Connect later"), then Choices (the Virtuademy version and how scripts are
    /// written), then Summary (the choices, what the setup will do, what cannot be undone, and Start setup), then Run, then Project
    /// status: the result of a run and the home of a project that is set up.</item>
    /// <item>The pages only start the work and show it. The work is <see cref="SetupRun"/>'s (every step, across the domain reloads),
    /// and the connections are the SDK's (<see cref="SetupConnections"/>). A page reads the same <see cref="projectConfig"/> and
    /// <see cref="packageManagerConfig"/> as the long page; the checks are the window's own.</item>
    /// <item><b>Development mode</b>, at the foot, is one switch for the whole setup: the development version in the lists, and the
    /// details of the Summary. The long page as it always was is a menu entry of its own, <c>Virtuademy/Advanced/All setup checks</c>,
    /// with <b>Back to the guided setup</b> at its top.</item>
    /// </list>
    /// </summary>
    public partial class CreatorKitSetupWindow
    {
        // The values are serialized (wizardPage): a new page goes at the end.
        private enum WizardPage { Intro, Connection, Choices, Run, Status, Summary }

        // Once per editor session the window picks its page by itself (a project that is not set up opens the Introduction, a project that
        // is set up opens its status), whatever page a window restored from the layout was on. Within the session the page is kept,
        // because every package action ends in a domain reload that rebuilds the window.
        private const string wizard_page_chosen_key = "Virtuademy.SDK.Environments.Setup.WizardPageChosenThisSession";

        private const string console_menu_path = "Window/General/Console";

        // Serialized: an EditorWindow keeps these across a domain reload.
        [SerializeField] private int wizardPage = -1;
        [SerializeField] private bool showAllChecks;

        private bool dataLoaded;
        private bool loadFailed;
        private bool wizardBuilt;

        // Elements of the long page that the wizard hides.
        private VisualElement settingsSection;
        private VisualElement packageSection;
        private readonly List<VisualElement> pageSeparators = new();

        // Elements the wizard adds.
        private Label wizardLoading;
        private VisualElement pageIntro;
        private VisualElement pageConnection;
        private VisualElement pageChoices;
        private VisualElement pageSummary;
        private VisualElement pageRun;
        private VisualElement pageStatus;
        private VisualElement wizardNav;
        private Button navBack;
        private Label navHint;
        private Button navSecondary;
        private Button navPrimary;
        private Toggle developmentModeToggle;
        private Button backToWizardButton;

        private WizardPage Page => (WizardPage)Mathf.Clamp(wizardPage, 0, (int)WizardPage.Summary);

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
            pageSeparators.AddRange(content.Children().Where(child => child.name == "separator"));

            VisualElement header = root.Q<VisualElement>("window-header");
            int at = content.IndexOf(header) + 1;

            // The UI is new: a page's signature, carried across the domain reload with the window's other private fields, would
            // tell it that its new, empty list is already drawn.
            runSignature = null;
            connectionsSignature = null;
            summarySignature = null;

            // The long page's way back to the wizard (it is opened from Virtuademy/Advanced/All setup checks).
            backToWizardButton = MakeButton("Back to the guided setup", () => SetShowAllChecks(false), "wizard-link");
            content.Insert(at++, backToWizardButton);

            wizardLoading = new Label("Checking your project...");
            wizardLoading.AddToClassList("wizard-loading");
            content.Insert(at++, wizardLoading);

            pageIntro = BuildIntroPage();
            content.Insert(at++, pageIntro);

            pageConnection = BuildConnectionPage();
            content.Insert(at++, pageConnection);

            pageChoices = BuildChoicesPage();
            content.Insert(at++, pageChoices);

            pageSummary = BuildSummaryPage();
            content.Insert(at++, pageSummary);

            pageRun = BuildRunPage();
            content.Insert(at++, pageRun);

            pageStatus = BuildStatusPage();
            content.Insert(at++, pageStatus);

            VisualElement footer = root.Q<VisualElement>("window-footer");
            wizardNav = BuildNavigation();
            footer.parent.Insert(footer.parent.IndexOf(footer), wizardNav);

            // One switch for the whole setup. It is the long page's "Show pre-releases", kept in the project's setup configuration,
            // which an install of the development version turns on by itself.
            developmentModeToggle = new Toggle("Development mode (internal testing)");
            developmentModeToggle.AddToClassList("footer-startup-toggle");
            developmentModeToggle.tooltip = "Shows the development version of Virtuademy, which is for internal testing, in the lists of the setup, and the details of the Summary. The same setting as Show pre-releases.";
            developmentModeToggle.RegisterValueChangedCallback(evt => SetDevelopmentMode(evt.newValue));
            footer.Add(developmentModeToggle);

            SetupRun.Changed -= OnRunChanged;
            SetupRun.Changed += OnRunChanged;

            wizardBuilt = true;
            ApplyView();

            // What the pages show changes behind the window's back (a package resolves, a login finishes in the browser, the editor
            // finishes compiling): a light poll keeps them honest without hooking every event.
            root.schedule.Execute(PollWizard).Every(500);
        }

        private void OnDisable()
        {
            SetupRun.Changed -= OnRunChanged;
        }

        #region Helpers

        private static Label MakeLabel(string text, params string[] classes)
        {
            Label label = new(text);
            foreach (string className in classes)
                label.AddToClassList(className);
            return label;
        }

        private static Button MakeButton(string text, System.Action onClick, params string[] classes)
        {
            Button button = new(onClick) { text = text };
            foreach (string className in classes)
                button.AddToClassList(className);
            return button;
        }

        private static VisualElement NewPage(string name)
        {
            VisualElement page = new() { name = name };
            page.AddToClassList("wizard-page");
            return page;
        }

        // Flex, not StyleKeyword.Null: on 6000.3 an element hidden once and then given back to its
        // stylesheet with Null kept resolving to None, so going Back to a page showed nothing.
        private static void SetShown(VisualElement element, bool shown)
        {
            if (element != null)
                element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static void OpenConsole() => EditorApplication.ExecuteMenuItem(console_menu_path);

        private enum RowState { None, Ok, Bad, Warn }

        /// <summary>A row of a page: a status icon, a name with a line of detail under it, and a button. Built once and updated in place,
        /// so a click is never lost to a row that was rebuilt under the pointer.</summary>
        private sealed class RowView
        {
            internal readonly VisualElement Root = new();
            private readonly VisualElement icon = new();
            private readonly Label name = new();
            private readonly Label chip = new();
            private readonly Label detail = new();
            private readonly Button button = new();
            private System.Action onClick;

            internal RowView(bool withIcon)
            {
                Root.AddToClassList("wizard-row");

                if (withIcon)
                {
                    icon.AddToClassList("wizard-icon");
                    Root.Add(icon);
                }

                VisualElement main = new();
                main.AddToClassList("wizard-row__main");
                VisualElement title = new();
                title.AddToClassList("wizard-row__title");
                name.AddToClassList("wizard-row__name");
                chip.AddToClassList("wizard-chip");
                title.Add(name);
                title.Add(chip);
                detail.AddToClassList("wizard-row__detail");
                main.Add(title);
                main.Add(detail);
                Root.Add(main);

                button.AddToClassList("wizard-row__button");
                button.clicked += () => onClick?.Invoke();
                Root.Add(button);
            }

            internal void Set(RowState state, string title, string detailText, string buttonText = null, bool buttonEnabled = true, System.Action click = null)
            {
                icon.EnableInClassList("wizard-icon--ok", state == RowState.Ok);
                icon.EnableInClassList("wizard-icon--bad", state == RowState.Bad);
                icon.EnableInClassList("wizard-icon--warn", state == RowState.Warn);

                name.text = title;
                detail.text = detailText ?? string.Empty;
                SetShown(detail, !string.IsNullOrEmpty(detailText));
                detail.EnableInClassList("wizard-bad", state == RowState.Bad);
                detail.EnableInClassList("wizard-warn", state == RowState.Warn);

                button.text = buttonText ?? string.Empty;
                SetShown(button, !string.IsNullOrEmpty(buttonText));
                button.SetEnabled(buttonEnabled);
                onClick = click;
                SetShown(chip, false);
            }

            /// <summary>A small coloured label after the title, after <see cref="Set"/>: <paramref name="modifier"/> picks the colour
            /// (<c>wizard-chip--{modifier}</c>), null the neutral one.</summary>
            internal void SetChip(string text, string modifier)
            {
                chip.text = text ?? string.Empty;
                chip.EnableInClassList("wizard-chip--sandbox", modifier == "sandbox");
                chip.EnableInClassList("wizard-chip--prep", modifier == "prep");
                SetShown(chip, !string.IsNullOrEmpty(text));
            }
        }

        #endregion

        #region Introduction

        private VisualElement BuildIntroPage()
        {
            VisualElement page = NewPage("wizard-intro");
            page.Add(MakeLabel("Set up this project for Virtuademy", "wizard-title"));

            page.Add(IntroRow("1", "Connect your organization", "Import the file from the Back Office and log in. You can do it later."));
            page.Add(IntroRow("2", "Choose the version and scripting", "Which Virtuademy version, and whether you also write C# scripts."));
            page.Add(IntroRow("3", "Start setup", "5 to 15 minutes. Unity restarts at the end only if a setting needs it, and only when you click Restart."));

            Label backup = MakeLabel("Back up or commit the project first: the setup changes project settings.", "wizard-text", "wizard-warn");
            backup.style.marginTop = 8;
            page.Add(backup);

            Toggle hide = new("Don't show this introduction again") { tooltip = "Saved for this project and this user, in UserSettings/Virtuademy/setup.json." };
            hide.AddToClassList("wizard-check");
            hide.SetValueWithoutNotify(SetupUserSettings.HideIntroduction);
            hide.RegisterValueChangedCallback(evt => SetupUserSettings.HideIntroduction = evt.newValue);
            page.Add(hide);

            VisualElement buttons = new();
            buttons.AddToClassList("wizard-buttons");
            buttons.Add(MakeButton("Get started", () => GoTo(WizardPage.Connection), "wizard-button", "wizard-primary"));
            page.Add(buttons);

            return page;
        }

        private static VisualElement IntroRow(string number, string title, string text)
        {
            VisualElement row = new();
            row.AddToClassList("wizard-intro-row");
            row.Add(MakeLabel(number, "wizard-intro-row__number"));

            VisualElement body = new();
            body.AddToClassList("wizard-intro-row__body");
            body.Add(MakeLabel(title, "wizard-intro-row__title"));
            body.Add(MakeLabel(text, "wizard-note"));
            row.Add(body);
            return row;
        }

        #endregion

        #region Navigation

        private VisualElement BuildNavigation()
        {
            VisualElement nav = new() { name = "wizard-nav" };
            nav.AddToClassList("wizard-nav");

            navBack = MakeButton("Back", OnNavBack, "wizard-nav__button");
            nav.Add(navBack);

            navHint = MakeLabel(string.Empty, "wizard-nav__hint");
            nav.Add(navHint);

            navSecondary = MakeButton(string.Empty, OnNavSecondary, "wizard-nav__button");
            nav.Add(navSecondary);

            navPrimary = MakeButton(string.Empty, OnNavPrimary, "wizard-nav__button", "wizard-primary");
            nav.Add(navPrimary);
            return nav;
        }

        /// <summary>Which buttons the bar under the page has, and what they say: it is the one place that knows the flow between the pages.</summary>
        private void RefreshNav()
        {
            if (!wizardBuilt)
                return;

            bool shown = true;
            bool back = false;
            string secondary = null;
            string primary = null;
            bool primaryEnabled = true;
            string hint = string.Empty;

            switch (Page)
            {
                case WizardPage.Intro:
                    shown = false;
                    break;
                case WizardPage.Connection:
                    back = !connectionFromStatus;
                    secondary = connectionFromStatus ? null : "Connect later";
                    primary = connectionFromStatus ? "Done" : "Next";
                    break;
                case WizardPage.Choices:
                    back = true;
                    primary = "Next";
                    hint = choicesStartBlock;
                    primaryEnabled = string.IsNullOrEmpty(choicesStartBlock);
                    break;
                case WizardPage.Summary:
                    back = true;
                    primary = "Start setup";
                    hint = summaryStartBlock;
                    primaryEnabled = string.IsNullOrEmpty(summaryStartBlock);
                    break;
                case WizardPage.Run:
                    shown = !SetupRun.IsRunning && RunHasFailed;
                    back = true;
                    primary = "Try again";
                    break;
                case WizardPage.Status:
                    primary = "Run setup again";
                    primaryEnabled = !SetupRun.IsRunning && !compiling;
                    break;
            }

            SetShown(navBack, back);
            SetShown(navSecondary, secondary != null);
            navSecondary.text = secondary ?? string.Empty;
            SetShown(navPrimary, primary != null);
            navPrimary.text = primary ?? string.Empty;
            navPrimary.SetEnabled(primaryEnabled);
            navHint.text = hint ?? string.Empty;
            SetShown(navHint, !string.IsNullOrEmpty(hint));

            bool visible = shown && dataLoaded && !loadFailed && !showAllChecks;
            SetShown(wizardNav, visible);

        }

        private void OnNavBack()
        {
            switch (Page)
            {
                case WizardPage.Connection:
                    GoTo(WizardPage.Intro);
                    break;
                case WizardPage.Choices:
                    GoTo(choicesChangeMode ? WizardPage.Status : WizardPage.Connection);
                    break;
                case WizardPage.Summary:
                    GoTo(summaryFromStatus ? WizardPage.Status : WizardPage.Choices);
                    break;
                case WizardPage.Run:
                    GoTo(runReturnPage == (int)WizardPage.Status ? WizardPage.Status : WizardPage.Choices);
                    break;
            }
        }

        private void OnNavSecondary()
        {
            // "Connect later": the same way on as Next, and nothing is asked of the person.
            if (Page == WizardPage.Connection)
                LeaveConnectionPage();
        }

        private void OnNavPrimary()
        {
            switch (Page)
            {
                case WizardPage.Connection:
                    LeaveConnectionPage();
                    break;
                case WizardPage.Choices:
                    OpenSummary(false);
                    break;
                case WizardPage.Summary:
                    StartFromSummary();
                    break;
                case WizardPage.Run:
                    TryRunAgain();
                    break;
                case WizardPage.Status:
                    RunSetupAgain();
                    break;
            }
        }

        #endregion

        #region State

        // Read once per poll (and per load): asking Package Manager several times per tick is waste.
        private bool sdkRegisteredSnapshot;
        private bool compiling;
        private bool importing;

        private void UpdateSdkSnapshot()
        {
            sdkRegisteredSnapshot = IsSdkRegistered();
            compiling = EditorApplication.isCompiling;
            importing = EditorApplication.isUpdating;
        }

        private static bool IsSdkRegistered()
            => UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(p => p.name == sdk_environments_package_name);

        /// <summary>The version the project has installed: the one it records, once the SDK is in. Null for a project that has no SDK yet, whose
        /// recorded version is only the window's default.</summary>
        private string InstalledVersion()
            => sdkRegisteredSnapshot && !string.IsNullOrEmpty(packageManagerConfig?.CurrentInstallationVersion) ? packageManagerConfig.CurrentInstallationVersion : null;

        /// <summary>The page a window opens on: where the run is while one is going, the Introduction (or the Connection page, when the
        /// person turned the Introduction off) for a project that is not set up, the status of one that is.</summary>
        private WizardPage ChooseStartPage()
        {
            if (SetupRun.IsRunning)
                return WizardPage.Run;

            if (!sdkRegisteredSnapshot)
                return SetupUserSettings.HideIntroduction ? WizardPage.Connection : WizardPage.Intro;

            return WizardPage.Status;
        }

        /// <summary>Called after every successful load: the checks have values now.</summary>
        private void OnWizardDataLoaded()
        {
            dataLoaded = true;
            loadFailed = false;
            cachedPlan = null;
            UpdateSdkSnapshot();
            pendingConnection = SetupUserSettings.Pending;

            // The engine plans against this registry (the Unity version of an entry) instead of reading it again.
            SetupSteps.UseRegistry(packageManagerConfig.AllVersionsPackageRegistry);

            if (wizardPage < 0 || wizardPage > (int)WizardPage.Summary || !SessionState.GetBool(wizard_page_chosen_key, false))
            {
                wizardPage = (int)ChooseStartPage();
                SessionState.SetBool(wizard_page_chosen_key, true);
            }

            // Nothing to see on the Run page once the run is over and went well: its result is the status.
            if (Page == WizardPage.Run && !SetupRun.IsRunning && !RunHasFailed)
                wizardPage = (int)WizardPage.Status;

            ReadConnections();
            EnterPage(Page);
            ApplyView();
            RefreshNav();
        }

        private void GoTo(WizardPage page)
        {
            wizardPage = (int)page;
            root.Q<ScrollView>("ScrollView").scrollOffset = Vector2.zero;
            EnterPage(page);
            ApplyView();
            RefreshNav();
        }

        private void EnterPage(WizardPage page)
        {
            switch (page)
            {
                case WizardPage.Connection:
                    RefreshConnectionPage(true);
                    break;
                case WizardPage.Choices:
                    RefreshChoicesPage();
                    break;
                case WizardPage.Summary:
                    RefreshSummaryPage();
                    break;
                case WizardPage.Run:
                    RefreshRunPage();
                    break;
                case WizardPage.Status:
                    RefreshStatusPage(true);
                    break;
            }
        }

        /// <summary>The long page (true) or the wizard. The long page's buttons would start work beside a run, so it is refused while one is going.</summary>
        private void SetShowAllChecks(bool value)
        {
            if (value && SetupRun.IsRunning)
            {
                EditorUtility.DisplayDialog("All setup checks", "A setup run is going. The page of all checks is available when it ends.", "OK");
                return;
            }

            showAllChecks = value;
            ApplyView();
            RefreshNav();
        }

        private bool DevelopmentMode => packageManagerConfig != null && packageManagerConfig.ShowPrereleases;

        /// <summary>Turns development mode on or off for the whole setup: the version lists and the details of the Summary. Saved in the
        /// project's setup configuration, so it outlasts the editor restart that ends a setup.</summary>
        private void SetDevelopmentMode(bool value)
        {
            if (packageManagerConfig == null)
            {
                developmentModeToggle?.SetValueWithoutNotify(false);
                return;
            }

            packageManagerConfig.ShowPrereleases = value;
            EditorUtility.SetDirty(packageManagerConfig);
            AssetDatabase.SaveAssetIfDirty(packageManagerConfig);
            UpdateAvailableVersions();

            ApplyView();

            connectionsSignature = null;
            switch (Page)
            {
                case WizardPage.Connection:
                    RefreshConnectionPage(true);
                    break;
                case WizardPage.Choices:
                    RefreshChoicesPage();
                    break;
                case WizardPage.Summary:
                    RefreshSummaryPage();
                    break;
                case WizardPage.Status:
                    RefreshStatusPage(true);
                    break;
            }
        }

        private void OnRunChanged()
        {
            if (!wizardBuilt || root == null || root.panel == null || !dataLoaded)
                return;

            if (Page == WizardPage.Run)
                RefreshRunPage();
            if (Page == WizardPage.Status)
                RefreshStatusPage(true);
            RefreshNav();

            // The run changed the project: read it again, so the status shows what is true now.
            if (runStartedHere && !SetupRun.IsRunning)
            {
                runStartedHere = false;
                if (!isSetupping)
                    SetupWindowData();
            }
        }

        private void PollWizard()
        {
            if (!wizardBuilt || showAllChecks || !dataLoaded || loadFailed)
                return;

            UpdateSdkSnapshot();
            ResumePendingConnection();

            switch (Page)
            {
                case WizardPage.Connection:
                    RefreshConnectionPage(false);
                    break;
                case WizardPage.Choices:
                    RefreshChoicesPage();
                    break;
                case WizardPage.Summary:
                    RefreshSummaryPage();
                    break;
                case WizardPage.Run:
                    RefreshRunPage();
                    break;
                case WizardPage.Status:
                    RefreshStatusPage(false);
                    break;
            }

            RefreshNav();
        }

        #endregion

        #region View

        /// <summary>
        /// Which parts of the window are visible. In "show all checks" mode this is the long page as it always was: both sections,
        /// minus what the load error hides. Otherwise it is the page of the wizard, the bar under it, and none of the long page.
        /// </summary>
        private void ApplyView()
        {
            if (!wizardBuilt)
                return;

            bool ready = dataLoaded && !loadFailed;

            developmentModeToggle.SetValueWithoutNotify(DevelopmentMode);
            SetShown(backToWizardButton, showAllChecks);

            bool wizard = !showAllChecks;

            SetShown(wizardLoading, wizard && !ready && !loadFailed);
            SetShown(pageIntro, wizard && ready && Page == WizardPage.Intro);
            SetShown(pageConnection, wizard && ready && Page == WizardPage.Connection);
            SetShown(pageChoices, wizard && ready && Page == WizardPage.Choices);
            SetShown(pageSummary, wizard && ready && Page == WizardPage.Summary);
            SetShown(pageRun, wizard && ready && Page == WizardPage.Run);
            SetShown(pageStatus, wizard && ready && Page == WizardPage.Status);

            // Both sections are the long page's: the wizard has its own pages.
            SetShown(settingsSection, !wizard && !loadFailed);
            SetShown(packageSection, !wizard && !loadFailed);
            pageSeparators.ForEach(separator => SetShown(separator, !wizard));

            RefreshNav();
        }

        #endregion
    }
}
