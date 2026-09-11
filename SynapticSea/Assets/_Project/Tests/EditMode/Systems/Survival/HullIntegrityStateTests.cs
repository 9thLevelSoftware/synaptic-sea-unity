using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class HullIntegrityStateTests
    {
        static HullIntegrityState Configured()
        {
            var hull = new HullIntegrityState();
            hull.Configure(new GdDict
            {
                {
                    "compartments", GdArray.Of(
                        new GdDict { { "compartment_id", "bow" }, { "health", 0.9 } },
                        new GdDict { { "compartment_id", "stern" }, { "health", 1.4 }, { "isolation_rating", 0.8 } },
                        new GdDict { { "compartment_id", "" } },
                        "not a row")
                },
            });
            return hull;
        }

        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var hull = Configured();
            hull.DamageCompartment("bow", 0.6);
            GdDict summary = hull.GetSummary();

            var restored = new HullIntegrityState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsFalse(restored.ApplySummary(summary), "identical summary reports unchanged");
        }

        [Test]
        public void DamageBreaches_SealRepairs()
        {
            var hull = Configured();
            Assert.AreEqual(2, hull.Compartments.Count);
            Assert.IsTrue(hull.DamageCompartment("bow", 0.5));
            Assert.AreEqual(1, hull.GetBreachCount());
            Assert.AreEqual(0.7, hull.AverageIntegrity(), 1e-9);
            CollectionAssert.AreEqual(new[] { "Hull Integrity 70% breaches=1", "Hull bow BREACHED 40%" }, hull.GetStatusLines());
            Assert.IsTrue(hull.SealCompartment("bow", 0.4));
            Assert.AreEqual(0, hull.GetBreachCount());
            Assert.IsFalse(hull.DamageCompartment("missing", 1.0));
        }
    }
}
