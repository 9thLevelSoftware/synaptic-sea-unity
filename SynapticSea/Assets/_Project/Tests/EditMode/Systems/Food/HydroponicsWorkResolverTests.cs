using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class HydroponicsWorkResolverTests
    {
        sealed class FakeSpoilage : FoodTravelPlanner.IFoodRegistry, FoodTravelPlanner.IFoodLookup
        {
            public readonly GdDict Added = new GdDict();
            public void AddFood(string itemId, GdDict config) => Added[itemId] = config;
            public bool HasFood(string itemId) => Added.Has(itemId);
        }

        static WorkActionState Work(string status) => new WorkActionState { Status = status };

        static GdDict Crop() => new GdDict
        {
            { "crop_id", "greens" }, { "produce_item_id", "hydroponic_greens" }, { "produce_quantity", 3L },
            { "growth_seconds", 10.0 }, { "water_cost", 2.0 }, { "power_cost", 3.0 },
        };

        [Test]
        public void PlantThenHarvestYieldsIntoInventory()
        {
            var hydro = new HydroponicsState();
            Assert.AreEqual("not_completed", HydroponicsWorkResolver.ResolvePlant(Work(WorkActionState.STATUS_ACTIVE), hydro, Crop(), 0, 10.0, 10.0)["reason"]);
            Assert.AreEqual("insufficient_water", HydroponicsWorkResolver.ResolvePlant(Work(WorkActionState.STATUS_COMPLETED), hydro, Crop(), 0, 1.0, 10.0)["reason"]);
            GdDict planted = HydroponicsWorkResolver.ResolvePlant(Work(WorkActionState.STATUS_COMPLETED), hydro, Crop(), 0, 10.0, 10.0);
            Assert.AreEqual(true, planted["ok"]);
            Assert.AreEqual(2.0, planted["water_consumed"]);

            var inventory = new GdDict();
            Assert.AreEqual("not_harvestable", HydroponicsWorkResolver.ResolveHarvest(Work(WorkActionState.STATUS_COMPLETED), hydro, inventory)["reason"]);
            hydro.Tick(10.0);
            var spoilage = new FakeSpoilage();
            GdDict harvested = HydroponicsWorkResolver.ResolveHarvest(Work(WorkActionState.STATUS_COMPLETED), hydro, inventory, spoilage);
            Assert.AreEqual(true, harvested["ok"]);
            Assert.AreEqual(3L, harvested["quantity"]);
            Assert.AreEqual(3L, inventory["hydroponic_greens"]);
            Assert.IsTrue(spoilage.HasFood("hydroponic_greens"), "harvest registers spoilage tracking");
        }
    }
}
