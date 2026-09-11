// Ported from scripts/tools/bridge_terminal.gd @ 96ecb2b0
using System;
using System.Diagnostics;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// The bridge command terminal of a pilotable ship. Interacting = "log in". Sensor + signal only: login gating
    /// lives in the coordinator. Strict in-range gate.
    /// </summary>
    public sealed class BridgeTerminal : SessionInteractable
    {
        public override string Kind => "bridge_terminal";

        /// <summary>signal login_requested(ship_id)</summary>
        public event Action<string> LoginRequested;

        public string ShipId = "";

        public void Configure(string shipId, Vec3 worldPosition, double radius = 1.8)
        {
            Debug.Assert(radius >= 0.0, "BridgeTerminal.configure: radius must be non-negative");
            ShipId = shipId;
            InteractionRadius = radius;
            LocalPosition = worldPosition;
            NodeName = "BridgeTerminal_" + shipId;
            // RUNTIME: set_meta("bridge_terminal", true); sphere collision (radius); box marker
            // (radius*0.4 x radius x radius*0.4, 0.2,0.7,0.95,0.7 unshaded, no shadow), always visible.
        }

        /// <summary>Raises LoginRequested(ship_id) and returns true iff the player is in direct range.</summary>
        public bool TryLogin(Vec3 playerPosition)
        {
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            LoginRequested?.Invoke(ShipId);
            return true;
        }
    }
}
