using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class InventoryStateTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void SummaryRoundTrips()
        {
            var inv = new InventoryState();
            inv.AddTool("portable_oxygen_pump");
            inv.AddItem("scrap_metal", 3);
            inv.AddItem("ration_pack", 2);
            GdDict summary = inv.GetSummary();
            Assert.AreEqual(new[] { "items", "tool_ids", "active_effects", "drain_multiplier", "total_weight", "max_weight" }, summary.Keys.ToArray());
            var restored = new InventoryState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsFalse(new InventoryState().ApplySummary(new GdDict()), "empty summary rejected");

            var legacy = new InventoryState();
            Assert.IsTrue(legacy.ApplySummary(new GdDict { { "tool_ids", GdArray.Of("portable_oxygen_pump") } }));
            Assert.IsTrue(legacy.HasTool("portable_oxygen_pump"), "legacy tool_ids shape reconstructs tools");
        }

        [Test]
        public void AddRemoveHonorsStackButNotWeight()
        {
            var inv = new InventoryState();
            Assert.AreEqual(0L, inv.AddItem("", 3));
            Assert.AreEqual(20L, inv.AddItem("scrap_metal", 25), "max_stack 20 caps the add");
            Assert.AreEqual(0L, inv.AddItem("scrap_metal", 1), "full stack accepts nothing");
            Assert.IsFalse(inv.CanAccept("scrap_metal", 1));
            Assert.AreEqual(100.0, inv.GetTotalWeight(), "20 * 5.0");
            Assert.IsTrue(inv.IsOverCapacity(), "soft cap: over capacity is allowed");
            Assert.That(inv.GetLoadRatio(), Is.GreaterThan(1.0));
            inv.BonusCapacity = 60.0;
            Assert.IsFalse(inv.IsOverCapacity(), "container bonus lifts the budget");
            inv.BonusCapacity = 0.0;
            inv.WeightReduction = 60.0;
            Assert.AreEqual(40.0, inv.GetEffectiveWeight());
            Assert.IsFalse(inv.IsOverCapacity());

            Assert.AreEqual(5L, inv.RemoveItem("scrap_metal", 5));
            Assert.AreEqual(15L, inv.GetQuantity("scrap_metal"));
            Assert.AreEqual(15L, inv.RemoveItem("scrap_metal", 99));
            Assert.IsFalse(inv.Items.Has("scrap_metal"), "emptied stacks are erased");
            Assert.AreEqual(0L, inv.RemoveItem("scrap_metal", 1));
        }

        [Test]
        public void ToolShimsAndStatusLines()
        {
            var inv = new InventoryState();
            Assert.AreEqual(1.0, inv.GetDrainMultiplier());
            Assert.IsTrue(inv.AddTool("portable_oxygen_pump"));
            Assert.IsFalse(inv.AddTool("portable_oxygen_pump"), "duplicate add_tool refused");
            Assert.AreEqual(new List<string> { "portable_oxygen_pump" }, inv.ToolIds);
            Assert.AreEqual(0.5, inv.GetSummary()["drain_multiplier"]);
            inv.AddItem("scrap_metal", 2);
            var lines = inv.GetStatusLines();
            CollectionAssert.Contains(lines, "Tool: Portable Oxygen Pump");
            CollectionAssert.Contains(lines, "tool=portable_oxygen_pump");
            CollectionAssert.Contains(lines, "drain_multiplier=0.5");
            CollectionAssert.Contains(lines, "item=scrap_metal x2");
            Assert.IsTrue(inv.RemoveTool("portable_oxygen_pump"));
            Assert.AreEqual(1.0, inv.GetDrainMultiplier());
        }

        [Test]
        public void CargoTransferBetweenPlayerAndHold()
        {
            var player = new InventoryState();
            player.AddItem("scrap_metal", 5);
            player.AddItem("ration_pack", 4);
            player.AddTool("portable_oxygen_pump");
            var hold = ShipInventory.Create(12.0);
            GdDict result = CargoTransfer.DepositAll(player, hold);
            Assert.AreEqual(2L, hold.GetQuantity("scrap_metal"), "hold weight cap: floor(12/5) scrap");
            Assert.AreEqual(3L, player.GetQuantity("scrap_metal"));
            Assert.AreEqual(1L, player.GetQuantity("portable_oxygen_pump"), "tools never auto-deposited");
            Assert.AreEqual(V.I64(result["total_moved"]), hold.GetQuantity("scrap_metal") + hold.GetQuantity("ration_pack"));
        }
    }
}
