// Ported from scripts/systems/run_snapshot.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-012 current-run save snapshot. Pure data; holds only current-run state allowed by ADR-0007.
    /// Persistence is handled by SaveLoadService. Adding a field requires a new ADR.
    /// ADR-0031 added slot identity fields; ADR-0046 (gate2-current-run-4) added play_time_seconds,
    /// current_location, and world_seed.
    /// </summary>
    public class RunSnapshot
    {
        public string LayoutPath = "";
        public string KitPath = "";
        public string GameplaySlicePath = "";
        public GdArray PlayerPosition = GdArray.Of(0.0, 0.0, 0.0);
        public long CurrentObjectiveSequence = 1;
        public GdDict ShipSystemsSummary = new GdDict();
        public GdDict RouteControlSummary = new GdDict();
        public GdDict OxygenSummary = new GdDict();
        public GdDict InventorySummary = new GdDict();
        public GdDict FireSummary = new GdDict();
        public GdDict ElectricalArcSummary = new GdDict();
        public GdDict ObjectiveProgressSummary = new GdDict();
        public GdDict PlayerProgressionSummary = new GdDict();
        public GdDict SkillTreeSummary = new GdDict();
        public GdDict SettingsSummary = new GdDict();
        public GdDict AudioSummary = new GdDict();
        public GdDict SpoilageSummary = new GdDict();
        public GdDict HydroponicsSummary = new GdDict();
        public GdDict WaterRecyclerSummary = new GdDict();
        public GdDict CraftingSummary = new GdDict();
        public GdDict MaterialSummary = new GdDict();
        public GdDict ConsumableSummary = new GdDict();
        public GdDict MedicineSummary = new GdDict();
        public GdDict StimulantSummary = new GdDict();
        public GdDict AddictionSummary = new GdDict();
        public GdDict AmmoSummary = new GdDict();
        public GdDict UtilitySummary = new GdDict();

        // REQ-SV: survival vitals summaries
        public GdDict VitalsSummary = new GdDict();
        public GdDict SanitySummary = new GdDict();
        public GdDict RadiationSummary = new GdDict();
        public GdDict TemperatureSummary = new GdDict();
        public GdDict StatusEffectsSummary = new GdDict();
        // Session 3 B3: HallucinationDirector state (ADR-0042).
        public GdDict HallucinationSummary = new GdDict();
        // PKG-D8: pre-polish pillar models (empty defaults for historical fixtures).
        public GdDict ModuleIntegritySummary = new GdDict();
        public GdDict ComponentPlacementSummary = new GdDict();
        public GdDict WorkActionSummary = new GdDict();
        // PKG-D2.6: hub ship modification install manifest (power budget / plating).
        public GdDict ShipModificationSummary = new GdDict();

        // Unity port, gate2-current-run-5 (not in Godot's schema; SaveMigrationService gives older saves these empty defaults).
        // E2: wounds, web chart and tutorial/codex state; E3: what a manual slot must restore that only rode world.json;
        // C4: the run's seed / biome / difficulty.
        public GdDict WoundSummary = new GdDict();
        public GdDict WebChartSummary = new GdDict();
        public GdDict TutorialSummary = new GdDict();
        public GdDict EquipmentSummary = new GdDict();
        public GdArray HomeLootedContainers = new GdArray();
        public GdDict HomeShipInventory = new GdDict();
        public GdDict RunContext = new GdDict();

        /// <summary>The keys gate2-current-run-5 added over Godot's gate2-current-run-4 schema, in ToDict order.</summary>
        public static readonly GdArray PortExtensionFields = GdArray.Of(
            "wound_summary",
            "web_chart_summary",
            "tutorial_summary",
            "equipment_summary",
            "home_looted_containers",
            "home_ship_inventory",
            "run_context"
        );

        // ADR-0046: real slot metadata.
        public double PlayTimeSeconds = 0.0;
        public string CurrentLocation = "";
        public long WorldSeed = 0;

        public string SlotId = "";
        public string SlotKind = "";
        public bool IsAutosave = false;
        public bool IsQuicksave = false;
        // ADR-0043: reserved/unused (manual-slot loads never read it).
        public string ParentWorldSlot = "";
        // run_id slot-ownership rework (ADR-0043 addendum): stamped by SaveLoadService on every write.
        public string RunId = "";
        public string SliceVersion = "";
        /// <summary>Engine version stamp; the schema key stays <c>godot_version</c> (CoreServices.Engine.VersionString).</summary>
        public string GodotVersion = "";
        public string SavedAt = "";
        public long SavedAtEpoch = 0;

        /// <summary>The model summaries the snapshot carries.</summary>
        public static readonly GdArray SummaryFields = GdArray.Of(
            "ship_systems_summary",
            "route_control_summary",
            "oxygen_summary",
            "inventory_summary",
            "fire_summary",
            "electrical_arc_summary",
            "objective_progress_summary",
            "player_progression_summary",
            "skill_tree_summary",
            "settings_summary",
            "audio_summary",
            "spoilage_summary",
            "hydroponics_summary",
            "water_recycler_summary",
            "consumable_summary",
            "medicine_summary",
            "stimulant_summary",
            "addiction_summary",
            "ammo_summary",
            "utility_summary",
            "crafting_summary",
            "material_summary",
            "vitals_summary",
            "sanity_summary",
            "radiation_summary",
            "temperature_summary",
            "status_effects_summary",
            "hallucination_summary",
            "module_integrity_summary",
            "component_placement_summary",
            "work_action_summary",
            "ship_modification_summary"
        );

        public int GetSummaryCount() => SummaryFields.Count;

        public GdDict ToDict()
        {
            return new GdDict
            {
                { "layout_path", LayoutPath },
                { "kit_path", KitPath },
                { "gameplay_slice_path", GameplaySlicePath },
                { "player_position", PlayerPosition.ShallowCopy() },
                { "current_objective_sequence", CurrentObjectiveSequence },
                { "ship_systems_summary", ShipSystemsSummary.DeepCopy() },
                { "route_control_summary", RouteControlSummary.DeepCopy() },
                { "oxygen_summary", OxygenSummary.DeepCopy() },
                { "inventory_summary", InventorySummary.DeepCopy() },
                { "fire_summary", FireSummary.DeepCopy() },
                { "electrical_arc_summary", ElectricalArcSummary.DeepCopy() },
                { "objective_progress_summary", ObjectiveProgressSummary.DeepCopy() },
                { "player_progression_summary", PlayerProgressionSummary.DeepCopy() },
                { "skill_tree_summary", SkillTreeSummary.DeepCopy() },
                { "settings_summary", SettingsSummary.DeepCopy() },
                { "audio_summary", AudioSummary.DeepCopy() },
                { "spoilage_summary", SpoilageSummary.DeepCopy() },
                { "hydroponics_summary", HydroponicsSummary.DeepCopy() },
                { "water_recycler_summary", WaterRecyclerSummary.DeepCopy() },
                { "crafting_summary", CraftingSummary.DeepCopy() },
                { "material_summary", MaterialSummary.DeepCopy() },
                { "consumable_summary", ConsumableSummary.DeepCopy() },
                { "medicine_summary", MedicineSummary.DeepCopy() },
                { "stimulant_summary", StimulantSummary.DeepCopy() },
                { "addiction_summary", AddictionSummary.DeepCopy() },
                { "ammo_summary", AmmoSummary.DeepCopy() },
                { "utility_summary", UtilitySummary.DeepCopy() },
                { "vitals_summary", VitalsSummary.DeepCopy() },
                { "sanity_summary", SanitySummary.DeepCopy() },
                { "radiation_summary", RadiationSummary.DeepCopy() },
                { "temperature_summary", TemperatureSummary.DeepCopy() },
                { "status_effects_summary", StatusEffectsSummary.DeepCopy() },
                { "hallucination_summary", HallucinationSummary.DeepCopy() },
                { "module_integrity_summary", ModuleIntegritySummary.DeepCopy() },
                { "component_placement_summary", ComponentPlacementSummary.DeepCopy() },
                { "work_action_summary", WorkActionSummary.DeepCopy() },
                { "ship_modification_summary", ShipModificationSummary.DeepCopy() },
                { "wound_summary", WoundSummary.DeepCopy() },
                { "web_chart_summary", WebChartSummary.DeepCopy() },
                { "tutorial_summary", TutorialSummary.DeepCopy() },
                { "equipment_summary", EquipmentSummary.DeepCopy() },
                { "home_looted_containers", HomeLootedContainers.ShallowCopy() },
                { "home_ship_inventory", HomeShipInventory.DeepCopy() },
                { "run_context", RunContext.DeepCopy() },
                { "play_time_seconds", PlayTimeSeconds },
                { "current_location", CurrentLocation },
                { "world_seed", WorldSeed },
                { "slot_id", SlotId },
                { "slot_kind", SlotKind },
                { "is_autosave", IsAutosave },
                { "is_quicksave", IsQuicksave },
                { "parent_world_slot", ParentWorldSlot },
                { "run_id", RunId },
                { "slice_version", SliceVersion },
                { "godot_version", GodotVersion },
                { "saved_at", SavedAt },
                { "saved_at_epoch", SavedAtEpoch },
            };
        }

        /// <summary>
        /// Reconstructs a RunSnapshot from a parsed JSON dictionary. Returns null when the data is missing, not a
        /// dictionary, empty, or either version marker does not match (ADR-0007: incompatible saves are rejected).
        /// </summary>
        public static RunSnapshot FromDict(object data, string expectedSliceVersion, string expectedGodotVersion)
        {
            if (!(data is GdDict dict)) return null;
            if (dict.IsEmpty) return null;
            if (V.Str(dict.Get("slice_version", "")) != expectedSliceVersion) return null;
            if (V.Str(dict.Get("godot_version", "")) != expectedGodotVersion) return null;
            var snapshot = new RunSnapshot();
            snapshot.LayoutPath = V.Str(dict.Get("layout_path", ""));
            snapshot.KitPath = V.Str(dict.Get("kit_path", ""));
            snapshot.GameplaySlicePath = V.Str(dict.Get("gameplay_slice_path", ""));
            object pos = dict.Get("player_position", GdArray.Of(0.0, 0.0, 0.0));
            if (pos is GdArray posArray && posArray.Count >= 3)
                snapshot.PlayerPosition = GdArray.Of(V.F64(posArray[0]), V.F64(posArray[1]), V.F64(posArray[2]));
            snapshot.CurrentObjectiveSequence = V.I64(dict.Get("current_objective_sequence", 1L));
            snapshot.ShipSystemsSummary = DeepCopyDict(dict.Get("ship_systems_summary", new GdDict()));
            snapshot.RouteControlSummary = DeepCopyDict(dict.Get("route_control_summary", new GdDict()));
            snapshot.OxygenSummary = DeepCopyDict(dict.Get("oxygen_summary", new GdDict()));
            snapshot.InventorySummary = DeepCopyDict(dict.Get("inventory_summary", new GdDict()));
            snapshot.FireSummary = DeepCopyDict(dict.Get("fire_summary", new GdDict()));
            snapshot.ElectricalArcSummary = DeepCopyDict(dict.Get("electrical_arc_summary", new GdDict()));
            snapshot.ObjectiveProgressSummary = DeepCopyDict(dict.Get("objective_progress_summary", new GdDict()));
            snapshot.PlayerProgressionSummary = DeepCopyDict(dict.Get("player_progression_summary", new GdDict()));
            snapshot.SkillTreeSummary = DeepCopyDict(dict.Get("skill_tree_summary", new GdDict()));
            snapshot.SettingsSummary = DeepCopyDict(dict.Get("settings_summary", new GdDict()));
            snapshot.AudioSummary = DeepCopyDict(dict.Get("audio_summary", new GdDict()));
            snapshot.SpoilageSummary = DeepCopyDict(dict.Get("spoilage_summary", new GdDict()));
            snapshot.HydroponicsSummary = DeepCopyDict(dict.Get("hydroponics_summary", new GdDict()));
            snapshot.WaterRecyclerSummary = DeepCopyDict(dict.Get("water_recycler_summary", new GdDict()));
            snapshot.CraftingSummary = DeepCopyDict(dict.Get("crafting_summary", new GdDict()));
            snapshot.MaterialSummary = DeepCopyDict(dict.Get("material_summary", new GdDict()));
            snapshot.ConsumableSummary = DeepCopyDict(dict.Get("consumable_summary", new GdDict()));
            snapshot.MedicineSummary = DeepCopyDict(dict.Get("medicine_summary", new GdDict()));
            snapshot.StimulantSummary = DeepCopyDict(dict.Get("stimulant_summary", new GdDict()));
            snapshot.AddictionSummary = DeepCopyDict(dict.Get("addiction_summary", new GdDict()));
            snapshot.AmmoSummary = DeepCopyDict(dict.Get("ammo_summary", new GdDict()));
            snapshot.UtilitySummary = DeepCopyDict(dict.Get("utility_summary", new GdDict()));
            snapshot.VitalsSummary = DeepCopyDict(dict.Get("vitals_summary", new GdDict()));
            snapshot.SanitySummary = DeepCopyDict(dict.Get("sanity_summary", new GdDict()));
            snapshot.RadiationSummary = DeepCopyDict(dict.Get("radiation_summary", new GdDict()));
            snapshot.TemperatureSummary = DeepCopyDict(dict.Get("temperature_summary", new GdDict()));
            snapshot.StatusEffectsSummary = DeepCopyDict(dict.Get("status_effects_summary", new GdDict()));
            snapshot.HallucinationSummary = DeepCopyDict(dict.Get("hallucination_summary", new GdDict()));
            snapshot.ModuleIntegritySummary = DeepCopyDict(dict.Get("module_integrity_summary", new GdDict()));
            snapshot.ComponentPlacementSummary = DeepCopyDict(dict.Get("component_placement_summary", new GdDict()));
            snapshot.WorkActionSummary = DeepCopyDict(dict.Get("work_action_summary", new GdDict()));
            snapshot.ShipModificationSummary = DeepCopyDict(dict.Get("ship_modification_summary", new GdDict()));
            snapshot.WoundSummary = DeepCopyDict(dict.Get("wound_summary", new GdDict()));
            snapshot.WebChartSummary = DeepCopyDict(dict.Get("web_chart_summary", new GdDict()));
            snapshot.TutorialSummary = DeepCopyDict(dict.Get("tutorial_summary", new GdDict()));
            snapshot.EquipmentSummary = DeepCopyDict(dict.Get("equipment_summary", new GdDict()));
            snapshot.HomeLootedContainers = new GdArray();
            if (dict.Get("home_looted_containers", new GdArray()) is GdArray looted)
            {
                foreach (object cid in looted)
                    snapshot.HomeLootedContainers.Add(V.Str(cid));
            }
            snapshot.HomeShipInventory = DeepCopyDict(dict.Get("home_ship_inventory", new GdDict()));
            snapshot.RunContext = DeepCopyDict(dict.Get("run_context", new GdDict()));
            snapshot.PlayTimeSeconds = V.F64(dict.Get("play_time_seconds", 0.0));
            snapshot.CurrentLocation = V.Str(dict.Get("current_location", ""));
            snapshot.WorldSeed = V.I64(dict.Get("world_seed", 0L));
            snapshot.SlotId = V.Str(dict.Get("slot_id", ""));
            snapshot.SlotKind = V.Str(dict.Get("slot_kind", ""));
            snapshot.IsAutosave = V.Bool(dict.Get("is_autosave", false));
            snapshot.IsQuicksave = V.Bool(dict.Get("is_quicksave", false));
            snapshot.ParentWorldSlot = V.Str(dict.Get("parent_world_slot", ""));
            snapshot.RunId = V.Str(dict.Get("run_id", ""));
            snapshot.SliceVersion = V.Str(dict.Get("slice_version", ""));
            snapshot.GodotVersion = V.Str(dict.Get("godot_version", ""));
            snapshot.SavedAt = V.Str(dict.Get("saved_at", ""));
            snapshot.SavedAtEpoch = V.I64(dict.Get("saved_at_epoch", 0L));
            return snapshot;
        }

        static GdDict DeepCopyDict(object src)
        {
            if (!(src is GdDict d)) return new GdDict();
            return d.DeepCopy();
        }
    }
}
