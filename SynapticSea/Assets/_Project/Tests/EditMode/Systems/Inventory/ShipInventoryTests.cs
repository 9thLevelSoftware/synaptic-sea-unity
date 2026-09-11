using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ShipInventoryTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void SummaryRoundTrips()
        {
            var hold = ShipInventory.Create(500.0);
            Assert.AreEqual(5L, hold.AddItem("scrap_metal", 5));
            Assert.AreEqual(2L, hold.RemoveItem("scrap_metal", 2));
            GdDict summary = hold.GetSummary();
            var restored = ShipInventory.Create(1.0);
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.AreEqual(3L, restored.GetQuantity("scrap_metal"));
            Assert.AreEqual(500.0, restored.GetMaxWeight());
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsFalse(ShipInventory.Create().ApplySummary(new GdDict()), "empty summary rejected");
        }

        [Test]
        public void WeightCapLimitsAdds()
        {
            var tiny = ShipInventory.Create(12.0);
            Assert.AreEqual(2L, tiny.AddItem("scrap_metal", 999), "12-cap hold fits exactly 2 scrap (5.0 each)");
            Assert.That(tiny.GetTotalWeight(), Is.LessThanOrEqualTo(12.0 + 0.0001));
            Assert.AreEqual(0L, tiny.AddItem("scrap_metal", 1));
            GdArray parts = tiny.GetItemsByCategory("part");
            Assert.AreEqual(1, parts.Count);
            Assert.AreEqual(5.0, ((GdDict)parts[0])["weight_each"]);
        }
    }
}
