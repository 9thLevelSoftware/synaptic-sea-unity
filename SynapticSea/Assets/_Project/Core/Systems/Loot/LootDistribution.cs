// Ported from scripts/systems/loot_distribution.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Context-aware loot distribution: biome / condition / container / depth weighting, rarity resolution,
    /// world-unique filtering, and per-item merge. Deterministic for a given (table, seed_source, context).
    /// </summary>
    /// <remarks>
    /// The GDScript context Dictionary may carry two non-Variant values: <c>item_definitions</c> (a Dictionary,
    /// supported here as a <see cref="GdDict"/> under the same key) and <c>unique_state</c> (a UniqueItemState
    /// object, which a <see cref="GdDict"/> cannot hold). Pass the latter through
    /// <see cref="RollWithUniqueState"/>; <see cref="Roll"/> is the <c>unique_state == null</c> case.
    /// </remarks>
    public static class LootDistribution
    {
        public static GdArray Roll(string tableKey, string seedSource, GdDict tables, GdDict context = null) =>
            RollWithUniqueState(tableKey, seedSource, tables, context, null);

        /// <summary><c>roll(...)</c> with <c>context["unique_state"] = uniqueState</c>.</summary>
        public static GdArray RollWithUniqueState(string tableKey, string seedSource, GdDict tables, GdDict context, UniqueItemState uniqueState)
        {
            context = context ?? new GdDict();
            object tableVariant = tables?.Get(tableKey, null);
            if (!(tableVariant is GdDict table))
                return new GdArray();
            object entriesVariant = table.Get("entries", new GdArray());
            if (!(entriesVariant is GdArray entries) || entries.IsEmpty)
                return new GdArray();
            GodotRandom rng = GodotRandom.FromSeed(LootRoller.StableSeed(
                tableKey + "|" +
                seedSource + "|" +
                V.Str(context.Get("biome_id", "abyssal_synaptic_sea")) + "|" +
                V.Str(context.Get("depth", 0L)) + "|" +
                V.Str(context.Get("container_kind", V.Str(table.Get("container_kind", tableKey))))));
            long rolls = Math.Max(1L, V.I64(table.Get("rolls", 1L)));
            GdDict itemDefs = context.Has("item_definitions")
                ? context["item_definitions"] as GdDict
                : ItemDefs.LoadDefinitions();
            var results = new GdArray();
            for (long rollIndex = 0; rollIndex < rolls; rollIndex++)
            {
                GdArray weighted = WeightedEntries(entries, context, uniqueState, seedSource, itemDefs);
                if (weighted.IsEmpty)
                    continue;
                GdDict choice = ChooseEntry(weighted, rng);
                long qtyMin = V.I64(choice.Get("qty_min", 1L));
                long qtyMax = V.I64(choice.Get("qty_max", Math.Max(1L, qtyMin)));
                long quantity = rng.RandiRange(Math.Min(qtyMin, qtyMax), Math.Max(qtyMin, qtyMax));
                if (quantity <= 0)
                    continue;
                string itemId = V.Str(choice.Get("item_id", ""));
                if (itemId.Length == 0)
                    continue;
                string rarity = ResolveRarity(choice, context, rng, itemDefs, itemId);
                string uniqueId = V.Str(choice.Get("unique_id", ItemDefs.UniqueId(itemDefs, itemId)));
                string codexEntryId = V.Str(choice.Get("codex_entry_id", ItemDefs.CodexEntryId(itemDefs, itemId)));
                var entry = new GdDict
                {
                    { "item_id", itemId },
                    { "quantity", quantity },
                    { "rarity", rarity },
                    { "container_kind", V.Str(context.Get("container_kind", V.Str(table.Get("container_kind", tableKey)))) },
                    { "biome_id", V.Str(context.Get("biome_id", "abyssal_synaptic_sea")) },
                    { "depth", V.I64(context.Get("depth", 0L)) },
                    { "condition", V.Str(context.Get("condition", "damaged")) },
                    { "seed_key", seedSource + "|" + GdString.FormatInt(rollIndex) + "|" + itemId },
                    { "world_unique", uniqueId.Length != 0 },
                };
                if (uniqueId.Length != 0)
                    entry["unique_id"] = uniqueId;
                if (codexEntryId.Length != 0)
                    entry["codex_entry_id"] = codexEntryId;
                results.Add(entry);
            }
            return MergeResults(results);
        }

        static GdArray WeightedEntries(GdArray entries, GdDict context, UniqueItemState uniqueState, string seedSource, GdDict itemDefs)
        {
            var outArr = new GdArray();
            string biomeId = V.Str(context.Get("biome_id", "abyssal_synaptic_sea"));
            long depth = V.I64(context.Get("depth", 0L));
            string condition = V.Str(context.Get("condition", "damaged"));
            string containerKind = V.Str(context.Get("container_kind", "generic_crate"));
            foreach (object entryV in entries)
            {
                if (!(entryV is GdDict rawEntry))
                    continue;
                GdDict entry = rawEntry.DeepCopy();
                string itemId = V.Str(entry.Get("item_id", ""));
                if (itemId.Length == 0)
                    continue;
                string uniqueId = V.Str(entry.Get("unique_id", ItemDefs.UniqueId(itemDefs, itemId)));
                if (uniqueState != null && uniqueId.Length != 0)
                {
                    string probeSeed = seedSource + "|" + itemId;
                    if (!uniqueState.CanClaim(uniqueId, probeSeed))
                        continue;
                }
                double weight = Math.Max(0.0, V.F64(entry.Get("weight", 1.0)));
                weight *= LookupMultiplier(entry.Get("biome_weights", new GdDict()), biomeId);
                weight *= LookupMultiplier(entry.Get("condition_weights", new GdDict()), condition);
                weight *= LookupMultiplier(entry.Get("container_weights", new GdDict()), containerKind);
                weight *= Math.Max(0.10, 1.0 + V.F64(entry.Get("depth_weight_scale", 0.0)) * (double)depth);
                weight *= RarityTier.WeightMultiplier(V.Str(entry.Get("rarity", ItemDefs.Rarity(itemDefs, itemId))));
                if (weight > 0.0)
                {
                    entry["_effective_weight"] = weight;
                    outArr.Add(entry);
                }
            }
            return outArr;
        }

        static GdDict ChooseEntry(GdArray weighted, GodotRandom rng)
        {
            double totalWeight = 0.0;
            foreach (object entry in weighted)
                totalWeight += V.F64(((GdDict)entry).Get("_effective_weight", 0.0));
            if (totalWeight <= 0.0)
                return new GdDict();
            double pick = rng.Randf() * totalWeight;
            foreach (object entry in weighted)
            {
                pick -= V.F64(((GdDict)entry).Get("_effective_weight", 0.0));
                if (pick <= 0.0)
                    return ((GdDict)entry).DeepCopy();
            }
            return ((GdDict)weighted.Back()).DeepCopy();
        }

        static double LookupMultiplier(object variant, string key)
        {
            if (variant is GdDict table)
            {
                if (table.Has(key))
                    return Math.Max(0.0, V.F64(table[key]));
                if (table.Has("default"))
                    return Math.Max(0.0, V.F64(table["default"]));
            }
            return 1.0;
        }

        static string ResolveRarity(GdDict entry, GdDict context, GodotRandom rng, GdDict itemDefs, string itemId)
        {
            string explicitRarity = V.Str(entry.Get("rarity", ""));
            if (explicitRarity.Length != 0)
                return RarityTier.Normalize(explicitRarity);
            double baseRoll = rng.Randf();
            baseRoll += 0.04 * V.F64(context.Get("depth", 0L));
            baseRoll += (V.F64(context.Get("loot_quality_modifier", 1.0)) - 1.0) * 0.15;
            if (V.Str(context.Get("condition", "damaged")) == "wrecked")
                baseRoll += 0.04;
            string fromRoll = RarityTier.FromRoll(baseRoll);
            return RarityTier.MaxRarity(ItemDefs.Rarity(itemDefs, itemId), fromRoll);
        }

        static GdArray MergeResults(GdArray results)
        {
            var merged = new GdDict();
            foreach (object resultV in results)
            {
                if (!(resultV is GdDict result))
                    continue;
                string key = V.Str(result.Get("unique_id", result.Get("item_id", "")));
                if (key.Length == 0)
                    continue;
                if (!merged.Has(key))
                {
                    merged[key] = result.DeepCopy();
                }
                else
                {
                    var current = (GdDict)merged[key];
                    current["quantity"] = V.I64(current.Get("quantity", 0L)) + V.I64(result.Get("quantity", 0L));
                    current["rarity"] = RarityTier.MaxRarity(V.Str(current.Get("rarity", "common")), V.Str(result.Get("rarity", "common")));
                    merged[key] = current;
                }
            }
            var keys = new List<object>(merged.Keys);
            GdSort.Sort(keys);
            var outArr = new GdArray();
            foreach (object key in keys)
                outArr.Add(merged[key]);
            return outArr;
        }
    }
}
