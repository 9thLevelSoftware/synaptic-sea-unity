using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class SpoilageStateTests
    {
        static GdDict RationConfig() => new GdDict
        {
            { "display_name", "Ration Pack" },
            { "hunger_restore", 15.0 },
            { "thirst_restore", 5.0 },
            { "sanity_restore", 2.0 },
            { "spoilage_seconds", 100.0 },
            { "fresh_multiplier", 1.0 },
            { "stale_multiplier", 0.6 },
            { "rotten_multiplier", 0.2 },
            { "rotten_sickness_risk", 0.25 },
        };

        sealed class FakeSanity : EffectDispatcher.ISanityTarget
        {
            public double Total;
            public double AdjustSanity(double amount) => Total += amount;
            public GdDict GetSummary() => new GdDict();
        }

        [Test]
        public void Summary_RoundTripsOntoInstanceTrackingTheSameFoods()
        {
            var model = new SpoilageState();
            model.AddFood("ration_pack", RationConfig());
            model.Tick(60.0);
            GdDict summary = model.GetSummary();

            // A bare instance restores foods through FoodState.apply_summary, which (as in Godot) does not carry every
            // field (display_name and the multipliers fall back); round-trip onto one tracking the same food instead.
            var restored = new SpoilageState();
            restored.AddFood("ration_pack", RationConfig());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()), restored.GetSummary().ToString());
        }

        [Test]
        public void StagesAdvanceAndReportTransitions()
        {
            var model = new SpoilageState();
            model.AddFood("ration_pack", RationConfig());
            Assert.AreEqual(0L, model.Tick(10.0));
            Assert.AreEqual(1L, model.Tick(45.0)); // 55% -> STALE
            Assert.AreEqual(1L, model.GetFoodCountByStage((long)FoodState.Stage.STALE));
            Assert.AreEqual(1L, model.Tick(50.0)); // 105% -> ROTTEN
            Assert.IsTrue(model.GetAnyRotten());
            Assert.IsTrue(model.GetSummary().GetBool("rotten_present"));
        }

        [Test]
        public void EatAppliesStageScaledRestores_AndPlugsIntoTravelPlanner()
        {
            var model = new SpoilageState();
            model.AddFood("ration_pack", RationConfig());
            model.Tick(60.0); // STALE (x0.6)
            Assert.AreEqual(9.0 / 12.0, model.TravelRangeDays(), 1e-9);
            var vitals = new VitalsState();
            vitals.Configure(new GdDict { { "hunger", 50.0 }, { "thirst", 50.0 } });
            var sanity = new FakeSanity();
            GdDict result = model.Eat("ration_pack", null, vitals, sanity);
            Assert.IsTrue(result.GetBool("ok"));
            Assert.AreEqual(1L, result.Get("stage"));
            Assert.AreEqual(59.0, vitals.Hunger, 1e-9);
            Assert.AreEqual(53.0, vitals.Thirst, 1e-9);
            Assert.AreEqual(1.2, sanity.Total, 1e-9);
            Assert.IsFalse(model.HasFood("ration_pack"), "no inventory stacks left -> tracking dropped");
            Assert.AreEqual("not_tracked", model.Eat("ration_pack").GetString("reason"));

            Assert.IsTrue(FoodTravelPlanner.RegisterHarvest(model, "hydroponic_greens"));
            Assert.IsTrue(model.HasFood("hydroponic_greens"));
        }
    }

    public class SustenanceStateTests
    {
        static GdDict TickContext() => new GdDict
        {
            { "powered_ratio", 1.0 },
            { "hydroponics_summary", new GdDict { { "state", 2L }, { "power_cost", 3.0 }, { "water_cost", 2.0 } } },
            { "water_recycler_summary", new GdDict { { "power_cost", 2.0 }, { "input_quantity", 0L }, { "output_ready", 4L } } },
            { "meals_active", true },
        };

        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var state = new SustenanceState();
            state.Configure(new GdDict { { "facilities", new GdDict { { "hydroponics", new GdDict() }, { "synthesizer", new GdDict() }, { "water_recycler", new GdDict() } } } });
            state.Tick(1.0, TickContext());
            GdDict snap = state.GetSummary();

            var restored = new SustenanceState();
            restored.Configure(new GdDict());
            Assert.IsTrue(restored.ApplySummary(snap));
            Assert.IsTrue(V.VariantEquals(snap, restored.GetSummary()), restored.GetSummary().ToString());
            Assert.AreEqual(4L, restored.PurifiedWaterReady);
        }

        [Test]
        public void RollsUpFacilityOutputs_AndUnpoweredConsumesNoPower()
        {
            var state = new SustenanceState();
            state.Configure(new GdDict());
            state.Tick(1.0, TickContext());
            Assert.AreEqual(1L, state.HarvestReady);
            Assert.AreEqual(1L, state.MealsReady);
            Assert.AreEqual(4L, state.PurifiedWaterReady);
            Assert.AreEqual(5.0, state.TotalPowerConsumed);
            Assert.AreEqual(2.0, state.TotalMaterialsConsumed);

            var ctx = TickContext();
            ctx["powered_ratio"] = 0.2;
            state.Tick(1.0, ctx);
            Assert.AreEqual(0.0, state.TotalPowerConsumed);
        }
    }
}
