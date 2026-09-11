using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ConsumableStateTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        sealed class FakeSpoilage : ConsumableState.ISpoilageFoodSource
        {
            public readonly Dictionary<string, FoodState> Foods = new Dictionary<string, FoodState>();
            public FoodState GetFood(string itemId) => Foods.TryGetValue(itemId, out FoodState f) ? f : null;
        }

        static Dictionary<string, object> Pipeline(FakeVitals vitals, FakeSanity sanity, FakeStatusEffects statuses)
        {
            var dispatcher = new EffectDispatcher();
            dispatcher.Configure();
            var medicine = new MedicineState();
            medicine.Configure();
            var stimulant = new StimulantState();
            stimulant.Configure();
            var addiction = new AddictionState();
            addiction.Configure(new GdDict());
            var utility = new UtilityItemResolver();
            utility.Configure();
            return new Dictionary<string, object>
            {
                { "effect_dispatcher", dispatcher },
                { "medicine_state", medicine },
                { "stimulant_state", stimulant },
                { "addiction_state", addiction },
                { "utility_state", utility },
                { "vitals_state", vitals },
                { "sanity_state", sanity },
                { "status_effects_state", statuses },
            };
        }

        [Test]
        public void HotbarUseAndRoundTrip()
        {
            var inventory = new InventoryState();
            inventory.AddItem("bandage_kit", 1);
            inventory.AddItem("focus_ampoule", 1);
            inventory.AddItem("flare", 1);
            var vitals = new FakeVitals { Health = 60.0, Stamina = 20.0 };
            var statuses = new FakeStatusEffects();
            var context = Pipeline(vitals, new FakeSanity { Sanity = 50.0 }, statuses);
            var consumable = new ConsumableState();
            consumable.Configure();
            Assert.IsTrue(consumable.AssignHotbarSlot(0, "bandage_kit"));
            Assert.IsTrue(consumable.AssignHotbarSlot(1, "focus_ampoule"));
            Assert.IsFalse(consumable.AssignHotbarSlot(2, "scrap_metal"), "parts have no use action");
            Assert.IsFalse(consumable.AssignHotbarSlot(3, "bandage_kit"), "slot out of range");

            GdDict med = consumable.UseHotbarSlot(0, inventory, context);
            Assert.AreEqual(true, med["ok"]);
            Assert.AreEqual(0L, inventory.GetQuantity("bandage_kit"));
            Assert.AreEqual(78.0, vitals.Health, 0.001);
            GdDict stim = consumable.UseHotbarSlot(1, inventory, context);
            Assert.AreEqual(true, stim["ok"]);
            Assert.IsTrue(statuses.HasEffect("stim_focus"));
            GdDict flare = consumable.UseItem("flare", inventory, context);
            Assert.AreEqual(true, flare["ok"]);
            Assert.IsTrue(((UtilityItemResolver)context["utility_state"]).ActiveFlags.Has("flare"));
            Assert.AreEqual("missing_quantity", consumable.UseItem("flare", inventory, context)["reason"]);

            GdDict summary = consumable.GetSummary();
            var restored = new ConsumableState();
            restored.Configure();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.AreEqual("focus_ampoule", restored.HotbarSlots[1]);
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void FoodRestoresHonourTrackedSpoilageStage()
        {
            var inventory = new InventoryState();
            inventory.AddItem("hydroponic_greens", 3);
            var vitals = new FakeVitals { Hunger = 50.0, Thirst = 50.0 };
            var sanity = new FakeSanity { Sanity = 50.0 };
            var context = Pipeline(vitals, sanity, new FakeStatusEffects());
            var consumable = new ConsumableState();
            consumable.Configure();

            GdDict fresh = consumable.UseItem("hydroponic_greens", inventory, context);
            Assert.AreEqual(12.0, ((GdDict)((GdArray)fresh["results"])[0])["hunger_restored"]);
            Assert.AreEqual(62.0, vitals.Hunger, 0.0001);
            Assert.AreEqual(51.0, sanity.Sanity, 0.0001);

            var spoilage = new FakeSpoilage();
            spoilage.Foods["hydroponic_greens"] = new FoodState { CurrentStage = (long)FoodState.Stage.STALE };
            context["spoilage_state"] = spoilage;
            GdDict stale = consumable.UseItem("hydroponic_greens", inventory, context, true);
            Assert.AreEqual(2L, stale["used"], "use_all consumes the whole stack");
            var first = (GdDict)((GdArray)stale["results"])[0];
            Assert.AreEqual(12.0 * 0.6, V.F64(first["hunger_restored"]), 1e-9, "stale multiplier applied");
            Assert.AreEqual(0L, inventory.GetQuantity("hydroponic_greens"));
        }
    }
}
