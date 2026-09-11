// Ported from scripts/tools/cart_control.gd @ 96ecb2b0
using System;
using System.Diagnostics;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// A pushable cart's walk-up control. Interact to grab (push) it, or load/unload salvage. Sensor + signal only:
    /// it never moves items or reparents itself. Strict in-range gate.
    /// </summary>
    public sealed class CartControl : SessionInteractable
    {
        public override string Kind => "cart_control";

        /// <summary>signal cart_grab_requested(cart_id)</summary>
        public event Action<string> CartGrabRequested;
        /// <summary>signal cart_load_requested(cart_id)</summary>
        public event Action<string> CartLoadRequested;
        /// <summary>signal cart_unload_requested(cart_id, category)</summary>
        public event Action<string, string> CartUnloadRequested;

        public string CartId = "";

        public void Configure(string cartId, Vec3 worldPosition, double radius = 1.8)
        {
            Debug.Assert(radius >= 0.0, "CartControl.configure: radius must be non-negative");
            CartId = cartId;
            InteractionRadius = radius;
            LocalPosition = worldPosition;
            NodeName = "CartControl_" + cartId;
            // RUNTIME: set_meta("cart_control", true); sphere collision (radius); box marker
            // (radius*0.5 x radius*0.4 x radius*0.7, amber 0.85,0.75,0.2,0.7 unshaded, no shadow), always visible.
        }

        public bool TryGrab(Vec3 playerPosition)
        {
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            CartGrabRequested?.Invoke(CartId);
            return true;
        }

        public bool TryLoad(Vec3 playerPosition)
        {
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            CartLoadRequested?.Invoke(CartId);
            return true;
        }

        public bool TryUnload(Vec3 playerPosition, string category)
        {
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            CartUnloadRequested?.Invoke(CartId, category);
            return true;
        }
    }
}
