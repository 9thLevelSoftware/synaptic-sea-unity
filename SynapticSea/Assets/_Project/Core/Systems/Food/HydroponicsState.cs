// Ported from scripts/systems/hydroponics_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for hydroponics tray growth cycle.
    /// Plants a crop, ticks growth, and produces harvestable food.
    /// Never touches the scene tree.
    /// </summary>
    public sealed class HydroponicsState : ISimModel, IStatusLineProvider
    {
        public enum State { IDLE = 0, PLANTED = 1, HARVESTABLE = 2 }

        public string CropId = "";
        public string CropName = "";
        public string ProduceItemId = "";
        public long ProduceQuantity = 0;
        public double GrowthSeconds = 120.0;
        public double WaterCost = 2.0;
        public double PowerCost = 3.0;
        public long RequiredSkillLevel = 0;

        /// <summary>GDScript <c>state</c> (an int holding a <see cref="State"/> value).</summary>
        public long CurrentState = (long)State.IDLE;
        public double ProgressSeconds = 0.0;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            CropId = V.Str(config.Get("crop_id", ""));
            CropName = V.Str(config.Get("display_name", CropId));
            ProduceItemId = V.Str(config.Get("produce_item_id", ""));
            ProduceQuantity = V.I64(config.Get("produce_quantity", 0L));
            GrowthSeconds = Math.Max(0.1, V.F64(config.Get("growth_seconds", 120.0)));
            WaterCost = V.F64(config.Get("water_cost", 2.0));
            PowerCost = V.F64(config.Get("power_cost", 3.0));
            RequiredSkillLevel = V.I64(config.Get("required_skill_level", 0L));
            CurrentState = (long)State.IDLE;
            ProgressSeconds = 0.0;
        }

        public GdDict Plant(GdDict cropConfig, long skillLevel, double availableWater, double availablePower)
        {
            if (CurrentState != (long)State.IDLE) return new GdDict { { "ok", false }, { "reason", "not_idle" } };
            cropConfig = cropConfig ?? new GdDict();
            // Validate against the *incoming* crop, not stale prior-tray fields.
            long needSkill = V.I64(cropConfig.Get("required_skill_level", 0L));
            double needWater = V.F64(cropConfig.Get("water_cost", 2.0));
            double needPower = V.F64(cropConfig.Get("power_cost", 3.0));
            if (skillLevel < needSkill) return new GdDict { { "ok", false }, { "reason", "insufficient_skill" } };
            if (availableWater < needWater) return new GdDict { { "ok", false }, { "reason", "insufficient_water" } };
            if (availablePower < needPower) return new GdDict { { "ok", false }, { "reason", "insufficient_power" } };
            Configure(cropConfig);
            CurrentState = (long)State.PLANTED;
            ProgressSeconds = 0.0;
            return new GdDict { { "ok", true }, { "reason", "" }, { "water_consumed", WaterCost }, { "power_consumed", PowerCost } };
        }

        public bool Tick(double deltaSeconds)
        {
            if (CurrentState != (long)State.PLANTED) return false;
            if (deltaSeconds <= 0.0) return false;
            ProgressSeconds += deltaSeconds;
            if (ProgressSeconds >= GrowthSeconds)
            {
                CurrentState = (long)State.HARVESTABLE;
                return true;
            }
            return false;
        }

        public double GetProgressRatio()
        {
            if (GrowthSeconds <= 0.0) return 0.0;
            return GdMath.Clampf(ProgressSeconds / GrowthSeconds, 0.0, 1.0);
        }

        public GdDict Harvest()
        {
            if (CurrentState != (long)State.HARVESTABLE)
                return new GdDict { { "ok", false }, { "item_id", "" }, { "quantity", 0L } };
            var outDict = new GdDict
            {
                { "ok", true },
                { "item_id", ProduceItemId },
                { "quantity", ProduceQuantity },
            };
            CurrentState = (long)State.IDLE;
            ProgressSeconds = 0.0;
            CropId = "";
            return outDict;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "crop_id", CropId },
                { "crop_name", CropName },
                { "state", CurrentState },
                { "progress_seconds", ProgressSeconds },
                { "growth_seconds", GrowthSeconds },
                { "progress_ratio", GetProgressRatio() },
                { "produce_item_id", ProduceItemId },
                { "produce_quantity", ProduceQuantity },
                { "water_cost", WaterCost },
                { "power_cost", PowerCost },
                { "required_skill_level", RequiredSkillLevel },
            };
        }

        /// <summary>Returns true when any field changed, like the GDScript.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            string newCrop = V.Str(summary.Get("crop_id", CropId));
            if (newCrop != CropId) { CropId = newCrop; changed = true; }
            string newName = V.Str(summary.Get("crop_name", CropName));
            if (newName != CropName) { CropName = newName; changed = true; }
            long newState = V.I64(summary.Get("state", CurrentState));
            if (newState != CurrentState) { CurrentState = newState; changed = true; }
            double newProgress = V.F64(summary.Get("progress_seconds", ProgressSeconds));
            if (Math.Abs(newProgress - ProgressSeconds) > 0.001) { ProgressSeconds = newProgress; changed = true; }
            double newGrowth = V.F64(summary.Get("growth_seconds", GrowthSeconds));
            if (Math.Abs(newGrowth - GrowthSeconds) > 0.001) { GrowthSeconds = Math.Max(0.1, newGrowth); changed = true; }
            string newProd = V.Str(summary.Get("produce_item_id", ProduceItemId));
            if (newProd != ProduceItemId) { ProduceItemId = newProd; changed = true; }
            long newQty = V.I64(summary.Get("produce_quantity", ProduceQuantity));
            if (newQty != ProduceQuantity) { ProduceQuantity = newQty; changed = true; }
            double newWater = V.F64(summary.Get("water_cost", WaterCost));
            if (Math.Abs(newWater - WaterCost) > 0.001) { WaterCost = newWater; changed = true; }
            double newPower = V.F64(summary.Get("power_cost", PowerCost));
            if (Math.Abs(newPower - PowerCost) > 0.001) { PowerCost = newPower; changed = true; }
            long newSkill = V.I64(summary.Get("required_skill_level", RequiredSkillLevel));
            if (newSkill != RequiredSkillLevel) { RequiredSkillLevel = newSkill; changed = true; }
            return changed;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            string stateName = "IDLE";
            if (CurrentState == (long)State.PLANTED) stateName = "PLANTED";
            else if (CurrentState == (long)State.HARVESTABLE) stateName = "HARVESTABLE";
            lines.Add("Hydroponics: " + CropName + " [" + stateName + "]");
            if (CurrentState == (long)State.PLANTED)
                lines.Add("  growth=" + ItemsCompat.D((long)GdMath.Round(GetProgressRatio() * 100.0)) + "%");
            else if (CurrentState == (long)State.HARVESTABLE)
                lines.Add("  ready: " + ProduceItemId + " x" + ItemsCompat.D(ProduceQuantity));
            return lines;
        }
    }
}
