// Ported from scripts/tools/tool_pickup.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// REQ-007 pickup node. Carries a single tool id; interacting adds the id to InventoryState exactly once, hides
    /// the marker + collision, and raises <see cref="ToolAcquired"/>.
    /// </summary>
    public sealed class ToolPickup : SessionInteractable
    {
        public override string Kind => "tool_pickup";

        /// <summary>signal tool_acquired(tool_id)</summary>
        public event Action<string> ToolAcquired;

        public string ToolId = "";
        public InventoryState InventoryState;
        public bool Acquired = false;
        public bool MarkerVisible = true;

        /// <summary>RUNTIME: <c>marker.visible = marker_visible and not acquired</c>.</summary>
        public bool MarkerShown => MarkerVisible && !Acquired;

        /// <summary>RUNTIME: <c>collision_shape.disabled</c> (set true on acquisition).</summary>
        public bool CollisionDisabled => Acquired;

        public void Configure(string toolId, InventoryState inventoryState, Vec3 worldPosition, double radius = 1.8)
        {
            ToolId = toolId;
            InventoryState = inventoryState;
            InteractionRadius = radius;
            Acquired = false;
            CandidatePlayerInRange = false;
            LocalPosition = worldPosition;
            NodeName = "ToolPickup_" + toolId;
            // RUNTIME: set_meta("tool_id", ...), set_meta("tool_pickup", true); sphere collision (radius);
            // GameplayPropFactory.build("tool_case") visual, marker visible = MarkerShown.
        }

        public void SetMarkerVisible(bool isVisible)
        {
            MarkerVisible = isVisible;
            // RUNTIME: marker.visible = marker_visible and not acquired.
            NotifyChanged();
        }

        /// <summary>True when the player is in range of a still-live pickup (already-owned deny still counts).</summary>
        public bool IsInteractCandidate(Vec3 playerPosition)
        {
            if (Acquired)
                return false;
            return IsPlayerInDirectRangeLenient(playerPosition);
        }

        public bool TryInteract(Vec3 playerPosition)
        {
            if (Acquired)
                return false;
            // Always require direct range at the moment of interaction (candidate_player alone is not trusted: a
            // teleport without body_exited leaves it stale).
            if (!IsPlayerInDirectRangeLenient(playerPosition))
                return false;
            if (InventoryState == null)
                return false;
            if (!InventoryState.AddTool(ToolId))
                return false;
            Acquired = true;
            SetMarkerVisible(false);
            // RUNTIME: collision_shape.disabled = true.
            NotifyChanged();
            ToolAcquired?.Invoke(ToolId);
            return true;
        }
    }
}
