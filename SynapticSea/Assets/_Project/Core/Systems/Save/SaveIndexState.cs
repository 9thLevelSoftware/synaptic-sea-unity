// Ported from scripts/systems/save_index_state.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// On-disk save index (ADR-0031). Pure data; lives at <c>user://saves/index.json</c>. Lists every slot the
    /// service has ever written so the menu can render without re-scanning disk. Slot files are the source of
    /// truth; the index is a cache and a corruption sentinel.
    /// </summary>
    public class SaveIndexState
    {
        public const string INDEX_VERSION = "save-index-1";

        public string Version = INDEX_VERSION;
        public string GodotVersion = "";
        public string UpdatedAt = "";
        public List<SaveSlotState> Slots = new List<SaveSlotState>();

        public GdDict ToDict()
        {
            var slotDicts = new GdArray();
            foreach (SaveSlotState row in Slots)
            {
                if (row != null)
                    slotDicts.Append(row.ToDict());
            }
            return new GdDict
            {
                { "version", Version },
                { "godot_version", GodotVersion },
                { "updated_at", UpdatedAt },
                { "slots", slotDicts },
            };
        }

        public static SaveIndexState FromDict(object data)
        {
            var idx = new SaveIndexState();
            if (!(data is GdDict dict))
                return idx;
            idx.Version = V.Str(dict.Get("version", INDEX_VERSION));
            idx.GodotVersion = V.Str(dict.Get("godot_version", ""));
            idx.UpdatedAt = V.Str(dict.Get("updated_at", ""));
            idx.Slots = new List<SaveSlotState>();
            object rawSlots = dict.Get("slots", new GdArray());
            if (rawSlots is GdArray rawArr)
            {
                foreach (object raw in rawArr)
                {
                    SaveSlotState row = SaveSlotState.FromDict(raw);
                    if (SaveSlotState.Validate(row))
                        idx.Slots.Add(row);
                }
            }
            return idx;
        }

        public void AddOrReplace(SaveSlotState row)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                SaveSlotState existing = Slots[i];
                if (existing != null && existing.SlotId == row.SlotId)
                {
                    Slots[i] = row;
                    return;
                }
            }
            Slots.Add(row);
        }

        public bool Remove(string slotId)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                SaveSlotState existing = Slots[i];
                if (existing != null && existing.SlotId == slotId)
                {
                    Slots.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public SaveSlotState Find(string slotId)
        {
            foreach (SaveSlotState row in Slots)
            {
                if (row != null && row.SlotId == slotId)
                    return row;
            }
            return null;
        }

        public List<SaveSlotState> SortedBySavedAtDesc()
        {
            var copy = new List<SaveSlotState>(Slots);
            GdSort.SortCustom(copy, (a, b) =>
            {
                long ea = a != null ? a.SavedAtEpoch : 0;
                long eb = b != null ? b.SavedAtEpoch : 0;
                return ea > eb;
            });
            return copy;
        }

        /// <summary>
        /// Marks rows whose slot file is no longer present on disk <c>corrupt=true</c>.
        /// Returns the number of slots reclassified.
        /// </summary>
        public long ReclassifyCorrupt(IEnumerable<object> slotIdPresent)
        {
            var presentSet = new HashSet<string>();
            foreach (object sid in slotIdPresent)
                presentSet.Add(V.Str(sid));
            long reclassified = 0;
            foreach (SaveSlotState row in Slots)
            {
                if (row == null)
                    continue;
                if (!presentSet.Contains(row.SlotId) && !row.Corrupt)
                {
                    row.Corrupt = true;
                    reclassified += 1;
                }
            }
            return reclassified;
        }
    }
}
