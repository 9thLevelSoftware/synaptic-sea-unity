// Ported from scripts/tools/work_yield_drop.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// Floor drop for WorkAction yields that could not fit the cart (overload). Interact once to scoop items into
    /// InventoryState; a full scoop frees the node (Godot <c>queue_free()</c> -> <see cref="SessionInteractable.Free"/>).
    /// </summary>
    public sealed class WorkYieldDrop : SessionInteractable
    {
        public override string Kind => "work_yield_drop";

        /// <summary>signal scooped(drop_id, granted)</summary>
        public event Action<string, GdDict> Scooped;

        public string DropId = "";
        /// <summary>item_id -> qty</summary>
        public GdDict Items = new GdDict();
        public InventoryState InventoryState = null;
        public bool ScoopedFlag = false;

        /// <summary>RUNTIME: <c>marker.visible</c> (hidden once fully scooped).</summary>
        public bool MarkerShown => !ScoopedFlag;

        /// <summary>RUNTIME: <c>collision_shape.disabled</c> (true once fully scooped).</summary>
        public bool CollisionDisabled => ScoopedFlag;

        public void Configure(string dropId, GdDict items, InventoryState inventoryState, Vec3 worldPosition, double radius = 1.8)
        {
            DropId = dropId;
            Items = (items ?? new GdDict()).DeepCopy();
            InventoryState = inventoryState;
            InteractionRadius = radius;
            ScoopedFlag = false;
            LocalPosition = worldPosition;
            NodeName = "WorkYieldDrop_" + DropId;
            // RUNTIME: set_meta("work_yield_drop", true); sphere collision (radius, created once); box marker
            // (0.35 x 0.25 x 0.35 at local y 0.2, 0.9,0.75,0.25,0.9 unshaded).
        }

        /// <summary>True when the player is in scoop range of a still-live pile (stack-full deny still counts).</summary>
        public bool IsInteractCandidate(Vec3 playerPosition)
        {
            if (ScoopedFlag)
                return false;
            return CandidatePlayerInRange || InRange(playerPosition);
        }

        public bool TryInteract(Vec3 playerPosition)
        {
            if (ScoopedFlag || InventoryState == null)
                return false;
            if (!CandidatePlayerInRange && !InRange(playerPosition))
                return false;
            var granted = new GdDict();
            foreach (object itemId in new List<object>(Items.Keys))
            {
                long qty = V.I64(Items[itemId]);
                if (qty <= 0)
                    continue;
                long added = InventoryState.AddItem(V.Str(itemId), qty);
                if (added > 0)
                {
                    granted[V.Str(itemId)] = added;
                    Items[itemId] = qty - added;
                }
            }
            if (granted.IsEmpty)
                // Inventory full / cannot accept — leave drop in place for later scoop.
                return false;
            // Clear fully taken stacks; keep residual for partial scoops.
            var remaining = new GdDict();
            foreach (object itemId2 in new List<object>(Items.Keys))
            {
                long left = V.I64(Items[itemId2]);
                if (left > 0)
                    remaining[V.Str(itemId2)] = left;
            }
            Items = remaining;
            if (remaining.IsEmpty)
            {
                ScoopedFlag = true;
                Scooped?.Invoke(DropId, granted);
                // RUNTIME: marker.visible = false; collision_shape.disabled = true; queue_free().
                NotifyChanged();
                Free();
            }
            else
            {
                // Partial scoop: keep the pile, emit granted portion.
                Scooped?.Invoke(DropId, granted);
            }
            return true;
        }

        /// <summary><c>_in_range</c>: global distance within the radius plus a 0.15 m slack.</summary>
        bool InRange(Vec3 playerPosition)
        {
            return GlobalPosition.DistanceTo(playerPosition) <= InteractionRadius + 0.15;
        }
    }
}
