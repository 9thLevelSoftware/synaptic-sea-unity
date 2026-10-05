using SynapticSea.Core.Systems;
namespace SynapticSea.Core.Variant
{
    internal sealed partial class ParticipantProjectionCohort
    {
        internal bool TryCaptureInventoryInput(InventoryState actualInventory, out InventoryInputReceipt receipt, out string reason)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                receipt = null;
                if (!ReferenceEquals(_inventory, actualInventory)) { reason = "foreign_inventory_input"; return false; }
                if (!CurrentUnderGate()) { reason = "participant_cohort_not_ready"; return false; }
                // Pure existing stock lookup: portable_oxygen_pump tool possession yields .5 or 1.
                // No ComponentMass, rejection, presentation delegate or resource reader is invoked.
                receipt = InventoryInputReceipt.Mint(this, _inventory, _owners[0], _stamps[0], _inventory.Items,
                    _inventory.GetDrainMultiplier(), _maintenanceIssuer);
                reason = ""; return true;
            }
        }
        internal sealed class InventoryInputReceipt
        {
            readonly ParticipantProjectionCohort _cohort;
            readonly InventoryState _inventory;
            readonly TrackedParticipantOwner _owner;
            readonly GdDict _items;
            readonly ulong _stamp;
            internal double DrainMultiplier { get; }
            private InventoryInputReceipt(ParticipantProjectionCohort cohort, InventoryState inventory,
                TrackedParticipantOwner owner, ulong stamp, GdDict items, double drain)
            { _cohort = cohort; _inventory = inventory; _owner = owner; _stamp = stamp; _items = items; DrainMultiplier = drain; }
            internal static InventoryInputReceipt Mint(ParticipantProjectionCohort cohort, InventoryState inventory,
                TrackedParticipantOwner owner, ulong stamp, GdDict items, double drain, object issuer)
            {
                CommonParticipantGate.RequireHeld();
                if (cohort == null || !cohort.OwnsMaintenanceIssuer(issuer) || !ReferenceEquals(cohort._inventory, inventory) ||
                    !ReferenceEquals(cohort._owners[0], owner) || cohort._stamps[0] != stamp || !ReferenceEquals(inventory.Items, items) ||
                    !cohort.CurrentUnderGate() || (drain != .5 && drain != 1)) throw new System.ArgumentException("unissued_inventory_input");
                return new InventoryInputReceipt(cohort, inventory, owner, stamp, items, drain);
            }
            internal bool MatchesUnderGate(InventoryState actualInventory)
            {
                CommonParticipantGate.RequireHeld();
                return ReferenceEquals(_inventory, actualInventory) && ReferenceEquals(_cohort._inventory, actualInventory) &&
                    ReferenceEquals(_owner, _cohort._owners[0]) && _owner.MatchesUnderGate(_stamp) &&
                    ReferenceEquals(_inventory.Items, _items) && _cohort.CurrentUnderGate();
            }
        }
    }
}
