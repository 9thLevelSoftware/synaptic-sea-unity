using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Phase 1.1b: survival pacing in game time, from <c>res://data/balance/survival.json</c>. Hunger and thirst are
    /// expressed per game hour; spoilage and production take a multiplier on their authored (game-second) durations.
    /// At real-time pacing (clock scale 1.0, the "off" choice and every pre-1.1b save) none of this applies: rates stay
    /// the per-real-second legacy values, so that mode reproduces the old numbers exactly.
    /// </summary>
    public sealed class SurvivalTuning
    {
        public const string Path = "res://data/balance/survival.json";
        public const double DefaultHungerPerGameHour = 100.0 / 24.0;
        public const double DefaultThirstPerGameHour = 100.0 / 12.0;
        public const double DefaultSpoilageMultiplier = 72.0;
        public const double DefaultProductionMultiplier = 120.0;

        public double HungerPerGameHour = DefaultHungerPerGameHour;
        public double ThirstPerGameHour = DefaultThirstPerGameHour;
        /// <summary>Authored spoilage seconds are stretched by this factor at scaled pacing (a ration authored at 3600 lasts 72 game hours).</summary>
        public double SpoilageMultiplier = DefaultSpoilageMultiplier;
        /// <summary>Authored hydroponics and recycler durations are stretched by this factor at scaled pacing.</summary>
        public double ProductionMultiplier = DefaultProductionMultiplier;

        /// <summary>Reads the <c>survival</c> object; missing or invalid values keep the defaults.</summary>
        public static SurvivalTuning FromDict(GdDict root)
        {
            var tuning = new SurvivalTuning();
            GdDict d = root == null ? new GdDict() : root.GetDictOrEmpty("survival");
            tuning.HungerPerGameHour = Positive(d, "hunger_per_game_hour", DefaultHungerPerGameHour);
            tuning.ThirstPerGameHour = Positive(d, "thirst_per_game_hour", DefaultThirstPerGameHour);
            tuning.SpoilageMultiplier = Positive(d, "spoilage_time_multiplier", DefaultSpoilageMultiplier);
            tuning.ProductionMultiplier = Positive(d, "production_time_multiplier", DefaultProductionMultiplier);
            return tuning;
        }

        static double Positive(GdDict d, string key, double fallback)
        {
            if (!d.Has(key)) return fallback;
            double v = V.F64(d.Get(key, fallback));
            return v > 0.0 && !double.IsNaN(v) && !double.IsInfinity(v) ? v : fallback;
        }

        /// <summary>Sets the vitals drain rates for the clock: per-game-second at scaled pacing, the legacy per-second values at real time.</summary>
        public void ApplyTo(VitalsState vitals, WorldClock clock)
        {
            if (vitals == null) return;
            if (clock == null || clock.IsRealTime)
            {
                vitals.HungerDrainRate = VitalsState.DEFAULT_HUNGER_DRAIN;
                vitals.ThirstDrainRate = VitalsState.DEFAULT_THIRST_DRAIN;
                return;
            }
            vitals.HungerDrainRate = HungerPerGameHour / WorldClock.SecondsPerHour;
            vitals.ThirstDrainRate = ThirstPerGameHour / WorldClock.SecondsPerHour;
        }

        /// <summary>Divisor for the game-time delta handed to spoilage (1.0 at real time).</summary>
        public double SpoilageDivisor(WorldClock clock) => clock == null || clock.IsRealTime ? 1.0 : SpoilageMultiplier;

        /// <summary>Divisor for the game-time delta handed to hydroponics and the water recycler (1.0 at real time).</summary>
        public double ProductionDivisor(WorldClock clock) => clock == null || clock.IsRealTime ? 1.0 : ProductionMultiplier;
    }
}
