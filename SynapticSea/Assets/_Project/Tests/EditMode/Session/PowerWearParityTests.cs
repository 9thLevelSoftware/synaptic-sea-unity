using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// fixtures/godot/models/power_wear_coherent_ship_001.fullprec.json (parity_capture_roundtrip_wear.gd --mode wear): the
    /// golden ship booted in Godot 4.7.1, threats cleared, the player frozen, then 120 synchronous <c>_process(0.25)</c>
    /// calls. The headless session replays the same sequence and must reproduce every sample: every subcomponent's health,
    /// power_percent, world_time, run play time, the player's health and suit oxygen, and when the run ends.
    /// </summary>
    public class PowerWearParityTests
    {
        const string Fixture = "godot/models/power_wear_coherent_ship_001.fullprec.json";
        const double Step = 0.25;

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

        static GdDict Sample(RunSession s, double t)
        {
            var systems = new GdDict();
            foreach (KeyValuePair<string, ShipSystem> kv in s.ShipSystemsManager.Systems)
            {
                var subs = new GdDict();
                foreach (ShipSubcomponent sub in kv.Value.Subcomponents)
                    subs[sub.SubcomponentId] = sub.Health;
                systems[kv.Key] = subs;
            }
            return new GdDict
            {
                { "t", t },
                { "world_time", s.WorldTime },
                { "run_play_time_seconds", s.RunPlayTimeSeconds },
                { "slice_complete", s.SliceComplete },
                { "power_percent", s.GetShipSystemsSummary().Get("power_percent", -1L) },
                { "vitals_health", s.VitalsState.Health },
                { "oxygen", V.F64(s.GetOxygenSummary().Get("oxygen", -1.0)) },
                { "systems", systems },
            };
        }

        [Test]
        public void GoldenShip_ThirtySeconds_MatchesTheGodotWearTrace()
        {
            GdDict godot = Fixtures.ReadDict(Fixture);
            Assert.AreEqual(Step, godot.GetFloat("step_seconds"));
            long ticks = godot.GetInt("ticks");
            GdArray samples = godot.GetArrayOrEmpty("samples");
            Assert.AreEqual(ticks + 1, samples.Count);

            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            s.ThreatManager.Threats.Clear();
            // Godot's player body settled under physics for one boot frame before it was frozen; use that exact position (the
            // 3D fire-zone radius and breach proximity are position-sensitive: the port's spawn point is 0.21 m higher).
            GdArray godotPos = godot.GetArrayOrEmpty("player_position_start");
            rig.Scene.PlayerPosition = new Vec3(V.F64(godotPos[0]), V.F64(godotPos[1]), V.F64(godotPos[2]));

            var report = new StringBuilder();
            for (int i = 0; i <= ticks; i++)
            {
                if (i > 0)
                {
                    rig.Clock.Advance(Step);
                    s.Tick(TickContext.Frame(Step, rig.Scene.PlayerPosition));
                }
                var expected = (GdDict)samples[i];
                // Values compare type-loosely only because the fixture's JSON cannot tell 20 from 20.0; floats compare exactly.
                GdDict actual = Sample(s, i * Step);
                List<string> diffs = TreeDiff.Compare(expected, actual, new TreeDiff.Options { MaxDifferences = 50, IntFloatEquivalent = true, IgnoreKeys = { "hub_slow_acc", "manager_summary_power_percent" } });
                if (diffs.Count > 0)
                    report.AppendLine($"t={i * Step}: " + TreeDiff.Format(diffs));
            }
            Assert.IsEmpty(report.ToString(), report.ToString());

            // The capture's headline numbers, so a regenerated fixture that changes them is noticed.
            GdDict last = (GdDict)samples[samples.Count - 1];
            GdDict power = last.GetDictOrEmpty("systems").GetDictOrEmpty("power");
            Assert.AreEqual(0.0375, power.GetFloat("reactor_core"), 1e-12);
            // Godot quirk kept for parity: with threats cleared the idle player still dies at 29.25 s, because the wearing
            // power grid starves home life support (LifeSupportState health drain climbs to 5/s).
            Assert.AreEqual(29.25, godot.GetFloat("slice_complete_flipped_at"));
            Assert.IsTrue(s.SliceComplete);
        }

        [Test]
        public void GoldenShip_IdleWithEmergencyFloor_OutlivesTheLifeSupportStarvation()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            deps.HomeLifeSupportPowerFloor = new RunSessionDeps().HomeLifeSupportPowerFloor;
            Assert.Greater(deps.HomeLifeSupportPowerFloor, 0.0, "the game default carries the floor");
            RunSession s = RunSession.Create(deps);
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            s.ThreatManager.Threats.Clear();
            // Godot (floor 0.0, the parity test above) is dead at 29.25 s. With the floor the atmosphere is still clean
            // at 34 s, while the grid allocates nothing to life support.
            for (int i = 0; i < 34.0 / Step; i++)
            {
                rig.Clock.Advance(Step);
                s.Tick(TickContext.Frame(Step, rig.Scene.PlayerPosition));
            }
            Assert.IsFalse(s.SliceComplete, "the idle player outlives Godot's 29.25 s life-support death");
            Assert.AreEqual(0.0, s.PowerGridState.GetAllocationRatio("life_support"), "the grid itself still starves life support");
            Assert.AreEqual(0.0, s.LifeSupportExpandedState.GetHealthDrainPerSecond(), 1e-9, "emergency cells keep the atmosphere breathable");
            Assert.Greater(s.LifeSupportExpandedState.OxygenPercent, 95.0);
        }
    }
}
