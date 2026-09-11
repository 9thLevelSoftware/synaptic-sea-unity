// Ported from scripts/systems/balance_ledger.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-INT-004 balance sanity ledger. Data-driven threshold checker for cross-system scenarios.
    /// </summary>
    public class BalanceLedger : IStatusLineProvider
    {
        readonly GdDict _scenarioRules = new GdDict(); // scenario_id -> Array[Dictionary]
        readonly GdArray _lastResults = new GdArray();

        public bool Configure(GdDict data)
        {
            _scenarioRules.Clear();
            _lastResults.Clear();
            if (data == null || data.IsEmpty) return false;
            GdArray scenarios = InfraCompat.AsArray(data.Get("scenarios", new GdArray()));
            foreach (var raw in scenarios)
            {
                GdDict row = InfraCompat.AsDict(raw);
                string scenarioId = V.Str(row.Get("scenario_id", ""));
                if (scenarioId.Length == 0) continue;
                var metrics = new GdArray();
                foreach (var metricRaw in InfraCompat.AsArray(row.Get("metrics", new GdArray())))
                {
                    GdDict metric = InfraCompat.AsDict(metricRaw);
                    if (V.Str(metric.Get("metric", "")).Length == 0) continue;
                    metrics.Add(metric);
                }
                _scenarioRules[scenarioId] = metrics;
            }
            return !_scenarioRules.IsEmpty;
        }

        public GdDict EvaluateScenario(string scenarioId, GdDict metrics)
        {
            var failures = new GdArray();
            long checkedCount = 0;
            GdArray rules = InfraCompat.AsArray(_scenarioRules.Get(scenarioId, new GdArray()));
            if (rules.IsEmpty)
            {
                return new GdDict
                {
                    { "pass", false },
                    { "scenario_id", scenarioId },
                    { "checked", 0L },
                    { "failures", GdArray.Of(new GdDict { { "reason", "unknown_scenario" } }) },
                };
            }
            foreach (var rawRule in rules)
            {
                GdDict rule = InfraCompat.AsDict(rawRule);
                string metricName = V.Str(rule.Get("metric", ""));
                if (metricName.Length == 0) continue;
                checkedCount += 1;
                if (!metrics.Has(metricName))
                {
                    failures.Add(new GdDict { { "metric", metricName }, { "reason", "missing" } });
                    continue;
                }
                double value = V.F64(metrics.Get(metricName, 0.0));
                if (rule.Has("min") && value < V.F64(rule.Get("min", 0.0)))
                {
                    failures.Add(new GdDict
                    {
                        { "metric", metricName }, { "reason", "below_min" }, { "value", value }, { "min", V.F64(rule.Get("min", 0.0)) },
                    });
                }
                if (rule.Has("max") && value > V.F64(rule.Get("max", value)))
                {
                    failures.Add(new GdDict
                    {
                        { "metric", metricName }, { "reason", "above_max" }, { "value", value }, { "max", V.F64(rule.Get("max", value)) },
                    });
                }
            }
            var result = new GdDict
            {
                { "pass", failures.IsEmpty },
                { "scenario_id", scenarioId },
                { "checked", checkedCount },
                { "failures", failures },
            };
            _lastResults.Add(result);
            return result;
        }

        public GdDict GetSummary()
        {
            long passCount = 0;
            foreach (var result in _lastResults)
            {
                if (V.Bool(((GdDict)result).Get("pass", false))) passCount += 1;
            }
            return new GdDict
            {
                { "scenario_count", _scenarioRules.Count },
                { "result_count", _lastResults.Count },
                { "pass_count", passCount },
            };
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            GdDict summary = GetSummary();
            lines.Add(InfraCompat.Fmt("Balance Ledger: scenarios={0} results={1} pass={2}",
                V.I64(summary.Get("scenario_count", 0L)),
                V.I64(summary.Get("result_count", 0L)),
                V.I64(summary.Get("pass_count", 0L))));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();
    }
}
