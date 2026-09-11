// Ported from scripts/systems/food_travel_planner.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-C3.2: pure travel-range constraint from food stores + optional SeaGraph route.
    /// Does not mutate scene; SeaGraph.apply_travel_cost still owns fuel/food resource dicts.
    /// </summary>
    /// <remarks>
    /// The GDScript duck-types the spoilage object with <c>has_method</c>; those checks become the optional
    /// nested interfaces below (a spoilage object that does not implement one behaves like one without that method).
    /// </remarks>
    public static class FoodTravelPlanner
    {
        /// <summary>SpoilageState <c>travel_range_days(hunger_per_day) -> float</c>.</summary>
        public interface ITravelRangeSource
        {
            double TravelRangeDays(double hungerDrainPerDay);
        }

        /// <summary>SpoilageState <c>add_food(item_id, config)</c>.</summary>
        public interface IFoodRegistry
        {
            void AddFood(string itemId, GdDict config);
        }

        /// <summary>SpoilageState <c>has_food(item_id) -> bool</c>.</summary>
        public interface IFoodLookup
        {
            bool HasFood(string itemId);
        }

        public const double DEFAULT_HUNGER_PER_DAY = 12.0;
        public const double DEFAULT_FOOD_UNITS_PER_DAY = 1.0;

        /// <summary>
        /// spoilage: SpoilageState-like with travel_range_days; route: SeaGraph find_route result with food
        /// cost; resources: { food: float } inventory units for route food_cost.
        /// </summary>
        public static GdDict CanAttemptRoute(object spoilage, GdDict route, GdDict resources = null, double hungerPerDay = DEFAULT_HUNGER_PER_DAY)
        {
            route = route ?? new GdDict();
            resources = resources ?? new GdDict();
            var outDict = new GdDict
            {
                { "ok", false },
                { "reason", "" },
                { "travel_days", 0.0 },
                { "food_range_days", 0.0 },
                { "route_food_cost", 0.0 },
                { "food_units", 0.0 },
            };
            if (!V.Bool(route.Get("ok", false)))
            {
                outDict["reason"] = "bad_route";
                return outDict;
            }
            double rangeDays = 0.0;
            if (spoilage is ITravelRangeSource rangeSource)
                rangeDays = rangeSource.TravelRangeDays(hungerPerDay);
            outDict["food_range_days"] = rangeDays;
            // Convert route distance to days: assume 50 distance units per day of travel
            double distance = V.F64(route.Get("distance", 0.0));
            double travelDays = distance > 0.0 ? distance / 50.0 : 0.0;
            outDict["travel_days"] = travelDays;
            double routeFood = V.F64(route.Get("food", 0.0));
            outDict["route_food_cost"] = routeFood;
            double foodUnits = V.F64(resources.Get("food", 0.0));
            outDict["food_units"] = foodUnits;
            if (foodUnits < routeFood)
            {
                outDict["reason"] = "insufficient_food_units";
                return outDict;
            }
            if (rangeDays + 0.001 < travelDays)
            {
                outDict["reason"] = "insufficient_spoilage_stores";
                return outDict;
            }
            outDict["ok"] = true;
            return outDict;
        }

        /// <summary>Register harvested produce into spoilage tracking with default food config.</summary>
        public static bool RegisterHarvest(object spoilage, string itemId, long qty = 1, GdDict config = null)
        {
            config = config ?? new GdDict();
            if (spoilage == null || string.IsNullOrEmpty(itemId) || qty <= 0) return false;
            if (!(spoilage is IFoodRegistry registry)) return false;
            var cfg = new GdDict
            {
                { "item_id", itemId },
                { "display_name", V.Str(config.Get("display_name", itemId)) },
                { "hunger_restore", V.F64(config.Get("hunger_restore", 15.0)) },
                { "thirst_restore", V.F64(config.Get("thirst_restore", 5.0)) },
                { "sanity_restore", V.F64(config.Get("sanity_restore", 0.0)) },
                { "spoilage_seconds", V.F64(config.Get("spoilage_seconds", 1800.0)) },
            };
            // Track one FoodState per item_id; harvest stacks share stage (MVP).
            if (spoilage is IFoodLookup lookup && lookup.HasFood(itemId)) return true;
            registry.AddFood(itemId, cfg);
            return true;
        }
    }
}
