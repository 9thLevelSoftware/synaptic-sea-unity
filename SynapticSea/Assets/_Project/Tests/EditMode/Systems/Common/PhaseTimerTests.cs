using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class PhaseTimerTests
    {
        [Test]
        public void ClampsDurations_IncludingNonNumeric()
        {
            var t = new PhaseTimer();
            t.Configure(new GdDict { { "A", 0.0 }, { "B", -5.0 } });
            Assert.AreEqual(PhaseTimer.Phase.A, t.CurrentPhase());
            Assert.AreEqual(PhaseTimer.MINIMUM_PHASE_DURATION, t.CurrentPhaseDuration());
            Assert.IsTrue(t.Tick(PhaseTimer.MINIMUM_PHASE_DURATION));
            Assert.AreEqual(PhaseTimer.Phase.B, t.CurrentPhase());

            var nonNumeric = new PhaseTimer();
            nonNumeric.Configure(new GdDict { { "A", "fast" }, { "B", null } });
            Assert.AreEqual(PhaseTimer.MINIMUM_PHASE_DURATION, nonNumeric.CurrentPhaseDuration());
        }

        [Test]
        public void FlipsOncePerTick_CarriesRemainder_CapsProgress()
        {
            var b = new PhaseTimer();
            b.Configure(new GdDict { { "A", 2.0 }, { "B", 3.0 } });
            Assert.IsFalse(b.Tick(1.9));
            Assert.IsTrue(b.Tick(0.1));
            Assert.AreEqual(PhaseTimer.Phase.B, b.CurrentPhase());
            Assert.AreEqual(0.0, b.GetTimeInPhase(), 0.0001);

            var s = new PhaseTimer();
            s.Configure(new GdDict { { "A", 1.0 }, { "B", 1.0 } });
            s.Tick(10.0);
            Assert.AreEqual(PhaseTimer.Phase.B, s.CurrentPhase());
            Assert.AreEqual(9.0, s.GetTimeInPhase(), 0.0001);
            Assert.IsFalse(s.Tick(0.0) || s.Tick(-1.0));

            var p = new PhaseTimer();
            p.Configure(new GdDict { { "A", 4.0 }, { "B", 1.0 } });
            Assert.AreEqual(0.0, p.NormalizedProgress());
            p.Tick(1.0);
            Assert.AreEqual(0.25, p.NormalizedProgress(), 0.0001);
            p.Tick(5.0);
            Assert.AreEqual(1.0, p.NormalizedProgress());
        }
    }
}
