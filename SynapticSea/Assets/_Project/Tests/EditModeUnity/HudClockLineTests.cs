using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;

namespace SynapticSea.Tests.Unity
{
    /// <summary>Phase 1.1b: the HUD clock line and the launch-request pacing defaults.</summary>
    public class HudClockLineTests
    {
        [Test]
        public void ClockLineShowsTheGameTime_AndIsHiddenWhenEmpty()
        {
            var hud = new HudVitalsCluster();
            Assert.AreEqual("", hud.ClockLine, "hidden until a scaled clock reports a time");
            hud.SetClockLine("Day 2  14:05");
            Assert.AreEqual("Day 2  14:05", hud.ClockLine);
            hud.SetClockLine("");
            Assert.AreEqual("", hud.ClockLine);
        }

        [Test]
        public void TitleNewRunDefaultsToSixtyX_ButTheBareFactoryAndContinueStayRealTime()
        {
            Assert.AreEqual(WorldClock.DefaultNewRunScale, RunLaunchRequest.NewRun(5, "breach_field", "standard").TimeScale);
            Assert.AreEqual(30.0, RunLaunchRequest.NewRun(5, "breach_field", "standard", 30.0).TimeScale);
            Assert.AreEqual(WorldClock.DefaultScale, RunLaunchRequest.NewRun().TimeScale, "bare NewRun() (tests and fallbacks) keeps real-time pacing");
            Assert.AreEqual(WorldClock.DefaultScale, RunLaunchRequest.ContinueWorld().TimeScale, "Continue takes the pacing from the save");
        }
    }
}
