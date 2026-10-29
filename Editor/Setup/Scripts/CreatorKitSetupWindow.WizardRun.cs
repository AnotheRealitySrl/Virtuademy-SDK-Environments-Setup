using System.Collections.Generic;
using System.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.UIElements;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// The Run page: "Setting up your project", the position and percentage of <see cref="SetupRun"/>, a progress bar and the list of its
    /// steps with their state and detail. It starts nothing by itself and runs nothing: <see cref="StartRun"/> hands a
    /// <see cref="SetupChoices"/> to the engine, and the page follows <see cref="SetupRun.Changed"/> (and the poll, so a step that
    /// blocks the editor is on the page before it starts). A run that ends well moves on to the Project status page; one that fails stays
    /// here, with the step's reason, and the bar under the page offers Back and Try again.
    /// </summary>
    public partial class CreatorKitSetupWindow
    {
        // Serialized: the run goes on across the domain reloads that installing packages causes, and so does this page.
        [SerializeField] private int runReturnPage = (int)WizardPage.Choices;
        [SerializeField] private bool runStartedHere;
        [SerializeField] private string lastRunVersion;
        [SerializeField] private int lastRunScripting;
        [SerializeField] private bool lastRunDowngrade;
        [SerializeField] private string lastRunOnly;
        [SerializeField] private bool runStopRequested;

        private Label runPositionLabel;
        private VisualElement runProgressFill;
        private VisualElement runList;
        private Label runBlockingLabel;
        private Label runKeepOpenLabel;
        private Button runStopButton;
        private VisualElement runFailure;
        [System.NonSerialized] private string runSignature;

        // The steps that keep the editor from answering while they work (the engine says so in its documentation).
        private static readonly string[] blocking_steps = { SetupStepIds.Interpreter, SetupStepIds.VisualScriptingNodes };

        private bool RunHasFailed => SetupRun.Steps.Any(step => step.State == SetupStepState.Failed);

        #region Building

        private VisualElement BuildRunPage()
        {
            VisualElement page = NewPage("wizard-run");
            page.Add(MakeLabel("Setting up your project", "wizard-title"));

            runPositionLabel = MakeLabel(string.Empty, "wizard-text");
            page.Add(runPositionLabel);

            VisualElement track = new();
            track.AddToClassList("wizard-progress");
            runProgressFill = new VisualElement();
            runProgressFill.AddToClassList("wizard-progress__fill");
            track.Add(runProgressFill);
            page.Add(track);

            runList = new VisualElement();
            page.Add(runList);

            runBlockingLabel = MakeLabel("Unity may not respond for a minute or two.", "wizard-text", "wizard-warn");
            runBlockingLabel.style.marginTop = 6;
            page.Add(runBlockingLabel);

            runKeepOpenLabel = MakeLabel("Unity reloads a few times. Keep the editor open.", "wizard-note");
            page.Add(runKeepOpenLabel);

            VisualElement buttons = new();
            buttons.AddToClassList("wizard-buttons");
            runStopButton = MakeButton("Stop after this step", OnStopClicked, "wizard-button");
            buttons.Add(runStopButton);
            page.Add(buttons);

            runFailure = new VisualElement();
            runFailure.Add(MakeLabel("The setup stopped. The step that failed says why; the Console has the details. Fix it, then try again: the setup only does what is left.", "wizard-text", "wizard-bad"));
            VisualElement failureButtons = new();
            failureButtons.AddToClassList("wizard-buttons");
            failureButtons.Add(MakeButton("Open the Console", OpenConsole, "wizard-button"));
            runFailure.Add(failureButtons);
            page.Add(runFailure);
            return page;
        }

        #endregion

        #region Starting

        /// <summary>
        /// Starts a run and moves to the Run page. <paramref name="only"/> limits it to some steps (a "Fix" of the status page); null is the
        /// whole setup. What was asked is kept, so that Try again after a failure does the same.
        /// </summary>
        private void StartRun(SetupChoices choices, string[] only, WizardPage returnPage)
        {
            if (!SetupRun.IsRunning)
            {
                lastRunVersion = choices.Version;
                lastRunScripting = (int)choices.Scripting;
                lastRunDowngrade = choices.AllowDowngrade;
                lastRunOnly = only == null ? null : string.Join(",", only);
                runReturnPage = (int)returnPage;
                runStartedHere = true;
                runStopRequested = false;
                runSignature = null;

                SetupRun.Start(choices, only);
            }

            GoTo(WizardPage.Run);
        }

        private void TryRunAgain()
        {
            SetupChoices choices = new()
            {
                Version = lastRunVersion,
                Scripting = (SetupScripting)lastRunScripting,
                AllowDowngrade = lastRunDowngrade,
            };

            StartRun(choices, string.IsNullOrEmpty(lastRunOnly) ? null : lastRunOnly.Split(','), (WizardPage)runReturnPage);
        }

        private void OnStopClicked()
        {
            runStopRequested = true;
            SetupRun.StopAfterCurrentStep();
            RefreshRunPage();
        }

        #endregion

        #region Refreshing

        private void RefreshRunPage()
        {
            if (runList == null)
                return;

            IReadOnlyList<SetupStepStatus> steps = SetupRun.Steps;
            bool running = SetupRun.IsRunning;
            bool failed = RunHasFailed;

            // The run is over and went well (or had nothing to do): its result is the status of the project.
            if (!running && !failed && Page == WizardPage.Run)
            {
                GoTo(WizardPage.Status);
                return;
            }

            if (!running)
                runStopRequested = false;

            // A step a limited run leaves out is not a step of this run: it is not listed, nor counted.
            List<SetupStepStatus> shown = steps.Where(step => !(step.State == SetupStepState.Skipped && step.Detail == SetupRun.NotPartOfRun)).ToList();
            List<SetupStepStatus> counted = shown.Where(step => step.State != SetupStepState.Skipped).ToList();
            int position = counted.FindIndex(step => step.State != SetupStepState.Done);
            if (position < 0)
                position = counted.Count - 1;

            int percent = Mathf.RoundToInt(SetupRun.Progress * 100f);
            runPositionLabel.text = counted.Count == 0
                ? $"{percent}%"
                : failed
                    ? $"Stopped at step {position + 1} of {counted.Count} · {percent}%"
                    : $"Step {position + 1} of {counted.Count} · {percent}%";
            runProgressFill.style.width = Length.Percent(percent);

            SetupStepStatus current = steps.FirstOrDefault(step => step.State == SetupStepState.Running);
            SetShown(runBlockingLabel, running && current != null && blocking_steps.Contains(current.Id));
            SetShown(runKeepOpenLabel, running);
            SetShown(runStopButton, running);
            runStopButton.text = runStopRequested ? "Stopping after this step..." : "Stop after this step";
            runStopButton.SetEnabled(!runStopRequested);
            SetShown(runFailure, !running && failed);

            string signature = string.Join("|", shown.Select(step => $"{step.Id}/{step.State}/{step.Detail}"));
            if (signature == runSignature)
                return;

            runSignature = signature;
            runList.Clear();
            foreach (SetupStepStatus step in shown)
                runList.Add(BuildStepRow(step));
        }

        private static VisualElement BuildStepRow(SetupStepStatus step)
        {
            VisualElement row = new();
            row.AddToClassList("wizard-row");

            VisualElement dot = new();
            dot.AddToClassList("wizard-dot");
            dot.EnableInClassList("wizard-dot--running", step.State == SetupStepState.Running);
            // A step skipped had nothing to do, which is a good outcome: green, like a step done.
            dot.EnableInClassList("wizard-dot--done", step.State == SetupStepState.Done || step.State == SetupStepState.Skipped);
            dot.EnableInClassList("wizard-dot--failed", step.State == SetupStepState.Failed);
            row.Add(dot);

            VisualElement main = new();
            main.AddToClassList("wizard-row__main");
            main.Add(MakeLabel(step.Title, "wizard-row__name"));

            string detail = step.State == SetupStepState.Skipped && !string.IsNullOrEmpty(step.Detail) ? "Not needed: " + step.Detail : step.Detail;
            if (!string.IsNullOrEmpty(detail))
                main.Add(MakeLabel(detail, "wizard-row__detail", step.State == SetupStepState.Failed ? "wizard-bad" : "wizard-muted"));

            row.Add(main);
            return row;
        }

        #endregion
    }
}
