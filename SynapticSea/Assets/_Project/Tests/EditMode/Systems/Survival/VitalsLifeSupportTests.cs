using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class VitalsStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var model = new VitalsState();
            model.Configure(new GdDict());
            model.Tick(10.0, new GdDict { { "moving", true }, { "fire_health_drain", 2.0 } });
            GdDict summary = model.GetSummary();

            var restored = new VitalsState();
            restored.Configure(new GdDict());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()), restored.GetSummary().ToString());
        }

        [Test]
        public void MovingDrainsStamina_RestRecovers_HazardsDrainHealth()
        {
            var model = new VitalsState();
            model.Configure(new GdDict());
            Assert.IsTrue(model.Tick(1.0, new GdDict { { "moving", true } }));
            Assert.AreEqual(98.0, model.Stamina, 1e-9);
            Assert.AreEqual(99.5, model.Hunger, 1e-9);
            Assert.AreEqual(99.2, model.Thirst, 1e-9);
            model.Tick(1.0, new GdDict { { "moving", false } });
            Assert.AreEqual(100.0, model.Stamina, 1e-9);
            model.Tick(1.0, new GdDict { { "moving", false }, { "radiation_health_drain", 3.0 } });
            Assert.AreEqual(97.0, model.Health, 1e-9);
        }

        [Test]
        public void ImplementsVitalsTargetAndCurvesAreContinuous()
        {
            EffectDispatcher.IVitalsTarget target = new VitalsState();
            GdDict after = target.ApplyDelta(new GdDict { { "health", -150.0 }, { "hunger", -90.0 } });
            Assert.AreEqual(0.0, after.GetFloat("health"));
            Assert.AreEqual(0.0, after.GetFloat("move_mult"), "incapacitated cannot move");
            Assert.IsTrue(after.GetBool("hunger_stamina_cascade_active"));
            Assert.AreEqual(1.0, VitalsState.HungerStaminaRecoveryCurve(50.0, 100.0));
            Assert.AreEqual(0.25, VitalsState.HungerStaminaRecoveryCurve(0.0, 100.0));
            Assert.AreEqual(1.75, VitalsState.ThirstStaminaDrainCurve(0.0, 100.0));
        }
    }

    public class LifeSupportStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var model = new LifeSupportState();
            model.Configure(new GdDict { { "oxygen_percent", 100.0 }, { "co2_percent", 2.0 }, { "water_liters", 40.0 } });
            model.Tick(5.0, new GdDict { { "powered_ratio", 0.0 }, { "breach_count", 1L }, { "recycled_water", 0.0 } });
            GdDict summary = model.GetSummary();

            var restored = new LifeSupportState();
            restored.Configure(new GdDict());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()), restored.GetSummary().ToString());
        }

        [Test]
        public void OfflineDegradesAtmosphere_PoweredRecovers_BreachesLeak()
        {
            var model = new LifeSupportState();
            model.Configure(new GdDict());
            model.Tick(5.0, new GdDict { { "powered_ratio", 0.0 }, { "breach_count", 1L } });
            Assert.AreEqual(100.0 - 4.0 * 1.35 * 5.0, model.OxygenPercent, 1e-9);
            Assert.IsFalse(model.IsNominal()); // CO2 climbed to 25.6%
            double before = model.OxygenPercent;
            model.Tick(5.0, new GdDict { { "powered_ratio", 1.0 }, { "breach_count", 0L }, { "recycled_water", 2.0 } });
            Assert.Greater(model.OxygenPercent, before);

            var leaky = new LifeSupportState();
            leaky.Configure(new GdDict());
            leaky.Tick(2.0, new GdDict { { "powered_ratio", 1.0 }, { "breach_count", 3L } });
            Assert.AreEqual(91.0, leaky.OxygenPercent, 1e-9); // min(100, 100 + 4) - 1.5 * 3 * 2
        }

        [Test]
        public void FouledAtmosphereDrainsHealthAndHeatRaisesThirst()
        {
            var model = new LifeSupportState();
            model.Configure(new GdDict { { "oxygen_percent", 25.0 }, { "temperature_c", 37.0 } });
            Assert.AreEqual(2.5, model.GetHealthDrainPerSecond(), 1e-9);
            Assert.AreEqual(1.5, model.GetThirstMultiplier(), 1e-9);
            model.Configure(new GdDict());
            Assert.AreEqual(0.0, model.GetHealthDrainPerSecond());
            Assert.AreEqual(1.0, model.GetThirstMultiplier());
        }
    }
}
