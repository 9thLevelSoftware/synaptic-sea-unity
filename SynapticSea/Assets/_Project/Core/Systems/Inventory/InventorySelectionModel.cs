// Ported from scripts/systems/inventory_selection_model.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure per-list selection state for the inventory UI + a static context-action
    /// resolver. No scene-tree access. The view (inventory_panel.gd) owns one of these per
    /// visible list and asks it what is selected and which menu actions apply.
    /// </summary>
    public sealed class InventorySelectionModel
    {
        static readonly GdArray UseCategories = GdArray.Of("medicine", "stimulant", "ammo", "utility", "food", "drink");

        /// <summary>Ordered item ids currently shown in this list.</summary>
        public List<string> Ids = new List<string>();
        GdDict _selected = new GdDict(); // index:int -> true
        long _anchor = -1;

        /// <summary>Replace the ordered id list; drop any selection/anchor now out of range.</summary>
        public void SetIds(GdArray pIds)
        {
            Ids = new List<string>();
            if (pIds != null)
                foreach (object v in pIds) Ids.Add(V.Str(v));
            var keep = new GdDict();
            foreach (var kv in _selected)
            {
                long idx = V.I64(kv.Key);
                if (idx >= 0 && idx < Ids.Count)
                    keep[idx] = true;
            }
            _selected = keep;
            if (_anchor >= Ids.Count)
                _anchor = -1;
        }

        public void Clear()
        {
            _selected.Clear();
            _anchor = -1;
        }

        /// <summary>Plain click: select exactly one and set the range anchor.</summary>
        public void SelectSingle(long index)
        {
            _selected.Clear();
            if (index >= 0 && index < Ids.Count)
            {
                _selected[index] = true;
                _anchor = index;
            }
        }

        /// <summary>Ctrl-click: add/remove one; the anchor follows the click.</summary>
        public void Toggle(long index)
        {
            if (index < 0 || index >= Ids.Count)
                return;
            if (_selected.Has(index))
                _selected.Erase(index);
            else
                _selected[index] = true;
            _anchor = index;
        }

        /// <summary>Shift-click: select the contiguous block from the anchor to index.</summary>
        public void SelectRangeTo(long index)
        {
            if (index < 0 || index >= Ids.Count)
                return;
            if (_anchor < 0)
            {
                SelectSingle(index);
                return;
            }
            _selected.Clear();
            long lo = Math.Min(_anchor, index);
            long hi = Math.Max(_anchor, index);
            for (long i = lo; i < hi + 1; i++)
                _selected[i] = true;
        }

        public bool IsSelected(long index) => _selected.Has(index);

        public List<long> GetSelectedIndices()
        {
            var keys = new List<object>();
            foreach (var kv in _selected) keys.Add(V.I64(kv.Key));
            GdSort.Sort(keys);
            var outList = new List<long>();
            foreach (object k in keys) outList.Add((long)k);
            return outList;
        }

        public GdArray GetSelectedIds()
        {
            var outArr = new GdArray();
            foreach (long i in GetSelectedIndices())
                outArr.Add(Ids[(int)i]);
            return outArr;
        }

        /// <summary>
        /// Resolve the right-click menu action set for one row. <paramref name="rowIsContainer"/> is true when the
        /// right-clicked row lives in the container pane; it is retained as pane context but no longer gates
        /// equip. Equippable rows offer "equip" in BOTH panes: a SELF row equips directly; a CONTAINER row
        /// triggers equip-from-container (ADR-0026).
        /// </summary>
        public static List<string> ContextActions(string itemId, GdDict defs, bool inTransferMode, bool rowIsContainer, bool isEquippedSlot)
        {
            var actions = new List<string>();
            if (isEquippedSlot)
            {
                actions.Add("unequip");
                return actions;
            }
            bool equippable = ItemDefs.EquipSlot(defs, itemId).Length != 0;
            if (inTransferMode)
            {
                actions.Add("transfer");
                actions.Add("transfer_all");
                actions.Add("split");
                if (!rowIsContainer && UseCategories.Contains(ItemDefs.Category(defs, itemId)))
                {
                    actions.Add("use");
                    actions.Add("use_all");
                }
                if (equippable)
                    actions.Add("equip");
            }
            else
            {
                if (UseCategories.Contains(ItemDefs.Category(defs, itemId)))
                {
                    actions.Add("use");
                    actions.Add("use_all");
                }
                if (equippable)
                    actions.Add("equip");
            }
            return actions;
        }
    }
}
