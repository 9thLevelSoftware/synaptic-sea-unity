// Ported from scripts/systems/sanity_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Pure model for player sanity (REQ-SV-002): drains in the Synaptic Sea field, recovers in safe zones.</summary>
    public sealed class SanityState : ISimModel, ITickable, IStatusLineProvider
    {
        public const double DEFAULT_MAX_SANITY = 100.0;
        public const double DEFAULT_DRAIN_RATE = 1.5;
        public const double DEFAULT_RECOVERY_RATE = 3.0;
        public const double PERCEPTION_PRESSURE_THRESHOLD = 40.0;

        public double MaxSanity = DEFAULT_MAX_SANITY;
        public double DrainRate = DEFAULT_DRAIN_RATE;
        public double RecoveryRate = DEFAULT_RECOVERY_RATE;

        public double Sanity = DEFAULT_MAX_SANITY;
        public bool InSafeZone = false;

        /// <summary>Domain 5: &lt;1.0 when a flare steadies the player.</summary>
        public double SteadyMultiplier = 1.0;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            MaxSanity = F(config, "max_sanity", DEFAULT_MAX_SANITY);
            DrainRate = F(config, "drain_rate", DEFAULT_DRAIN_RATE);
            RecoveryRate = F(config, "recovery_rate", DEFAULT_RECOVERY_RATE);
            Sanity = GdMath.Clampf(F(config, "sanity", Sanity), 0.0, MaxSanity);
            InSafeZone = V.Bool(config.Get("in_safe_zone", false));
            SteadyMultiplier = 1.0;
        }

        public bool Tick(double deltaSeconds, GdDict context = null)
        {
            if (deltaSeconds <= 0.0) return false;
            bool changed = false;
            if (InSafeZone)
            {
                double rec = RecoveryRate * deltaSeconds;
                if (rec > 0.0 && Sanity < MaxSanity)
                {
                    Sanity = Math.Min(MaxSanity, Sanity + rec);
                    changed = true;
                }
            }
            else
            {
                double drn = DrainRate * SteadyMultiplier * deltaSeconds;
                if (drn > 0.0 && Sanity > 0.0)
                {
                    Sanity = Math.Max(0.0, Sanity - drn);
                    changed = true;
                }
            }
            return changed;
        }

        public double AdjustSanity(double amount)
        {
            Sanity = GdMath.Clampf(Sanity + amount, 0.0, MaxSanity);
            return Sanity;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "sanity", Sanity },
                { "max_sanity", MaxSanity },
                { "drain_rate", DrainRate },
                { "recovery_rate", RecoveryRate },
                { "in_safe_zone", InSafeZone },
                { "perception_pressure_active", Sanity < PERCEPTION_PRESSURE_THRESHOLD },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            if (summary.Has("sanity"))
            {
                double newVal = V.F64(summary.Get("sanity", 0.0));
                if (Math.Abs(newVal - Sanity) > 0.001)
                {
                    Sanity = newVal;
                    changed = true;
                }
            }
            if (summary.Has("max_sanity"))
            {
                double newVal = V.F64(summary.Get("max_sanity", 0.0));
                if (Math.Abs(newVal - MaxSanity) > 0.001)
                {
                    MaxSanity = newVal;
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
            if (summary.Has("in_safe_zone"))
            {
                bool newSafe = V.Bool(summary.Get("in_safe_zone", false));
                if (newSafe != InSafeZone)
                {
                    InSafeZone = newSafe;
                    changed = true;
                }
            }
            Sanity = GdMath.Clampf(Sanity, 0.0, MaxSanity);
            return changed;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            long pct = MaxSanity > 0.0 ? GdMath.RoundI((Sanity / MaxSanity) * 100.0) : 0;
            string suffix = "";
            if (Sanity < PERCEPTION_PRESSURE_THRESHOLD) suffix = " CRITICAL";
            lines.Add("Sanity: " + SurvivalCompat.FormatD(pct) + "%" + suffix);
            if (Sanity < PERCEPTION_PRESSURE_THRESHOLD) lines.Add("PERCEPTION PRESSURE -> hallucination risk");
            return lines;
        }

        static double F(GdDict config, string key, double fallback)
        {
            if (config.Has(key)) return Math.Max(0.0, V.F64(config.Get(key, fallback)));
            return fallback;
        }
    }
}
