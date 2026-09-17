// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _build_run_snapshot (10112-10239) and
// _apply_run_snapshot (10524-10754).
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// Builds a <see cref="RunSnapshot"/> from a live <see cref="RunSession"/> and applies one back. Every summary key,
    /// the RunSnapshot field set and <c>SUMMARY_FIELDS</c> order come from the ported RunSnapshot; this class only
    /// reproduces which model feeds which field, in the Godot order. The player position is the scene half
    /// (<see cref="IRunSceneState"/>).
    /// </summary>
    public static class RunSnapshotAssembler
    {
        /// <summary><c>_build_run_snapshot(use_home_arc_summary)</c>; null when not started / complete / no save service.</summary>
        public static RunSnapshot Build(RunSession s, bool useHomeArcSummary = false)
        {
            if (!s.PlayableStarted || s.SliceComplete)
                return null;
            if (s.SaveLoadService == null)
                return null;
            var snapshot = new RunSnapshot();
            snapshot.LayoutPath = s.LayoutPath;
            snapshot.KitPath = s.KitPath;
            snapshot.GameplaySlicePath = s.GameplaySlicePath;
            if (s.Scene != null && s.Scene.HasPlayer)
            {
                Vec3 pos = s.Scene.PlayerPosition;
                snapshot.PlayerPosition = GdArray.Of((double)pos.X, (double)pos.Y, (double)pos.Z);
            }
            snapshot.CurrentObjectiveSequence = s.CurrentObjectiveSequence;
            if (s.ShipSystemsManager != null)
            {
                snapshot.ShipSystemsSummary = s.ShipSystemsManager.GetSummary();
                // Only the authoritative objective record is persisted; flag-shaped fields are derived live (ADR-0009).
                snapshot.ShipSystemsSummary["completed_objective_types"] = new GdArray(s.CompletedObjectiveTypes.Keys);
                GdDict expanded = s.GetShipSystemsExpandedSummary();
                foreach (object key in expanded.Keys)
                    snapshot.ShipSystemsSummary[key] = s.GetShipSystemsExpandedSummary()[key];
            }
            if (s.RouteControlState != null)
                snapshot.RouteControlSummary = s.GetRouteControlSummary();
            if (s.OxygenState != null)
                snapshot.OxygenSummary = s.GetOxygenSummary();
            if (s.InventoryState != null)
                snapshot.InventorySummary = s.InventoryState.GetSummary();
            if (s.CraftingState != null)
            {
                snapshot.CraftingSummary = s.CraftingState.GetSummary();
                if (s.FieldCraftingState != null)
                    snapshot.CraftingSummary["field_crafting"] = s.FieldCraftingState.GetSummary().Get("field_crafting", new GdDict());
            }
            if (s.MaterialState != null)
                snapshot.MaterialSummary = s.MaterialState.GetSummary();
            if (s.ThreatManager != null)
            {
                snapshot.InventorySummary["combat_hotbar_text"] = s.LastWeaponHotbarText;
                snapshot.InventorySummary["threat_summary"] = s.ThreatManager.GetSummary();
            }
            // M7-B Task 7: the legacy fire_summary stays at its default (authoritative fire rides fire_suppression_summary).
            if (useHomeArcSummary)
            {
                if (s.HomeShip != null && !s.HomeShip.ArcSummary.IsEmpty)
                    snapshot.ElectricalArcSummary = s.HomeShip.ArcSummary.DeepCopy();
            }
            else if (s.ElectricalArcState != null)
            {
                s.SyncArcSummaryForSave();
                snapshot.ElectricalArcSummary = s.ElectricalArcState.GetSummary();
            }
            if (s.ObjectiveProgressState != null)
                snapshot.ObjectiveProgressSummary = s.ObjectiveProgressState.GetSummary();
            if (s.PlayerProgression != null)
                snapshot.PlayerProgressionSummary = s.PlayerProgression.GetSummary();
            if (s.SkillTreeState != null)
                snapshot.SkillTreeSummary = s.SkillTreeState.ToDict();
            if (s.SettingsState != null)
                snapshot.SettingsSummary = s.SettingsState.GetSummary();
            if (s.AudioManager != null)
                snapshot.AudioSummary = s.AudioManager.GetSummary();
            if (s.VitalsState != null)
                snapshot.VitalsSummary = s.VitalsState.GetSummary();
            if (s.SanityState != null)
                snapshot.SanitySummary = s.SanityState.GetSummary();
            if (s.RadiationState != null)
                snapshot.RadiationSummary = s.RadiationState.GetSummary();
            if (s.BodyTemperatureState != null)
                snapshot.TemperatureSummary = s.BodyTemperatureState.GetSummary();
            if (s.StatusEffectsState != null)
                snapshot.StatusEffectsSummary = s.StatusEffectsState.GetSummary();
            if (s.HallucinationDirector != null)
                snapshot.HallucinationSummary = s.HallucinationDirector.GetSummary();
            if (s.SpoilageState != null)
                snapshot.SpoilageSummary = s.SpoilageState.GetSummary();
            if (s.HydroponicsState != null)
                snapshot.HydroponicsSummary = s.HydroponicsState.GetSummary();
            if (s.WaterRecyclerState != null)
                snapshot.WaterRecyclerSummary = s.WaterRecyclerState.GetSummary();
            if (s.ConsumableState != null)
                snapshot.ConsumableSummary = s.ConsumableState.GetSummary();
            if (s.MedicineState != null)
                snapshot.MedicineSummary = s.MedicineState.GetSummary();
            if (s.StimulantState != null)
                snapshot.StimulantSummary = s.StimulantState.GetSummary();
            if (s.AddictionState != null)
                snapshot.AddictionSummary = s.AddictionState.GetSummary();
            if (s.AmmoState != null)
                snapshot.AmmoSummary = s.AmmoState.GetSummary();
            if (s.UtilityItemState != null)
                snapshot.UtilitySummary = s.UtilityItemState.GetSummary();
            s.SyncPillarSummariesForSave();
            if (s.ModuleIntegrityMap != null)
                snapshot.ModuleIntegritySummary = s.ModuleIntegrityMap.GetSummary();
            else if (s.CurrentShip != null && !s.CurrentShip.ModuleIntegritySummary.IsEmpty)
                snapshot.ModuleIntegritySummary = s.CurrentShip.ModuleIntegritySummary.DeepCopy();
            if (s.ComponentPlacementState != null)
                snapshot.ComponentPlacementSummary = s.ComponentPlacementState.GetSummary();
            else if (s.CurrentShip != null && !s.CurrentShip.ComponentPlacementSummary.IsEmpty)
                snapshot.ComponentPlacementSummary = s.CurrentShip.ComponentPlacementSummary.DeepCopy();
            if (s.WorkActionDriver != null && s.WorkActionDriver.Work != null)
            {
                string wst = s.WorkActionDriver.GetStatus();
                if (wst == "active" || wst == "interrupted")
                {
                    snapshot.WorkActionSummary = new GdDict
                    {
                        { "schema", "work_action_v1" },
                        { "active", true },
                        { "summary", s.WorkActionDriver.Work.GetSummary() },
                    };
                }
            }
            if (s.ShipModificationState != null)
                snapshot.ShipModificationSummary = s.ShipModificationState.GetSummary();
            // Unity port (gate2-current-run-5): E2 wounds / chart / tutorial, E3 manual-slot state, C4 run context.
            if (s.WoundState != null)
                snapshot.WoundSummary = s.WoundState.GetSummary();
            if (s.WebChartState != null)
                snapshot.WebChartSummary = s.WebChartState.GetSummary();
            if (s.TutorialState != null)
                snapshot.TutorialSummary = s.TutorialState.GetSummary();
            if (s.EquipmentState != null)
                snapshot.EquipmentSummary = s.EquipmentState.GetSummary();
            if (s.HomeShip != null)
            {
                snapshot.HomeLootedContainers = s.HomeShip.LootedContainerIds.ShallowCopy();
                snapshot.HomeShipInventory = s.HomeShip.GetInventory().GetSummary();
            }
            snapshot.RunContext = s.GetRunContextSummary();
            // ADR-0046: real slot metadata.
            snapshot.PlayTimeSeconds = s.RunPlayTimeSeconds;
            snapshot.CurrentLocation = "home";
            if (s.CurrentShip != null && s.CurrentShip.MarkerId != "")
                snapshot.CurrentLocation = s.CurrentShip.MarkerId;
            if (s.SynapticSeaWorld != null)
                snapshot.WorldSeed = s.SynapticSeaWorld.WorldSeed;
            snapshot.SliceVersion = SaveLoadService.CURRENT_SLICE_VERSION;
            snapshot.GodotVersion = s.Deps.Engine.VersionString;
            snapshot.SavedAt = s.Clock.DateTimeString(true);
            return snapshot;
        }

        /// <summary>
        /// <c>_apply_run_snapshot(snapshot)</c>: reset the live slice, reload the ship through the normal load path, then
        /// apply every saved model summary in the Godot order, rebuild the dependent interactables, restore the objective
        /// sequence, and finally put the player at the saved position (the scene half).
        /// </summary>
        public static bool Apply(RunSession s, RunSnapshot snapshot) => s.ApplyRunSnapshotInternal(snapshot);
    }
}
