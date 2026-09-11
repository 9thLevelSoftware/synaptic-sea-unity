// Ported from scripts/systems/equipment_state.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The player's worn equipment, keyed by body-location slot (one item per slot).
    /// Pure model; never touches the scene tree. Worn containers raise carry capacity;
    /// a suit modifies the oxygen drain. Round-trips via get_summary/apply_summary.
    /// </summary>
    public sealed class EquipmentState
    {
        public static readonly GdArray SLOTS = GdArray.Of("suit", "back", "waist", "primary_hand", "secondary_hand");

        /// <summary>slot_id: String -> item_id: String (absent = empty)</summary>
        public GdDict Slots = new GdDict();
        GdDict _defs = new GdDict();

        public EquipmentState()
        {
            _defs = ItemDefs.LoadDefinitions();
        }

        /// <summary>The GDScript load()-self-reference factory.</summary>
        public static EquipmentState Create() => new EquipmentState();

        /// <summary>True iff the item declares a slot in SLOTS.</summary>
        public bool CanEquip(string itemId)
        {
            string slot = ItemDefs.EquipSlot(_defs, itemId);
            return SLOTS.Contains(slot);
        }

        /// <summary>
        /// Equips item_id into its declared slot, displacing whatever was there.
        /// Returns { "ok": bool, "displaced": String } (displaced "" if the slot was empty or on failure).
        /// </summary>
        public GdDict Equip(string itemId)
        {
            if (!CanEquip(itemId))
                return new GdDict { { "ok", false }, { "displaced", "" } };
            string slot = ItemDefs.EquipSlot(_defs, itemId);
            string displaced = V.Str(Slots.Get(slot, ""));
            Slots[slot] = itemId;
            return new GdDict { { "ok", true }, { "displaced", displaced } };
        }

        /// <summary>Removes and returns the item in <paramref name="slot"/> ("" if empty).</summary>
        public string Unequip(string slot)
        {
            string itemId = V.Str(Slots.Get(slot, ""));
            if (itemId != "")
                Slots.Erase(slot);
            return itemId;
        }

        public string GetEquipped(string slot) => V.Str(Slots.Get(slot, ""));

        public bool IsSlotOccupied(string slot) => Slots.Has(slot) && V.Str(Slots[slot]) != "";

        /// <summary>Sum of container_capacity across all worn containers.</summary>
        public double GetCarryCapacityBonus()
        {
            double bonus = 0.0;
            foreach (var kv in Slots)
                bonus += ItemDefs.ContainerCapacity(_defs, V.Str(kv.Value));
            return bonus;
        }

        /// <summary>
        /// [{capacity, reduction}] for each worn item that is a container (capacity &gt; 0).
        /// The suit (no container_capacity) is excluded. Pure data; feeds
        /// Encumbrance.weight_reduction_saved at the coordinator.
        /// </summary>
        public GdArray GetContainerReductions()
        {
            var outArr = new GdArray();
            foreach (var kv in Slots)
            {
                double cap = ItemDefs.ContainerCapacity(_defs, V.Str(kv.Value));
                if (cap > 0.0)
                {
                    outArr.Add(new GdDict
                    {
                        { "capacity", cap },
                        { "reduction", ItemDefs.WeightReduction(_defs, V.Str(kv.Value)) },
                    });
                }
            }
            return outArr;
        }

        /// <summary>Product of all worn 'oxygen_drain' effect values (default 1.0 = neutral).</summary>
        public double GetOxygenDrainMultiplier()
        {
            double mult = 1.0;
            foreach (var kv in Slots)
            {
                foreach (object fx in ItemDefs.Effects(_defs, V.Str(kv.Value)))
                {
                    if (fx is GdDict fxDict && V.Str(fxDict.Get("type", "")) == "oxygen_drain")
                        mult *= V.F64(fxDict.Get("value", 1.0));
                }
            }
            return mult;
        }

        public GdDict GetSummary() => new GdDict { { "slots", Slots.DeepCopy() } };

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            Slots.Clear();
            object slotsVariant = summary.Get("slots", null);
            if (slotsVariant is GdDict slotsDict)
            {
                foreach (var kv in slotsDict)
                {
                    string itemId = V.Str(kv.Value);
                    if (SLOTS.Contains(V.Str(kv.Key)) && itemId != "")
                        Slots[V.Str(kv.Key)] = itemId;
                }
            }
            return true;
        }
    }
}
