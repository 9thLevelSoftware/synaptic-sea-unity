// Ported from scripts/systems/life_support_system.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The one ship system with a model-level time effect: it owns an <see cref="OxygenState"/> and drains it
    /// while Life Support is not operational, by mapping "life support offline" onto the oxygen model's
    /// "player_in_breach_zone" gate.
    /// </summary>
    public class LifeSupportSystem : ShipSystem
    {
        public OxygenState OxygenState;

        public LifeSupportSystem(string pSystemId = "life_support", IEnumerable<string> pDependencyIds = null)
            : base(pSystemId, pDependencyIds)
        {
            OxygenState = new OxygenState();
            // Configure with a single zone so breach_open == true and the model is in its drainable state; we
            // never seal it. Drain/regen is then gated purely by the operational flag passed into Advance().
            OxygenState.Configure(new GdDict { { "zone_ids", GdArray.Of("life_support") } });
        }

        public OxygenState GetOxygenState() => OxygenState;

        /// <summary>Offline -> oxygen drains (player_in_breach_zone == true). Operational -> oxygen regenerates.</summary>
        public override void Advance(double delta, bool operational)
        {
            OxygenState.Tick(delta, new GdDict { { "player_in_breach_zone", !operational } });
        }

        public override GdDict GetSummary()
        {
            GdDict baseSummary = base.GetSummary();
            baseSummary["oxygen"] = OxygenState.GetSummary();
            return baseSummary;
        }

        public override bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            bool changed = base.ApplySummary(summary);
            object oxyVariant = summary.Get("oxygen", null);
            if (oxyVariant is GdDict oxy)
            {
                if (OxygenState.ApplySummary(oxy))
                    changed = true;
            }
            return changed;
        }
    }
}
