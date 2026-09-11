// Ported from scripts/systems/ship_inventory.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Per-ship cargo hold. A focused, weight-capped item container — no player-tool
    /// shims. Shares item weights/stack-limits with the player InventoryState via
    /// ItemDefs. Pure model; never touches the scene tree. Round-trips via
    /// get_summary/apply_summary.
    /// </summary>
    public sealed class ShipInventory : CargoTransfer.ICargoHold
    {
        public const double MAX_WEIGHT_DEFAULT = 500.0;

        /// <summary>item_id: String -> quantity: int</summary>
        public GdDict Items = new GdDict();
        public double MaxWeight = MAX_WEIGHT_DEFAULT;
        GdDict _defs = new GdDict();

        public ShipInventory()
        {
            _defs = ItemDefs.LoadDefinitions();
        }

        /// <summary>The GDScript load()-self-reference factory.</summary>
        public static ShipInventory Create(double pMaxWeight = MAX_WEIGHT_DEFAULT)
        {
            var inst = new ShipInventory();
            inst.MaxWeight = pMaxWeight;
            return inst;
        }

        public double GetMaxWeight() => MaxWeight;

        public double GetTotalWeight()
        {
            double total = 0.0;
            foreach (var kv in Items)
                total += ItemDefs.WeightEach(_defs, V.Str(kv.Key)) * V.F64(kv.Value);
            return total;
        }

        public long GetQuantity(string itemId) => V.I64(Items.Get(itemId, 0L));

        /// <summary>
        /// Adds up to qty, honoring max_stack and the weight cap. Returns the quantity
        /// actually added (0 if none fit). Weight-0 items ignore the cap.
        /// </summary>
        public long AddItem(string itemId, long qty)
        {
            if (string.IsNullOrEmpty(itemId) || qty <= 0)
                return 0;
            long current = GetQuantity(itemId);
            long stackRoom = Math.Max(0L, ItemDefs.MaxStack(_defs, itemId) - current);
            long want = Math.Min(qty, stackRoom);
            if (want <= 0)
                return 0;
            double w = ItemDefs.WeightEach(_defs, itemId);
            if (w > 0.0)
            {
                double remaining = MaxWeight - GetTotalWeight();
                long weightRoom = V.I64(GdMath.Floor(remaining / w + 0.0001));
                want = Math.Min(want, Math.Max(0L, weightRoom));
            }
            if (want <= 0)
                return 0;
            Items[itemId] = current + want;
            return want;
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
                if (ItemDefs.Category(_defs, itemId) == category)
                {
                    outArr.Add(new GdDict
                    {
                        { "id", idV },
                        { "quantity", GetQuantity(itemId) },
                        { "weight_each", ItemDefs.WeightEach(_defs, itemId) },
                    });
                }
            }
            return outArr;
        }

        public void Reset()
        {
            Items.Clear();
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "items", Items.DeepCopy() },
                { "max_weight", MaxWeight },
            };
        }

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
            if (summary.Has("max_weight"))
                MaxWeight = V.F64(summary["max_weight"]);
            return true;
        }
    }
}
