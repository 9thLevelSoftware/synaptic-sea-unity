// Ported from scripts/systems/hydroponics_work_resolver.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-C3.2: resolve plant/harvest WorkActions against HydroponicsState + inventory/spoilage.
    /// </summary>
    /// <remarks>
    /// The GDScript params are untyped <c>RefCounted</c>s probed with <c>has_method("plant"/"harvest")</c>; here they
    /// are a <see cref="WorkActionState"/> and a <see cref="HydroponicsState"/>, which always have those methods, so
    /// the "no_plant"/"no_harvest" branches cannot trigger. <c>spoilage</c> is passed straight to
    /// <see cref="FoodTravelPlanner.RegisterHarvest"/> (an <see cref="FoodTravelPlanner.IFoodRegistry"/>).
    /// </remarks>
    public static class HydroponicsWorkResolver
    {
        /// <summary>Complete plant WorkAction: crop_config must be provided (from crop catalog).</summary>
        public static GdDict ResolvePlant(
            WorkActionState work,
            HydroponicsState hydro,
            GdDict cropConfig,
            long skillLevel,
            double availableWater,
            double availablePower)
        {
            var outDict = new GdDict { { "ok", false }, { "reason", "" }, { "water_consumed", 0.0 }, { "power_consumed", 0.0 } };
            if (work == null || hydro == null)
            {
                outDict["reason"] = "no_work_or_hydro";
                return outDict;
            }
            if (work.Status != WorkActionState.STATUS_COMPLETED)
            {
                outDict["reason"] = "not_completed";
                return outDict;
            }
            GdDict res = hydro.Plant(cropConfig, skillLevel, availableWater, availablePower);
            if (!V.Bool(res.Get("ok", false)))
            {
                outDict["reason"] = V.Str(res.Get("reason", "plant_failed"));
                return outDict;
            }
            outDict["ok"] = true;
            outDict["water_consumed"] = V.F64(res.Get("water_consumed", 0.0));
            outDict["power_consumed"] = V.F64(res.Get("power_consumed", 0.0));
            return outDict;
        }

        /// <summary>Complete harvest WorkAction: yields into inventory dict and registers spoilage.</summary>
        public static GdDict ResolveHarvest(
            WorkActionState work,
            HydroponicsState hydro,
            GdDict inventory,
            object spoilage = null)
        {
            var outDict = new GdDict { { "ok", false }, { "reason", "" }, { "item_id", "" }, { "quantity", 0L } };
            if (work == null || hydro == null)
            {
                outDict["reason"] = "no_work_or_hydro";
                return outDict;
            }
            if (work.Status != WorkActionState.STATUS_COMPLETED)
            {
                outDict["reason"] = "not_completed";
                return outDict;
            }
            GdDict res = hydro.Harvest();
            if (!V.Bool(res.Get("ok", false)))
            {
                outDict["reason"] = "not_harvestable";
                return outDict;
            }
            string itemId = V.Str(res.Get("item_id", ""));
            long qty = Math.Max(0L, V.I64(res.Get("quantity", 0L)));
            if (itemId.Length == 0 || qty <= 0)
            {
                outDict["reason"] = "empty_yield";
                return outDict;
            }
            inventory[itemId] = V.I64(inventory.Get(itemId, 0L)) + qty;
            if (spoilage != null)
            {
                FoodTravelPlanner.RegisterHarvest(spoilage, itemId, qty, new GdDict
                {
                    { "hunger_restore", 15.0 },
                    { "thirst_restore", 5.0 },
                    { "spoilage_seconds", 1800.0 },
                });
            }
            outDict["ok"] = true;
            outDict["item_id"] = itemId;
            outDict["quantity"] = qty;
            return outDict;
        }
    }
}
