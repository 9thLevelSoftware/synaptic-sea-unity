using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class EquipmentStateTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void SummaryRoundTrips()
        {
            var eq = EquipmentState.Create();
            eq.Equip("eva_backpack");
            eq.Equip("hardsuit");
            GdDict summary = eq.GetSummary();
            var clone = EquipmentState.Create();
            Assert.IsTrue(clone.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, clone.GetSummary()));
            Assert.AreEqual(0.75, clone.GetOxygenDrainMultiplier(), "suit effect round-tripped");
            Assert.IsFalse(EquipmentState.Create().ApplySummary(new GdDict()), "empty summary rejected");
        }

        [Test]
        public void EquipDisplacesAndAggregates()
        {
            var eq = EquipmentState.Create();
            Assert.IsFalse(eq.CanEquip("scrap_metal"));
            Assert.AreEqual(false, eq.Equip("scrap_metal")["ok"]);
            GdDict r1 = eq.Equip("eva_backpack");
            Assert.AreEqual(true, r1["ok"]);
            Assert.AreEqual("", r1["displaced"]);
            eq.Equip("tool_belt");
            Assert.AreEqual(52.0, eq.GetCarryCapacityBonus(), "backpack 40 + tool_belt 12");
            eq.Equip("hardsuit");
            Assert.AreEqual(2, eq.GetContainerReductions().Count, "suit is not a container");
            GdDict r2 = eq.Equip("field_pack");
            Assert.AreEqual("eva_backpack", r2["displaced"]);
            Assert.AreEqual(27.0, eq.GetCarryCapacityBonus());
            Assert.AreEqual("tool_belt", eq.Unequip("waist"));
            Assert.AreEqual("", eq.Unequip("waist"));
        }
    }
}
