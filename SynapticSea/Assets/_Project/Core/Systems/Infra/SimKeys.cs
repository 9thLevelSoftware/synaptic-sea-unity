// Ported from scripts/systems/sim_keys.gd @ 96ecb2b0
using System.Collections.Generic;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Canonical string keys for tick / pipeline context dictionaries (PKG-A2).
    /// Values are the historical wire strings; do not rename without a migration plan.
    /// </summary>
    public static class SimKeys
    {
        // --- Vitals hot path (VitalsState.tick) ---
        public const string Moving = "moving";
        public const string RadiationHealthDrain = "radiation_health_drain";
        public const string AtmosphereHealthDrain = "atmosphere_health_drain";
        public const string FireHealthDrain = "fire_health_drain";
        public const string SanityHealthDrain = "sanity_health_drain";
        public const string EncumbranceHealthDrain = "encumbrance_health_drain";
        public const string TemperatureThirstMult = "temperature_thirst_mult";
        public const string TemperatureHungerMult = "temperature_hunger_mult"; // PKG-C3.1b cold->hunger
        public const string WoundThirstMult = "wound_thirst_mult";             // PKG-C3.1b wounds->thirst
        public const string WoundHealthDrain = "wound_health_drain";           // PKG-C3.1b bleed->health
        public const string StatusStaminaRecoveryMult = "status_stamina_recovery_mult";
        public const string SanityStaminaRecoveryMult = "sanity_stamina_recovery_mult";

        // --- Threat perception / AI ---
        public const string NoiseLevel = "noise_level";
        public const string LightLevel = "light_level";
        public const string SightLevel = "sight_level";
        public const string Crouching = "crouching";
        public const string SameRoom = "same_room";
        public const string DetectThreshold = "detect_threshold";
        public const string RoomId = "room_id";
        public const string PlayerPosition = "player_position";

        // --- Ship systems tick contexts ---
        public const string PoweredRatio = "powered_ratio";
        public const string BreachCount = "breach_count";
        public const string RecycledWater = "recycled_water";
        public const string ManagerOperational = "manager_operational";
        public const string HullPenalty = "hull_penalty";
        public const string BreachedCompartments = "breached_compartments";
        public const string DamagedCompartments = "damaged_compartments";
        public const string ShipOxygenPresent = "ship_oxygen_present";
        public const string ArcArcing = "arc_arcing";
        public const string ClosedLinks = "closed_links";

        // --- Sustenance rollup ---
        public const string HydroponicsSummary = "hydroponics_summary";
        public const string WaterRecyclerSummary = "water_recycler_summary";
        public const string MealsActive = "meals_active";

        // --- Hallucination director ---
        public const string Sanity = "sanity";
        public const string InSafeZone = "in_safe_zone";
        public const string AnchorPositions = "anchor_positions";

        // --- Effect / consumable pipeline object handles ---
        public const string VitalsState = "vitals_state";
        public const string SanityState = "sanity_state";
        public const string RadiationState = "radiation_state";
        public const string BodyTemperatureState = "body_temperature_state";
        public const string StatusEffectsState = "status_effects_state";
        public const string EffectDispatcher = "effect_dispatcher";
        public const string MedicineState = "medicine_state";
        public const string StimulantState = "stimulant_state";
        public const string AddictionState = "addiction_state";
        public const string UtilityState = "utility_state";
        public const string SpoilageState = "spoilage_state";

        // --- Oxygen / field atmosphere ---
        public const string PlayerInBreachZone = "player_in_breach_zone";

        // --- Loot distribution ---
        public const string BiomeId = "biome_id";
        public const string Depth = "depth";
        public const string ContainerKind = "container_kind";
        public const string ItemDefinitions = "item_definitions";
        public const string UniqueState = "unique_state";
        public const string Condition = "condition";
        public const string LootQualityModifier = "loot_quality_modifier";

        /// <summary>Stable set of vitals hot-path keys (for smokes / contract checks).</summary>
        public static List<string> VitalsHotPathKeys()
        {
            return new List<string>
            {
                Moving,
                RadiationHealthDrain,
                AtmosphereHealthDrain,
                FireHealthDrain,
                SanityHealthDrain,
                EncumbranceHealthDrain,
                TemperatureThirstMult,
                TemperatureHungerMult,
                WoundThirstMult,
                WoundHealthDrain,
                StatusStaminaRecoveryMult,
                SanityStaminaRecoveryMult,
            };
        }

        /// <summary>All documented keys in this catalog (group order above).</summary>
        public static List<string> AllKeys()
        {
            return new List<string>
            {
                Moving, RadiationHealthDrain, AtmosphereHealthDrain, FireHealthDrain,
                SanityHealthDrain, EncumbranceHealthDrain, TemperatureThirstMult,
                TemperatureHungerMult, WoundThirstMult, WoundHealthDrain,
                StatusStaminaRecoveryMult, SanityStaminaRecoveryMult,
                NoiseLevel, LightLevel, SightLevel, Crouching, SameRoom, DetectThreshold,
                RoomId, PlayerPosition,
                PoweredRatio, BreachCount, RecycledWater, ManagerOperational, HullPenalty,
                BreachedCompartments, DamagedCompartments, ShipOxygenPresent, ArcArcing, ClosedLinks,
                HydroponicsSummary, WaterRecyclerSummary, MealsActive,
                Sanity, InSafeZone, AnchorPositions,
                VitalsState, SanityState, RadiationState, BodyTemperatureState,
                StatusEffectsState, EffectDispatcher, MedicineState, StimulantState,
                AddictionState, UtilityState, SpoilageState,
                PlayerInBreachZone,
                BiomeId, Depth, ContainerKind, ItemDefinitions, UniqueState, Condition,
                LootQualityModifier,
            };
        }
    }
}
