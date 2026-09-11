using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class RadiationStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var r = new RadiationState();
            r.Configure(new GdDict());
            r.InRadiationZone = true;
            r.Tick(30.0);
            GdDict summary = r.GetSummary();

            var restored = new RadiationState();
            restored.Configure(new GdDict());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void AccumulatesInZone_DrainsHealthAboveHalf_DecaysOutside()
        {
            var r = new RadiationState();
            r.Configure(new GdDict());
            Assert.AreEqual(0.0, r.Radiation);
            r.InRadiationZone = true;
            Assert.IsTrue(r.Tick(1.0));
            Assert.AreEqual(2.0, r.Radiation);
            r.Radiation = 60.0;
            Assert.AreEqual(1.0, r.GetHealthDrainPerSecond());
            r.InRadiationZone = false;
            r.Tick(1.0);
            Assert.AreEqual(59.5, r.Radiation);
            CollectionAssert.AreEqual(new[] { "Radiation: 60% CRITICAL", "RADIATION SICKNESS -> health drain" }, r.GetStatusLines());
        }
    }

    public class SanityStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var s = new SanityState();
            s.Configure(new GdDict { { "sanity", 35.0 } });
            GdDict summary = s.GetSummary();

            var restored = new SanityState();
            restored.Configure(new GdDict());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void DrainsOutsideSafeZone_RecoversInside_PressureBelowForty()
        {
            var s = new SanityState();
            s.Configure(new GdDict());
            Assert.AreEqual(100.0, s.Sanity);
            s.Tick(1.0);
            Assert.AreEqual(98.5, s.Sanity);
            s.Sanity = 50.0;
            s.InSafeZone = true;
            s.Tick(1.0);
            Assert.AreEqual(53.0, s.Sanity);
            s.Sanity = 35.0;
            Assert.IsTrue(V.Bool(s.GetSummary()["perception_pressure_active"]));
            CollectionAssert.AreEqual(new[] { "Sanity: 35% CRITICAL", "PERCEPTION PRESSURE -> hallucination risk" }, s.GetStatusLines());
        }
    }
}
