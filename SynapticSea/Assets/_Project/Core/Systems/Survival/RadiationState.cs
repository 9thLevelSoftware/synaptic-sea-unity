// Ported from scripts/systems/radiation_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Pure model for radiation accumulation and decay (REQ-SV-003).</summary>
    public sealed class RadiationState : ISimModel, ITickable, IStatusLineProvider
    {
        public const double DEFAULT_MAX_RADIATION = 100.0;
        public const double DEFAULT_ACCUMULATION_RATE = 2.0;
        public const double DEFAULT_DECAY_RATE = 0.5;
        public const double HEALTH_DRAIN_THRESHOLD = 50.0;
        public const double DEFAULT_HEALTH_DRAIN_RATE = 1.0;

        public double MaxRadiation = DEFAULT_MAX_RADIATION;
        public double AccumulationRate = DEFAULT_ACCUMULATION_RATE;
        public double DecayRate = DEFAULT_DECAY_RATE;
        public double HealthDrainRate = DEFAULT_HEALTH_DRAIN_RATE;

        public double Radiation = 0.0;
        public bool InRadiationZone = false;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            MaxRadiation = F(config, "max_radiation", DEFAULT_MAX_RADIATION);
            AccumulationRate = F(config, "accumulation_rate", DEFAULT_ACCUMULATION_RATE);
            DecayRate = F(config, "decay_rate", DEFAULT_DECAY_RATE);
            HealthDrainRate = F(config, "health_drain_rate", DEFAULT_HEALTH_DRAIN_RATE);
            Radiation = GdMath.Clampf(F(config, "radiation", Radiation), 0.0, MaxRadiation);
            InRadiationZone = V.Bool(config.Get("in_radiation_zone", false));
        }

        public bool Tick(double deltaSeconds, GdDict context = null)
        {
            if (deltaSeconds <= 0.0) return false;
            bool changed = false;
            if (InRadiationZone)
            {
                double acc = AccumulationRate * deltaSeconds;
                if (acc > 0.0 && Radiation < MaxRadiation)
                {
                    Radiation = Math.Min(MaxRadiation, Radiation + acc);
                    changed = true;
                }
            }
            else
            {
                double dec = DecayRate * deltaSeconds;
                if (dec > 0.0 && Radiation > 0.0)
                {
                    Radiation = Math.Max(0.0, Radiation - dec);
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>Current passive health drain per second caused by radiation.</summary>
        public double GetHealthDrainPerSecond()
        {
            if (Radiation >= HEALTH_DRAIN_THRESHOLD) return HealthDrainRate;
            return 0.0;
        }

        public double AdjustRadiation(double amount)
        {
            Radiation = GdMath.Clampf(Radiation + amount, 0.0, MaxRadiation);
            return Radiation;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "radiation", Radiation },
                { "max_radiation", MaxRadiation },
                { "accumulation_rate", AccumulationRate },
                { "decay_rate", DecayRate },
                { "health_drain_rate", HealthDrainRate },
                { "in_radiation_zone", InRadiationZone },
                { "health_drain_active", Radiation >= HEALTH_DRAIN_THRESHOLD },
                { "health_drain_per_second", GetHealthDrainPerSecond() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            if (summary.Has("radiation"))
            {
                double newVal = V.F64(summary.Get("radiation", 0.0));
                if (Math.Abs(newVal - Radiation) > 0.001)
                {
                    Radiation = newVal;
                    changed = true;
                }
            }
            if (summary.Has("max_radiation"))
            {
                double newVal = V.F64(summary.Get("max_radiation", 0.0));
                if (Math.Abs(newVal - MaxRadiation) > 0.001)
                {
                    MaxRadiation = newVal;
                    changed = true;
                }
            }
            if (summary.Has("accumulation_rate"))
            {
                double newVal = V.F64(summary.Get("accumulation_rate", 0.0));
                if (Math.Abs(newVal - AccumulationRate) > 0.001)
                {
                    AccumulationRate = newVal;
                    changed = true;
                }
            }
            if (summary.Has("decay_rate"))
            {
                double newVal = V.F64(summary.Get("decay_rate", 0.0));
                if (Math.Abs(newVal - DecayRate) > 0.001)
                {
                    DecayRate = newVal;
                    changed = true;
                }
            }
            if (summary.Has("health_drain_rate"))
            {
                double newVal = V.F64(summary.Get("health_drain_rate", 0.0));
                if (Math.Abs(newVal - HealthDrainRate) > 0.001)
                {
                    HealthDrainRate = newVal;
                    changed = true;
                }
            }
            if (summary.Has("in_radiation_zone"))
            {
                bool newZone = V.Bool(summary.Get("in_radiation_zone", false));
                if (newZone != InRadiationZone)
                {
                    InRadiationZone = newZone;
                    changed = true;
                }
            }
            Radiation = GdMath.Clampf(Radiation, 0.0, MaxRadiation);
            return changed;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            long pct = MaxRadiation > 0.0 ? GdMath.RoundI((Radiation / MaxRadiation) * 100.0) : 0;
            string suffix = "";
            if (Radiation >= HEALTH_DRAIN_THRESHOLD) suffix = " CRITICAL";
            lines.Add("Radiation: " + SurvivalCompat.FormatD(pct) + "%" + suffix);
            if (Radiation >= HEALTH_DRAIN_THRESHOLD) lines.Add("RADIATION SICKNESS -> health drain");
            return lines;
        }

        static double F(GdDict config, string key, double fallback)
        {
            if (config.Has(key)) return Math.Max(0.0, V.F64(config.Get(key, fallback)));
            return fallback;
        }
    }
}
