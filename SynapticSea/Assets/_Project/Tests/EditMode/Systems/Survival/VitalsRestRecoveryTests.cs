using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>Phase 1.3 passive healing: a resting, fed survivor with no meaningful health drain heals; legacy (rate 0) is untouched.</summary>
    public class VitalsRestRecoveryTests
    {
        static VitalsState Hurt(double rate)
        {
            var vitals = new VitalsState();
            vitals.Configure(new GdDict());
            vitals.Health = 50.0;
            vitals.RestRecoveryRate = rate;
            return vitals;
        }

        static GdDict Rest() => new GdDict { { "moving", false } };

        [Test]
        public void RestingFedSurvivorHeals_AtTheConfiguredPerGameSecondRate()
        {
            VitalsState v = Hurt(0.01);
            v.Tick(10.0, new GdDict { { "moving", false }, { SimKeys.GameDelta, 600.0 } });
            Assert.AreEqual(56.0, v.Health, 1e-9);
        }

        [Test]
        public void RateZero_IsTheLegacyNoRecovery()
        {
            VitalsState v = Hurt(0.0);
            v.Tick(10.0, Rest());
            Assert.AreEqual(50.0, v.Health, 1e-9);
        }

        [Test]
        public void MovingBlocksRecovery()
        {
            VitalsState v = Hurt(1.0);
            v.Tick(1.0, new GdDict { { "moving", true } });
            Assert.AreEqual(50.0, v.Health, 1e-9);
        }

        [Test]
        public void HungryOrThirstyBlocksRecovery()
        {
            VitalsState hungry = Hurt(1.0); hungry.Hunger = 30.0;
            hungry.Tick(1.0, Rest());
            Assert.AreEqual(50.0, hungry.Health, 1e-9);
            VitalsState thirsty = Hurt(1.0); thirsty.Thirst = 30.0;
            thirsty.Tick(1.0, Rest());
            Assert.AreEqual(50.0, thirsty.Health, 1e-9);
        }

        [Test]
        public void RealHazardDrainBlocksRecovery_ButATinyBackgroundDrainDoesNot()
        {
            VitalsState burning = Hurt(1.0);
            burning.Tick(1.0, new GdDict { { "moving", false }, { "fire_health_drain", 2.0 } });
            Assert.Less(burning.Health, 50.0, "a fire hurts and nothing heals");
            VitalsState background = Hurt(1.0);
            background.Tick(1.0, new GdDict { { "moving", false }, { "radiation_health_drain", 0.04 } });
            Assert.Greater(background.Health, 50.0, "a drain under the tolerance still allows recovery");
        }

        [Test]
        public void NeverHealsPastMaxOrTheDead()
        {
            VitalsState nearlyFull = Hurt(100.0); nearlyFull.Health = 99.5;
            nearlyFull.Tick(1.0, Rest());
            Assert.AreEqual(nearlyFull.MaxHealth, nearlyFull.Health, 1e-9);
            VitalsState dead = Hurt(100.0); dead.Health = 0.0;
            dead.Tick(1.0, Rest());
            Assert.AreEqual(0.0, dead.Health, 1e-9);
        }

        [Test]
        public void SurvivalTuning_EnablesRecoveryOnlyAtScaledPacing()
        {
            var tuning = SurvivalTuning.FromDict(new GdDict { { "survival", new GdDict { { "health_recovery_per_game_hour", 36.0 } } } });
            var vitals = new VitalsState();
            var realTime = new WorldClock();
            tuning.ApplyTo(vitals, realTime);
            Assert.AreEqual(0.0, vitals.RestRecoveryRate, "real-time pacing keeps the legacy no-recovery behavior");
            var scaled = new WorldClock();
            scaled.SetScale(60.0);
            tuning.ApplyTo(vitals, scaled);
            Assert.AreEqual(36.0 / 3600.0, vitals.RestRecoveryRate, 1e-12);
        }
    }
}
