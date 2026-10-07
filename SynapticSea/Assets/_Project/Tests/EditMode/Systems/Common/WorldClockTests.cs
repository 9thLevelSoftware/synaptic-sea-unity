using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class WorldClockTests
    {
        [Test]
        public void DefaultScale_GameTimeEqualsRealTimeExactly()
        {
            var clock = new WorldClock();
            double expected = 0.0;
            for (int i = 0; i < 1000; i++)
            {
                double delta = 0.016 + i % 7 * 0.003;
                expected += delta;
                Assert.AreEqual(delta, clock.Advance(delta), "scale 1.0 must hand back the real delta bit-for-bit");
            }
            Assert.AreEqual(expected, clock.GameSeconds, "no rounding drift at scale 1.0");
            Assert.IsTrue(clock.IsDefaultConfiguration);
        }

        [Test]
        public void ScaledClock_AdvancesGameSecondsPerRealSecond()
        {
            var clock = new WorldClock();
            clock.SetScale(60.0);
            Assert.AreEqual(60.0, clock.Advance(1.0), 1e-12);
            Assert.AreEqual(60.0, clock.GameSeconds, 1e-12);
            Assert.IsFalse(clock.IsDefaultConfiguration);
        }

        [TestCase(0.0)]
        [TestCase(-3.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void InvalidScale_FallsBackToDefault(double scale)
        {
            var clock = new WorldClock();
            clock.SetScale(30.0);
            clock.SetScale(scale);
            Assert.AreEqual(WorldClock.DefaultScale, clock.Scale);
        }

        [Test]
        public void TimeOfDay_FollowsGameSecondsFromTheStartHour()
        {
            var clock = new WorldClock();
            Assert.AreEqual(6.0, clock.HourOfDay, 1e-9);
            Assert.AreEqual(0L, clock.DayIndex);
            Assert.IsFalse(clock.IsNight);
            clock.GameSeconds = 14 * 3600.0;
            Assert.AreEqual(20.0, clock.HourOfDay, 1e-9);
            Assert.IsTrue(clock.IsNight);
            clock.GameSeconds = 18 * 3600.0;
            Assert.AreEqual(0.0, clock.HourOfDay, 1e-9);
            Assert.AreEqual(1L, clock.DayIndex);
            Assert.IsTrue(clock.IsNight);
            clock.GameSeconds = 24 * 3600.0;
            Assert.AreEqual(6.0, clock.HourOfDay, 1e-9);
            Assert.IsFalse(clock.IsNight);
        }

        [Test]
        public void Summary_RoundTripsAndRejectsMalformedInput()
        {
            var clock = new WorldClock { GameSeconds = 1234.5 };
            clock.SetScale(120.0);
            clock.SetStartHour(8.0);
            var restored = new WorldClock();
            Assert.IsTrue(restored.ApplySummary(clock.GetSummary()));
            Assert.AreEqual(1234.5, restored.GameSeconds);
            Assert.AreEqual(120.0, restored.Scale);
            Assert.AreEqual(8.0, restored.StartHourOfDay);

            var untouched = new WorldClock { GameSeconds = 5.0 };
            Assert.IsFalse(untouched.ApplySummary(new GdDict()));
            Assert.IsFalse(untouched.ApplySummary(new GdDict { { "game_seconds", -1.0 }, { "scale", 1.0 } }));
            Assert.IsFalse(untouched.ApplySummary(new GdDict { { "game_seconds", 1.0 }, { "scale", 0.0 } }));
            Assert.AreEqual(5.0, untouched.GameSeconds, "a rejected summary leaves the clock unchanged");
        }
    }
}
