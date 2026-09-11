using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class QualityTierResolverTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void SummaryRoundTrips()
        {
            var resolver = new QualityTierResolver();
            GdDict summary = resolver.GetSummary();
            var restored = new QualityTierResolver();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void TiersAndScore()
        {
            Assert.AreEqual("poor", QualityTierResolver.TierForScore(0.34));
            Assert.AreEqual("standard", QualityTierResolver.TierForScore(0.35));
            Assert.AreEqual("masterwork", QualityTierResolver.TierForScore(0.90));
            var resolver = new QualityTierResolver();
            foreach (object tier in QualityTierResolver.TIER_ORDER)
                Assert.That(resolver.MultiplierForTier((string)tier), Is.GreaterThan(0.0));
            GdDict with = resolver.Resolve(0.5, 0, 0, true);
            GdDict without = resolver.Resolve(0.5, 0, 0, false);
            Assert.IsInstanceOf<double>(with["score"]);
            Assert.Greater((double)with["score"], (double)without["score"]);
        }
    }
}
