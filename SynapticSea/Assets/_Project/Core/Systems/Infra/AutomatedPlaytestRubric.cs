// Ported from scripts/systems/automated_playtest_rubric.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-INT-005 automated cross-system playtest rubric. Pure deterministic scorer for scripted
    /// scenario summaries.
    /// </summary>
    public class AutomatedPlaytestRubric : IStatusLineProvider
    {
        public GdArray RequiredStages = new GdArray();
        public long MinVisibleConsequences = 1;
        public long MinPlayerChoices = 1;
        public long MaxStuckEvents = 0;
        public double MinScore = 0.75;
        GdDict _lastResult = new GdDict();

        public bool Configure(GdDict data)
        {
            if (data == null) data = new GdDict();
            RequiredStages = ToStringArray(data.Get("required_stages", new GdArray()));
            MinVisibleConsequences = V.I64(data.Get("min_visible_consequences", 1L));
            MinPlayerChoices = V.I64(data.Get("min_player_choices", 1L));
            MaxStuckEvents = V.I64(data.Get("max_stuck_events", 0L));
            MinScore = V.F64(data.Get("min_score", 0.75));
            return true;
        }

        public GdDict EvaluateScenario(GdDict scenario)
        {
            var stagesSeen = new GdDict();
            foreach (var stage in ToStringArray(scenario.Get("stages", new GdArray())))
                stagesSeen[stage] = true;
            long visibleCount = 0;
            var systemTags = new GdDict();
            GdArray steps = AsArray(scenario.Get("steps", new GdArray()));
            foreach (var rawStep in steps)
            {
                GdDict step = AsDict(rawStep);
                string stage = V.Str(step.Get("stage", ""));
                if (stage.Length != 0) stagesSeen[stage] = true;
                if (V.Bool(step.Get("visible_consequence", false))) visibleCount += 1;
                foreach (var systemId in ToStringArray(step.Get("systems", new GdArray())))
                {
                    if (((string)systemId).Length != 0) systemTags[systemId] = true;
                }
            }
            var missingStages = new GdArray();
            foreach (var required in RequiredStages)
            {
                if (!stagesSeen.Has(V.Str(required))) missingStages.Add(V.Str(required));
            }
            long requiredCount = Math.Max(1L, (long)RequiredStages.Count);
            long coveredCount = requiredCount - missingStages.Count;
            double stageScore = GdMath.Clampf((double)coveredCount / (double)requiredCount, 0.0, 1.0);
            double visibleScore = GdMath.Clampf((double)visibleCount / (double)Math.Max(1L, MinVisibleConsequences), 0.0, 1.0);
            long choiceCount = V.I64(scenario.Get("player_choice_count", scenario.Get("hud_updates", 0L)));
            double choiceScore = GdMath.Clampf((double)choiceCount / (double)Math.Max(1L, MinPlayerChoices), 0.0, 1.0);
            long stuckEvents = V.I64(scenario.Get("stuck_events", 0L));
            double stuckScore = stuckEvents <= MaxStuckEvents ? 1.0 : 0.0;
            double score = (stageScore + visibleScore + choiceScore + stuckScore) / 4.0;
            bool passed = missingStages.IsEmpty && visibleCount >= MinVisibleConsequences && choiceCount >= MinPlayerChoices && stuckEvents <= MaxStuckEvents && score >= MinScore;
            _lastResult = new GdDict
            {
                { "pass", passed },
                { "score", score },
                { "covered_stage_count", coveredCount },
                { "required_stage_count", requiredCount },
                { "missing_stages", missingStages },
                { "visible_consequence_count", visibleCount },
                { "system_tag_count", systemTags.Count },
                { "choice_count", choiceCount },
                { "stuck_events", stuckEvents },
            };
            return _lastResult.DeepCopy();
        }

        public GdDict GetSummary() => _lastResult.DeepCopy();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Automated Playtest Rubric: score=" + GdFloatFormat.FormatFixed(V.F64(_lastResult.Get("score", 0.0)), 2)
                + " pass=" + (V.Bool(_lastResult.Get("pass", false)) ? "true" : "false"));
            lines.Add(InfraCompat.Fmt("  stages={0}/{1} visible={2} choices={3} stuck={4}",
                V.I64(_lastResult.Get("covered_stage_count", 0L)),
                V.I64(_lastResult.Get("required_stage_count", 0L)),
                V.I64(_lastResult.Get("visible_consequence_count", 0L)),
                V.I64(_lastResult.Get("choice_count", 0L)),
                V.I64(_lastResult.Get("stuck_events", 0L))));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        static GdArray ToStringArray(object value) => InfraCompat.ToStringArray(value);
        static GdArray AsArray(object value) => InfraCompat.AsArray(value);
        static GdDict AsDict(object value) => InfraCompat.AsDict(value);
    }
}
