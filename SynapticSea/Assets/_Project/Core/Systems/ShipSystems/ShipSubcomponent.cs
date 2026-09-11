// Ported from scripts/systems/ship_subcomponent.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// One repairable part of a ship system. Pure data model: never touches the scene tree.
    /// health is 0.0 (destroyed) .. 1.0 (perfect); at/above operational_threshold the part counts as functional.
    /// </summary>
    public class ShipSubcomponent
    {
        public string SubcomponentId = "";
        public double Health = 1.0;
        public double OperationalThreshold = 0.5;
        public List<string> RequiredParts = new List<string>();
        public List<string> RequiredTools = new List<string>();
        public long MinSkill = 0;
        public double RepairSeconds = 5.0;

        public ShipSubcomponent(
            string pId = "",
            IEnumerable<string> pRequiredParts = null,
            IEnumerable<string> pRequiredTools = null,
            long pMinSkill = 0,
            double pRepairSeconds = 5.0,
            double pOperationalThreshold = 0.5)
        {
            SubcomponentId = pId;
            RequiredParts = pRequiredParts != null ? new List<string>(pRequiredParts) : new List<string>();
            RequiredTools = pRequiredTools != null ? new List<string>(pRequiredTools) : new List<string>();
            MinSkill = pMinSkill;
            RepairSeconds = pRepairSeconds;
            OperationalThreshold = pOperationalThreshold;
        }

        public bool IsFunctional() => Health >= OperationalThreshold;

        /// <summary>
        /// Parameterized repair. Deterministic: success is fully determined by the requirements being met.
        /// Returns {success, reason, seconds}.
        /// </summary>
        public GdDict Repair(GdArray availableParts, GdArray availableTools, long skillLevel)
        {
            availableParts = availableParts ?? new GdArray();
            availableTools = availableTools ?? new GdArray();
            if (IsFunctional())
                return Result(false, "already_functional", 0.0);
            foreach (string part in RequiredParts)
                if (!availableParts.Contains(part))
                    return Result(false, "missing_parts", 0.0);
            foreach (string tool in RequiredTools)
                if (!availableTools.Contains(tool))
                    return Result(false, "missing_tools", 0.0);
            if (skillLevel < MinSkill)
                return Result(false, "insufficient_skill", 0.0);
            Health = 1.0;
            double factor = 1.0 + 0.1 * (double)Math.Max(0L, skillLevel - MinSkill);
            return Result(true, "ok", RepairSeconds / factor);
        }

        static GdDict Result(bool success, string reason, double seconds) =>
            new GdDict { { "success", success }, { "reason", reason }, { "seconds", seconds } };

        public GdDict GetSummary() =>
            new GdDict
            {
                { "subcomponent_id", SubcomponentId },
                { "health", Health },
                { "operational_threshold", OperationalThreshold },
                { "required_parts", ShipCompat.ToGdArray(RequiredParts) },
                { "required_tools", ShipCompat.ToGdArray(RequiredTools) },
                { "min_skill", MinSkill },
                { "repair_seconds", RepairSeconds },
            };

        /// <summary>
        /// Restores mutable runtime state (health) from a summary. Static config (requirements/threshold)
        /// is not re-applied. Returns false on empty input.
        /// </summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            bool changed = false;
            double newHealth = GdMath.Clampf(V.F64(summary.Get("health", Health)), 0.0, 1.0);
            if (Math.Abs(newHealth - Health) > 0.0001)
            {
                Health = newHealth;
                changed = true;
            }
            return changed;
        }
    }
}
