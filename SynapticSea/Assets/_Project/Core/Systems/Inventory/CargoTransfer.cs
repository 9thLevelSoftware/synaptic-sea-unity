// Ported from scripts/systems/cargo_transfer.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure-static cargo transfer between the player InventoryState and a ship
    /// ShipInventory. Conservation is the contract: every move removes from the source
    /// EXACTLY what the destination's add_item reported accepting, so partial fills
    /// never duplicate or lose items. Iterates over a snapshot of source ids so removals
    /// during iteration are safe.
    /// </summary>
    public static class CargoTransfer
    {
        /// <summary>The duck-typed inventory surface every move uses (InventoryState and ShipInventory).</summary>
        public interface ICargoStore
        {
            long GetQuantity(string itemId);

            /// <summary>Returns the quantity actually accepted.</summary>
            long AddItem(string itemId, long qty);

            /// <summary>Returns the quantity actually removed.</summary>
            long RemoveItem(string itemId, long qty);
        }

        /// <summary>Player side of <see cref="DepositAll"/>: exposes <c>items</c> and <c>get_category</c>.</summary>
        public interface ICargoPlayer : ICargoStore
        {
            /// <summary>GDScript <c>items</c> (item_id -> quantity); only the keys are read here.</summary>
            GdDict Items { get; }

            string GetCategory(string itemId);
        }

        /// <summary>Hold side of <see cref="WithdrawCategory"/>.</summary>
        public interface ICargoHold : ICargoStore
        {
            /// <summary>Returns <c>[{id, quantity, weight_each}]</c>.</summary>
            GdArray GetItemsByCategory(string category);
        }

        /// <summary>
        /// Salvage categories moved by deposit-all. Tools are intentionally excluded —
        /// survival gear stays on the player and is never auto-dumped into a hold.
        /// </summary>
        public static readonly GdArray HAULABLE_CATEGORIES = GdArray.Of("part", "supply");

        /// <summary>
        /// Moves all part+supply stacks from player -> hold, capped by the hold's weight
        /// room. Returns { "moved": {id:qty}, "total_moved": int }.
        /// </summary>
        public static GdDict DepositAll(ICargoPlayer player, ICargoStore hold)
        {
            var moved = new GdDict();
            long total = 0;
            if (player == null || hold == null)
                return new GdDict { { "moved", moved }, { "total_moved", 0L } };
            var ids = new List<object>(player.Items.Keys);
            GdSort.Sort(ids);
            foreach (object idV in ids)
            {
                string itemId = V.Str(idV);
                if (!HAULABLE_CATEGORIES.Contains(player.GetCategory(itemId))) continue;
                long have = player.GetQuantity(itemId);
                if (have <= 0) continue;
                long accepted = hold.AddItem(itemId, have);
                if (accepted <= 0) continue;
                long pulled = player.RemoveItem(itemId, accepted);
                // pulled == accepted by construction; guard anyway.
                if (pulled > 0)
                {
                    moved[itemId] = moved.GetInt(itemId, 0) + pulled;
                    total += pulled;
                }
            }
            return new GdDict { { "moved", moved }, { "total_moved", total } };
        }

        /// <summary>
        /// Moves as much of <paramref name="category"/> from hold -> player as the player's carry room
        /// accepts. Returns { "moved": {id:qty}, "total_moved": int }.
        /// </summary>
        public static GdDict WithdrawCategory(ICargoHold hold, ICargoStore player, string category)
        {
            var moved = new GdDict();
            long total = 0;
            if (player == null || hold == null || string.IsNullOrEmpty(category))
                return new GdDict { { "moved", moved }, { "total_moved", 0L } };
            GdArray entries = hold.GetItemsByCategory(category) ?? new GdArray();
            foreach (object entryV in entries)
            {
                var entry = entryV as GdDict ?? new GdDict();
                string itemId = V.Str(entry.Get("id", ""));
                long have = V.I64(entry.Get("quantity", 0L));
                if (itemId.Length == 0 || have <= 0) continue;
                long accepted = player.AddItem(itemId, have);
                if (accepted <= 0) continue;
                long pulled = hold.RemoveItem(itemId, accepted);
                if (pulled > 0)
                {
                    moved[itemId] = moved.GetInt(itemId, 0) + pulled;
                    total += pulled;
                }
            }
            return new GdDict { { "moved", moved }, { "total_moved", total } };
        }

        /// <summary>
        /// Moves up to <paramref name="qty"/> of one item_id from src -> dst. The destination enforces its own
        /// cap inside add_item, and src loses EXACTLY what dst accepted. Returns the count actually moved.
        /// </summary>
        public static long MoveItem(ICargoStore src, ICargoStore dst, string itemId, long qty)
        {
            if (src == null || dst == null || string.IsNullOrEmpty(itemId) || qty <= 0) return 0;
            long have = src.GetQuantity(itemId);
            long want = Math.Min(qty, have);
            if (want <= 0) return 0;
            long accepted = dst.AddItem(itemId, want);
            if (accepted <= 0) return 0;
            return src.RemoveItem(itemId, accepted);
        }

        /// <summary>Applies <see cref="MoveItem"/> per entry (ids sorted for determinism). Returns total moved.</summary>
        public static long MoveItems(ICargoStore src, ICargoStore dst, GdDict idToQty)
        {
            long total = 0;
            if (idToQty == null) return total;
            var ids = new List<object>(idToQty.Keys);
            GdSort.Sort(ids);
            foreach (object idV in ids)
                total += MoveItem(src, dst, V.Str(idV), V.I64(idToQty[idV]));
            return total;
        }
    }
}
