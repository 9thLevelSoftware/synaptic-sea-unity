using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>Phase 1.1a: the world clock is wired into the session without changing behaviour at scale 1.0.</summary>
    public class WorldClockSessionTests
    {
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

        static void Tick(SessionHarness.Rig rig, double seconds, double step = 0.25)
        {
            for (double t = 0; t < seconds - 1e-9; t += step)
            {
                rig.Clock.Advance(step);
                rig.Session.Tick(TickContext.Frame(step, rig.Scene.PlayerPosition));
            }
        }

        [Test]
        public void ScaleOne_TickTraceMatchesRealDeltasBitForBit()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            double world = 0.0, play = 0.0;
            for (int i = 0; i < 200; i++)
            {
                double step = i % 3 == 0 ? 0.25 : 1.0 / 60.0;
                rig.Clock.Advance(step);
                s.Tick(TickContext.Frame(step, rig.Scene.PlayerPosition));
                world += step;
                if (s.PlayableStarted && !s.SliceComplete) play += step;
                Assert.AreEqual(world, s.WorldTime, "WorldTime tracks the sum of real deltas exactly (tick " + i + ")");
                Assert.AreEqual(play, s.RunPlayTimeSeconds, "RunPlayTimeSeconds is unchanged (tick " + i + ")");
            }
            Assert.AreEqual(WorldClock.DefaultScale, s.GameClock.Scale);
            Assert.IsFalse(s.GetRunContextSummary().Has("time_scale"), "default-scale runs save exactly the old run_context");
        }

        [Test]
        public void ScaledClock_AdvancesWorldTimeButNotPlayTime()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            s.GameClock.SetScale(60.0);
            Tick(rig, 2.0);
            Assert.AreEqual(120.0, s.WorldTime, 1e-6);
            Assert.AreEqual(2.0, s.RunPlayTimeSeconds, 1e-6);
            Assert.AreEqual(60.0, V.F64(s.GetRunContextSummary().Get("time_scale", 0.0)));
        }

        [Test]
        public void ManualLoad_RestoresTheClockFromTheSnapshot()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            Tick(rig, 10.0);
            double worldAtSave = s.WorldTime;
            RunSnapshot snapshot = RunSnapshotAssembler.Build(s);

            var loadedRig = SessionHarness.CreateGolden();
            Assert.AreEqual(0.0, loadedRig.Session.WorldTime, "a fresh session starts at 0 (the old manual-load value)");
            Assert.IsTrue(RunSnapshotAssembler.Apply(loadedRig.Session, snapshot));
            Assert.AreEqual(worldAtSave, loadedRig.Session.WorldTime, 1e-9, "manual load restores WorldTime");
        }

        [Test]
        public void LegacySnapshotWithoutWorldClock_SeedsTheClockFromPlayTime()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            Tick(rig, 6.0);
            RunSnapshot snapshot = RunSnapshotAssembler.Build(s);
            snapshot.WorldClock = new GdDict();

            var restored = RunSnapshot.FromDict(snapshot.ToDict(), snapshot.SliceVersion, snapshot.GodotVersion);
            Assert.IsNotNull(restored);
            Assert.IsTrue(restored.WorldClock.IsEmpty);
            var loadedRig = SessionHarness.CreateGolden();
            Assert.IsTrue(RunSnapshotAssembler.Apply(loadedRig.Session, restored));
            Assert.AreEqual(restored.PlayTimeSeconds, loadedRig.Session.WorldTime, 1e-9);
        }

        [Test]
        public void ScaledRun_SavesAndRestoresClockAndScale()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            s.GameClock.SetScale(60.0);
            Tick(rig, 3.0);
            RunSnapshot snapshot = RunSnapshotAssembler.Build(s);
            Assert.IsFalse(snapshot.WorldClock.IsEmpty, "a non-default clock is saved");

            var roundTrip = RunSnapshot.FromDict(snapshot.ToDict(), snapshot.SliceVersion, snapshot.GodotVersion);
            var loadedRig = SessionHarness.CreateGolden();
            Assert.IsTrue(RunSnapshotAssembler.Apply(loadedRig.Session, roundTrip));
            Assert.AreEqual(60.0, loadedRig.Session.GameClock.Scale);
            Assert.AreEqual(s.WorldTime, loadedRig.Session.WorldTime, 1e-9);

            WorldSnapshot world = WorldSnapshotAssembler.Build(s);
            Assert.IsFalse(world.WorldClock.IsEmpty);
            WorldSnapshot worldBack = WorldSnapshot.FromDict(world.ToDict(), world.SliceVersion, world.GodotVersion);
            Assert.IsNotNull(worldBack);
            Assert.AreEqual(60.0, V.F64(worldBack.WorldClock.Get("scale", 0.0)));
        }

        [Test]
        public void DefaultClock_AddsNoWorldClockKeyToSavedWorld()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.IsFalse(WorldSnapshotAssembler.Build(s).ToDict().Has("world_clock"));
        }
    }
}
