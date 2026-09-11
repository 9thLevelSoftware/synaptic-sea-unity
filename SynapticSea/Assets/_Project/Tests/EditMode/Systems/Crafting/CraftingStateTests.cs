using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class CraftingStateTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        static InventoryState PowerCellInputs()
        {
            var inv = new InventoryState();
            inv.AddItem("scrap_metal", 5);
            inv.AddItem("wiring_bundle", 5);
            inv.AddItem("reactive_gel", 2);
            return inv;
        }

        [Test]
        public void SummaryRoundTrips()
        {
            var crafting = new CraftingState();
            Assert.That(crafting.RecipeCount(), Is.GreaterThanOrEqualTo(50));
            var inv = PowerCellInputs();
            Assert.IsTrue(crafting.BeginCraft("craft_power_cell", inv, new MaterialState(), 2));
            crafting.Tick(10.0);
            GdDict summary = crafting.GetSummary();
            var restored = new CraftingState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.AreEqual("craft_power_cell", restored.GetActiveRecipeId());
        }

        [Test]
        public void CraftConsumesIngredientsAndProducesOutput()
        {
            var crafting = new CraftingState();
            Assert.AreEqual("fabricator", crafting.GetStationKind("craft_power_cell"));
            Assert.AreEqual(2L, crafting.GetRequiredSkillLevel("craft_power_cell"));
            Assert.AreEqual(30.0, crafting.GetCraftTime("craft_power_cell"));
            var inv = PowerCellInputs();
            Assert.IsTrue(crafting.CanCraft("craft_power_cell", inv));
            var mat = new MaterialState();
            mat.SetQuality("scrap_metal", 0.8);
            mat.SetQuality("wiring_bundle", 0.6);
            mat.SetQuality("reactive_gel", 0.7);
            Assert.IsFalse(crafting.BeginCraft("craft_power_cell", inv, mat, 1), "skill gate rejects under-skilled");
            Assert.AreEqual(5L, inv.GetQuantity("scrap_metal"), "rejected craft consumes nothing");
            Assert.IsTrue(crafting.BeginCraft("craft_power_cell", inv, mat, 2));
            Assert.IsTrue(crafting.IsCrafting());
            Assert.AreEqual(4L, inv.GetQuantity("scrap_metal"));
            Assert.AreEqual(3L, inv.GetQuantity("wiring_bundle"));
            Assert.AreEqual(1L, inv.GetQuantity("reactive_gel"));
            Assert.IsTrue(crafting.Tick(30.0));
            GdDict result = crafting.FinishCraft();
            Assert.AreEqual("power_cell", result["item_id"]);
            Assert.AreEqual(1L, result["quantity"]);
            Assert.IsInstanceOf<double>(result["quality_multiplier"]);
            Assert.That((double)result["quality_multiplier"], Is.GreaterThan(0.0));
            Assert.IsFalse(crafting.IsCrafting());
        }

        [Test]
        public void RecipeListingStatusesAndTierDerivation()
        {
            var crafting = new CraftingState();
            GdArray entries = crafting.ListRecipeEntries("fabricator", PowerCellInputs(), 2);
            Assert.That(entries.Count, Is.GreaterThan(0));
            string prev = "";
            foreach (object o in entries)
            {
                var e = (GdDict)o;
                string rid = V.Str(e["recipe_id"]);
                Assert.IsTrue(GdString.Less(prev, rid) || prev == "", "sorted by recipe_id");
                prev = rid;
                if (rid == "craft_power_cell") Assert.AreEqual("ready", e["status"]);
            }
            GdArray lowSkill = crafting.ListRecipeEntries("fabricator", PowerCellInputs(), 0);
            foreach (object o in lowSkill)
                if (V.Str(((GdDict)o)["recipe_id"]) == "craft_power_cell")
                    Assert.AreEqual("insufficient_skill", ((GdDict)o)["status"]);

            var placed = GdArray.Of(
                new GdDict { { "station_tier_bonus", 2L }, { "station_affinity", "fabricator" } },
                new GdDict { { "station_tier_bonus", 3L }, { "station_affinity", "kitchen" } },
                new GdDict { { "station_tier_bonus", 4L }, { "mounted", false } });
            Assert.AreEqual(2L, CraftingState.DeriveTierFromComponents("fabricator", placed));
            Assert.AreEqual(2L, crafting.RefreshStationTier("fabricator", placed));
        }
    }
}
