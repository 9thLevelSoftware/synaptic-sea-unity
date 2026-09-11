// Ported from scripts/systems/web_infestation_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Domain 4: the biomatter-web infestation that slowly devours a ship's hull. Coverage grows while attached
    /// to the web and recedes once cut free. A continuous hazard (not PhaseTimer based); hazard_kind only guards
    /// save-load. <c>tick(delta, contact)</c> returns hull damage, so this does not implement <see cref="ITickable"/>.
    /// </summary>
    public sealed class WebInfestationState : ISimModel, IStatusLineProvider
    {
        public const string HAZARD_KIND = "web_infestation";

        public bool AttachedToWeb = true;
        /// <summary>0..1 infestation level.</summary>
        public double Coverage = 0.0;
        /// <summary>Coverage/sec while attached.</summary>
        public double GrowthRate = 0.02;
        /// <summary>Coverage/sec while cut free.</summary>
        public double RecessionRate = 0.05;
        /// <summary>Hull damage/sec at full coverage.</summary>
        public double DamageRate = 0.03;
        /// <summary>Extra growth/sec while docked to an attached derelict.</summary>
        public double ContactBoost = 0.03;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            GrowthRate = Math.Max(0.0, V.F64(config.Get("growth_rate", 0.02)));
            RecessionRate = Math.Max(0.0, V.F64(config.Get("recession_rate", 0.05)));
            DamageRate = Math.Max(0.0, V.F64(config.Get("damage_rate", 0.03)));
            ContactBoost = Math.Max(0.0, V.F64(config.Get("contact_boost", 0.03)));
            Coverage = GdMath.Clampf(V.F64(config.Get("seed_coverage", 0.0)), 0.0, 1.0);
            AttachedToWeb = V.Bool(config.Get("attached_to_web", true));
        }

        /// <summary>Advances coverage by one tick and returns the hull-damage magnitude for this tick.</summary>
        public double Tick(double delta, bool contact)
        {
            if (delta <= 0.0) return 0.0;
            if (AttachedToWeb)
            {
                double rate = GrowthRate + (contact ? ContactBoost : 0.0);
                Coverage = GdMath.Clampf(Coverage + rate * delta, 0.0, 1.0);
            }
            else
            {
                Coverage = GdMath.Clampf(Coverage - RecessionRate * delta, 0.0, 1.0);
            }
            return Coverage * DamageRate * delta;
        }

        public void CutFree()
        {
            AttachedToWeb = false;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "hazard_kind", HAZARD_KIND },
                { "attached_to_web", AttachedToWeb },
                { "coverage", Coverage },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (V.Str(summary.Get("hazard_kind", "")) != HAZARD_KIND) return false;
            AttachedToWeb = V.Bool(summary.Get("attached_to_web", AttachedToWeb));
            Coverage = GdMath.Clampf(V.F64(summary.Get("coverage", Coverage)), 0.0, 1.0);
            return true;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (Coverage > 0.0)
            {
                string tag = AttachedToWeb ? "SPREADING" : "RECEDING";
                lines.Add("Web Infestation " + SurvivalCompat.FormatD(GdMath.RoundI(Coverage * 100.0)) + "% [" + tag + "]");
            }
            return lines;
        }
    }
}
