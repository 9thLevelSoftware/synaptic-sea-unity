using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class StatusEffectsStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var state = new StatusEffectsState();
            state.Configure();
            state.AddEffect("bleed", 5.0, 1);
            state.AddEffect("burn", 3.0, 2);
            GdDict summary = state.GetSummary();

            var restored = new StatusEffectsState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsFalse(restored.ApplySummary(summary), "identical effects report unchanged");
        }

        [Test]
        public void StacksTickRemoveAndExpire()
        {
            var state = new StatusEffectsState();
            state.Configure(new GdDict());
            Assert.IsTrue(state.AddEffect("bleed", 5.0, 1));
            Assert.IsTrue(state.AddEffect("burn", 3.0, 2));
            Assert.AreEqual(2, state.GetStacks("burn"));
            state.Tick(1.5);
            Assert.AreEqual(2L, state.GetSummary()["count"]);
            Assert.IsTrue(state.RemoveEffect("burn", 1));
            Assert.AreEqual(1, state.GetStacks("burn"));
            CollectionAssert.AreEqual(new[] { "Status: bleed x1 (3.5s)", "Status: burn x1 (1.5s)" }, state.GetStatusLines());
            state.Tick(4.0);
            Assert.IsFalse(state.HasEffect("bleed") || state.HasEffect("burn"));

            state.AddEffect("radiation_sickness", 2.0);
            state.AddEffect("stim_haste", 2.0);
            Assert.AreEqual(0.75 * 1.5, state.GetModifier("stamina_recovery"), 1e-12);
        }
    }
}
