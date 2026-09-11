// Ported from scripts/systems/propulsion_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Propulsion model: thrust follows powered ratio minus hull penalty; engine temperature follows thrust.</summary>
    public class PropulsionState
    {
        public double ThrustPercent = 0.0;
        public double EngineTemperatureC = 28.0;
        public double FuelEfficiency = 1.0;
        public double PowerThreshold = 0.5;
        public bool Operational = true;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            ThrustPercent = GdMath.Clampf(V.F64(config.Get("thrust_percent", 0.0)), 0.0, 100.0);
            EngineTemperatureC = V.F64(config.Get("engine_temperature_c", 28.0));
            FuelEfficiency = Math.Max(0.1, V.F64(config.Get("fuel_efficiency", 1.0)));
            PowerThreshold = GdMath.Clampf(V.F64(config.Get("power_threshold", 0.5)), 0.05, 1.0);
            Operational = V.Bool(config.Get("operational", true));
        }

        public void Tick(double delta, GdDict context)
        {
            context = context ?? new GdDict();
            double poweredRatio = GdMath.Clampf(V.F64(context.Get(SimKeys.PoweredRatio, 0.0)), 0.0, 1.0);
            bool managerOperational = V.Bool(context.Get(SimKeys.ManagerOperational, true));
            double hullPenalty = GdMath.Clampf(V.F64(context.Get(SimKeys.HullPenalty, 0.0)), 0.0, 1.0);
            Operational = managerOperational && poweredRatio >= PowerThreshold && hullPenalty < 0.6;
            double target = 100.0 * Math.Max(0.0, poweredRatio - hullPenalty);
            if (!Operational)
                target = 0.0;
            ThrustPercent = GdMath.Lerpf(ThrustPercent, target, Math.Min(1.0, Math.Max(0.05, delta * 0.5)));
            EngineTemperatureC = GdMath.Lerpf(EngineTemperatureC, 30.0 + ThrustPercent * 0.35, Math.Min(1.0, delta * 0.25));
        }

        public bool CanPropel() => Operational && ThrustPercent >= 50.0;

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "thrust_percent", ThrustPercent },
                { "engine_temperature_c", EngineTemperatureC },
                { "fuel_efficiency", FuelEfficiency },
                { "power_threshold", PowerThreshold },
                { "operational", Operational },
            };
        }

        double GetField(string key)
        {
            switch (key)
            {
                case "thrust_percent": return ThrustPercent;
                case "engine_temperature_c": return EngineTemperatureC;
                case "fuel_efficiency": return FuelEfficiency;
                default: return PowerThreshold; // "power_threshold"
            }
        }

        void SetField(string key, double value)
        {
            switch (key)
            {
                case "thrust_percent": ThrustPercent = value; break;
                case "engine_temperature_c": EngineTemperatureC = value; break;
                case "fuel_efficiency": FuelEfficiency = value; break;
                default: PowerThreshold = value; break; // "power_threshold"
            }
        }

        static readonly string[] FloatKeys = { "thrust_percent", "engine_temperature_c", "fuel_efficiency", "power_threshold" };

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            bool changed = false;
            foreach (string key in FloatKeys)
            {
                double newValue = V.F64(summary.Get(key, GetField(key)));
                if (Math.Abs(newValue - GetField(key)) > 0.001)
                {
                    SetField(key, newValue);
                    changed = true;
                }
            }
            bool newOperational = V.Bool(summary.Get("operational", Operational));
            if (newOperational != Operational)
            {
                Operational = newOperational;
                changed = true;
            }
            return changed;
        }

        public List<string> GetStatusLines()
        {
            return new List<string>
            {
                "Propulsion thrust=" + GdString.FormatInt(GdMath.RoundI(ThrustPercent)) + "% temp=" +
                GdString.FormatFixed(EngineTemperatureC, 1) + "C " + (Operational ? "ONLINE" : "OFFLINE"),
            };
        }
    }
}
