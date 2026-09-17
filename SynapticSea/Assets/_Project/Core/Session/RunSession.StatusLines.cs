// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: tracker status lines (8367-8433), the manager
// compat summary (8435-8486) and the inventory HUD refresh (3005).
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        void RefreshTrackerSystemStatusLines() => Events.RaiseTrackerSystemStatusLines(CombinedSystemStatusLines());

        void RefreshInventoryHud() => RefreshTrackerSystemStatusLines();

        /// <summary><c>_combined_system_status_lines()</c>: the tracker's system/status block.</summary>
        public List<string> CombinedSystemStatusLines()
        {
            var lines = new List<string>();
            if (ShipSystemsManager != null)
            {
                GdDict compat = ManagerCompatSummary();
                lines.Add("Power: " + V.I64(compat.Get("power_percent", 0L)) + "%");
                lines.Add("Reactor: " + V.I64(compat.Get("reactor_stability_percent", 0L)) + "%");
                lines.Add("Supplies: " + (compat.GetBool("emergency_supplies_recovered") ? "OK" : "LOW"));
                lines.Add("Main Power: " + (compat.GetBool("main_power_restored") ? "ON" : "OFF"));
                lines.Add("Logs: " + (compat.GetBool("navigation_logs_downloaded") ? "DOWNLOADED" : "PENDING"));
                lines.Add("Reactor: " + (compat.GetBool("reactor_stabilized") ? "STABLE" : "UNSTABLE"));
            }
            if (RouteControlState != null)
                lines.AddRange(RouteControlState.GetStatusLines());
            if (InventoryState != null)
            {
                foreach (string line in InventoryState.GetStatusLines())
                {
                    if (GdString.BeginsWith(line, "weight="))
                        continue;
                    lines.Add(line);
                }
            }
            if (_lastLootFeedbackLine.Length > 0)
                lines.Add(_lastLootFeedbackLine);
            if (_lastCaptionLine.Length > 0)
                lines.Add("Caption: " + _lastCaptionLine);
            if (UniqueItemState != null)
                lines.AddRange(UniqueItemState.GetStatusLines());
            if (PowerGridState != null)
                lines.AddRange(PowerGridState.GetStatusLines());
            if (LifeSupportExpandedState != null)
                lines.AddRange(LifeSupportExpandedState.GetStatusLines());
            HullIntegrityState hull = ActiveHull();
            if (hull != null)
                lines.AddRange(hull.GetStatusLines());
            FireSuppressionState activeFire = ActiveFireState();
            if (activeFire != null)
                lines.AddRange(activeFire.GetStatusLines());
            if (PropulsionExpandedState != null)
                lines.AddRange(PropulsionExpandedState.GetStatusLines());
            if (SustenanceState != null)
                lines.AddRange(SustenanceState.GetStatusLines());
            if (PlayerProgression != null)
                lines.Add("Repair Skill: " + PlayerProgression.GetSkillLevel("repair"));
            if (HallucinationManager != null)
                lines.AddRange(HallucinationManager.GetHallucinatedStatusLines());
            return lines;
        }

        public bool CombinedSystemStatusLinesContain(string token)
        {
            foreach (string line in CombinedSystemStatusLines())
            {
                if (GdString.Contains(line, token))
                    return true;
            }
            return false;
        }

        double SubHealth(string systemId, string subId)
        {
            ShipSubcomponent sub = ShipSystemsManager?.GetSystem(systemId)?.GetSubcomponent(subId);
            return sub != null ? sub.Health : 0.0;
        }

        bool SubFunctional(string systemId, string subId)
        {
            ShipSubcomponent sub = ShipSystemsManager?.GetSystem(systemId)?.GetSubcomponent(subId);
            return sub != null && sub.IsFunctional();
        }

        /// <summary>
        /// Flag-shaped summary derived from manager subcomponent state + the narrative record; feeds route control, the
        /// breach oxygen model and the HUD (ADR-0009: never stored).
        /// </summary>
        public GdDict ManagerCompatSummary()
        {
            bool powerRestored = SubFunctional("power", "power_distribution") && SubFunctional("power", "battery_cells");
            bool reactorFull = SubHealth("power", "reactor_core") >= 1.0;
            double powerHealth = 0.0;
            if (ShipSystemsManager != null && ShipSystemsManager.GetSystem("power") != null)
                powerHealth = ShipSystemsManager.GetSystem("power").Health();
            return new GdDict
            {
                { "emergency_supplies_recovered", CompletedObjectiveTypes.Has("recover_supplies") },
                { "main_power_restored", powerRestored },
                { "navigation_logs_downloaded", CompletedObjectiveTypes.Has("download_logs") },
                { "reactor_stabilized", reactorFull },
                { "blocked_routes_cleared", powerRestored },
                { "extraction_unlocked", reactorFull },
                { "power_percent", (long)GdMath.Round(powerHealth * 100.0) },
                { "reactor_stability_percent", (long)GdMath.Round(SubHealth("power", "reactor_core") * 100.0) },
            };
        }

        public long GetBlockedAffordanceVisibleCount() => BlockedAffordancesCleared ? 0 : RouteGateNodes.Count;

        public GdDict GetShipSystemsSummary()
        {
            if (ShipSystemsManager == null)
            {
                return new GdDict
                {
                    { "main_power_restored", false }, { "extraction_unlocked", false }, { "power_percent", 0L },
                    { "reactor_stability_percent", 0L }, { "blocked_affordance_visible_count", 0L },
                };
            }
            GdDict summary = ManagerCompatSummary();
            summary["blocked_affordance_visible_count"] = GetBlockedAffordanceVisibleCount();
            GdDict expanded = ExpandedShipSystemsSummary();
            foreach (object key in expanded.Keys)
                summary[key] = expanded[key];
            return summary;
        }
    }
}
