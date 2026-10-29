using System.Collections.Generic;
using System.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.UIElements;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// The Choices page: which Virtuademy version, and how scripts are written (Visual Scripting only, or with C# too). Next opens the
    /// Summary page, which is the confirmation: the choices, what the setup will do (<see cref="SetupRun.Plan"/>'s answer, not a second
    /// opinion), what cannot be undone, the state of this computer when something is missing, and Start setup, which hands a
    /// <see cref="SetupChoices"/> to <see cref="SetupRun"/>. In development mode the Summary also shows each step's detail, what is
    /// already done, and the computer's checks when they pass.
    ///
    /// Only the versions the run can install are offered: those whose registry entry has <c>virtuademy-sdk-environments</c> (2026.6 on).
    /// The default is the platform version the computer's connections agree on, when the registry lists it, else the latest release; a
    /// project that has the SDK already opens on its own version.
    /// </summary>
    public partial class CreatorKitSetupWindow
    {
        // Serialized, like the page: a package action ends in a domain reload.
        [SerializeField] private bool choicesChangeMode;
        [SerializeField] private string choiceVersion;
        [SerializeField] private int choiceScripting;
        [SerializeField] private bool choiceAllowDowngrade;

        // The Summary's tick for removing the interpreter from a project that has C# scripts: asked again each time the Summary opens.
        [SerializeField] private bool choiceAllowScriptRemoval;

        // The Summary was opened by the status page's Run setup again (Back goes there), not by Choices.
        [SerializeField] private bool summaryFromStatus;

        // Why Next (Choices) or Start setup (Summary) is not possible now, shown by the bar under the page; null when it is.
        private string choicesStartBlock;
        private string summaryStartBlock;

        private DropdownField versionDropdown;
        private Label versionUnityLabel;
        private Label versionNoteLabel;
        private Toggle downgradeToggle;
        private DropdownField scriptingDropdown;
        private Label scriptingNoteLabel;
        private VisualElement summaryBody;
        [System.NonSerialized] private string summarySignature;

        // The plan reads the project (the assets, the registered packages) and the page is refreshed by the poll twice a second:
        // it is read again when a choice changes, when the project is read again, or after this long.
        private const double plan_refresh_seconds = 3;
        private IReadOnlyList<SetupStepStatus> cachedPlan;
        private string cachedPlanKey;
        private double cachedPlanAt;

        private static readonly string[] scripting_choices = { "Visual Scripting only", "Visual Scripting and C# scripts" };
        private static readonly string[] scripting_notes =
        {
            "Graphs only. The fastest setup; you can add C# later.",
            "Also C# scripts in Assets/VirtuademyEnvironmentScripts. Installs the interpreter: about 5 minutes more, needs git.",
        };

        #region Building

        private VisualElement BuildChoicesPage()
        {
            VisualElement page = NewPage("wizard-choices");
            page.Add(MakeLabel("Choose your setup", "wizard-title"));

            page.Add(MakeLabel("Virtuademy version", "wizard-heading"));
            versionDropdown = new DropdownField();
            versionDropdown.AddToClassList("wizard-field");
            versionDropdown.RegisterValueChangedCallback(evt => OnVersionPicked(evt.newValue));
            page.Add(versionDropdown);

            versionUnityLabel = MakeLabel(string.Empty, "wizard-text");
            page.Add(versionUnityLabel);
            versionNoteLabel = MakeLabel(string.Empty, "wizard-text");
            page.Add(versionNoteLabel);

            downgradeToggle = new Toggle("I committed or backed up the project");
            downgradeToggle.AddToClassList("wizard-check");
            downgradeToggle.RegisterValueChangedCallback(evt =>
            {
                choiceAllowDowngrade = evt.newValue;
                RefreshChoicesPage();
            });
            page.Add(downgradeToggle);

            page.Add(MakeLabel("Scripting", "wizard-heading"));
            scriptingDropdown = new DropdownField(scripting_choices.ToList(), 0);
            scriptingDropdown.AddToClassList("wizard-field");
            scriptingDropdown.RegisterValueChangedCallback(_ =>
            {
                choiceScripting = scriptingDropdown.index;
                RefreshChoicesPage();
            });
            page.Add(scriptingDropdown);
            scriptingNoteLabel = MakeLabel(string.Empty, "wizard-note");
            page.Add(scriptingNoteLabel);
            return page;
        }

        private VisualElement BuildSummaryPage()
        {
            VisualElement page = NewPage("wizard-summary");
            page.Add(MakeLabel("Ready to set up", "wizard-title"));
            summaryBody = new VisualElement();
            page.Add(summaryBody);
            return page;
        }

        #endregion

        #region Versions

        private PackageRegistry[] Registry => packageManagerConfig?.AllVersionsPackageRegistry ?? new PackageRegistry[0];

        /// <summary>The platform version the computer's connections agree on, when the registry lists it (and the list shows it).</summary>
        private PackageRegistry RecommendedEntry(List<PackageRegistry> offered, out string label)
        {
            label = null;

            List<string> versions = connections.Select(connection => NormalizedVersion(connection.PlatformVersion)).Where(version => version.Length > 0).Distinct().ToList();
            if (versions.Count != 1)
                return null;

            PackageRegistry entry = offered.FirstOrDefault(candidate => NormalizedVersion(candidate.ReflectisVersion) == versions[0]);
            if (entry == null || (entry.Prerelease && !packageManagerConfig.ShowPrereleases))
                return null;

            List<string> tenants = connections
                .Where(connection => NormalizedVersion(connection.PlatformVersion) == versions[0] && !string.IsNullOrEmpty(connection.TenantLabel))
                .Select(connection => connection.TenantLabel)
                .Distinct()
                .ToList();
            label = $"{entry.ReflectisVersion} · {(tenants.Count == 1 ? tenants[0] + "'s" : "your organizations'")} version (recommended)";
            return entry;
        }

        /// <summary>The list of the dropdown: the recommended version, the latest release, the previous ones newest first, the development version in development mode.</summary>
        private List<(PackageRegistry entry, string label)> BuildVersionOptions(out PackageRegistry recommended)
        {
            List<PackageRegistry> offered = Registry
                .Where(entry => entry.Packages != null && entry.Packages.Any(package => package.Name == sdk_environments_package_name))
                .ToList();

            List<(PackageRegistry entry, string label)> options = new();
            recommended = RecommendedEntry(offered, out string recommendedLabel);
            if (recommended != null)
                options.Add((recommended, recommendedLabel));

            List<PackageRegistry> releases = offered.Where(entry => !entry.Prerelease).ToList();
            releases.Reverse();
            for (int i = 0; i < releases.Count; i++)
            {
                if (releases[i] != recommended)
                    options.Add((releases[i], i == 0 ? $"{releases[i].ReflectisVersion} (latest)" : releases[i].ReflectisVersion));
            }

            if (packageManagerConfig.ShowPrereleases)
            {
                PackageRegistry development = offered.FirstOrDefault(entry => entry.ReflectisVersion == "develop") ?? offered.LastOrDefault(entry => entry.Prerelease);
                if (development != null && development != recommended)
                    options.Add((development, $"{development.ReflectisVersion} (development)"));
            }

            return options;
        }

        private void OnVersionPicked(string label)
        {
            List<(PackageRegistry entry, string label)> options = BuildVersionOptions(out _);
            PackageRegistry picked = options.FirstOrDefault(option => option.label == label).entry;
            if (picked == null || picked.ReflectisVersion == choiceVersion)
                return;

            choiceVersion = picked.ReflectisVersion;
            // The confirmation is for one version: another choice asks again.
            choiceAllowDowngrade = false;
            RefreshChoicesPage();
        }

        #endregion

        #region Refreshing

        private void RefreshChoicesPage()
        {
            if (versionDropdown == null || packageManagerConfig == null || !dataLoaded)
                return;

            List<(PackageRegistry entry, string label)> options = BuildVersionOptions(out PackageRegistry recommended);
            string installed = InstalledVersion();

            PackageRegistry selected = options.FirstOrDefault(option => option.entry.ReflectisVersion == choiceVersion).entry;
            if (selected == null)
            {
                // A project that has the SDK opens on its own version; a new one on what its connections run, else the latest release.
                selected = (choicesChangeMode ? options.FirstOrDefault(option => option.entry.ReflectisVersion == installed).entry : null)
                           ?? recommended
                           ?? options.FirstOrDefault().entry;
                choiceVersion = selected?.ReflectisVersion;
                choiceAllowDowngrade = false;
            }

            List<string> labels = options.Select(option => option.label).ToList();
            if (!versionDropdown.choices.SequenceEqual(labels))
                versionDropdown.choices = labels;
            versionDropdown.SetValueWithoutNotify(options.FirstOrDefault(option => option.entry == selected).label);
            versionDropdown.SetEnabled(options.Count > 0);

            // Unity: the engine refuses a version for another editor.
            string unityText = string.Empty;
            bool unityOk = true;
            if (selected != null)
            {
                string required = selected.RequiredUnityVersion;
                unityOk = !string.IsNullOrEmpty(required) && required == UnityVersion;
                unityText = string.IsNullOrEmpty(required)
                    ? $"Virtuademy {selected.ReflectisVersion} does not name a Unity version."
                    : unityOk
                        ? $"Needs Unity {required}: this editor."
                        : $"Needs Unity {required}: this editor is {UnityVersion}. Open the project with that editor, or choose another version.";
            }
            else
            {
                unityText = "No version is available.";
                unityOk = false;
            }

            // Said only when it blocks: the Summary names the Unity in development mode.
            versionUnityLabel.text = unityText;
            versionUnityLabel.EnableInClassList("wizard-bad", !unityOk);
            SetShown(versionUnityLabel, !unityOk);

            // Upgrade or downgrade of a project that has the SDK.
            int? order = selected == null || installed == null ? null : SetupRun.CompareVersions(selected.ReflectisVersion, installed);
            string note = string.Empty;
            bool noteWarns = false;
            if (order < 0)
            {
                note = $"Virtuademy {selected.ReflectisVersion} is older than the installed {installed}. An older version can break the project.";
                noteWarns = true;
            }
            else if (order > 0)
            {
                note = $"This project has Virtuademy {installed}. The update routine of the new version runs too, when it has one.";
            }
            else if (order == 0)
            {
                note = $"Virtuademy {installed} is already installed: the setup checks the rest of the project.";
            }

            versionNoteLabel.text = note;
            SetShown(versionNoteLabel, note.Length > 0);
            versionNoteLabel.EnableInClassList("wizard-warn", noteWarns);
            versionNoteLabel.EnableInClassList("wizard-muted", !noteWarns);

            bool downgrade = order < 0;
            SetShown(downgradeToggle, downgrade);
            downgradeToggle.SetValueWithoutNotify(choiceAllowDowngrade);

            // Scripting.
            scriptingDropdown.SetValueWithoutNotify(scripting_choices[Mathf.Clamp(choiceScripting, 0, 1)]);

            // Visual Scripting only on a project that has the interpreter takes it out: said here already, in orange.
            bool removesInterpreter = choiceScripting == 0 && SetupSteps.InterpreterInManifest();
            scriptingNoteLabel.text = removesInterpreter
                ? "Removes the interpreter installed in this project: its C# scripts stay, but no longer run in Virtuademy."
                : scripting_notes[Mathf.Clamp(choiceScripting, 0, 1)];
            scriptingNoteLabel.EnableInClassList("wizard-warn", removesInterpreter);

            choicesStartBlock = ChoicesBlock(selected, unityOk, order);
        }

        private IReadOnlyList<SetupStepStatus> ReadPlan(SetupChoices choices)
        {
            string key = $"{choices.Version}|{choices.Scripting}|{choices.AllowDowngrade}|{sdkRegisteredSnapshot}";
            double now = EditorApplication.timeSinceStartup;
            if (cachedPlan == null || key != cachedPlanKey || now - cachedPlanAt > plan_refresh_seconds)
            {
                cachedPlan = SetupRun.Plan(choices);
                cachedPlanKey = key;
                cachedPlanAt = now;
            }

            return cachedPlan;
        }

        private SetupChoices CurrentChoices(PackageRegistry selected)
        {
            int? order = selected == null || InstalledVersion() == null ? null : SetupRun.CompareVersions(selected.ReflectisVersion, InstalledVersion());
            return new SetupChoices
            {
                Version = selected?.ReflectisVersion,
                Scripting = choiceScripting == 1 ? SetupScripting.VisualScriptingAndCSharp : SetupScripting.VisualScriptingOnly,
                AllowDowngrade = order < 0 && choiceAllowDowngrade,
                AllowScriptRemoval = choiceAllowScriptRemoval,
            };
        }

        /// <summary>What keeps the choices from going on to the Summary: the version, its Unity, an unconfirmed downgrade, C# without an interpreter.</summary>
        private string ChoicesBlock(PackageRegistry selected, bool unityOk, int? order)
        {
            if (SetupRun.IsRunning)
                return "A setup run is going.";
            if (selected == null)
                return "No version is available.";
            if (!unityOk)
                return $"Another Unity is needed for Virtuademy {selected.ReflectisVersion}.";
            if (order < 0 && !choiceAllowDowngrade)
                return "Confirm that the project is backed up to go back to an older version.";
            if (choiceScripting == 1 && selected.Interpreter == null)
                return $"Virtuademy {selected.ReflectisVersion} has no interpreter for C# scripts (versions before 2026.6 have none).";
            return null;
        }

        #endregion

        #region Summary

        /// <summary>Opens the Summary: from Choices (Next), or from the status page's Run setup again, with the installed version.</summary>
        private void OpenSummary(bool fromStatus)
        {
            summaryFromStatus = fromStatus;
            summarySignature = null;
            choiceAllowScriptRemoval = false;
            GoTo(WizardPage.Summary);
        }

        // Any entry of the registry, not only those the dropdown lists: Run setup again uses the installed one.
        private PackageRegistry SummaryEntry => Registry.FirstOrDefault(entry => entry.ReflectisVersion == choiceVersion);

        /// <summary>
        /// The confirmation before a run: the choices, what the setup will do, what cannot be undone, and this computer when something is
        /// missing. Development mode adds each step's detail, what is already done, and the computer's checks that pass. Rebuilt only when
        /// what it shows changed.
        /// </summary>
        private void RefreshSummaryPage()
        {
            if (summaryBody == null || packageManagerConfig == null || !dataLoaded)
                return;

            PackageRegistry selected = SummaryEntry;
            string installed = InstalledVersion();
            int? order = selected == null || installed == null ? null : SetupRun.CompareVersions(selected.ReflectisVersion, installed);
            bool unityOk = selected != null && !string.IsNullOrEmpty(selected.RequiredUnityVersion) && selected.RequiredUnityVersion == UnityVersion;
            SetupChoices choices = CurrentChoices(selected);
            IReadOnlyList<SetupStepStatus> plan = selected == null ? new List<SetupStepStatus>() : ReadPlan(choices);
            List<string> irreversible = selected == null ? new List<string>() : SetupRun.DestructiveActions(choices, plan);
            bool git = projectConfig.IsGitInstalled;
            bool modules = projectConfig.AllEditorModulesInstalled;
            bool development = DevelopmentMode;

            // The run takes the interpreter out (Visual Scripting only on a project that has it); with C# scripts in the project, only
            // after the person ticks that they understand those scripts stop running.
            bool removesInterpreter = plan.Any(step => step.Id == SetupStepIds.Interpreter && step.State == SetupStepState.Pending
                                                       && step.Detail == SetupRun.RemovesInterpreterDetail);
            int scripts = removesInterpreter ? SetupSteps.CountEnvironmentScripts() : 0;

            summaryStartBlock = SummaryBlock(selected, unityOk, order, plan, git, modules)
                                ?? (scripts > 0 && !choiceAllowScriptRemoval ? "Confirm that your C# scripts will no longer run." : null);

            string signature = string.Join("|", plan.Select(step => $"{step.Id}/{step.State}/{step.Detail}"))
                               + $"#{choices.Version}#{choices.Scripting}#{installed}#{string.Join("|", irreversible)}#{git}#{modules}#{development}#{isSetupping}#{scripts}";
            if (signature == summarySignature)
                return;

            summarySignature = signature;
            summaryBody.Clear();

            // The choices, in a card: what the person confirms, so the values stand out and the names are small.
            summaryBody.Add(MakeLabel("Your choices", "wizard-heading"));
            VisualElement card = new();
            card.AddToClassList("wizard-choice-card");

            string versionNote = order > 0 ? $"Updates the project from {installed}."
                : order < 0 ? $"Goes back from {installed}: an older version can break the project."
                : order == 0 ? "Already installed: the setup checks the rest of the project."
                : null;
            card.Add(ChoiceLine("Virtuademy version", selected == null ? "None" : selected.ReflectisVersion,
                selected != null && selected.Prerelease ? "Development" : null, versionNote, order < 0));

            bool csharp = choices.Scripting == SetupScripting.VisualScriptingAndCSharp;
            card.Add(ChoiceLine("Scripting", scripting_choices[csharp ? 1 : 0], null,
                csharp ? "Installs the interpreter for C# scripts: about 5 minutes more."
                : removesInterpreter ? "Removes the interpreter installed in this project."
                : null,
                removesInterpreter));
            summaryBody.Add(card);

            // What the setup will do: the titles, with the reason of a step it refuses; the details in development mode.
            summaryBody.Add(MakeLabel("What the setup will do", "wizard-heading"));
            List<SetupStepStatus> todo = plan.Where(step => step.State != SetupStepState.Skipped).ToList();
            if (todo.Count == 0)
                summaryBody.Add(MakeLabel("Nothing: the project is already set up.", "wizard-text", "wizard-muted"));

            foreach (SetupStepStatus step in todo)
            {
                bool failed = step.State == SetupStepState.Failed;
                bool removal = step.Id == SetupStepIds.Interpreter && step.Detail == SetupRun.RemovesInterpreterDetail;
                string text = removal ? "Remove the interpreter for C# scripts"
                    : (failed || development) && !string.IsNullOrEmpty(step.Detail) ? $"{step.Title}: {step.Detail}"
                    : step.Title;
                summaryBody.Add(MakeLabel("• " + text, "wizard-text", failed ? "wizard-bad" : removal ? "wizard-warn" : "wizard-muted"));
            }

            if (development)
            {
                List<string> skipped = plan.Where(step => step.State == SetupStepState.Skipped).Select(step => step.Title).ToList();
                if (skipped.Count > 0)
                    summaryBody.Add(MakeLabel($"Already done or not needed: {string.Join(", ", skipped)}.", "wizard-note"));
            }

            // What cannot be undone: this page is the confirmation, so it says it here, before Start setup.
            if (irreversible.Count > 0)
            {
                summaryBody.Add(MakeLabel("Cannot be undone from here", "wizard-heading"));
                foreach (string action in irreversible)
                    summaryBody.Add(MakeLabel("• " + action, "wizard-text", "wizard-warn"));
                summaryBody.Add(MakeLabel("Back up or commit the project before you start.", "wizard-text", "wizard-warn"));
            }

            // The C# scripts that stop running: in red, with a tick that Start setup waits for.
            if (scripts > 0)
            {
                VisualElement danger = new();
                danger.AddToClassList("wizard-banner");
                danger.AddToClassList("wizard-banner--danger");
                danger.Add(MakeLabel($"Your C# scripts will stop working", "wizard-heading", "wizard-bad"));
                danger.Add(MakeLabel($"This project has {scripts} C# script(s) in {SetupSteps.environment_scripts_folder}. Without the interpreter " +
                                     "they stay in the project but no longer run in Virtuademy: the environments that use them stop doing what " +
                                     "the scripts do, and publishing them does not include the scripts. Choose \"Visual Scripting and C# scripts\" " +
                                     "to keep them.", "wizard-text"));
                Toggle understood = new("I understand that my C# scripts will no longer run");
                understood.AddToClassList("wizard-check");
                understood.SetValueWithoutNotify(choiceAllowScriptRemoval);
                understood.RegisterValueChangedCallback(evt =>
                {
                    choiceAllowScriptRemoval = evt.newValue;
                    RefreshSummaryPage();
                    RefreshNav();
                });
                danger.Add(understood);
                summaryBody.Add(danger);
            }
            else
            {
                summaryBody.Add(MakeLabel("The setup changes project settings: back up or commit the project first.", "wizard-note"));
            }

            // This computer: only when something is missing, or in development mode.
            if (!git || !modules || development)
            {
                summaryBody.Add(MakeLabel("This computer", "wizard-heading"));

                RowView gitRow = new(withIcon: true);
                gitRow.Set(git ? RowState.Ok : RowState.Bad, "Git",
                    git ? projectConfig.GitVersion : "Git is not installed. Install it from git-scm.com, then press Check again.");
                summaryBody.Add(gitRow.Root);

                RowView modulesRow = new(withIcon: true);
                modulesRow.Set(modules ? RowState.Ok : RowState.Bad, "Editor build modules",
                    modules
                        ? "Android, iOS, WebGL and Windows are installed."
                        : $"Missing: {string.Join(", ", projectConfig.InstalledModules.Where(module => !module.Value).Select(module => module.Key))}. Install them from Unity Hub, then press Check again.");
                summaryBody.Add(modulesRow.Root);

                VisualElement buttons = new();
                buttons.AddToClassList("wizard-buttons");
                Button check = MakeButton("Check again", () =>
                {
                    if (!isSetupping)
                        SetupWindowData();
                }, "wizard-button");
                check.tooltip = "Reads the computer and the project again, as the refresh button of the Package manager does.";
                check.SetEnabled(!isSetupping);
                buttons.Add(check);
                summaryBody.Add(buttons);
            }

            if (development && selected != null)
                summaryBody.Add(MakeLabel($"Needs Unity {selected.RequiredUnityVersion}; this editor is {UnityVersion}.", "wizard-note"));
        }

        /// <summary>One row of the Summary's grid of choices: the name in bold on the left; on the right the value, a chip, and a grey note under it.</summary>
        private static VisualElement ChoiceLine(string name, string value, string chip, string note, bool noteWarns)
        {
            VisualElement line = new();
            line.AddToClassList("wizard-choice");
            line.Add(MakeLabel(name, "wizard-choice__name"));

            VisualElement body = new();
            body.AddToClassList("wizard-choice__body");

            VisualElement valueRow = new();
            valueRow.AddToClassList("wizard-row__title");
            valueRow.Add(MakeLabel(value, "wizard-choice__value"));
            if (!string.IsNullOrEmpty(chip))
                valueRow.Add(MakeLabel(chip, "wizard-chip", "wizard-chip--dev"));
            body.Add(valueRow);

            if (!string.IsNullOrEmpty(note))
                body.Add(noteWarns ? MakeLabel(note, "wizard-choice__note", "wizard-warn") : MakeLabel(note, "wizard-choice__note"));

            line.Add(body);
            return line;
        }

        /// <summary>What keeps Start setup from running: the choices' own blocks, then this computer, a busy Unity, and a step the engine refuses.</summary>
        private string SummaryBlock(PackageRegistry selected, bool unityOk, int? order, IReadOnlyList<SetupStepStatus> plan, bool git, bool modules)
        {
            string block = ChoicesBlock(selected, unityOk, order);
            if (block != null)
                return block;
            if (!git)
                return "Git is missing: install it, then press Check again.";
            if (!modules)
                return "An editor module is missing: install it from Unity Hub, then press Check again.";
            if (compiling || importing)
                return "Unity is busy: wait for it to finish.";

            SetupStepStatus refused = plan.FirstOrDefault(step => step.State == SetupStepState.Failed);
            return refused?.Detail;
        }

        private void StartFromSummary()
        {
            RefreshSummaryPage();
            PackageRegistry selected = SummaryEntry;
            if (selected == null || !string.IsNullOrEmpty(summaryStartBlock))
                return;

            StartRun(CurrentChoices(selected), null, summaryFromStatus || choicesChangeMode ? WizardPage.Status : WizardPage.Choices);
        }

        #endregion
    }
}
