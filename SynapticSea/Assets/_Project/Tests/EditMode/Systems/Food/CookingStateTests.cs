using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class CookingStateTests
    {
        static GdDict Config() => new GdDict
        {
            { "recipe_id", "cook_ration" },
            { "display_name", "Cooked Ration" },
            { "ingredients", new GdDict { { "ration_pack", 2.0 }, { "purified_water", 1.0 } } },
            { "produces", new GdDict { { "item_id", "cooked_meal" }, { "quantity", 1.0 } } },
            { "power_cost", 4.0 },
            { "cook_time_seconds", 20.0 },
            { "required_skill_level", 1.0 },
        };

        [Test]
        public void SummaryRoundTrips()
        {
            var cooking = new CookingState();
            cooking.Configure(Config());
            cooking.StartCooking(new GdDict { { "items", new GdDict { { "ration_pack", 2L }, { "purified_water", 1L } } } }, 1, 10.0);
            cooking.Tick(5.0);
            GdDict summary = cooking.GetSummary();
            // ingredients are not restored by apply_summary; configure from the same recipe first.
            var restored = new CookingState();
            restored.Configure(Config());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void CooksAndCollects()
        {
            var cooking = new CookingState();
            cooking.Configure(Config());
            Assert.AreEqual("insufficient_skill", cooking.StartCooking(new GdDict(), 0, 10.0)["reason"]);
            Assert.AreEqual("missing_ingredient_ration_pack",
                cooking.StartCooking(new GdDict { { "ration_pack", 1L } }, 1, 10.0)["reason"]);
            GdDict started = cooking.StartCooking(new GdDict { { "items", new GdDict { { "ration_pack", 2L }, { "purified_water", 1L } } } }, 1, 10.0);
            Assert.IsTrue(V.Bool(started["ok"]));
            Assert.IsFalse(cooking.Tick(19.0));
            Assert.IsTrue(cooking.Tick(1.0));
            GdDict result = cooking.CollectResult();
            Assert.AreEqual("cooked_meal", result["item_id"]);
            Assert.AreEqual(1L, result["quantity"]);
            Assert.AreEqual((long)CookingState.State.IDLE, cooking.CurrentState);
        }
    }
}
