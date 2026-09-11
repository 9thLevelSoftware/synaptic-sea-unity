using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class UtilityItemResolverTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        static readonly GdDict Lockpick = new GdDict
        {
            { "effects", GdArray.Of("utility_lockpick") },
            { "utility_flag", "lockpick" },
            { "use_note", "Picks one sealed hatch." },
        };

        [Test]
        public void FlagChargesStackAndConsume()
        {
            var dispatcher = new EffectDispatcher();
            dispatcher.Configure();
            var resolver = new UtilityItemResolver();
            resolver.Configure();
            var statuses = new FakeStatusEffects();
            var context = new Dictionary<string, object> { { "status_effects_state", statuses } };
            resolver.UseItem("lockpick", Lockpick, dispatcher, context);
            resolver.UseItem("lockpick", Lockpick, dispatcher, context);
            Assert.AreEqual(2L, ((GdDict)resolver.ActiveFlags["lockpick"])["count"]);
            Assert.IsTrue(statuses.HasEffect("utility_lockpick_ready"));

            GdDict summary = resolver.GetSummary();
            var restored = new UtilityItemResolver();
            restored.Configure();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));

            Assert.IsFalse(resolver.ConsumeFlag("lockpick"), "a charge remains");
            Assert.IsTrue(resolver.ConsumeFlag("lockpick"), "last charge erases the flag");
            Assert.IsFalse(resolver.ActiveFlags.Has("lockpick"));
        }
    }
}
