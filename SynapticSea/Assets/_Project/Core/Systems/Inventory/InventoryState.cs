// Ported from scripts/systems/inventory_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Inventories that answer <c>can_accept(item_id, qty)</c> (the GDScript <c>has_method("can_accept")</c>
    /// probe in CraftingState). Only the player <see cref="InventoryState"/> implements it.
    /// </summary>
    public interface IItemAcceptor
    {
        bool CanAccept(string itemId, long qty);
    }

    /// <summary>
    /// Player-global inventory: quantitied, categorized (part/supply/tool), SOFT weight-capped
    /// (PZ-style: carrying over capacity is allowed and penalized via Heavy Load movement, NOT
    /// refused — see get_capacity/get_load_ratio/is_over_capacity; add_item gates on max_stack only).
    /// Pure model; never touches the scene tree. Tools are category 'tool' items, exposed
    /// through legacy shims (add_tool/has_tool/tool_ids/get_drain_multiplier) so OxygenState,
    /// ToolPickup, and the junction gate are untouched. Round-trips via get/apply_summary.
    /// </summary>
    public sealed class InventoryState : CargoTransfer.ICargoPlayer, CargoTransfer.ICargoHold, IItemAcceptor, IStatusLineProvider
    {
        public const string ITEM_DEFINITIONS_PATH = "res://data/items/item_definitions.json";
        public const string TOOL_DEFINITIONS_PATH = "res://data/tools/tool_definitions.json";
        public const double MAX_WEIGHT = 50.0;
        public const double DEFAULT_TOOL_WEIGHT = 2.0;
        public const long DEFAULT_MAX_STACK = 99;

        /// <summary>item_id: String -> quantity: int</summary>
        public GdDict Items { get; set; } = new GdDict();

        /// <summary>Added by worn containers (set by the coordinator).</summary>
        public double BonusCapacity = 0.0;

        /// <summary>Saved kg from worn containers (set by the coordinator).</summary>
        public double WeightReduction = 0.0;

        GdDict _definitions = new GdDict(); // item_id -> def Dictionary (merged)

        public InventoryState()
        {
            LoadDefinitions();
        }

        void LoadDefinitions()
        {
            _definitions = ItemDefs.LoadDefinitions();
        }

        // --- definition helpers ---

        public GdDict GetDefinition(string itemId) => ItemDefs.GetDefinition(_definitions, itemId);

        public string GetCategory(string itemId) => ItemDefs.Category(_definitions, itemId);

        public double GetWeightEach(string itemId) => ItemDefs.WeightEach(_definitions, itemId);

        long MaxStack(string itemId) => ItemDefs.MaxStack(_definitions, itemId);

        public string GetDisplayName(string itemId) => ItemDefs.DisplayName(_definitions, itemId);

        // --- item API ---

        public double GetMaxWeight() => MAX_WEIGHT;

        /// <summary>Effective carry budget = base cap + worn-container bonus (+ future strength).</summary>
        public double GetCapacity() => MAX_WEIGHT + BonusCapacity;

        /// <summary>
        /// Raw weight minus the worn-container weight reduction (saved kg), floored at 0.
        /// get_total_weight() stays the true mass; this is what encumbrance keys off.
        /// </summary>
        public double GetEffectiveWeight() => Math.Max(0.0, GetTotalWeight() - WeightReduction);

        /// <summary>effective_weight / capacity. &gt;1.0 means over-encumbered (Heavy Load).</summary>
        public double GetLoadRatio() => GetEffectiveWeight() / Math.Max(0.0001, GetCapacity());

        public bool IsOverCapacity() => GetEffectiveWeight() > GetCapacity();

        public double GetTotalWeight()
        {
            double total = 0.0;
            foreach (var kv in Items)
                total += GetWeightEach(V.Str(kv.Key)) * V.F64(kv.Value);
            return total;
        }

        public long GetQuantity(string itemId) => V.I64(Items.Get(itemId, 0L));

        /// <summary>
        /// Adds up to qty, honoring max_stack ONLY. Weight does NOT gate (PZ soft-cap):
        /// the player may carry over capacity and suffer a Heavy Load movement penalty.
        /// Returns the quantity actually added (0 if the stack is full).
        /// </summary>
        public long AddItem(string itemId, long qty)
        {
            if (string.IsNullOrEmpty(itemId) || qty <= 0)
                return 0;
            long current = GetQuantity(itemId);
            long stackRoom = Math.Max(0L, MaxStack(itemId) - current);
            long want = Math.Min(qty, stackRoom);
            if (want <= 0)
                return 0;
            Items[itemId] = current + want;
            return want;
        }

        /// <summary>
        /// Returns true if at least <paramref name="qty"/> of item_id can be added without exceeding max_stack.
        /// Weight is a soft-cap (never blocks); only the per-item stack ceiling gates here. Use to
        /// guard actions that consume inputs and then deposit an output (e.g. crafting), so the
        /// output is never silently dropped after the inputs are spent.
        /// </summary>
        public bool CanAccept(string itemId, long qty)
        {
            if (string.IsNullOrEmpty(itemId) || qty <= 0)
                return true;
            return (MaxStack(itemId) - GetQuantity(itemId)) >= qty;
        }

        public long RemoveItem(string itemId, long qty)
        {
            if (qty <= 0)
                return 0;
            long current = GetQuantity(itemId);
            long removed = Math.Min(qty, current);
            if (removed <= 0)
                return 0;
            if (removed >= current)
                Items.Erase(itemId);
            else
                Items[itemId] = current - removed;
            return removed;
        }

        public GdArray GetItemsByCategory(string category)
        {
            var outArr = new GdArray();
            var ids = new List<object>(Items.Keys);
            GdSort.Sort(ids);
            foreach (object idV in ids)
            {
                string itemId = V.Str(idV);
                if (GetCategory(itemId) == category)
                {
                    outArr.Add(new GdDict
                    {
                        { "id", idV },
                        { "quantity", GetQuantity(itemId) },
                        { "weight_each", GetWeightEach(itemId) },
                    });
                }
            }
            return outArr;
        }

        public void Reset()
        {
            Items.Clear();
            LoadDefinitions();
        }

        // --- legacy tool shims (REQ-007 consumers depend on these) ---

        /// <summary>GDScript computed property <c>tool_ids</c>: sorted ids whose category is 'tool'.</summary>
        public List<string> ToolIds
        {
            get
            {
                var outList = new List<string>();
                var ids = new List<object>(Items.Keys);
                GdSort.Sort(ids);
                foreach (object idV in ids)
                {
                    if (GetCategory(V.Str(idV)) == "tool")
                        outList.Add(V.Str(idV));
                }
                return outList;
            }
        }

        public bool AddTool(string toolId)
        {
            if (string.IsNullOrEmpty(toolId) || GetQuantity(toolId) > 0)
                return false;
            return AddItem(toolId, 1) == 1;
        }

        public bool HasTool(string toolId) => GetQuantity(toolId) > 0 && GetCategory(toolId) == "tool";

        public bool RemoveTool(string toolId) => RemoveItem(toolId, 1) == 1;

        public double GetDrainMultiplier() => HasTool("portable_oxygen_pump") ? 0.5 : 1.0;

        // --- save/load ---

        public GdDict GetSummary()
        {
            var effects = new GdArray();
            List<string> toolIds = ToolIds;
            foreach (string toolId in toolIds)
            {
                object effect = GetDefinition(toolId).Get("effect", new GdDict());
                if (effect is GdDict effectDict)
                {
                    effects.Add(new GdDict
                    {
                        { "tool_id", toolId },
                        { "type", V.Str(effectDict.Get("type", "")) },
                        { "value", effectDict.Get("value", 1.0) },
                    });
                }
            }
            return new GdDict
            {
                { "items", Items.DeepCopy() },
                { "tool_ids", GdString.ToGdArray(ToolIds) },   // derived; kept for backward compat
                { "active_effects", effects },
                { "drain_multiplier", GetDrainMultiplier() },  // OxygenState consumes this
                { "total_weight", GetTotalWeight() },
                { "max_weight", GetMaxWeight() },
            };
        }

        /// <summary>Accepts the new ("items") shape AND the legacy ("tool_ids"-only) shape.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            Items.Clear();
            object itemsVariant = summary.Get("items", null);
            if (itemsVariant is GdDict itemsDict)
            {
                foreach (var kv in itemsDict)
                    Items[V.Str(kv.Key)] = V.I64(kv.Value);
            }
            else
            {
                // Legacy save: reconstruct tool items from tool_ids.
                object legacyIds = summary.Get("tool_ids", new GdArray());
                if (legacyIds is GdArray legacyArr)
                {
                    foreach (object toolId in legacyArr)
                        Items[V.Str(toolId)] = 1L;
                }
            }
            return true;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            // Tools first, preserving the REQ-007 markers the inventory HUD smoke greps.
            foreach (string toolId in ToolIds)
            {
                lines.Add("Tool: " + GetDisplayName(toolId));
                lines.Add("tool=" + toolId);
                if (toolId == "portable_oxygen_pump" && GetDrainMultiplier() != 1.0)
                    lines.Add("drain_multiplier=" + V.Str(GetDrainMultiplier()));
            }
            // Then non-tool items + a weight readout for the loot HUD.
            foreach (string cat in new[] { "part", "supply" })
            {
                foreach (object entryV in GetItemsByCategory(cat))
                {
                    var entry = (GdDict)entryV;
                    lines.Add("item=" + V.Str(entry["id"]) + " x" + GdString.FormatInt(V.I64(entry["quantity"])));
                }
            }
            lines.Add("weight=" + V.Str(GdMath.Snapped(GetTotalWeight(), 0.1)) + "/" + V.Str(GdMath.Snapped(GetCapacity(), 0.1)));
            return lines;
        }
    }
}
