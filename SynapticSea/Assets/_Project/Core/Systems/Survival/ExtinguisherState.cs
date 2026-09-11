// Ported from scripts/systems/extinguisher_state.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Player fire-extinguisher tool charge (ADR-0041): consumed per manual extinguish, refilled at a powered port.</summary>
    public sealed class ExtinguisherState : ISimModel
    {
        public const double DEFAULT_MAX_CHARGE = 100.0;
        public const double DEFAULT_COST_PER_USE = 34.0;
        public const double DEFAULT_RECHARGE_PER_SECOND = 5.0;

        public double MaxCharge = DEFAULT_MAX_CHARGE;
        public double Charge = DEFAULT_MAX_CHARGE;
        public double ChargeCostPerUse = DEFAULT_COST_PER_USE;
        public double RechargePerSecond = DEFAULT_RECHARGE_PER_SECOND;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            MaxCharge = Math.Max(1.0, V.F64(config.Get("max_charge", DEFAULT_MAX_CHARGE)));
            ChargeCostPerUse = Math.Max(0.0, V.F64(config.Get("charge_cost_per_use", DEFAULT_COST_PER_USE)));
            RechargePerSecond = Math.Max(0.0, V.F64(config.Get("recharge_per_second", DEFAULT_RECHARGE_PER_SECOND)));
            Charge = GdMath.Clampf(V.F64(config.Get("charge", MaxCharge)), 0.0, MaxCharge);
        }

        public bool HasChargeForUse() => Charge >= ChargeCostPerUse;

        public bool ConsumeUse()
        {
            if (!HasChargeForUse()) return false;
            Charge = Math.Max(0.0, Charge - ChargeCostPerUse);
            return true;
        }

        public void Recharge(double delta)
        {
            if (delta <= 0.0) return;
            Charge = Math.Min(MaxCharge, Charge + RechargePerSecond * delta);
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "charge", Charge },
                { "max_charge", MaxCharge },
                { "charge_cost_per_use", ChargeCostPerUse },
                { "recharge_per_second", RechargePerSecond },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            // GDScript iterates these keys and uses get(key)/set(key) on the matching property.
            foreach (string key in new[] { "max_charge", "charge_cost_per_use", "recharge_per_second", "charge" })
            {
                if (!summary.Has(key)) continue;
                double newVal = V.F64(summary[key]);
                if (Math.Abs(newVal - GetField(key)) > 0.001)
                {
                    SetField(key, newVal);
                    changed = true;
                }
            }
            MaxCharge = Math.Max(1.0, MaxCharge);
            ChargeCostPerUse = Math.Max(0.0, ChargeCostPerUse);
            RechargePerSecond = Math.Max(0.0, RechargePerSecond);
            Charge = GdMath.Clampf(Charge, 0.0, MaxCharge);
            return changed;
        }

        double GetField(string key)
        {
            switch (key)
            {
                case "max_charge": return MaxCharge;
                case "charge_cost_per_use": return ChargeCostPerUse;
                case "recharge_per_second": return RechargePerSecond;
                default: return Charge;
            }
        }

        void SetField(string key, double value)
        {
            switch (key)
            {
                case "max_charge": MaxCharge = value; break;
                case "charge_cost_per_use": ChargeCostPerUse = value; break;
                case "recharge_per_second": RechargePerSecond = value; break;
                default: Charge = value; break;
            }
        }
    }
}
