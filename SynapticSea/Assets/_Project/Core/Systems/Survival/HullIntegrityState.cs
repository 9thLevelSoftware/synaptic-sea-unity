// Ported from scripts/systems/hull_integrity_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Per-compartment hull health and breach flags.</summary>
    public sealed class HullIntegrityState : ISimModel, IStatusLineProvider
    {
        /// <summary>compartment_id -> { health, breach_open, isolation_rating }.</summary>
        public GdDict Compartments = new GdDict();

        public void Configure(GdDict config)
        {
            Compartments.Clear();
            object rows = config != null ? config.Get("compartments", new GdArray()) : new GdArray();
            if (!(rows is GdArray entries)) return;
            foreach (object entry in entries)
            {
                if (!(entry is GdDict row)) continue;
                string compartmentId = V.Str(row.Get("compartment_id", ""));
                if (compartmentId.Length == 0) continue;
                Compartments[compartmentId] = new GdDict
                {
                    { "health", GdMath.Clampf(V.F64(row.Get("health", 1.0)), 0.0, 1.0) },
                    { "breach_open", V.Bool(row.Get("breach_open", false)) },
                    { "isolation_rating", GdMath.Clampf(V.F64(row.Get("isolation_rating", 0.5)), 0.0, 1.0) },
                };
            }
        }

        public bool DamageCompartment(string compartmentId, double amount, bool forceBreach = false)
        {
            if (!Compartments.Has(compartmentId)) return false;
            GdDict row = ((GdDict)Compartments[compartmentId]).DeepCopy();
            row["health"] = Math.Max(0.0, V.F64(row.Get("health", 1.0)) - Math.Max(0.0, amount));
            if (forceBreach || V.F64(row["health"]) <= 0.45)
                row["breach_open"] = true;
            Compartments[compartmentId] = row;
            return true;
        }

        public bool SealCompartment(string compartmentId, double repairAmount)
        {
            if (!Compartments.Has(compartmentId)) return false;
            GdDict row = ((GdDict)Compartments[compartmentId]).DeepCopy();
            row["health"] = Math.Min(1.0, V.F64(row.Get("health", 1.0)) + Math.Max(0.0, repairAmount));
            if (V.F64(row["health"]) >= 0.75)
                row["breach_open"] = false;
            Compartments[compartmentId] = row;
            return true;
        }

        public long GetBreachCount()
        {
            long count = 0;
            foreach (var kv in Compartments)
            {
                if (V.Bool(((GdDict)kv.Value).Get("breach_open", false))) count += 1;
            }
            return count;
        }

        public double AverageIntegrity()
        {
            if (Compartments.IsEmpty) return 1.0;
            double total = 0.0;
            foreach (var kv in Compartments)
                total += V.F64(((GdDict)kv.Value).Get("health", 1.0));
            return total / Compartments.Count;
        }

        public GdDict GetSummary()
        {
            return new GdDict { { "compartments", Compartments.DeepCopy() } };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            object rows = summary.Get("compartments", null);
            if (!(rows is GdDict rowsDict)) return false;
            GdDict newRows = rowsDict.DeepCopy();
            if (GdJson.Stringify(newRows) == GdJson.Stringify(Compartments)) return false;
            Compartments = newRows;
            return true;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Hull Integrity " + SurvivalCompat.FormatD(GdMath.RoundI(AverageIntegrity() * 100.0)) +
                      "% breaches=" + SurvivalCompat.FormatD(GetBreachCount()));
            foreach (var kv in Compartments)
            {
                var row = (GdDict)kv.Value;
                if (V.Bool(row.Get("breach_open", false)))
                {
                    lines.Add("Hull " + V.Str(kv.Key) + " BREACHED " +
                              SurvivalCompat.FormatD(GdMath.RoundI(V.F64(row.Get("health", 0.0)) * 100.0)) + "%");
                }
            }
            return lines;
        }
    }
}
