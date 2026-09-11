// Ported from scripts/systems/hangar_bay.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Per-ship hangar bay: fixed slots that store other ships. Pure data (no scene tree). Slot occupancy is the
    /// source of truth for what a carrier holds; the coordinator owns the physical placement and the dock-graph
    /// edges. Persisted as a ship-summary sub-dict under "hangar".
    /// </summary>
    public class HangarBay
    {
        public long SlotCount = 0;
        public long SlotSizeClass = 0;

        /// <summary>Length == slot_count; "" = empty, else a bayed ship_id.</summary>
        public List<string> Slots = new List<string>();

        public static HangarBay Create(long pSlotCount, long pSlotSizeClass)
        {
            var b = new HangarBay();
            b.SlotCount = Math.Max(0L, pSlotCount);
            b.SlotSizeClass = Math.Max(0L, pSlotSizeClass);
            b.Slots.Clear();
            for (long i = 0; i < b.SlotCount; i++)
                b.Slots.Add("");
            return b;
        }

        /// <summary>First empty slot index for a ship of <paramref name="sizeClass"/>, or -1 (too large / bay full).</summary>
        public int FreeSlotFor(long sizeClass)
        {
            if (sizeClass > SlotSizeClass)
                return -1;
            for (int i = 0; i < Slots.Count; i++)
                if (Slots[i] == "")
                    return i;
            return -1;
        }

        /// <summary>
        /// Bays <paramref name="shipId"/> in the first fitting free slot. Returns the slot index, or -1 if the ship
        /// is already bayed here, the id is empty, or nothing fits.
        /// </summary>
        public int Dock(string shipId, long sizeClass)
        {
            if (shipId == "" || SlotOf(shipId) != -1)
                return -1;
            int idx = FreeSlotFor(sizeClass);
            if (idx == -1)
                return -1;
            Slots[idx] = shipId;
            return idx;
        }

        /// <summary>Empties <paramref name="slotIndex"/>, returning the ship_id it held (or "" if empty / out of range).</summary>
        public string Launch(long slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= Slots.Count)
                return "";
            string id = Slots[(int)slotIndex];
            Slots[(int)slotIndex] = "";
            return id;
        }

        public int SlotOf(string shipId)
        {
            if (shipId == "")
                return -1;
            return Slots.IndexOf(shipId);
        }

        public bool IsFull() => !Slots.Contains("");

        public GdDict GetSummary() =>
            new GdDict
            {
                { "slot_count", SlotCount },
                { "slot_size_class", SlotSizeClass },
                { "slots", ShipCompat.ToGdArray(Slots) },
            };

        public bool ApplySummary(object summary)
        {
            if (!(summary is GdDict d))
                return false;
            SlotCount = V.I64(d.Get("slot_count", 0L));
            SlotSizeClass = V.I64(d.Get("slot_size_class", 0L));
            Slots.Clear();
            object raw = d.Get("slots", new GdArray());
            if (raw is GdArray rawArr)
            {
                foreach (object s in rawArr)
                    Slots.Add(V.Str(s));
            }
            // Normalize length to slot_count so a corrupted/short array cannot desync.
            while (Slots.Count < SlotCount)
                Slots.Add("");
            // Godot's resize() refuses a negative size (error, array unchanged).
            if (Slots.Count > SlotCount && SlotCount >= 0)
                Slots.RemoveRange((int)SlotCount, Slots.Count - (int)SlotCount);
            return true;
        }
    }
}
