using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class FoodStateTests
    {
        static readonly GdDict Config = new GdDict
        {
            { "item_id", "ration_pack" },
            { "display_name", "Ration Pack" },
            { "spoilage_seconds", 3600.0 },
            { "hunger_restore", 15.0 },
            { "thirst_restore", 5.0 },
            { "sanity_restore", 2.0 },
            { "fresh_multiplier", 1.0 },
            { "stale_multiplier", 0.6 },
            { "rotten_multiplier", 0.2 },
            { "rotten_sickness_risk", 0.25 },
        };

        [Test]
        public void SummaryRoundTrips()
        {
            var food = new FoodState();
            food.Configure(Config);
            food.Tick(1800.0);
            GdDict summary = food.GetSummary();
            // apply_summary restores progress fields only (not display name / multipliers), so the
            // fresh instance is configured from the same catalog entry first, as SpoilageState does.
            var restored = new FoodState();
            restored.Configure(Config);
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void SpoilsFreshToStaleToRotten()
        {
            var food = new FoodState();
            food.Configure(Config);
            Assert.AreEqual((long)FoodState.Stage.FRESH, food.CurrentStage);
            Assert.IsTrue(food.Tick(1800.0));
            Assert.AreEqual((long)FoodState.Stage.STALE, food.CurrentStage);
            Assert.AreEqual(9.0, V.F64(food.GetEffectiveRestores()["hunger"]), 0.001);
            food.Tick(1800.0);
            Assert.AreEqual((long)FoodState.Stage.ROTTEN, food.CurrentStage);
            GdDict eff = food.Consume();
            Assert.AreEqual(3.0, V.F64(eff["hunger"]), 0.001);
            Assert.AreEqual(0.25, V.F64(eff["sickness_risk"]), 0.001);
            var lines = food.GetStatusLines();
            Assert.AreEqual("Food: Ration Pack [ROTTEN]", lines[0]);
            Assert.AreEqual("  spoil=100%", lines[1]);
            Assert.AreEqual("  hunger=+3.0 thirst=+1.0 sanity=+0.4", lines[2]);
            Assert.AreEqual("  sickness_risk=25%", lines[3]);
        }
    }
}
