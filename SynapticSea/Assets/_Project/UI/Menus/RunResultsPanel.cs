// Ported from scripts/ui/run_results_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// End-of-run results for death, extraction and abort (TERMINAL surface: existing terminal run state, result actions
    /// only). Accepts the completion summary directly and never invents values the run did not track. Initial focus is
    /// Return to Title; Cancel also returns to title.
    /// Layout (UI presentation spec: cause/outcome, playtime, frozen epitaph, safe next action, no false Continue):
    /// a severity banner, a stats block of 44 px label/value rows, the epitaph block on death, and the run context line.
    /// <see cref="BodyText"/> keeps Godot's line list ("Outcome: …", "Cause: …", "Epitaph: …", …).
    /// </summary>
    public sealed class RunResultsPanel : SurfacePanel
    {
        public const string CopyPath = "res://data/ui/run_results_copy.json";
        public const string DefaultEpitaph = "The sea keeps what it takes.";
        public const string NoStatsText = "No additional run statistics were recorded.";

        public const string ResultsClass = "ss-results";
        public const string BannerClass = "ss-results__banner";
        public const string StatsClass = "ss-results__stats";
        public const string RowClass = "ss-results__row";
        public const string RowLabelClass = "ss-results__label";
        public const string RowValueClass = "ss-results__value";
        public const string EpitaphClass = "ss-results__epitaph";
        public const string ContextClass = "ss-results__context";

        GdDict _summary = new GdDict();
        GdDict _epitaphs = new GdDict();
        string _bodyText = "";
        readonly Label _banner;
        readonly VisualElement _stats;
        readonly Label _epitaph;
        readonly Label _empty;
        readonly Label _context;
        readonly List<VisualElement> _rows = new List<VisualElement>();
        readonly Button _return;
        readonly Button _newRun;

        public event Action ReturnToTitleRequested;
        public event Action NewRunRequested;

        public RunResultsPanel() : base("RUN RESULTS", SurfaceTime.Terminal)
        {
            AddToClassList(ResultsClass);
            _banner = UiFactory.Text("", BannerClass, UiClasses.LabelHeading);
            _banner.name = "run-results-banner";
            _stats = UiFactory.Box(UiClasses.Detail, StatsClass);
            _epitaph = UiFactory.Text("", EpitaphClass, UiClasses.LabelMono);
            _epitaph.name = "run-results-epitaph";
            _empty = UiFactory.Text(NoStatsText, UiClasses.Empty, UiClasses.LabelSecondary);
            _context = UiFactory.Text("", ContextClass, UiClasses.LabelMono, UiClasses.LabelSecondary);
            _context.name = "run-results-context";
            Body.Add(_banner);
            Body.Add(_stats);
            Body.Add(_epitaph);
            Body.Add(_empty);
            Body.Add(_context);
            UiFactory.SetShown(_epitaph, false);
            UiFactory.SetShown(_empty, false);
            UiFactory.SetShown(_context, false);
            _return = UiFactory.Button("Return to Title", OnReturnPressed, "act:return_to_title");
            _newRun = UiFactory.Button("New Run", OnNewRunPressed, "act:new_run");
            ActionBar.Clear();
            ActionBar.Add(_return);
            ActionBar.Add(_newRun);
            SetViewVisible(true);
        }

        public override string SurfaceId => "run_results";

        public Button ReturnButton => _return;
        public Button NewRunButton => _newRun;

        /// <summary>Every result line, joined (Godot's single body label text).</summary>
        public string BodyText => _bodyText;

        public Label Banner => _banner;
        public Label EpitaphLabel => _epitaph;
        public Label EmptyLabel => _empty;
        public Label ContextLabel => _context;

        /// <summary>The label/value rows of the stats block (outcome, cause, time survived, tracked counters).</summary>
        public IReadOnlyList<VisualElement> StatRows => _rows;

        public void SetRunSummary(GdDict summary)
        {
            _summary = summary?.DeepCopy() ?? new GdDict();
            LoadCopy();
            RefreshContent();
        }

        /// <summary>The run context line under the results (e.g. "seed 17 · breach_field · standard"); empty hides it.</summary>
        public void SetContextLine(string text)
        {
            _context.text = text ?? "";
            UiFactory.SetShown(_context, _context.text.Length != 0);
        }

        public GdDict GetSummary() => _summary.DeepCopy();

        void LoadCopy()
        {
            GdDict parsed = CatalogRegistry.LoadDict(CopyPath);
            if (parsed != null && parsed.Get("epitaphs", null) is GdDict epitaphs) _epitaphs = epitaphs.DeepCopy();
            if (_epitaphs.IsEmpty) _epitaphs = new GdDict { { "default", DefaultEpitaph } };
        }

        void RefreshContent()
        {
            string outcome = NormalizedOutcome();
            Severity severity = outcome == "death" ? Severity.Danger : outcome == "extraction" ? Severity.Success : Severity.Caution;
            _stats.Clear();
            _rows.Clear();
            var lines = new List<string> { "Outcome: " + outcome };
            AddRow("Outcome", outcome);

            string cause = SummaryString("cause", "death_cause", "failure_cause");
            if (cause.Length != 0)
            {
                lines.Add("Cause: " + cause);
                AddRow("Cause", cause);
            }
            string epitaph = outcome == "death" ? EpitaphFor(cause) : "";
            if (epitaph.Length != 0) lines.Add("Epitaph: " + epitaph);
            object playTime = FirstPresent("play_time_seconds", "time_survived_seconds", "run_time_seconds", "survival_seconds");
            if (playTime != null)
            {
                string duration = FormatDuration(V.F64(playTime));
                lines.Add("Time survived: " + duration);
                AddRow("Time survived", duration);
            }
            AppendStat(lines, "Rooms discovered", "rooms_discovered", "rooms_discovered_count", "discovered_rooms");
            AppendStat(lines, "Threats killed", "threats_killed", "threats_defeated", "kills");
            AppendStat(lines, "Loot value", "loot_value", "loot_value_total", "loot_total");
            AppendStat(lines, "Objectives completed", "objectives_completed");
            bool nothingTracked = lines.Count == 1;
            if (nothingTracked) lines.Add(NoStatsText);
            _bodyText = string.Join("\n", lines);

            _banner.text = SeverityText.Format(severity, BannerText(outcome, cause));
            SeverityText.Apply(_banner, severity);
            _epitaph.text = epitaph.Length != 0 ? "“" + epitaph + "”" : "";
            UiFactory.SetShown(_epitaph, epitaph.Length != 0);
            UiFactory.SetShown(_empty, nothingTracked);
            SetTitle(outcome == "death" ? "RUN ENDED — DEATH" : outcome == "extraction" ? "RUN COMPLETE — EXTRACTION" : "RUN ABANDONED");
            SeverityText.Apply(TitleLabel, severity);
        }

        static string BannerText(string outcome, string cause)
        {
            switch (outcome)
            {
                case "death": return cause.Length != 0 ? "You died — " + cause : "You died";
                case "extraction": return "You extracted";
                default: return "The run was abandoned";
            }
        }

        void AddRow(string label, string value)
        {
            VisualElement row = UiFactory.Box(UiClasses.Row, RowClass);
            row.Add(UiFactory.Text(label, UiClasses.RowText, RowLabelClass));
            row.Add(UiFactory.Text(value, UiClasses.LabelMono, RowValueClass));
            _stats.Add(row);
            _rows.Add(row);
        }

        public string NormalizedOutcome()
        {
            string outcome = SummaryString("reason", "outcome");
            if (outcome.Length == 0) return "abort";
            outcome = outcome.ToLowerInvariant();
            if (outcome == "complete" || outcome == "completion") return "extraction";
            if (outcome == "abandon" || outcome == "aborted" || outcome == "quit") return "abort";
            if (outcome == "death" || outcome == "extraction" || outcome == "abort") return outcome;
            return "abort";
        }

        string SummaryString(params string[] keys)
        {
            foreach (string key in keys)
            {
                if (_summary.Has(key) && _summary[key] != null)
                {
                    string value = GdString.StripEdges(V.Str(_summary[key]));
                    if (value.Length != 0) return value;
                }
            }
            return "";
        }

        object FirstPresent(params string[] keys)
        {
            foreach (string key in keys)
            {
                if (_summary.Has(key) && _summary[key] != null) return _summary[key];
            }
            return null;
        }

        void AppendStat(List<string> lines, string label, params string[] keys)
        {
            object value = FirstPresent(keys);
            if (value == null) return;
            lines.Add(label + ": " + V.Str(value));
            AddRow(label, V.Str(value));
        }

        string EpitaphFor(string cause)
        {
            string key = GdString.StripEdges(cause.ToLowerInvariant());
            if (!_epitaphs.Has(key)) key = "default";
            return V.Str(_epitaphs.Get(key, DefaultEpitaph));
        }

        static string FormatDuration(double seconds)
        {
            long total = Math.Max(0, GdMath.FloorI(seconds));
            return GdString.FormatIntPadded(total / 60, 2) + ":" + GdString.FormatIntPadded(total % 60, 2);
        }

        void OnReturnPressed() => ReturnToTitleRequested?.Invoke();
        void OnNewRunPressed() => NewRunRequested?.Invoke();

        protected override void RequestClose() => OnReturnPressed();

        protected override VisualElement InitialFocusElement() => _return;
    }
}
