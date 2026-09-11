using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class WaterRecyclerStateTests
    {
        static GdDict Config() => new GdDict
        {
            { "output_item_id", "purified_water" },
            { "conversion_ratio", 0.75 },
            { "recycle_time_seconds", 30.0 },
            { "power_cost", 5.0 },
        };

        [Test]
        public void SummaryRoundTrips()
        {
            var recycler = new WaterRecyclerState();
            recycler.Configure(Config());
            recycler.LoadInput("contaminated_water", 4, 10.0);
            recycler.Tick(12.0);
            GdDict summary = recycler.GetSummary();
            var restored = new WaterRecyclerState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void RecyclesWithTruncatedRatio()
        {
            var recycler = new WaterRecyclerState();
            recycler.Configure(Config());
            Assert.AreEqual("insufficient_power", recycler.LoadInput("contaminated_water", 3, 1.0)["reason"]);
            Assert.AreEqual("no_input", recycler.LoadInput("contaminated_water", 0, 10.0)["reason"]);
            Assert.IsTrue(V.Bool(recycler.LoadInput("contaminated_water", 3, 10.0)["ok"]));
            Assert.AreEqual("not_idle", recycler.LoadInput("contaminated_water", 3, 10.0)["reason"]);
            Assert.IsTrue(recycler.Tick(30.0));
            GdDict output = recycler.CollectOutput();
            Assert.AreEqual(2L, output["quantity"], "int(3 * 0.75) truncates to 2");
            Assert.IsFalse(V.Bool(recycler.CollectOutput()["ok"]));
        }
    }
}
