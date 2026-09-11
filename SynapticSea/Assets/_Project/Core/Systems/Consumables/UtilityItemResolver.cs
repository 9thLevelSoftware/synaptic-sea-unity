// Ported from scripts/systems/utility_item_resolver.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Lightweight state for utility-item use. Utility items resolve through the same
    /// shared EffectDispatcher as medicine/stimulants, then retain a visible summary.
    /// </summary>
    public sealed class UtilityItemResolver : ISimModel, IStatusLineProvider
    {
        public string LastItemId = "";
        public string LastNote = "";
        public GdDict ActiveFlags = new GdDict();

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            LastItemId = "";
            LastNote = "";
            ActiveFlags.Clear();
            if (config.Get("active_flags", new GdDict()) is GdDict raw)
                ActiveFlags = raw.DeepCopy();
        }

        public GdDict UseItem(string itemId, GdDict definition, EffectDispatcher dispatcher, IDictionary<string, object> context)
        {
            LastItemId = itemId;
            LastNote = V.Str(definition.Get("use_note", ""));
            if (definition.Get("effects", new GdArray()) is GdArray effects)
                foreach (object effectIdVariant in effects)
                    dispatcher.DispatchEffect(V.Str(effectIdVariant), context);
            string utilityFlag = V.Str(definition.Get("utility_flag", ""));
            if (utilityFlag.Length != 0)
            {
                object existing = ActiveFlags.Get(utilityFlag, new GdDict());
                long count = existing is GdDict existingDict ? V.I64(existingDict.Get("count", 0L)) + 1 : 1;
                ActiveFlags[utilityFlag] = new GdDict
                {
                    { "item_id", itemId },
                    { "note", LastNote },
                    { "count", count },
                };
            }
            return new GdDict { { "ok", true }, { "item_id", itemId }, { "utility_flag", utilityFlag }, { "note", LastNote } };
        }

        /// <summary>
        /// Domain 5: a utility flag is consumed when its promised bypass fires. Decrements the charge count;
        /// erases the flag and returns true only when the last charge is spent. Returns false when charges remain.
        /// </summary>
        public bool ConsumeFlag(string flag)
        {
            if (string.IsNullOrEmpty(flag) || !ActiveFlags.Has(flag)) return false;
            object entry = ActiveFlags.Get(flag, new GdDict());
            long count = entry is GdDict entryDict ? V.I64(entryDict.Get("count", 1L)) : 1;
            count -= 1;
            if (count <= 0)
            {
                ActiveFlags.Erase(flag);
                return true;
            }
            ((GdDict)ActiveFlags[flag])["count"] = count;
            return false;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "last_item_id", LastItemId },
                { "last_note", LastNote },
                { "active_flags", ActiveFlags.DeepCopy() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            LastItemId = V.Str(summary.Get("last_item_id", LastItemId));
            LastNote = V.Str(summary.Get("last_note", LastNote));
            ActiveFlags.Clear();
            if (summary.Get("active_flags", new GdDict()) is GdDict raw)
                ActiveFlags = raw.DeepCopy();
            return true;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (LastItemId.Length != 0) lines.Add("Utility: " + LastItemId);
            if (LastNote.Length != 0) lines.Add("  " + LastNote);
            return lines;
        }
    }
}
