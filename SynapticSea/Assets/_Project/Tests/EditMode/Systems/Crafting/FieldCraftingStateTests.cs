using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class FieldCraftingStateTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        static InventoryState BandageInputs()
        {
            var inv = new InventoryState();
            inv.AddItem("synth_fiber", 5);
            inv.AddItem("medical_gauze", 2);
            return inv;
        }

        [Test]
        public void SummaryRoundTrips()
        {
            var field = new FieldCraftingState();
            var mat = new MaterialState();
            mat.SetQuality("synth_fiber", 0.6);
            Assert.IsTrue(field.BeginCraft("field_bandage", BandageInputs(), mat, 0));
            field.Tick(3.0);
            GdDict summary = field.GetSummary();
            var restored = new FieldCraftingState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.AreEqual("field_bandage", restored.GetActiveRecipeId());
        }

        [Test]
        public void FieldRecipesOnly_AndCraftCompletes()
        {
            var field = new FieldCraftingState();
            Assert.That(field.GetFieldRecipes().Count, Is.GreaterThanOrEqualTo(4));
            InventoryState inv = BandageInputs();
            Assert.IsTrue(field.CanCraft("field_bandage", inv));
            Assert.IsFalse(field.CanCraft("craft_power_cell", inv), "non-field recipe should be rejected");
            Assert.IsFalse(field.BeginCraft("craft_power_cell", inv, new MaterialState(), 0));
            Assert.AreEqual("field_bandage", field.FirstReadyRecipeId(inv));

            var mat = new MaterialState();
            mat.SetQuality("synth_fiber", 0.6);
            Assert.IsTrue(field.BeginCraft("field_bandage", inv, mat, 0));
            Assert.IsTrue(field.IsCrafting());
            Assert.AreEqual(3, inv.GetQuantity("synth_fiber"));
            Assert.IsTrue(field.Tick(8.0));
            GdDict result = field.FinishCraft();
            Assert.AreEqual("field_bandage", result.GetString("item_id"));
            Assert.AreEqual(1L, result["quantity"]);
            Assert.AreEqual("field_crafting", result.GetString("station_kind"));
            Assert.IsFalse(field.IsCrafting());
        }
    }
}
