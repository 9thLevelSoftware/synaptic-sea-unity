// Ported from scripts/systems/ship_system.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// One ship system: a set of subcomponents plus the ids of other systems it depends on. Pure data model.
    /// Operational status is NOT stored here: the manager computes it (it owns the dependency graph).
    /// This class answers the health half of that decision via <see cref="IsSelfFunctional"/>.
    /// </summary>
    public class ShipSystem
    {
        public string SystemId = "";
        public List<ShipSubcomponent> Subcomponents = new List<ShipSubcomponent>();
        public List<string> DependencyIds = new List<string>();

        public ShipSystem(string pSystemId = "", IEnumerable<string> pDependencyIds = null)
        {
            SystemId = pSystemId;
            DependencyIds = pDependencyIds != null ? new List<string>(pDependencyIds) : new List<string>();
            Subcomponents = new List<ShipSubcomponent>();
        }

        public void AddSubcomponent(ShipSubcomponent sub) => Subcomponents.Add(sub);

        public ShipSubcomponent GetSubcomponent(string subId)
        {
            foreach (var sub in Subcomponents)
                if (sub.SubcomponentId == subId)
                    return sub;
            return null;
        }

        /// <summary>
        /// Health of the weakest subcomponent (a system is only as good as its worst part).
        /// Returns 1.0 when there are no subcomponents.
        /// </summary>
        public double Health()
        {
            if (Subcomponents.Count == 0)
                return 1.0;
            double lowest = 1.0;
            foreach (var sub in Subcomponents)
                lowest = Math.Min(lowest, sub.Health);
            return lowest;
        }

        public bool IsSelfFunctional()
        {
            foreach (var sub in Subcomponents)
                if (!sub.IsFunctional())
                    return false;
            return true;
        }

        /// <summary>
        /// Base systems have no model-level time effect. Subclasses (LifeSupportSystem) override this.
        /// <paramref name="operational"/> is the manager-resolved status for this system.
        /// </summary>
        public virtual void Advance(double delta, bool operational)
        {
        }

        public virtual GdDict GetSummary()
        {
            var subs = new GdArray();
            foreach (var sub in Subcomponents)
                subs.Add(sub.GetSummary());
            return new GdDict
            {
                { "system_id", SystemId },
                { "dependency_ids", ShipCompat.ToGdArray(DependencyIds) },
                { "subcomponents", subs },
            };
        }

        public virtual bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            bool changed = false;
            object subsVariant = summary.Get("subcomponents", new GdArray());
            if (subsVariant is GdArray subs)
            {
                foreach (object subSummary in subs)
                {
                    if (!(subSummary is GdDict subDict))
                        continue;
                    var sub = GetSubcomponent(V.Str(subDict.Get("subcomponent_id", "")));
                    if (sub != null && sub.ApplySummary(subDict))
                        changed = true;
                }
            }
            return changed;
        }
    }
}
