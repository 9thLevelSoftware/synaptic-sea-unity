using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class HydroponicsStateTests
    {
        static GdDict Greens() => new GdDict
        {
            { "crop_id", "hydroponic_greens" },
            { "display_name", "Hydroponic Greens" },
            { "produce_item_id", "hydroponic_greens" },
            { "produce_quantity", 3L },
            { "growth_seconds", 120.0 },
            { "water_cost", 2.0 },
            { "power_cost", 3.0 },
            { "required_skill_level", 0L },
        };

        [Test]
        public void SummaryRoundTripsMidGrowth()
        {
            var tray = new HydroponicsState();
            tray.Plant(Greens(), 0, 5.0, 5.0);
            tray.Tick(60.0);
            GdDict summary = tray.GetSummary();
            var restored = new HydroponicsState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.AreEqual((long)HydroponicsState.State.PLANTED, restored.CurrentState);
        }

        [Test]
        public void PlantGrowHarvest()
        {
            var tray = new HydroponicsState();
            Assert.AreEqual("insufficient_water", tray.Plant(Greens(), 0, 1.0, 5.0)["reason"]);
            Assert.AreEqual("insufficient_power", tray.Plant(Greens(), 0, 5.0, 1.0)["reason"]);
            Assert.IsTrue(V.Bool(tray.Plant(Greens(), 0, 5.0, 5.0)["ok"]));
            Assert.IsTrue(tray.Tick(120.0));
            GdDict result = tray.Harvest();
            Assert.AreEqual("hydroponic_greens", result["item_id"]);
            Assert.AreEqual(3L, result["quantity"]);
            Assert.AreEqual((long)HydroponicsState.State.IDLE, tray.CurrentState);
        }
    }
}
