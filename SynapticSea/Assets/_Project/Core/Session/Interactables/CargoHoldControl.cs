// Ported from scripts/tools/cargo_hold_control.gd @ 96ecb2b0
using System;
using System.Diagnostics;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// The cargo-hold control of a ship. Interact to deposit all haulable salvage into this ship's hold, or withdraw
    /// a category back out. Sensor + signal only (the coordinator moves items). Strict in-range gate.
    /// </summary>
    public sealed class CargoHoldControl : SessionInteractable
    {
        public override string Kind => "cargo_hold_control";

        /// <summary>signal cargo_deposit_requested(carrier_id)</summary>
        public event Action<string> CargoDepositRequested;
        /// <summary>signal cargo_withdraw_requested(carrier_id, category)</summary>
        public event Action<string, string> CargoWithdrawRequested;

        public string CarrierId = "";

        public void Configure(string carrierId, Vec3 worldPosition, double radius = 1.8)
        {
            Debug.Assert(radius >= 0.0, "CargoHoldControl.configure: radius must be non-negative");
            CarrierId = carrierId;
            InteractionRadius = radius;
            LocalPosition = worldPosition;
            NodeName = "CargoHoldControl_" + carrierId;
            // RUNTIME: set_meta("cargo_hold_control", true); sphere collision (radius); box marker
            // (radius*0.5 cube, cyan 0.2,0.7,0.85,0.7 unshaded, no shadow), always visible.
        }

        /// <summary>Raises CargoDepositRequested(carrier_id) and returns true iff in range.</summary>
        public bool TryDeposit(Vec3 playerPosition)
        {
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            CargoDepositRequested?.Invoke(CarrierId);
            return true;
        }

        /// <summary>Raises CargoWithdrawRequested(carrier_id, category) and returns true iff in range.</summary>
        public bool TryWithdraw(Vec3 playerPosition, string category)
        {
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            CargoWithdrawRequested?.Invoke(CarrierId, category);
            return true;
        }
    }
}
