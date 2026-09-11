// Ported from scripts/systems/encumbrance.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure-static Heavy Load curve. Maps an inventory load-ratio (total_weight /
    /// capacity) to a movement-speed multiplier, modeled on Project Zomboid's Heavy
    /// Load tiers: no penalty at/under capacity; ~37% slower at 125%; ~75% slower at
    /// 175%; clamped to a 0.25 floor beyond. Health drain while overloaded is mild
    /// and ramps with the same tier breakpoints.
    /// </summary>
    public static class Encumbrance
    {
        public const double FLOOR_MULTIPLIER = 0.25;
        public const double MULT_AT_125 = 0.63;
        /// <summary>Health drain (HP/s) at the PZ tier breakpoints.</summary>
        public const double HEALTH_DRAIN_AT_125 = 0.5;
        public const double HEALTH_DRAIN_AT_175 = 2.0;

        public static double MoveSpeedMultiplier(double loadRatio)
        {
            if (loadRatio <= 1.0) return 1.0;
            if (loadRatio <= 1.25) return GdMath.Lerpf(1.0, MULT_AT_125, (loadRatio - 1.0) / 0.25);
            if (loadRatio <= 1.75) return GdMath.Lerpf(MULT_AT_125, FLOOR_MULTIPLIER, (loadRatio - 1.25) / 0.50);
            return FLOOR_MULTIPLIER;
        }

        /// <summary>Health drain per second while overloaded. 0 at/under capacity.</summary>
        public static double HealthDrainPerSecond(double loadRatio)
        {
            if (loadRatio <= 1.0) return 0.0;
            if (loadRatio <= 1.25) return GdMath.Lerpf(0.0, HEALTH_DRAIN_AT_125, (loadRatio - 1.0) / 0.25);
            if (loadRatio <= 1.75) return GdMath.Lerpf(HEALTH_DRAIN_AT_125, HEALTH_DRAIN_AT_175, (loadRatio - 1.25) / 0.50);
            return HEALTH_DRAIN_AT_175;
        }

        /// <summary>
        /// Capacity-share, best-first weight reduction. <paramref name="containerReductions"/> is an Array
        /// of { "capacity": float, "reduction": float }. Sorts best-first (highest reduction), lets each
        /// container cover up to its capacity of the remaining weight at its reduction rate, and returns
        /// the total kg saved (>= 0). Never exceeds total_weight.
        /// </summary>
        public static double WeightReductionSaved(double totalWeight, GdArray containerReductions)
        {
            GdArray sorted = containerReductions != null ? containerReductions.ShallowCopy() : new GdArray();
            sorted.SortCustom((a, b) => V.F64(((GdDict)a)["reduction"]) > V.F64(((GdDict)b)["reduction"]));
            double remaining = Math.Max(0.0, totalWeight);
            double saved = 0.0;
            foreach (object cV in sorted)
            {
                if (remaining <= 0.0) break;
                var c = (GdDict)cV;
                double covered = Math.Min(remaining, Math.Max(0.0, V.F64(c["capacity"])));
                saved += covered * GdMath.Clampf(V.F64(c["reduction"]), 0.0, 1.0);
                remaining -= covered;
            }
            return saved;
        }
    }
}
