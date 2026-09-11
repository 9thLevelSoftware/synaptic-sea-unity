// Ported from scripts/systems/item_defs.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Shared, all-static item-definition lookups. Extracted from InventoryState so
    /// both the player inventory and the per-ship ShipInventory read one source of
    /// truth for weights, stack limits, categories, and display names. Tool defs are
    /// merged first with a synthetic 'tool' category + default weight (preserving the
    /// original InventoryState merge order and semantics).
    /// </summary>
    public static class ItemDefs
    {
        public const string ITEM_DEFINITIONS_PATH = "res://data/items/item_definitions.json";
        public const string MEDICINE_DEFINITIONS_PATH = "res://data/items/medicine_definitions.json";
        public const string STIMULANT_DEFINITIONS_PATH = "res://data/items/stimulant_definitions.json";
        public const string AMMO_DEFINITIONS_PATH = "res://data/combat/ammo_definitions.json";
        public const string UTILITY_DEFINITIONS_PATH = "res://data/items/utility_item_definitions.json";
        public const string TRADE_DEFINITIONS_PATH = "res://data/items/trade_item_definitions.json";
        public const string TOOL_DEFINITIONS_PATH = "res://data/tools/tool_definitions.json";
        public const string MATERIAL_DEFINITIONS_PATH = "res://data/materials/material_definitions.json";
        public const string EQUIPMENT_DEFINITIONS_PATH = "res://data/items/equipment_definitions.json";
        public const string JUNK_ITEMS_PATH = "res://data/items/junk_items.json";
        public const string UNIQUE_ITEMS_PATH = "res://data/items/unique_items.json";
        public const string RARITY_PALETTE_PATH = "res://data/ui/rarity_palette.json";
        public const double DEFAULT_TOOL_WEIGHT = 2.0;
        public const long DEFAULT_MAX_STACK = 99;

        static readonly string[] ExtraPaths =
        {
            MEDICINE_DEFINITIONS_PATH,
            STIMULANT_DEFINITIONS_PATH,
            AMMO_DEFINITIONS_PATH,
            UTILITY_DEFINITIONS_PATH,
            TRADE_DEFINITIONS_PATH,
        };

        /// <summary>
        /// Merged tool+item definitions. Tools first (so item_definitions can override),
        /// tool defs get a synthetic 'tool' category + default weight while preserving
        /// their 'effect' field.
        /// </summary>
        public static GdDict LoadDefinitions()
        {
            var defs = new GdDict();
            GdDict toolDefs = ReadJsonDict(TOOL_DEFINITIONS_PATH);
            foreach (var kv in toolDefs)
            {
                if (!(kv.Value is GdDict rawDef)) continue; // skip malformed/corrupt tool entries
                GdDict def = rawDef.DeepCopy();
                def["category"] = "tool";
                if (!def.Has("weight")) def["weight"] = DEFAULT_TOOL_WEIGHT;
                defs[kv.Key] = def;
            }
            GdDict itemDefs = ReadJsonDict(ITEM_DEFINITIONS_PATH);
            foreach (var kv in itemDefs)
            {
                if (!(kv.Value is GdDict)) continue; // skip malformed/corrupt item entries
                defs[kv.Key] = kv.Value;
            }
            foreach (string extraPath in ExtraPaths)
            {
                GdDict extraDefs = ReadJsonDict(extraPath);
                foreach (var kv in extraDefs)
                {
                    if (!(kv.Value is GdDict extra)) continue;
                    // Field-merge onto any existing base def so keys present only in the
                    // base are preserved (the extra file's fields win per-key).
                    if (defs.Has(kv.Key) && defs[kv.Key] is GdDict baseDef)
                    {
                        GdDict merged = baseDef.DeepCopy();
                        foreach (var field in extra) merged[field.Key] = field.Value;
                        defs[kv.Key] = merged;
                    }
                    else
                    {
                        defs[kv.Key] = extra;
                    }
                }
            }
            // Crafting materials (wrapped in a "materials" root key). FILL-ONLY: an id already
            // defined above keeps its live-balance definition.
            GdDict materialRoot = ReadJsonDict(MATERIAL_DEFINITIONS_PATH);
            if (materialRoot.Get("materials", new GdDict()) is GdDict materialDefs)
            {
                foreach (var kv in materialDefs)
                {
                    if (!(kv.Value is GdDict)) continue;
                    if (!defs.Has(kv.Key)) defs[kv.Key] = kv.Value;
                }
            }
            GdDict equipDefs = ReadJsonDict(EQUIPMENT_DEFINITIONS_PATH);
            foreach (var kv in equipDefs)
            {
                if (!(kv.Value is GdDict)) continue; // skip malformed entries
                defs[kv.Key] = kv.Value;
            }
            GdDict junkRoot = ReadJsonDict(JUNK_ITEMS_PATH);
            if (junkRoot.Get("items", new GdDict()) is GdDict junkDefs)
            {
                foreach (var kv in junkDefs)
                {
                    if (!(kv.Value is GdDict rawJunk)) continue;
                    if (defs.Has(kv.Key) && defs[kv.Key] is GdDict baseDef)
                    {
                        GdDict mergedJunk = baseDef.DeepCopy();
                        foreach (var field in rawJunk) mergedJunk[field.Key] = field.Value;
                        defs[kv.Key] = mergedJunk;
                    }
                    else
                    {
                        defs[kv.Key] = rawJunk;
                    }
                }
            }
            GdDict uniqueRoot = ReadJsonDict(UNIQUE_ITEMS_PATH);
            if (uniqueRoot.Get("items", new GdDict()) is GdDict uniqueDefs)
            {
                foreach (var kv in uniqueDefs)
                {
                    if (!(kv.Value is GdDict rawUnique)) continue;
                    string itemId = V.Str(rawUnique.Get("item_id", kv.Key));
                    if (defs.Has(itemId) && defs[itemId] is GdDict baseDef)
                    {
                        GdDict mergedUnique = baseDef.DeepCopy();
                        foreach (var field in rawUnique) mergedUnique[field.Key] = field.Value;
                        defs[itemId] = mergedUnique;
                    }
                }
            }
            return defs;
        }

        static GdDict ReadJsonDict(string path) => ItemsCompat.ReadJsonDict(path);

        public static GdDict GetDefinition(GdDict defs, string itemId)
        {
            object def = defs?.Get(itemId, new GdDict());
            return def as GdDict ?? new GdDict();
        }

        public static string Category(GdDict defs, string itemId) =>
            V.Str(GetDefinition(defs, itemId).Get("category", ""));

        /// <summary>Unknown items weigh 0 so a foreign save round-trips without corrupting the cap.</summary>
        public static double WeightEach(GdDict defs, string itemId) =>
            V.F64(GetDefinition(defs, itemId).Get("weight", 0.0));

        public static long MaxStack(GdDict defs, string itemId) =>
            V.I64(GetDefinition(defs, itemId).Get("max_stack", DEFAULT_MAX_STACK));

        public static string DisplayName(GdDict defs, string itemId)
        {
            string name = V.Str(GetDefinition(defs, itemId).Get("display_name", ""));
            return name.Length != 0 ? name : ItemsCompat.Capitalize(itemId.Replace("_", " "));
        }

        public static string EquipSlot(GdDict defs, string itemId) =>
            V.Str(GetDefinition(defs, itemId).Get("equip_slot", ""));

        public static double ContainerCapacity(GdDict defs, string itemId) =>
            V.F64(GetDefinition(defs, itemId).Get("container_capacity", 0.0));

        public static double WeightReduction(GdDict defs, string itemId) =>
            GdMath.Clampf(V.F64(GetDefinition(defs, itemId).Get("weight_reduction", 0.0)), 0.0, 1.0);

        /// <summary>Returns the definition's own array (not a copy), like the GDScript.</summary>
        public static GdArray Effects(GdDict defs, string itemId)
        {
            object e = GetDefinition(defs, itemId).Get("effects", new GdArray());
            return e as GdArray ?? new GdArray();
        }

        public static string Icon(GdDict defs, string itemId) =>
            V.Str(GetDefinition(defs, itemId).Get("icon", ""));

        public static string Rarity(GdDict defs, string itemId) =>
            V.Str(GetDefinition(defs, itemId).Get("rarity", "common"));

        public static string CodexEntryId(GdDict defs, string itemId) =>
            V.Str(GetDefinition(defs, itemId).Get("codex_entry_id", ""));

        public static string UniqueId(GdDict defs, string itemId) =>
            V.Str(GetDefinition(defs, itemId).Get("unique_id", ""));

        public static GdArray JunkYields(GdDict defs, string itemId)
        {
            object yields = GetDefinition(defs, itemId).Get("junk_yields", new GdArray());
            return yields is GdArray arr ? arr.DeepCopy() : new GdArray();
        }

        public static string RarityColorHex(string itemId)
        {
            GdDict paletteRoot = ReadJsonDict(RARITY_PALETTE_PATH);
            if (paletteRoot.Get("rarities", new GdDict()) is GdDict rarities)
            {
                string rarityId = Rarity(LoadDefinitions(), itemId);
                if (rarities.Get(rarityId, new GdDict()) is GdDict entry)
                    return V.Str(entry.Get("color", "#9AA4AF"));
            }
            return "#9AA4AF";
        }
    }
}
