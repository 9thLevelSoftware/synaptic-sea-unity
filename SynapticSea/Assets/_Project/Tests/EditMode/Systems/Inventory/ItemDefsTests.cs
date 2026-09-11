using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ItemDefsTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void MergedDefinitionsExposeItemFields()
        {
            GdDict defs = ItemDefs.LoadDefinitions();
            Assert.That(defs.Count, Is.GreaterThan(50));
            Assert.AreEqual("part", ItemDefs.Category(defs, "scrap_metal"));
            Assert.AreEqual(5.0, ItemDefs.WeightEach(defs, "scrap_metal"));
            Assert.AreEqual(20L, ItemDefs.MaxStack(defs, "scrap_metal"));
            Assert.AreEqual("", ItemDefs.Icon(defs, "scrap_metal"), "icon default is empty (cargo_move_item_smoke)");
            Assert.AreEqual("tool", ItemDefs.Category(defs, "portable_oxygen_pump"), "tool defs get the synthetic category");
            Assert.AreEqual("legendary", ItemDefs.Rarity(defs, "captains_black_box"));
        }

        [Test]
        public void UnknownItemsFallBackLikeGodot()
        {
            GdDict defs = ItemDefs.LoadDefinitions();
            Assert.AreEqual(0.0, ItemDefs.WeightEach(defs, "nonexistent_id"));
            Assert.AreEqual(99L, ItemDefs.MaxStack(defs, "nonexistent_id"));
            Assert.AreEqual("common", ItemDefs.Rarity(defs, "nonexistent_id"));
            // replace("_", " ").capitalize(), including Godot's letter->digit word split.
            Assert.AreEqual("Nonexistent Id", ItemDefs.DisplayName(defs, "nonexistent_id"));
            Assert.AreEqual("Mk 2 Rifle", ItemDefs.DisplayName(defs, "mk2_rifle"));
            Assert.AreEqual("Plasma Core", ItemDefs.DisplayName(defs, "plasmaCore"));
        }
    }
}
