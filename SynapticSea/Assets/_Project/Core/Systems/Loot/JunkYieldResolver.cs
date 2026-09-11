// Ported from scripts/systems/junk_yield_resolver.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public static class JunkYieldResolver
    {
        public const string JUNK_ITEMS_PATH = "res://data/items/junk_items.json";

        public static GdDict LoadDefinitions()
        {
            if (ItemsCompat.ReadJson(JUNK_ITEMS_PATH) is GdDict root)
                return root.Get("items", new GdDict()) as GdDict ?? new GdDict();
            return new GdDict();
        }

        public static GdArray YieldsForItem(string itemId, GdDict defs = null)
        {
            GdDict catalog = defs != null && !defs.IsEmpty ? defs : LoadDefinitions();
            if (!(catalog.Get(itemId, new GdDict()) is GdDict def)) return new GdArray();
            object yields = def.Get("yields", new GdArray());
            return yields is GdArray arr ? arr.DeepCopy() : new GdArray();
        }

        public static long TotalMaterialValue(string itemId, GdDict defs = null)
        {
            long total = 0;
            foreach (object entry in YieldsForItem(itemId, defs))
                if (entry is GdDict e) total += V.I64(e.Get("quantity", 0L));
            return total;
        }

        public static string ToStatusLine(string itemId, GdDict defs = null)
        {
            GdArray yields = YieldsForItem(itemId, defs);
            var parts = new List<string>();
            foreach (object entry in yields)
                if (entry is GdDict e)
                    parts.Add(V.Str(e.Get("material_id", "")) + " x" + ItemsCompat.D(V.I64(e.Get("quantity", 0L))));
            return itemId + " -> " + string.Join(", ", parts);
        }
    }
}
