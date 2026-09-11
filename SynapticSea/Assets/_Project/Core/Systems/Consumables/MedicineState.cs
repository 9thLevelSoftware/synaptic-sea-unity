// Ported from scripts/systems/medicine_state.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Stateless helper around medicine definitions. Tracks the last medicine use so
    /// runtime/UI/save-load can surface what happened.
    /// </summary>
    public sealed class MedicineState : ISimModel, IStatusLineProvider
    {
        public string LastItemId = "";
        public List<string> LastCuredStatuses = new List<string>();
        public GdArray LastResults = new GdArray();

        public void Configure(GdDict config = null)
        {
            LastItemId = "";
            LastCuredStatuses.Clear();
            LastResults.Clear();
        }

        public GdDict UseMedicine(string itemId, GdDict definition, EffectDispatcher dispatcher, IDictionary<string, object> context)
        {
            LastItemId = itemId;
            LastCuredStatuses.Clear();
            LastResults.Clear();
            if (definition.Get("effects", new GdArray()) is GdArray effects)
            {
                foreach (object effectIdVariant in effects)
                {
                    string effectId = V.Str(effectIdVariant);
                    GdDict res = dispatcher.DispatchEffect(effectId, context);
                    LastResults.Add(res);
                    if (res.Get("cured", null) is GdArray cured)
                        foreach (object c in cured) LastCuredStatuses.Add(V.Str(c));
                }
            }
            return new GdDict
            {
                { "ok", true },
                { "item_id", itemId },
                { "cured_statuses", ItemsCompat.ToGdArray(LastCuredStatuses) },
                { "results", LastResults.DeepCopy() },
            };
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "last_item_id", LastItemId },
                { "last_cured_statuses", ItemsCompat.ToGdArray(LastCuredStatuses) },
                { "last_results", LastResults.DeepCopy() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            LastItemId = V.Str(summary.Get("last_item_id", LastItemId));
            LastCuredStatuses.Clear();
            if (summary.Get("last_cured_statuses", new GdArray()) is GdArray rawCured)
                foreach (object entry in rawCured) LastCuredStatuses.Add(V.Str(entry));
            LastResults = new GdArray();
            if (summary.Get("last_results", new GdArray()) is GdArray rawResults)
                LastResults = rawResults.DeepCopy();
            return true;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (LastItemId.Length == 0) return lines;
            lines.Add("Medicine: " + LastItemId);
            if (LastCuredStatuses.Count != 0) lines.Add("  cured=" + string.Join(",", LastCuredStatuses));
            return lines;
        }
    }
}
