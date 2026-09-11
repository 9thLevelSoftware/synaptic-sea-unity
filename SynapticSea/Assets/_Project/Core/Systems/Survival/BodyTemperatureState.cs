// Ported from scripts/systems/body_temperature_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Pure model for body temperature. Per REQ-SV-004.</summary>
    public sealed class BodyTemperatureState : ISimModel, ITickable, IStatusLineProvider, EffectDispatcher.IBodyTemperatureTarget
    {
        public const double DEFAULT_TEMPERATURE = 22.0;
        public const double DEFAULT_SAFE_MIN = 18.0;
        public const double DEFAULT_SAFE_MAX = 32.0;
        public const double DEFAULT_DRAIN_RATE = 0.5;
        public const double DEFAULT_RECOVERY_RATE = 1.0;

        public double Temperature = DEFAULT_TEMPERATURE;
        public double SafeMin = DEFAULT_SAFE_MIN;
        public double SafeMax = DEFAULT_SAFE_MAX;
        public double DrainRate = DEFAULT_DRAIN_RATE;
        public double RecoveryRate = DEFAULT_RECOVERY_RATE;
        public bool InExtremeZone = false;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            Temperature = F(config, "temperature", DEFAULT_TEMPERATURE);
            SafeMin = F(config, "safe_min", DEFAULT_SAFE_MIN);
            SafeMax = F(config, "safe_max", DEFAULT_SAFE_MAX);
            DrainRate = F(config, "drain_rate", DEFAULT_DRAIN_RATE);
            RecoveryRate = F(config, "recovery_rate", DEFAULT_RECOVERY_RATE);
            InExtremeZone = V.Bool(config.Get("in_extreme_zone", false));
        }

        public bool Tick(double deltaSeconds, GdDict context = null)
        {
            if (deltaSeconds <= 0.0)
                return false;
            context = context ?? new GdDict();
            bool changed = false;
            if (context.Has("ambient_temperature_c"))
            {
                double ambient = V.F64(context.Get("ambient_temperature_c", DEFAULT_TEMPERATURE));
                double ambientDiff = ambient - Temperature;
                if (Math.Abs(ambientDiff) > 0.01)
                {
                    double ambientRate = ambient < SafeMin || ambient > SafeMax ? DrainRate : RecoveryRate;
                    double ambientStep = Math.Min(Math.Abs(ambientDiff), ambientRate * deltaSeconds);
                    Temperature += GdMath.Signf(ambientDiff) * ambientStep;
                    changed = ambientStep > 0.0;
                }
                return changed;
            }
            if (InExtremeZone)
            {
                double drn = DrainRate * deltaSeconds;
                if (drn > 0.0)
                {
                    // Move away from safe center (arbitrary: heat up)
                    Temperature = Temperature + drn;
                    changed = true;
                }
            }
            else
            {
                // Recover toward default temperature
                double target = DEFAULT_TEMPERATURE;
                double diff = target - Temperature;
                if (Math.Abs(diff) > 0.01)
                {
                    double rec = RecoveryRate * deltaSeconds;
                    if (rec > Math.Abs(diff))
                        rec = Math.Abs(diff);
                    Temperature += GdMath.Signf(diff) * rec;
                    changed = true;
                }
            }
            return changed;
        }

        public bool IsSafe() => Temperature >= SafeMin && Temperature <= SafeMax;

        /// <summary>
        /// Returns thirst-drain multiplier when temperature is outside safe range.
        /// PKG-C3.1b: continuous curve (not a 1.0/1.5 cliff).
        /// </summary>
        public double GetThirstMultiplier()
        {
            if (IsSafe())
                return 1.0;
            double over = 0.0;
            if (Temperature < SafeMin)
                over = GdMath.Clampf((SafeMin - Temperature) / 10.0, 0.0, 1.0);
            else if (Temperature > SafeMax)
                over = GdMath.Clampf((Temperature - SafeMax) / 10.0, 0.0, 1.0);
            // smooth 1.0 → 1.8
            double t = over * over * (3.0 - 2.0 * over);
            return 1.0 + 0.8 * t;
        }

        /// <summary>PKG-C3.1b: cold raises hunger drain; heat does not.</summary>
        public double GetHungerMultiplier() => VitalsState.ColdHungerCurve(Temperature, SafeMin);

        public double AdjustTemperature(double amount)
        {
            Temperature += amount;
            return Temperature;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "temperature", Temperature },
                { "safe_min", SafeMin },
                { "safe_max", SafeMax },
                { "drain_rate", DrainRate },
                { "recovery_rate", RecoveryRate },
                { "in_extreme_zone", InExtremeZone },
                { "is_safe", IsSafe() },
                { "thirst_multiplier", GetThirstMultiplier() },
                { "hunger_multiplier", GetHungerMultiplier() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            bool changed = false;
            if (summary.Has("temperature"))
            {
                double newVal = V.F64(summary.Get("temperature", 0.0));
                if (Math.Abs(newVal - Temperature) > 0.001)
                {
                    Temperature = newVal;
                    changed = true;
                }
            }
            if (summary.Has("safe_min"))
            {
                double newVal = V.F64(summary.Get("safe_min", 0.0));
                if (Math.Abs(newVal - SafeMin) > 0.001)
                {
                    SafeMin = newVal;
                    changed = true;
                }
            }
            if (summary.Has("safe_max"))
            {
                double newVal = V.F64(summary.Get("safe_max", 0.0));
                if (Math.Abs(newVal - SafeMax) > 0.001)
                {
                    SafeMax = newVal;
                    changed = true;
                }
            }
            if (summary.Has("drain_rate"))
            {
                double newVal = V.F64(summary.Get("drain_rate", 0.0));
                if (Math.Abs(newVal - DrainRate) > 0.001)
                {
                    DrainRate = newVal;
                    changed = true;
                }
            }
            if (summary.Has("recovery_rate"))
            {
                double newVal = V.F64(summary.Get("recovery_rate", 0.0));
                if (Math.Abs(newVal - RecoveryRate) > 0.001)
                {
                    RecoveryRate = newVal;
                    changed = true;
                }
            }
            if (summary.Has("in_extreme_zone"))
            {
                bool newZone = V.Bool(summary.Get("in_extreme_zone", false));
                if (newZone != InExtremeZone)
                {
                    InExtremeZone = newZone;
                    changed = true;
                }
            }
            return changed;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            string suffix = "";
            if (!IsSafe())
                suffix = " DANGER";
            lines.Add("Temp: " + GdString.FormatFixed(Temperature, 1) + "C" + suffix);
            if (!IsSafe())
                lines.Add("EXTREME TEMP -> thirst drain increased");
            return lines;
        }

        static double F(GdDict config, string key, double fallback)
        {
            if (config.Has(key))
                return V.F64(config.Get(key, fallback));
            return fallback;
        }
    }
}
