using System;
using System.Linq;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Explicit authored finite stock. Absent legacy state never creates stock.</summary>
    public static class FiniteLootState
    {
        static bool Keys(GdDict d, params string[] keys) => d != null && d.Count == keys.Length && keys.All(d.Has);
        public static string SourceHash(string sourceId, GdDict initial) => CanonicalHash.Of(new GdDict {
            { "source_id", sourceId }, { "initial", initial }
        });
        public static bool Validate(GdDict state, string owner, out string reason)
        {
            reason = "invalid_finite_loot";
            if (state == null) return false;
            if (state.IsEmpty) { reason = ""; return true; }
            if (!Keys(state, "schema_version", "ship_id", "sources") || !(state.Get("schema_version") is long version) || version != 1 ||
                !(state.Get("ship_id") is string ship) || ship != owner || string.IsNullOrEmpty(ship) || !(state.Get("sources") is GdDict sources)) return false;
            GdDict definitions = ItemDefs.LoadDefinitions();
            foreach (var pair in sources)
            {
                if (!(pair.Key is string id) || string.IsNullOrEmpty(id) || !(pair.Value is GdDict row) ||
                    !Keys(row, "source_hash", "initial", "remaining", "search_training_awarded") || !(row.Get("source_hash") is string hash) ||
                    !(row.Get("search_training_awarded") is bool) || !(row.Get("initial") is GdDict initial) || initial.IsEmpty ||
                    !(row.Get("remaining") is GdDict remaining) || remaining.Count != initial.Count || hash != SourceHash(id, initial)) return false;
                foreach (var item in initial)
                    if (!(item.Key is string itemId) || !definitions.Has(itemId) || !(item.Value is long amount) || amount <= 0 ||
                        !(remaining.Get(itemId) is long left) || left < 0 || left > amount) return false;
                bool untouched = initial.All(item => V.VariantEquals(item.Value, remaining.Get(item.Key)));
                if (row.GetBool("search_training_awarded") == untouched) return false;
            }
            reason = ""; return true;
        }
        public static bool TryInitial(GdDict spec, out GdDict initial)
        {
            initial = new GdDict();
            if (!(spec?.Get("contents") is GdArray contents) || contents.IsEmpty) return false;
            GdDict definitions = ItemDefs.LoadDefinitions();
            foreach (object obj in contents)
            {
                if (!(obj is GdDict stack) || !(stack.Get("item_id") is string id) || !definitions.Has(id) || initial.Has(id)) return false;
                object raw = stack.Get("quantity", stack.Get("qty"));
                long quantity;
                if (raw is long integer) quantity = integer;
                else if (raw is double number && !double.IsNaN(number) && !double.IsInfinity(number)
                    && number > 0 && number <= 9007199254740991d && number == Math.Floor(number)) quantity = (long)number;
                else return false;
                if (quantity <= 0) return false;
                initial[id] = quantity;
            }
            return !initial.IsEmpty;
        }
        public static bool TryBind(ref GdDict state, string owner, string sourceId, GdDict spec, out GdDict row)
        {
            row = null;
            if (!Validate(state, owner, out _) || !TryInitial(spec, out GdDict initial)) return false;
            if (state.IsEmpty) state = new GdDict { { "schema_version", 1L }, { "ship_id", owner }, { "sources", new GdDict() } };
            GdDict sources = state.GetDictOrEmpty("sources");
            string hash = SourceHash(sourceId, initial);
            if (sources.Has(sourceId))
            {
                row = sources.Get(sourceId) as GdDict;
                return row != null && row.GetString("source_hash") == hash && V.VariantEquals(row.Get("initial"), initial);
            }
            row = new GdDict { { "source_hash", hash }, { "initial", initial.DeepCopy() }, { "remaining", initial.DeepCopy() }, { "search_training_awarded", false } };
            sources[sourceId] = row;
            return true;
        }
        public static bool ValidateSources(GdDict state, string owner, GdArray specs, out string reason)
        {
            reason = "finite_source_mismatch";
            if (!Validate(state, owner, out reason)) return false;
            reason = "finite_source_mismatch";
            var authored = new GdDict();
            foreach (object obj in specs)
            {
                if (!(obj is GdDict spec) || !spec.Has("finite_source")) continue;
                if (!(spec.Get("finite_source") is bool flag) || !flag || !(spec.Get("id") is string id) || string.IsNullOrEmpty(id)
                    || authored.Has(id) || !TryInitial(spec, out GdDict initial)) return false;
                authored[id] = initial;
            }
            if (state.GetDictOrEmpty("sources").Count != authored.Count) return false;
            foreach (var pair in state.GetDictOrEmpty("sources"))
            {
                string id = V.Str(pair.Key); GdDict row = pair.Value as GdDict;
                if (!(authored.Get(id) is GdDict initial) || row == null || !V.VariantEquals(row.Get("initial"), initial)
                    || row.GetString("source_hash") != SourceHash(id, initial)) return false;
            }
            reason = ""; return true;
        }
        public static bool Depleted(GdDict row) => row.GetDictOrEmpty("remaining").Values.All(value => value is long count && count == 0);
    }
}
