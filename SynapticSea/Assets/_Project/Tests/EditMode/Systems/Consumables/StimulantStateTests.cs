using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class StimulantStateTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        static readonly GdDict Definition = new GdDict
        {
            { "effects", GdArray.Of("stim_haste", "restore_stamina_large") },
            { "withdrawal_effects", GdArray.Of("withdrawal_shakes", "withdrawal_fatigue") },
            { "stim_duration", 2.0 },
            { "withdrawal_duration", 3.0 },
            { "tolerance_gain", 0.5 },
            { "dependence_gain", 2.0 },
        };

        [Test]
        public void StimExpiresIntoWithdrawalAndRoundTrips()
        {
            var dispatcher = new EffectDispatcher();
            dispatcher.Configure();
            var stimulant = new StimulantState();
            stimulant.Configure();
            var addiction = new AddictionState();
            addiction.Configure();
            var statuses = new FakeStatusEffects();
            var vitals = new FakeVitals { Stamina = 10.0 };
            var context = new Dictionary<string, object> { { "status_effects_state", statuses }, { "vitals_state", vitals } };

            GdDict used = stimulant.UseStimulant("combat_stim", Definition, dispatcher, addiction, context);
            Assert.IsTrue(V.Bool(used["ok"]));
            Assert.IsTrue(stimulant.HasActiveStim("combat_stim"));
            Assert.IsTrue(statuses.HasEffect("stim_haste"));
            Assert.AreEqual(50.0, vitals.Stamina, 0.001);

            GdDict summary = stimulant.GetSummary();
            var restored = new StimulantState();
            restored.Configure();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));

            Assert.IsTrue(stimulant.Tick(2.1, addiction, context));
            Assert.IsFalse(stimulant.HasActiveStim("combat_stim"));
            Assert.IsTrue(addiction.HasWithdrawal());
            Assert.IsTrue(statuses.HasEffect("withdrawal_shakes"));
        }
    }
}
