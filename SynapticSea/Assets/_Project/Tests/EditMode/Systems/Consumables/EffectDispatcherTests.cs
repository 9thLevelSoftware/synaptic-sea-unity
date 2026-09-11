using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class EffectDispatcherTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void SummaryRoundTrips()
        {
            var dispatcher = new EffectDispatcher();
            dispatcher.Configure();
            GdDict summary = dispatcher.GetSummary();
            Assert.That(dispatcher.EffectDefinitions.Count, Is.GreaterThan(5));
            var fresh = new EffectDispatcher();
            fresh.Configure();
            // get_summary carries no effect_definitions, so apply_summary rejects it (GDScript parity).
            Assert.IsFalse(fresh.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, fresh.GetSummary()));
        }

        [Test]
        public void DispatchesIntoContextTargets()
        {
            var dispatcher = new EffectDispatcher();
            dispatcher.Configure();
            var vitals = new FakeVitals { Health = 50.0 };
            var sanity = new FakeSanity { Sanity = 40.0 };
            var radiation = new FakeRadiation { Radiation = 60.0 };
            var statuses = new FakeStatusEffects();
            statuses.AddEffect("radiation_sickness", 10.0, 1);
            var context = new Dictionary<string, object>
            {
                { "vitals_state", vitals },
                { "sanity_state", sanity },
                { "radiation_state", radiation },
                { "status_effects_state", statuses },
            };
            Assert.IsTrue(V.Bool(dispatcher.DispatchEffect("heal_small", context)["ok"]));
            Assert.AreEqual(68.0, vitals.Health, 0.001);
            Assert.IsTrue(V.Bool(dispatcher.DispatchEffect("stim_focus", context)["ok"]));
            Assert.IsTrue(statuses.HasEffect("stim_focus"));
            dispatcher.DispatchEffect("restore_sanity_small", context);
            dispatcher.DispatchEffect("reduce_radiation_minor", context);
            Assert.AreEqual(48.0, sanity.Sanity, 0.001);
            Assert.AreEqual(40.0, radiation.Radiation, 0.001);
            GdDict cure = dispatcher.DispatchEffect("cure_radiation_sickness", context);
            Assert.IsFalse(statuses.HasEffect("radiation_sickness"));
            Assert.IsTrue(V.VariantEquals(GdArray.Of("radiation_sickness"), cure["cured"]));

            GdDict missing = dispatcher.DispatchEffect("heal_small", new Dictionary<string, object>());
            Assert.AreEqual("missing_target", missing["reason"]);
            // effect_definitions.get(id, {}) yields a Dictionary, so an unknown id falls through to kind "" (GDScript parity).
            Assert.AreEqual("unsupported_kind", dispatcher.DispatchEffect("no_such_effect", context)["reason"]);
        }
    }
}
