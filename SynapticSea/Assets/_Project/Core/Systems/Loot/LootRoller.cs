// Ported from scripts/systems/loot_roller.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure, deterministic loot-table roller. Same (table_key, seed_source, tables)
    /// always yields the same result. Never touches the scene tree.
    /// </summary>
    public static class LootRoller
    {
        public const string LOOT_TABLES_PATH = "res://data/items/loot_tables.json";

        public static GdDict LoadTables() => ItemsCompat.ReadJson(LOOT_TABLES_PATH) as GdDict ?? new GdDict();

        /// <summary>Returns [{item_id, quantity}], merged by item_id, ordered by item_id.</summary>
        public static GdArray Roll(string tableKey, string seedSource, GdDict tables)
        {
            if (!(tables?.Get(tableKey, null) is GdDict table)) return new GdArray();
            if (!(table.Get("entries", new GdArray()) is GdArray entries) || entries.IsEmpty) return new GdArray();
            long rolls = System.Math.Max(1L, V.I64(table.Get("rolls", 1L)));

            GodotRandom rng = GodotRandom.FromSeed(StableSeed(seedSource));

            double totalWeight = 0.0;
            foreach (object entry in entries)
                totalWeight += V.F64(((GdDict)entry).Get("weight", 1.0));
            if (totalWeight <= 0.0) return new GdArray();

            var accum = new GdDict(); // item_id -> qty
            for (long i = 0; i < rolls; i++)
            {
                double pick = rng.Randf() * totalWeight;
                var chosen = (GdDict)entries[0];
                foreach (object entry in entries)
                {
                    pick -= V.F64(((GdDict)entry).Get("weight", 1.0));
                    if (pick <= 0.0)
                    {
                        chosen = (GdDict)entry;
                        break;
                    }
                }
                string itemId = V.Str(chosen.Get("item_id", ""));
                if (itemId.Length == 0) continue;
                long qty = rng.RandiRange(V.I64(chosen.Get("qty_min", 1L)), V.I64(chosen.Get("qty_max", 1L)));
                if (qty <= 0) continue;
                accum[itemId] = V.I64(accum.Get(itemId, 0L)) + qty;
            }

            var outArr = new GdArray();
            var ids = new List<object>(accum.Keys);
            GdSort.Sort(ids);
            foreach (object itemId in ids)
                outArr.Add(new GdDict { { "item_id", itemId }, { "quantity", V.I64(accum[itemId]) } });
            return outArr;
        }

        /// <summary>Deterministic non-negative seed from a string, stable within a Godot version.</summary>
        internal static long StableSeed(string seedSource) => ItemsCompat.AbsI(GodotHash.StringHash(seedSource));
    }
}
