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
    /// </summary>
    public sealed class RunResultsPanel : SurfacePanel
    {
        public const string CopyPath = "res://data/ui/run_results_copy.json";
        public const string DefaultEpitaph = "The sea keeps what it takes.";

        GdDict _summary = new GdDict();
        GdDict _epitaphs = new GdDict();
        readonly Label _body;
        readonly Button _return;
        readonly Button _newRun;

        public event Action ReturnToTitleRequested;
        public event Action NewRunRequested;

        public RunResultsPanel() : base("RUN RESULTS", SurfaceTime.Terminal)
        {
            AddToClassList("ss-results");
            _body = UiFactory.Text("");
            Body.Add(_body);
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
        public string BodyText => _body.text;

        public void SetRunSummary(GdDict summary)
        {
            _summary = summary?.DeepCopy() ?? new GdDict();
            LoadCopy();
            RefreshContent();
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
            var lines = new List<string> { "Outcome: " + outcome };
            string cause = SummaryString("cause", "death_cause", "failure_cause");
            if (cause.Length != 0) lines.Add("Cause: " + cause);
            if (outcome == "death") lines.Add("Epitaph: " + EpitaphFor(cause));
            object playTime = FirstPresent("play_time_seconds", "time_survived_seconds", "run_time_seconds", "survival_seconds");
            if (playTime != null) lines.Add("Time survived: " + FormatDuration(V.F64(playTime)));
            AppendStat(lines, "Rooms discovered", "rooms_discovered", "rooms_discovered_count", "discovered_rooms");
            AppendStat(lines, "Threats killed", "threats_killed", "threats_defeated", "kills");
            AppendStat(lines, "Loot value", "loot_value", "loot_value_total", "loot_total");
            AppendStat(lines, "Objectives completed", "objectives_completed");
            if (lines.Count == 1) lines.Add("No additional run statistics were recorded.");
            _body.text = string.Join("\n", lines);
            SetTitle(outcome == "death" ? "RUN ENDED — DEATH" : outcome == "extraction" ? "RUN COMPLETE — EXTRACTION" : "RUN ABANDONED");
            SeverityText.Apply(TitleLabel, outcome == "death" ? Severity.Danger : outcome == "extraction" ? Severity.Success : Severity.Caution);
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
            if (value != null) lines.Add(label + ": " + V.Str(value));
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
