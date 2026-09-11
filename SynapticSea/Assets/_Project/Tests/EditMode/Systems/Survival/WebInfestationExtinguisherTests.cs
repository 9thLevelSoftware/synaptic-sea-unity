using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class WebInfestationStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var w = new WebInfestationState();
            w.Configure(new GdDict { { "seed_coverage", 0.42 } });
            w.CutFree();
            GdDict summary = w.GetSummary();

            var restored = new WebInfestationState();
            restored.Configure(new GdDict());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsFalse(restored.AttachedToWeb);
            Assert.IsFalse(restored.ApplySummary(new GdDict { { "hazard_kind", "not_web" }, { "coverage", 0.9 } }));
        }

        [Test]
        public void GrowsAndDamagesWhileAttached_RecedesWhenCut()
        {
            var w = new WebInfestationState();
            w.Configure(new GdDict());
            double dmg = 0.0;
            for (int i = 0; i < 50; i++) dmg += w.Tick(1.0, false);
            Assert.That(w.Coverage, Is.GreaterThan(0.5));
            Assert.That(dmg, Is.GreaterThan(0.0));

            var cut = new WebInfestationState();
            cut.Configure(new GdDict { { "seed_coverage", 0.8 } });
            cut.CutFree();
            for (int i = 0; i < 5; i++) cut.Tick(1.0, false);
            Assert.AreEqual(0.55, cut.Coverage, 1e-9);
            CollectionAssert.AreEqual(new[] { "Web Infestation 55% [RECEDING]" }, cut.GetStatusLines());
        }
    }

    public class ExtinguisherStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var e = new ExtinguisherState();
            e.Configure(new GdDict { { "charge", 100.0 }, { "max_charge", 100.0 }, { "charge_cost_per_use", 34.0 }, { "recharge_per_second", 5.0 } });
            e.ConsumeUse();
            GdDict summary = e.GetSummary();

            var restored = new ExtinguisherState();
            restored.Configure(new GdDict { { "charge", 0.0 }, { "max_charge", 100.0 } });
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsFalse(restored.ApplySummary(new GdDict()));
        }

        [Test]
        public void ConsumesUntilShort_RechargeClamps_MaxChargeFloor()
        {
            var e = new ExtinguisherState();
            e.Configure(new GdDict { { "charge", 100.0 }, { "max_charge", 100.0 }, { "charge_cost_per_use", 34.0 }, { "recharge_per_second", 5.0 } });
            Assert.IsTrue(e.ConsumeUse());
            Assert.IsTrue(e.ConsumeUse());
            Assert.IsFalse(e.ConsumeUse());
            Assert.AreEqual(32.0, e.Charge, 0.001);
            e.Recharge(100.0);
            Assert.AreEqual(100.0, e.Charge);
            var small = new ExtinguisherState();
            small.Configure(new GdDict { { "max_charge", 0.5 }, { "charge", 0.5 } });
            Assert.AreEqual(1.0, small.MaxCharge);
        }
    }
}
