// Ported from scripts/tools/hangar_bay_control.gd @ 96ecb2b0
using System;
using System.Diagnostics;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// The hangar-bay control of a carrier ship. Interact to dock a co-present ship into a free slot, or launch a
    /// bayed ship back out. Sensor + signal only (eligibility lives in the coordinator). Strict in-range gate.
    /// slot_index == -1 means "coordinator chooses".
    /// </summary>
    public sealed class HangarBayControl : SessionInteractable
    {
        public override string Kind => "hangar_bay_control";

        /// <summary>signal bay_dock_requested(carrier_id, slot_index)</summary>
        public event Action<string, long> BayDockRequested;
        /// <summary>signal bay_launch_requested(carrier_id, slot_index)</summary>
        public event Action<string, long> BayLaunchRequested;

        public string CarrierId = "";

        public void Configure(string carrierId, Vec3 worldPosition, double radius = 1.8)
        {
            Debug.Assert(radius >= 0.0, "HangarBayControl.configure: radius must be non-negative");
            CarrierId = carrierId;
            InteractionRadius = radius;
            LocalPosition = worldPosition;
            NodeName = "HangarBayControl_" + carrierId;
            // RUNTIME: set_meta("hangar_bay_control", true); sphere collision (radius); box marker
            // (radius*0.5 x radius x radius*0.5, orange 0.95,0.6,0.15,0.7 unshaded, no shadow), always visible.
        }

        /// <summary>Raises BayDockRequested(carrier_id, slot_index) and returns true iff in range.</summary>
        public bool TryDock(Vec3 playerPosition, long slotIndex = -1)
        {
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            BayDockRequested?.Invoke(CarrierId, slotIndex);
            return true;
        }

        /// <summary>Raises BayLaunchRequested(carrier_id, slot_index) and returns true iff in range.</summary>
        public bool TryLaunch(Vec3 playerPosition, long slotIndex = -1)
        {
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            BayLaunchRequested?.Invoke(CarrierId, slotIndex);
            return true;
        }
    }
}
