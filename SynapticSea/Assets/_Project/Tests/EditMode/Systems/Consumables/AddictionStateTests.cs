using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class AddictionStateTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        static readonly GdDict Definition = new GdDict
        {
            { "tolerance_gain", 0.35 },
            { "dependence_gain", 0.55 },
            { "withdrawal_duration", 8.0 },
            { "withdrawal_effects", GdArray.Of("withdrawal_shakes", "withdrawal_fatigue") },
        };

        [Test]
        public void SummaryRoundTrips()
        {
            var addiction = new AddictionState();
            addiction.Configure();
            addiction.RecordDose("combat_stim", Definition);
            addiction.RecordDose("combat_stim", Definition);
            addiction.ActivateWithdrawalIfNeeded("combat_stim", new FakeStatusEffects());
            GdDict summary = addiction.GetSummary();
            var restored = new AddictionState();
            restored.Configure();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.AreEqual(0.70, restored.GetTolerance("combat_stim"), 1e-9);
            Assert.IsTrue(restored.HasWithdrawal());
        }

        [Test]
        public void WithdrawalStartsAndClears()
        {
            var addiction = new AddictionState();
            addiction.Configure();
            var statuses = new FakeStatusEffects();
            addiction.RecordDose("combat_stim", Definition);
            Assert.AreEqual("below_threshold", addiction.ActivateWithdrawalIfNeeded("combat_stim", statuses)["reason"]);
            addiction.RecordDose("combat_stim", Definition);
            GdDict triggered = addiction.ActivateWithdrawalIfNeeded("combat_stim", statuses);
            Assert.IsTrue(V.Bool(triggered["ok"]));
            Assert.AreEqual(32.0, triggered["duration"], "tuning withdrawal_duration wins over the dose's 8s");
            Assert.IsTrue(statuses.HasEffect("withdrawal_shakes") && statuses.HasEffect("withdrawal_fatigue"));
            Assert.IsTrue(addiction.Tick(40.0, statuses));
            Assert.IsFalse(addiction.HasWithdrawal());
            Assert.IsFalse(statuses.HasEffect("withdrawal_shakes"));
        }
    }
}
