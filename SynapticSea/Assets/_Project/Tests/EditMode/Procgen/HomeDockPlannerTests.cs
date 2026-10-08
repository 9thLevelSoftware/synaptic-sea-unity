using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>Phase 1.7c spike: the exterior-edge dock for generated homes is deterministic, fits the boat, and leaves every default untouched.</summary>
    public class HomeDockPlannerTests
    {
        const string Biome = "breach_field";
        const string Difficulty = "standard";
        const long Pristine = (long)ShipBlueprint.Condition.Pristine;

        [SetUp]
        public void SetUp()
        {
            CoreServices.Log = new CollectingLog();
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
            CoreServices.Log = NullLog.Instance;
        }

        [Test]
        public void TheDefaultPathStampsNoDockContract_AndTheGoldenPortIsUnchanged()
        {
            StartSceneBuilder.HomeStart hs = StartSceneBuilder.BuildHomeStart(7, Biome, Difficulty, condition: Pristine);
            Assert.NotNull(hs);
            Assert.IsFalse(hs.Documents.Layout.Has("docking_port"), "exteriorDock is off by default");

            string dir = Path.Combine(Fixtures.StreamingDataRoot, "data", "procgen", "golden", "coherent_ship_001");
            var golden = (GdDict)GdJson.Parse(File.ReadAllText(Path.Combine(dir, "layout.json")));
            GdDict port = DockPorts.ForDerelict(golden);
            Assert.IsFalse(port.IsEmpty);
            Assert.AreEqual(new Vec3(2.0, 0.0, 2.0), (Vec3)port["position"], "the golden hub still docks at its airlock room centroid");
            Assert.AreEqual(new Vec3(1.0, 0.0, 0.0), (Vec3)port["facing"]);
        }

        [Test]
        public void ExteriorDock_FitsTheBoatInAlmostEverySeed_AndRerollsTheRest()
        {
            int seeds = int.TryParse(Environment.GetEnvironmentVariable("SYNAPTICSEA_SEED_SWEEP"), out int n) && n > 0 ? n : 200;
            int ok = 0, first = 0;
            var rerolled = new List<long>();
            for (long seed = 1; seed <= seeds; seed++)
            {
                StartSceneBuilder.HomeStart hs = StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty, condition: Pristine, exteriorDock: true);
                if (hs == null) continue;
                ok++;
                if (hs.Attempts == 1) first++; else rerolled.Add(seed);
                GdDict layout = hs.Documents.Layout;
                Assert.IsTrue(layout.Has("docking_port"), "seed " + seed + " carries the contract");
                Assert.IsTrue(hs.Documents.SourceLayout.Has("docking_port"), "seed " + seed + " source layout carries the contract");
                Assert.IsTrue(hs.Documents.LayoutJson.Contains("\"docking_port\""), "seed " + seed + " layout json carries the contract");

                // The session reads the same port, and the boat then overlaps no other room.
                GdDict port = DockPorts.ForDerelict(layout);
                Assert.IsFalse(port.IsEmpty, "seed " + seed);
                HomeDockPlanner.Plan plan = HomeDockPlanner.TryPlan(layout, out string reason);
                Assert.NotNull(plan, "seed " + seed + ": " + reason);
                Assert.AreEqual(0, plan.OtherRoomOverlap, "seed " + seed + " boat overlaps no other room");
                var pos = GdArray.Of(((Vec3)port["position"]).X, ((Vec3)port["position"]).Y, ((Vec3)port["position"]).Z);
                Assert.AreEqual(V.F64(((GdArray)plan.Contract["position"])[0]), V.F64(pos[0]), 1e-6, "seed " + seed + " the session port is the planned port");
            }
            TestContext.WriteLine("exteriorDock: " + ok + " / " + seeds + " homes within " + StartSceneBuilder.MAX_START_ATTEMPTS + " attempts; first attempt " + first + "; rerolled seeds: " + string.Join(", ", rerolled));
            Assert.GreaterOrEqual(ok, (int)Math.Ceiling(seeds * 0.95), "at least 95% of seeds produce a home whose boat fits within 8 attempts");
            Assert.GreaterOrEqual(first, (int)Math.Ceiling(seeds * 0.90), "the per-attempt pass rate is high enough that retries are rare");
        }

        [Test]
        public void ExteriorDock_IsDeterministic()
        {
            for (long seed = 1; seed <= 8; seed++)
            {
                StartSceneBuilder.HomeStart a = StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty, condition: Pristine, exteriorDock: true);
                StartSceneBuilder.HomeStart b = StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty, condition: Pristine, exteriorDock: true);
                Assert.NotNull(a);
                Assert.AreEqual(a.Seed, b.Seed);
                Assert.AreEqual(a.Documents.LayoutJson, b.Documents.LayoutJson, "seed " + seed);
            }
        }

        [Test]
        public void ExteriorDock_RejectsAHomeWhoseDockRoomHasNoOutsideEdge()
        {
            // Ring templates can wrap the dock room inside the hull; those seeds must be rejected, not docked through walls.
            bool sawReject = false;
            for (long seed = 1; seed <= 200 && !sawReject; seed++)
            {
                StartSceneBuilder.HomeStart hs = StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty, condition: Pristine, exteriorDock: true);
                if (hs != null && hs.Rejections.Any(r => r.Contains("exterior-facing edge") || r.Contains("overlap"))) sawReject = true;
            }
            Assert.IsTrue(sawReject, "at least one seed in the first 200 is re-rolled for a boat that does not fit");
        }
    }
}
