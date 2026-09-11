using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class FoodTravelPlannerTests
    {
        sealed class FakeSpoilage : FoodTravelPlanner.ITravelRangeSource, FoodTravelPlanner.IFoodRegistry, FoodTravelPlanner.IFoodLookup
        {
            public double HungerStore;
            public readonly Dictionary<string, GdDict> Foods = new Dictionary<string, GdDict>();
            public double TravelRangeDays(double hungerDrainPerDay) => hungerDrainPerDay <= 0 ? 0 : HungerStore / hungerDrainPerDay;
            public void AddFood(string itemId, GdDict config) => Foods[itemId] = config;
            public bool HasFood(string itemId) => Foods.ContainsKey(itemId);
        }

        static readonly GdDict Route = new GdDict { { "ok", true }, { "distance", 100.0 }, { "food", 2.0 } };

        [Test]
        public void RouteNeedsUnitsAndSpoilageRange()
        {
            Assert.AreEqual("bad_route", FoodTravelPlanner.CanAttemptRoute(null, new GdDict())["reason"]);
            var empty = new FakeSpoilage();
            Assert.AreEqual("insufficient_food_units", FoodTravelPlanner.CanAttemptRoute(empty, Route, new GdDict { { "food", 0.0 } })["reason"]);
            Assert.AreEqual("insufficient_spoilage_stores", FoodTravelPlanner.CanAttemptRoute(empty, Route, new GdDict { { "food", 1000.0 } })["reason"]);
            var stocked = new FakeSpoilage { HungerStore = 30.0 };
            GdDict ok = FoodTravelPlanner.CanAttemptRoute(stocked, Route, new GdDict { { "food", 1000.0 } }, 12.0);
            Assert.IsTrue(V.Bool(ok["ok"]));
            Assert.AreEqual(2.0, ok["travel_days"]);
        }

        [Test]
        public void RegisterHarvestAddsOncePerItem()
        {
            var spoilage = new FakeSpoilage();
            Assert.IsTrue(FoodTravelPlanner.RegisterHarvest(spoilage, "hydroponic_greens", 3));
            Assert.AreEqual(1800.0, spoilage.Foods["hydroponic_greens"]["spoilage_seconds"]);
            spoilage.Foods["hydroponic_greens"]["marker"] = true;
            Assert.IsTrue(FoodTravelPlanner.RegisterHarvest(spoilage, "hydroponic_greens", 1));
            Assert.IsTrue(spoilage.Foods["hydroponic_greens"].Has("marker"), "existing entry is kept");
            Assert.IsFalse(FoodTravelPlanner.RegisterHarvest(new object(), "x", 1), "no add_food method");
        }
    }
}
