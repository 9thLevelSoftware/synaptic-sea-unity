// Ported from scripts/systems/life_support_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Ship atmosphere model: oxygen, CO2, temperature, and water driven by the powered ratio, breach count, and
    /// recycled water in the tick context. M7-A adds atmosphere "teeth": a health drain and a thirst multiplier.
    /// </summary>
    public sealed class LifeSupportState : ISimModel, IStatusLineProvider
    {
        public double OxygenPercent = 100.0;
        public double Co2Percent = 2.0;
        public double TemperatureC = 21.0;
        public double WaterLiters = 40.0;
        public double NominalTemperatureC = 21.0;
        public double OfflineOxygenDrainPerSecond = 4.0;
        public double OnlineOxygenRecoveryPerSecond = 2.0;
        public double OfflineCo2GainPerSecond = 3.5;
        public double OnlineCo2ScrubPerSecond = 2.0;
        public double OfflineTempDriftPerSecond = 0.3;
        public double WaterUsePerSecond = 0.2;
        public double LifeSupportPowerThreshold = 0.5;

        // M7-A atmosphere-teeth tunables (exposed in the summary so smokes can assert the tuning in use).
        public double AtmosphereSafeOxygen = 50.0;        // O2 % at/above which there is no drain
        public double AtmosphereSafeCo2 = 15.0;           // CO2 % at/below which there is no drain
        public double MaxAtmosphereHealthDrain = 5.0;     // hp/sec when atmosphere is fully fouled
        public double AtmosphereTempComfortBand = 8.0;    // +/- degrees C around nominal with no thirst penalty
        public double MaxAtmosphereThirstMult = 1.5;      // thirst multiplier at temperature extreme
        public double BreachOxygenLeakPerSecond = 1.5;    // per-breach atmosphere loss while powered

        public void Configure(GdDict config)
        {
            if (config == null) config = new GdDict();
            OxygenPercent = GdMath.Clampf(V.F64(config.Get("oxygen_percent", 100.0)), 0.0, 100.0);
            Co2Percent = GdMath.Clampf(V.F64(config.Get("co2_percent", 2.0)), 0.0, 100.0);
            TemperatureC = V.F64(config.Get("temperature_c", 21.0));
            WaterLiters = Math.Max(0.0, V.F64(config.Get("water_liters", 40.0)));
            NominalTemperatureC = V.F64(config.Get("nominal_temperature_c", 21.0));
            OfflineOxygenDrainPerSecond = Math.Max(0.1, V.F64(config.Get("offline_oxygen_drain_per_second", 4.0)));
            OnlineOxygenRecoveryPerSecond = Math.Max(0.1, V.F64(config.Get("online_oxygen_recovery_per_second", 2.0)));
            OfflineCo2GainPerSecond = Math.Max(0.1, V.F64(config.Get("offline_co2_gain_per_second", 3.5)));
            OnlineCo2ScrubPerSecond = Math.Max(0.1, V.F64(config.Get("online_co2_scrub_per_second", 2.0)));
            OfflineTempDriftPerSecond = Math.Max(0.01, V.F64(config.Get("offline_temp_drift_per_second", 0.3)));
            WaterUsePerSecond = Math.Max(0.01, V.F64(config.Get("water_use_per_second", 0.2)));
            LifeSupportPowerThreshold = GdMath.Clampf(V.F64(config.Get("life_support_power_threshold", 0.5)), 0.05, 1.0);
            AtmosphereSafeOxygen = GdMath.Clampf(V.F64(config.Get("atmosphere_safe_oxygen", 50.0)), 1.0, 100.0);
            AtmosphereSafeCo2 = GdMath.Clampf(V.F64(config.Get("atmosphere_safe_co2", 15.0)), 0.0, 99.0);
            MaxAtmosphereHealthDrain = Math.Max(0.0, V.F64(config.Get("max_atmosphere_health_drain", 5.0)));
            AtmosphereTempComfortBand = Math.Max(0.1, V.F64(config.Get("atmosphere_temp_comfort_band", 8.0)));
            MaxAtmosphereThirstMult = Math.Max(1.0, V.F64(config.Get("max_atmosphere_thirst_mult", 1.5)));
            BreachOxygenLeakPerSecond = Math.Max(0.0, V.F64(config.Get("breach_oxygen_leak_per_second", 1.5)));
        }

        public void Tick(double delta, GdDict context)
        {
            if (delta <= 0.0) return;
            if (context == null) context = new GdDict();
            double poweredRatio = GdMath.Clampf(V.F64(context.Get(SimKeys.PoweredRatio, 0.0)), 0.0, 1.0);
            long breachCount = Math.Max(0L, V.I64(context.Get(SimKeys.BreachCount, 0L)));
            double recycledWater = Math.Max(0.0, V.F64(context.Get(SimKeys.RecycledWater, 0.0)));
            bool powered = poweredRatio >= LifeSupportPowerThreshold;
            if (powered)
            {
                OxygenPercent = Math.Min(100.0, OxygenPercent + OnlineOxygenRecoveryPerSecond * poweredRatio * delta);
                Co2Percent = Math.Max(0.0, Co2Percent - OnlineCo2ScrubPerSecond * poweredRatio * delta);
                TemperatureC = GdMath.Lerpf(TemperatureC, NominalTemperatureC, Math.Min(1.0, 0.15 * delta));
                // M7-A: unsealed breaches leak atmosphere even while powered, so the player must SEAL them.
                if (breachCount > 0)
                {
                    double leak = BreachOxygenLeakPerSecond * breachCount * delta;
                    OxygenPercent = Math.Max(0.0, OxygenPercent - leak);
                    Co2Percent = Math.Min(100.0, Co2Percent + leak);
                }
            }
            else
            {
                double breachMult = 1.0 + breachCount * 0.35;
                OxygenPercent = Math.Max(0.0, OxygenPercent - OfflineOxygenDrainPerSecond * breachMult * delta);
                Co2Percent = Math.Min(100.0, Co2Percent + OfflineCo2GainPerSecond * breachMult * delta);
                TemperatureC += OfflineTempDriftPerSecond * delta * (breachCount > 0 ? 1.0 : -0.5);
            }
            WaterLiters = Math.Max(0.0, WaterLiters - WaterUsePerSecond * delta + recycledWater);
        }

        public bool IsNominal() => OxygenPercent >= 70.0 && Co2Percent <= 10.0 && WaterLiters > 5.0;

        /// <summary>
        /// M7-A: per-second health drain from a failing atmosphere. The worse of the O2-deficit and CO2-excess
        /// severities governs (max, not sum), scaled to max_atmosphere_health_drain.
        /// </summary>
        public double GetHealthDrainPerSecond()
        {
            double o2Deficit = GdMath.Clampf((AtmosphereSafeOxygen - OxygenPercent) / AtmosphereSafeOxygen, 0.0, 1.0);
            double co2Excess = GdMath.Clampf((Co2Percent - AtmosphereSafeCo2) / (100.0 - AtmosphereSafeCo2), 0.0, 1.0);
            return Math.Max(o2Deficit, co2Excess) * MaxAtmosphereHealthDrain;
        }

        /// <summary>M7-A: thirst multiplier from ambient temperature (1.0 inside the comfort band).</summary>
        public double GetThirstMultiplier()
        {
            double deviation = Math.Abs(TemperatureC - NominalTemperatureC);
            if (deviation <= AtmosphereTempComfortBand) return 1.0;
            double over = GdMath.Clampf((deviation - AtmosphereTempComfortBand) / AtmosphereTempComfortBand, 0.0, 1.0);
            return 1.0 + over * (MaxAtmosphereThirstMult - 1.0);
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "oxygen_percent", OxygenPercent },
                { "co2_percent", Co2Percent },
                { "temperature_c", TemperatureC },
                { "water_liters", WaterLiters },
                { "nominal_temperature_c", NominalTemperatureC },
                { "offline_oxygen_drain_per_second", OfflineOxygenDrainPerSecond },
                { "online_oxygen_recovery_per_second", OnlineOxygenRecoveryPerSecond },
                { "offline_co2_gain_per_second", OfflineCo2GainPerSecond },
                { "online_co2_scrub_per_second", OnlineCo2ScrubPerSecond },
                { "offline_temp_drift_per_second", OfflineTempDriftPerSecond },
                { "water_use_per_second", WaterUsePerSecond },
                { "life_support_power_threshold", LifeSupportPowerThreshold },
                { "atmosphere_safe_oxygen", AtmosphereSafeOxygen },
                { "atmosphere_safe_co2", AtmosphereSafeCo2 },
                { "max_atmosphere_health_drain", MaxAtmosphereHealthDrain },
                { "atmosphere_temp_comfort_band", AtmosphereTempComfortBand },
                { "max_atmosphere_thirst_mult", MaxAtmosphereThirstMult },
                { "breach_oxygen_leak_per_second", BreachOxygenLeakPerSecond },
            };
        }

        static readonly string[] SummaryKeys =
        {
            "oxygen_percent", "co2_percent", "temperature_c", "water_liters",
            "nominal_temperature_c", "offline_oxygen_drain_per_second",
            "online_oxygen_recovery_per_second", "offline_co2_gain_per_second",
            "online_co2_scrub_per_second", "offline_temp_drift_per_second",
            "water_use_per_second", "life_support_power_threshold",
            "atmosphere_safe_oxygen", "atmosphere_safe_co2", "max_atmosphere_health_drain",
            "atmosphere_temp_comfort_band", "max_atmosphere_thirst_mult",
            "breach_oxygen_leak_per_second",
        };

        /// <summary>Unclamped restore of every float field (the GDScript uses <c>set(key, value)</c> directly).</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            foreach (string key in SummaryKeys)
            {
                double current = GetField(key);
                double newValue = V.F64(summary.Get(key, current));
                if (Math.Abs(newValue - current) > 0.001)
                {
                    SetField(key, newValue);
                    changed = true;
                }
            }
            return changed;
        }

        double GetField(string key)
        {
            switch (key)
            {
                case "oxygen_percent": return OxygenPercent;
                case "co2_percent": return Co2Percent;
                case "temperature_c": return TemperatureC;
                case "water_liters": return WaterLiters;
                case "nominal_temperature_c": return NominalTemperatureC;
                case "offline_oxygen_drain_per_second": return OfflineOxygenDrainPerSecond;
                case "online_oxygen_recovery_per_second": return OnlineOxygenRecoveryPerSecond;
                case "offline_co2_gain_per_second": return OfflineCo2GainPerSecond;
                case "online_co2_scrub_per_second": return OnlineCo2ScrubPerSecond;
                case "offline_temp_drift_per_second": return OfflineTempDriftPerSecond;
                case "water_use_per_second": return WaterUsePerSecond;
                case "life_support_power_threshold": return LifeSupportPowerThreshold;
                case "atmosphere_safe_oxygen": return AtmosphereSafeOxygen;
                case "atmosphere_safe_co2": return AtmosphereSafeCo2;
                case "max_atmosphere_health_drain": return MaxAtmosphereHealthDrain;
                case "atmosphere_temp_comfort_band": return AtmosphereTempComfortBand;
                case "max_atmosphere_thirst_mult": return MaxAtmosphereThirstMult;
                case "breach_oxygen_leak_per_second": return BreachOxygenLeakPerSecond;
                default: throw new ArgumentException(key);
            }
        }

        void SetField(string key, double value)
        {
            switch (key)
            {
                case "oxygen_percent": OxygenPercent = value; break;
                case "co2_percent": Co2Percent = value; break;
                case "temperature_c": TemperatureC = value; break;
                case "water_liters": WaterLiters = value; break;
                case "nominal_temperature_c": NominalTemperatureC = value; break;
                case "offline_oxygen_drain_per_second": OfflineOxygenDrainPerSecond = value; break;
                case "online_oxygen_recovery_per_second": OnlineOxygenRecoveryPerSecond = value; break;
                case "offline_co2_gain_per_second": OfflineCo2GainPerSecond = value; break;
                case "online_co2_scrub_per_second": OnlineCo2ScrubPerSecond = value; break;
                case "offline_temp_drift_per_second": OfflineTempDriftPerSecond = value; break;
                case "water_use_per_second": WaterUsePerSecond = value; break;
                case "life_support_power_threshold": LifeSupportPowerThreshold = value; break;
                case "atmosphere_safe_oxygen": AtmosphereSafeOxygen = value; break;
                case "atmosphere_safe_co2": AtmosphereSafeCo2 = value; break;
                case "max_atmosphere_health_drain": MaxAtmosphereHealthDrain = value; break;
                case "atmosphere_temp_comfort_band": AtmosphereTempComfortBand = value; break;
                case "max_atmosphere_thirst_mult": MaxAtmosphereThirstMult = value; break;
                case "breach_oxygen_leak_per_second": BreachOxygenLeakPerSecond = value; break;
                default: throw new ArgumentException(key);
            }
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            return new List<string>
            {
                "Life Support O2=" + GdString.FormatInt(GdMath.RoundI(OxygenPercent)) + "% CO2=" + GdString.FormatInt(GdMath.RoundI(Co2Percent)) + "%",
                "Life Support Temp=" + GdString.FormatFixed(TemperatureC, 1) + "C Water=" + GdString.FormatFixed(WaterLiters, 1) + "L",
            };
        }
    }
}
