using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// FP-13 / SC-4: with game playability knobs, an idle player on golden coherent_ship_001 stays capable
    /// for 30 simulated minutes. GoldenDeps zeros HomeSpawnSafety so the Godot harness still dies at 29.25 s.
    /// </summary>
    public class HomeSpawnSafetyTests
    {
        const double Step = 0.25;
        const double CapableSeconds = 1800.0;

        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Engine = _previousEngine;
        }

        static SessionHarness.Rig BootGameGolden()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            rig.Session = RunSession.Create(deps);
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            return rig;
        }

        [Test]
        public void GameDeps_GoldenHub_DoesNotInventFallbackHunters()
        {
            SessionHarness.Rig rig = BootGameGolden();
            Assert.AreEqual(0, rig.Session.ThreatManager.Threats.Count, "empty encounters stay empty on game deps");
        }

        [Test]
        public void GameDeps_IdleOnGoldenHub_RemainsCapableForThirtySimulatedMinutes()
        {
            SessionHarness.Rig rig = BootGameGolden();
            RunSession s = rig.Session;
            Vec3 pose = rig.Scene.PlayerPosition;
            Assert.AreEqual(0.0, s.PlayerFireIntensity(), 1e-12, "spawn pose is not inside a fire volume");
            int ticks = (int)(CapableSeconds / Step);
            for (int i = 0; i < ticks; i++)
            {
                rig.Clock.Advance(Step);
                s.Tick(TickContext.Frame(Step, pose));
                if (s.VitalsState.IsIncapacitated() || s.SliceComplete)
                    Assert.Fail("incapacitated or slice complete at t=" + s.RunPlayTimeSeconds.ToString("0.##") + "s");
            }
            Assert.IsFalse(s.VitalsState.IsIncapacitated());
            Assert.IsFalse(s.SliceComplete);
            Assert.AreEqual(0.0, s.PlayerFireIntensity(), 1e-12, "frozen pose still outside fire volume");
            Assert.Greater(s.VitalsState.Health, 0.0);
            Assert.Greater(s.VitalsState.Hunger, 0.0);
            Assert.AreEqual(0.0, s.RadiationState.Radiation, 1e-9, "fallback breach is not a ship-wide field");
        }

        [Test]
        public void AwayWreck_StillAppliesGodotSurvivalPressure()
        {
            SessionHarness.Rig rig = BootGameGolden();
            RunSession s = rig.Session;
            s.ForceRepairAll();
            GdDict travel = null;
            foreach (string id in s.ScannableMarkerIds())
            {
                travel = s.TravelToMarkerId(id);
                if (travel.GetBool("success"))
                    break;
            }
            Assert.IsNotNull(travel, "a marker was in range");
            Assert.IsTrue(travel.GetBool("success"), "travel: " + V.Str(travel.Get("reason", "")));
            Assert.IsTrue(s.AwayFromStart, "away wreck is not the hub safety path");

            double healthBefore = s.VitalsState.Health;
            Vec3 pose = rig.Scene.PlayerPosition;
            for (int i = 0; i < 20.0 / Step; i++)
            {
                rig.Clock.Advance(Step);
                s.Tick(TickContext.Frame(Step, pose));
            }
            bool healthDropped = s.VitalsState.Health < healthBefore - 1e-9;
            bool radiationUp = s.RadiationState != null && s.RadiationState.Radiation > 1e-9;
            bool liveThreats = s.ThreatManager.Threats.Count > 0;
            Assert.IsTrue(healthDropped || radiationUp || liveThreats,
                "away wreck still applies Godot survival pressure (health " + s.VitalsState.Health +
                ", radiation " + (s.RadiationState != null ? s.RadiationState.Radiation : 0.0) +
                ", threats " + s.ThreatManager.Threats.Count + ")");
        }
    }
}
