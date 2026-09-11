// Ported from scripts/systems/power_grid_state.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public class PowerGridState : ISimModel, IStatusLineProvider
    {
        public static readonly IReadOnlyList<string> DEFAULT_SUBSYSTEM_ORDER = new[]
        {
            "life_support",
            "propulsion",
            "stations",
            "lights",
            "sustenance",
        };

        public double TotalSupplyUnits = 100.0;
        public double MinOperationalRatio = 0.5;
        public List<string> SubsystemOrder = new List<string>();
        public GdDict BaselineDemandUnits = new GdDict();
        public GdDict ManualRoutesUnits = new GdDict();
        public GdDict EffectiveRoutesUnits = new GdDict();
        public List<string> BlackoutSubsystems = new List<string>();
        public bool Overloaded = false;
        public double AvailableSupplyUnits = 100.0;
        public List<string> ManagerBrokenSystems = new List<string>();

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            TotalSupplyUnits = Math.Max(1.0, V.F64(config.Get("total_supply_units", 100.0)));
            MinOperationalRatio = GdMath.Clampf(V.F64(config.Get("min_operational_ratio", 0.5)), 0.05, 1.0);
            SubsystemOrder.Clear();
            // GDScript iterated whatever Variant was stored; only an Array (or the default) is meaningful here.
            if (config.Has("subsystem_order"))
            {
                if (config.Get("subsystem_order") is GdArray rawOrder)
                    foreach (object rawId in rawOrder)
                        SubsystemOrder.Add(V.Str(rawId));
            }
            else
            {
                SubsystemOrder.AddRange(DEFAULT_SUBSYSTEM_ORDER);
            }
            if (SubsystemOrder.Count == 0)
                SubsystemOrder = new List<string>(DEFAULT_SUBSYSTEM_ORDER);
            BaselineDemandUnits.Clear();
            GdDict demandConfig = config.GetDictOrEmpty("baseline_demand_units");
            foreach (string subsystemId in SubsystemOrder)
                BaselineDemandUnits[subsystemId] = V.F64(demandConfig.Get(subsystemId, 10.0));
            ManualRoutesUnits.Clear();
            foreach (string subsystemId in SubsystemOrder)
                ManualRoutesUnits[subsystemId] = V.F64(BaselineDemandUnits.Get(subsystemId, 0.0));
            EffectiveRoutesUnits = ManualRoutesUnits.DeepCopy();
            BlackoutSubsystems.Clear();
            Overloaded = false;
            AvailableSupplyUnits = TotalSupplyUnits;
            ManagerBrokenSystems.Clear();
        }

        public bool SetManualRoute(string subsystemId, double units)
        {
            if (!BaselineDemandUnits.Has(subsystemId))
                return false;
            ManualRoutesUnits[subsystemId] = Math.Max(0.0, units);
            return true;
        }

        public void Rebalance(double powerHealthRatio, IEnumerable<string> brokenSystems = null)
        {
            AvailableSupplyUnits = GdMath.Clampf(powerHealthRatio, 0.0, 1.0) * TotalSupplyUnits;
            ManagerBrokenSystems = brokenSystems != null ? new List<string>(brokenSystems) : new List<string>();
            EffectiveRoutesUnits.Clear();
            BlackoutSubsystems.Clear();
            Overloaded = false;
            double remaining = AvailableSupplyUnits;
            foreach (string subsystemId in SubsystemOrder)
            {
                double requested = Math.Max(0.0, V.F64(ManualRoutesUnits.Get(subsystemId, BaselineDemandUnits.Get(subsystemId, 0.0))));
                double granted = Math.Min(requested, remaining);
                if (ManagerBrokenSystems.Contains(subsystemId))
                    granted = 0.0;
                EffectiveRoutesUnits[subsystemId] = granted;
                remaining = Math.Max(0.0, remaining - granted);
                if (requested > granted + 0.001)
                    Overloaded = true;
                if (!IsSystemPowered(subsystemId))
                    BlackoutSubsystems.Add(subsystemId);
            }
        }

        public double GetAllocationRatio(string subsystemId)
        {
            double demand = Math.Max(0.001, V.F64(BaselineDemandUnits.Get(subsystemId, 0.0)));
            return GdMath.Clampf(V.F64(EffectiveRoutesUnits.Get(subsystemId, 0.0)) / demand, 0.0, 1.0);
        }

        public bool IsSystemPowered(string subsystemId)
        {
            if (ManagerBrokenSystems.Contains(subsystemId))
                return false;
            return GetAllocationRatio(subsystemId) >= MinOperationalRatio;
        }

        public GdDict GetSummary() =>
            new GdDict
            {
                { "total_supply_units", TotalSupplyUnits },
                { "available_supply_units", AvailableSupplyUnits },
                { "min_operational_ratio", MinOperationalRatio },
                { "subsystem_order", ShipCompat.ToGdArray(SubsystemOrder) },
                { "baseline_demand_units", BaselineDemandUnits.DeepCopy() },
                { "manual_routes_units", ManualRoutesUnits.DeepCopy() },
                { "effective_routes_units", EffectiveRoutesUnits.DeepCopy() },
                { "blackout_subsystems", ShipCompat.ToGdArray(BlackoutSubsystems) },
                { "manager_broken_systems", ShipCompat.ToGdArray(ManagerBrokenSystems) },
                { "overloaded", Overloaded },
            };

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            bool changed = false;
            // GDScript looped over the property names with get()/set(); unrolled here in the same order.
            ApplyFloat(summary, "total_supply_units", ref TotalSupplyUnits, ref changed);
            ApplyFloat(summary, "available_supply_units", ref AvailableSupplyUnits, ref changed);
            ApplyFloat(summary, "min_operational_ratio", ref MinOperationalRatio, ref changed);
            ApplyDict(summary, "baseline_demand_units", ref BaselineDemandUnits, ref changed);
            ApplyDict(summary, "manual_routes_units", ref ManualRoutesUnits, ref changed);
            ApplyDict(summary, "effective_routes_units", ref EffectiveRoutesUnits, ref changed);
            ApplyStringList(summary, "subsystem_order", ref SubsystemOrder, ref changed);
            ApplyStringList(summary, "blackout_subsystems", ref BlackoutSubsystems, ref changed);
            ApplyStringList(summary, "manager_broken_systems", ref ManagerBrokenSystems, ref changed);
            bool newOverloaded = V.Bool(summary.Get("overloaded", Overloaded));
            if (newOverloaded != Overloaded)
            {
                Overloaded = newOverloaded;
                changed = true;
            }
            return changed;
        }

        static void ApplyFloat(GdDict summary, string key, ref double field, ref bool changed)
        {
            double newValue = V.F64(summary.Get(key, field));
            if (Math.Abs(newValue - field) > 0.001)
            {
                field = newValue;
                changed = true;
            }
        }

        static void ApplyDict(GdDict summary, string key, ref GdDict field, ref bool changed)
        {
            object value = summary.Get(key, null);
            if (value is GdDict dict && GdJson.Stringify(dict) != GdJson.Stringify(field))
            {
                field = dict.DeepCopy();
                changed = true;
            }
        }

        static void ApplyStringList(GdDict summary, string key, ref List<string> field, ref bool changed)
        {
            object value = summary.Get(key, null);
            if (!(value is GdArray arr))
                return;
            var normalized = new List<string>();
            foreach (object entry in arr)
                normalized.Add(V.Str(entry));
            if (!SameStrings(normalized, field))
            {
                field = normalized;
                changed = true;
            }
        }

        static bool SameStrings(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            return true;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Grid " + ShipCompat.FormatF(AvailableSupplyUnits, 0) + "/" + ShipCompat.FormatF(TotalSupplyUnits, 0)
                      + " units" + (Overloaded ? " OVERLOAD" : ""));
            foreach (string subsystemId in SubsystemOrder)
            {
                long pct = (long)GdMath.Round(GetAllocationRatio(subsystemId) * 100.0);
                string state = IsSystemPowered(subsystemId) ? "ON" : "BLACKOUT";
                lines.Add("Grid " + subsystemId + " " + pct + "% " + state);
            }
            return lines;
        }
    }
}
