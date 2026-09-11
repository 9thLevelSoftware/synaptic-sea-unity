// Ported from scripts/systems/sustenance_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Sustenance rollup: aggregates the hydroponics / water-recycler summaries and the meals flag from the tick
    /// context into power, material, and ready-count totals for the HUD and save.
    /// </summary>
    public sealed class SustenanceState : ISimModel, IStatusLineProvider
    {
        public GdDict Facilities = new GdDict();
        public double TotalPowerConsumed = 0.0;
        public double TotalMaterialsConsumed = 0.0;
        public long PurifiedWaterReady = 0;
        public long HarvestReady = 0;
        public long MealsReady = 0;

        public void Configure(GdDict config)
        {
            // GDScript: (config.get("facilities", {}) as Dictionary).duplicate(true)
            Facilities = config.GetDictOrEmpty("facilities").DeepCopy();
            TotalPowerConsumed = 0.0;
            TotalMaterialsConsumed = 0.0;
            PurifiedWaterReady = 0;
            HarvestReady = 0;
            MealsReady = 0;
        }

        public void Tick(double delta, GdDict context)
        {
            if (context == null) context = new GdDict();
            double poweredRatio = GdMath.Clampf(V.F64(context.Get(SimKeys.PoweredRatio, 0.0)), 0.0, 1.0);
            GdDict hydro = context.GetDictOrEmpty(SimKeys.HydroponicsSummary).DeepCopy();
            GdDict water = context.GetDictOrEmpty(SimKeys.WaterRecyclerSummary).DeepCopy();
            TotalPowerConsumed = V.F64(hydro.Get("power_cost", 0.0)) + V.F64(water.Get("power_cost", 0.0));
            if (poweredRatio < 0.5) TotalPowerConsumed = 0.0;
            TotalMaterialsConsumed = V.F64(hydro.Get("water_cost", 0.0)) + V.F64(water.Get("input_quantity", 0L));
            HarvestReady = V.I64(hydro.Get("state", 0L)) == 2 ? 1 : 0; // 2 == HydroponicsState.State.HARVESTABLE
            MealsReady = V.Bool(context.Get(SimKeys.MealsActive, false)) ? 1 : 0;
            PurifiedWaterReady = V.I64(water.Get("output_ready", 0L));
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "facilities", Facilities.DeepCopy() },
                { "total_power_consumed", TotalPowerConsumed },
                { "total_materials_consumed", TotalMaterialsConsumed },
                { "purified_water_ready", PurifiedWaterReady },
                { "harvest_ready", HarvestReady },
                { "meals_ready", MealsReady },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            object newFacilities = summary.Get("facilities");
            if (newFacilities is GdDict nf && GdJson.Stringify(nf) != GdJson.Stringify(Facilities))
            {
                Facilities = nf.DeepCopy();
                changed = true;
            }
            foreach (string key in new[] { "total_power_consumed", "total_materials_consumed" })
            {
                double current = key == "total_power_consumed" ? TotalPowerConsumed : TotalMaterialsConsumed;
                double newValue = V.F64(summary.Get(key, current));
                if (Math.Abs(newValue - current) > 0.001)
                {
                    if (key == "total_power_consumed") TotalPowerConsumed = newValue;
                    else TotalMaterialsConsumed = newValue;
                    changed = true;
                }
            }
            foreach (string key in new[] { "purified_water_ready", "harvest_ready", "meals_ready" })
            {
                long current = GetIntField(key);
                long newValue = V.I64(summary.Get(key, current));
                if (newValue != current)
                {
                    SetIntField(key, newValue);
                    changed = true;
                }
            }
            return changed;
        }

        long GetIntField(string key)
        {
            switch (key)
            {
                case "purified_water_ready": return PurifiedWaterReady;
                case "harvest_ready": return HarvestReady;
                default: return MealsReady;
            }
        }

        void SetIntField(string key, long value)
        {
            switch (key)
            {
                case "purified_water_ready": PurifiedWaterReady = value; break;
                case "harvest_ready": HarvestReady = value; break;
                default: MealsReady = value; break;
            }
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            return new List<string>
            {
                "Sustenance power=" + GdString.FormatFixed(TotalPowerConsumed, 1) + " materials=" + GdString.FormatFixed(TotalMaterialsConsumed, 1),
                "Sustenance harvest=" + GdString.FormatInt(HarvestReady) + " meals=" + GdString.FormatInt(MealsReady) + " water=" + GdString.FormatInt(PurifiedWaterReady),
            };
        }
    }
}
