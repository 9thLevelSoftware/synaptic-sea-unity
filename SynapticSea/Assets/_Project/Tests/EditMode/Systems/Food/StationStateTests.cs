using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class StationStateTests
    {
        static StationState Fabricator()
        {
            var station = new StationState();
            station.Configure(new GdDict { { "station_kind", "fabricator" }, { "level", 2L }, { "powered", true } });
            return station;
        }

        [Test]
        public void SummaryRoundTrips()
        {
            var station = Fabricator();
            station.Enqueue("craft_sensor_module");
            station.StartRecipe("craft_power_cell", 30.0);
            station.Tick(10.0);
            GdDict summary = station.GetSummary();
            var restored = new StationState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void CraftQueueAndPower()
        {
            var station = Fabricator();
            Assert.IsTrue(station.StartRecipe("craft_power_cell", 30.0));
            Assert.IsFalse(station.Tick(10.0));
            Assert.AreEqual(10.0 / 30.0, station.GetProgressRatio(), 0.001);
            Assert.IsTrue(station.Tick(20.0));
            Assert.AreEqual((long)StationState.Status.COMPLETE, station.CurrentStatus);
            Assert.AreEqual("", station.FinishAndAdvance());

            station.Enqueue("craft_sensor_module");
            station.Enqueue("craft_data_core");
            station.StartRecipe("craft_power_cell", 30.0);
            station.Tick(30.0);
            Assert.AreEqual("craft_sensor_module", station.FinishAndAdvance());
            station.SetPower(false);
            Assert.AreEqual((long)StationState.Status.PAUSED_POWER, station.CurrentStatus);
            station.SetPower(true);
            Assert.AreEqual((long)StationState.Status.CRAFTING, station.CurrentStatus);
            Assert.AreEqual("Recipe: craft_sensor_module 0.0/30.0s", station.GetStatusLines()[1]);
            Assert.AreEqual(3L, station.EnqueueBatch("craft_data_core", 3));
            Assert.AreEqual(4L, station.QueueSpace(), "8 - (1 left + 3 batched)");
        }
    }
}
