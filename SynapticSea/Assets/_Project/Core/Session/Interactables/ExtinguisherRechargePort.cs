// Ported from scripts/tools/extinguisher_recharge_port.gd @ 96ecb2b0
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// Stationary recharge station for the player's fire extinguisher (ADR-0041). Refills ExtinguisherState charge
    /// while powered AND a player is in range. The coordinator drives <see cref="Powered"/> each frame from the
    /// "stations" power channel (same precedent as CraftingStation.SetPowered).
    /// </summary>
    public sealed class ExtinguisherRechargePort : SessionInteractable
    {
        public override string Kind => "extinguisher_recharge_port";

        public ExtinguisherState ExtinguisherState;
        public bool Powered = false;

        public void Configure(ExtinguisherState extinguisherState, Vec3 worldPosition, double radius = 1.8)
        {
            ExtinguisherState = extinguisherState;
            InteractionRadius = radius;
            CandidatePlayerInRange = false;
            Powered = false;
            LocalPosition = worldPosition;
            NodeName = "ExtinguisherRechargePort";
            // RUNTIME: set_meta("extinguisher_recharge_port", true); sphere collision (radius); box marker
            // (radius*0.5 cube, 0.2,0.85,0.6,0.7 unshaded, no shadow), always visible.
        }

        public void SetPowered(bool value)
        {
            Powered = value;
        }

        /// <summary>
        /// Godot <c>_process(delta)</c>: recharges while powered and the overlapping player
        /// (<see cref="SessionInteractable.CandidatePlayerInRange"/>, alive) is within the strict direct range.
        /// </summary>
        public void Process(double delta, Vec3 playerPosition, bool playerValid = true)
        {
            // Godot only runs _process on live, in-tree nodes.
            if (!IsValid || !IsInsideTree)
                return;
            if (!Powered || ExtinguisherState == null)
                return;
            if (!PlayerInRange(playerPosition, playerValid))
                return;
            ExtinguisherState.Recharge(delta);
        }

        bool PlayerInRange(Vec3 playerPosition, bool playerValid)
        {
            // is_instance_valid(candidate_player): the overlap must be set and the player alive.
            if (!CandidatePlayerInRange || !playerValid)
                return false;
            return IsPlayerInDirectRangeStrict(playerPosition);
        }
    }
}
