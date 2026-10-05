using System.Linq;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        internal bool AuxiliaryFabricatorFeedReady() => !AuxiliaryServicesEnabled || HomeShip != null &&
            IsAuxiliaryHardwareReady(HomeShip.ShipId, "maintenance_fabricator_feed_01");

        public GdDict GetCargoBulkEligibility(string ownerId)
        {
            var ship = FindShipById(ownerId);
            if (ship == null) return new GdDict { { "ok", false }, { "reason", "Cargo hold unavailable" } };
            if (!AuxiliaryServicesEnabled || ship != HomeShip) return new GdDict { { "ok", true }, { "reason", "" } };
            if (!IsAuxiliaryHardwareReady(ownerId, "maintenance_cargo_relay_01"))
                return new GdDict { { "ok", false }, { "reason", "Repair the cargo relay to use bulk transfer" } };
            bool powered = PowerGridState != null && PowerGridState.GetAllocationRatio("stations") > 0
                && (ShipModificationState == null || ShipModificationState.IsPowerBudgetOk());
            return new GdDict { { "ok", powered }, { "reason", powered ? "" : "Cargo relay has no power" } };
        }

        public GdDict GetAuxiliaryUtilityEffects(string ownerId, string serviceId)
        {
            string kind = serviceId == "maintenance_fabricator_feed_01" ? "fabricator_feed" :
                serviceId == "maintenance_cargo_relay_01" ? "cargo_relay" :
                serviceId == "medbay_task_light_01" ? "task_light" :
                serviceId == "airlock_dock_beacon_01" ? "dock_beacon" : "rack";
            var ship = FindShipById(ownerId);
            var service = ownerId == HomeShip?.ShipId ? GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(serviceId) : new GdDict();
            bool released = kind == "rack" && service.GetBool("released");
            bool ready = ship != null && IsAuxiliaryHardwareReady(ownerId, serviceId);
            bool powered = false;
            if (ship == HomeShip && ship != null && PowerGridState != null)
            {
                string circuit = kind == "task_light" || kind == "dock_beacon" ? "lights" : "stations";
                powered = ready && PowerGridState.GetAllocationRatio(circuit) > 0
                    && (circuit != "stations" || ShipModificationState == null || ShipModificationState.IsPowerBudgetOk());
            }
            var exterior = (ship?.SceneRoot as IShipLoaderView)?.GetAuthoredPortals()
                .Where(portal => portal != null && portal.IsValid && portal.IsExterior).ToArray();
            int exteriorCount = exterior?.Length ?? 0, openCount = exterior?.Count(portal => portal.IsOpen) ?? 0;
            string airlock = exteriorCount == 0 ? "unavailable" : openCount == 0 ? "closed" : openCount == exteriorCount ? "open" : "partly open";
            int connections = ship == null ? 0 : ship.DockedShips.Count + (ship.ParentShip == null ? 0 : 1);
            string status = !ready ? "Needs repair" : !powered ? "Repaired — no power" : "Online";
            if (kind == "rack") status = !released ? "Needs recovery" : GetAuxRackRemaining(serviceId).Values.Any(value => V.I64(value) > 0) ? "Stock available — take" : "Empty";
            if (kind == "dock_beacon" && powered) status = "Dock: " + connections + " connected; airlock " + airlock;
            return new GdDict { { "owner_id", ownerId ?? "" }, { "service_id", serviceId ?? "" }, { "kind", kind },
                { "released", released }, { "hardware_ready", ready }, { "powered", powered }, { "status", status },
                { "dock_connection_count", (long)connections }, { "airlock_state", airlock } };
        }
    }
}
