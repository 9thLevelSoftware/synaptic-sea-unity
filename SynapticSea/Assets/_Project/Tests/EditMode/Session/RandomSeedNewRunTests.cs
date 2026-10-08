using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// Phase 1.5: a New Run takes any seed. The seed drives the Synaptic Sea world (markers, sea graph) and the first away wreck;
    /// the home ship stays the golden <c>coherent_ship_001</c> with identical systems and loot for every seed.
    /// </summary>
    public class RandomSeedNewRunTests
    {
        const double ScanRadius = 250.0;

        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CoreServices.Log = new CollectingLog();
            CatalogRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Engine = _previousEngine;
            CoreServices.Log = NullLog.Instance;
        }

        static RunSession SessionFor(long seed, out SessionHarness.Rig rig)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out rig);
            MilestoneALaunch.ApplyHubPaths(deps);
            deps.RunSeed = seed;
            return RunSession.Create(deps);
        }

        static string MarkerSignature(RunSession s) =>
            string.Join(";", s.SynapticSeaWorld.MarkersInRange(ScanRadius).Select(m => m.MarkerId + ":" + m.SeedValue + ":" + m.SizeClass + ":" + m.Condition));

        [TestCase(1L)]
        [TestCase(99L)]
        [TestCase(4711L)]
        [TestCase(MilestoneALaunch.MaxSeed)]
        public void AnySeedInRangeIsAcceptedWithTheSliceBiomeAndDifficulty(long seed)
        {
            Assert.IsTrue(MilestoneALaunch.TryAccept(seed, "breach_field", "standard", out string reason), reason);
        }

        [TestCase(0L)]
        [TestCase(-5L)]
        [TestCase(MilestoneALaunch.MaxSeed + 1)]
        public void OutOfRangeSeedsFailClosed(long seed)
        {
            Assert.IsFalse(MilestoneALaunch.TryAccept(seed, "breach_field", "standard", out string reason));
            StringAssert.Contains("non_slice_launch", reason);
            StringAssert.Contains("seed=" + seed, reason);
        }

        [Test]
        public void TheRunSeedDrivesTheWorldAndTheDirectOpenSeedKeepsTheGoldenWorld()
        {
            RunSession golden = SessionFor(MilestoneALaunch.TitleStartSeed, out _);
            RunSession other = SessionFor(4711, out _);
            RunSession otherAgain = SessionFor(4711, out _);
            Assert.AreEqual(4711L, other.RunSeed);
            Assert.AreEqual(4711L, other.SynapticSeaWorld.WorldSeed);
            Assert.AreEqual(4711L, other.SeaGraph.WorldSeed, "the sea graph follows the run seed");
            Assert.AreEqual(MilestoneALaunch.TitleStartSeed, golden.SynapticSeaWorld.WorldSeed, "the golden blueprint seed is unchanged for the direct-open run");
            string goldenMarkers = MarkerSignature(golden), otherMarkers = MarkerSignature(other);
            Assert.IsNotEmpty(otherMarkers);
            Assert.AreNotEqual(goldenMarkers, otherMarkers, "a different seed shows different scanner contacts");
            Assert.AreEqual(otherMarkers, MarkerSignature(otherAgain), "the same seed shows the same contacts");
        }

        [Test]
        public void TheHomeShipIsTheGoldenHubForEverySeed()
        {
            RunSession a = SessionFor(MilestoneALaunch.TitleStartSeed, out _);
            RunSession b = SessionFor(987654321L, out _);
            Assert.AreEqual(a.LayoutPath, b.LayoutPath);
            StringAssert.Contains("coherent_ship_001", b.LayoutPath);
            Assert.IsFalse(b.AwayFromStart);
            Assert.AreEqual(GdJson.Stringify(a.ShipSystemsManager.GetSummary()), GdJson.Stringify(b.ShipSystemsManager.GetSummary()),
                "ship-systems damage stays on the golden blueprint seed");
            string Loot(RunSession s) => string.Join(";", s.LootContainers.OrderBy(c => c.ContainerId).Select(c => c.ContainerId + "@" + c.GlobalPosition));
            Assert.AreEqual(Loot(a), Loot(b), "the home containers are identical for every seed");
            Assert.IsTrue(b.LootContainers.Any(c => c.ContainerId == "start_supply_a"), "the repair cache");
            Assert.IsTrue(b.LootContainers.Any(c => c.ContainerId == "start_supply_c"), "the emergency stores");
            Assert.AreEqual(a.InventoryState.Items.Count, b.InventoryState.Items.Count);
        }

        [Test]
        public void TheSeedSurvivesSaveAndContinueWithTheSameContacts()
        {
            RunSession s = SessionFor(4711, out SessionHarness.Rig rig);
            string contacts = MarkerSignature(s);
            Assert.IsTrue(s.RequestSave());

            // A fresh session on the default seed adopts the saved run context and world on load.
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig other);
            deps.Storage = rig.Storage;
            MilestoneALaunch.ApplyHubPaths(deps);
            RunSession loaded = RunSession.Create(deps);
            Assert.AreNotEqual(contacts, MarkerSignature(loaded), "before the load the fresh session shows the golden world");
            Assert.IsTrue(loaded.RequestLoad());
            Assert.AreEqual(4711L, loaded.RunSeed);
            Assert.AreEqual(4711L, loaded.SynapticSeaWorld.WorldSeed);
            Assert.AreEqual(4711L, loaded.SeaGraph.WorldSeed);
            Assert.AreEqual(contacts, MarkerSignature(loaded), "Continue shows the same contacts");
            RunSnapshot snap = RunSnapshotAssembler.Build(loaded);
            Assert.AreEqual(4711L, snap.WorldSeed);
            Assert.AreEqual(4711L, V.I64(snap.RunContext.Get("seed")));
        }

        [Test]
        public void DirectOpenWithoutASeedStillFallsBackToTheBlueprintSeed()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out _);
            MilestoneALaunch.ApplyHubPaths(deps);
            RunSession s = RunSession.Create(deps);
            Assert.AreEqual(17L, s.RunSeed);
            Assert.AreEqual(17L, s.SynapticSeaWorld.WorldSeed);
        }

        /// <summary>
        /// D9 for any seed: whatever size and condition a random world's nearest contacts have, the first wreck is a validated hull (seed 42, else 777)
        /// that qualifies and always carries the emergency stores. The world count is 40 by default; set SYNAPTICSEA_SEED_SWEEP (for example 200) for a
        /// wider sweep.
        /// </summary>
        [Test]
        public void TheFirstWreckQualifiesForContactsOfManyWorlds()
        {
            int seeds = int.TryParse(Environment.GetEnvironmentVariable("SYNAPTICSEA_SEED_SWEEP"), out int n) && n > 0 ? n : 40;
            var contract = new FirstRunContract();
            Assert.IsTrue(contract.LoadContract());
            var generator = new ShipGenerator();
            generator.ConfigureRunContext(contract.Contract.GetString("biome_id"), contract.Contract.GetString("difficulty_id"));
            var patched = new FirstRunAwayGate.PatchedGenerator(generator, contract);
            int checkedContacts = 0;
            var cells = new HashSet<string>();
            var failures = new List<string>();
            for (long seed = 1; seed <= seeds; seed++)
            {
                var world = new SynapticSeaWorld(seed, Vec3.Zero);
                foreach (ShipMarker marker in world.MarkersInRange(ScanRadius).Take(3))
                {
                    checkedContacts++;
                    cells.Add(marker.SizeClass + "/" + marker.Condition);
                    FirstRunAwayGate.Result pick = FirstRunAwayGate.EvaluateCandidates(
                        contract, marker.SizeClass, marker.Condition, (sd, size, condition) => (ShipDocuments)patched.GenerateFromSeed(sd, size, condition));
                    string label = "world " + seed + " marker " + marker.MarkerId + " (size " + marker.SizeClass + ", condition " + marker.Condition + ")";
                    if (!pick.Success) { failures.Add(label + ": " + pick.Reason); continue; }
                    if (pick.Seed != 42L && pick.Seed != 777L) failures.Add(label + ": picked " + pick.Seed + ", not a preferred seed");
                    var docs = (ShipDocuments)patched.GenerateFromSeed(pick.Seed, marker.SizeClass, marker.Condition);
                    string reject = FirstRunAwayGate.RejectReason(contract, docs.Layout, docs.GameplaySlice, marker.Condition);
                    if (reject != "") failures.Add(label + ": the accepted wreck is rejected: " + reject);
                    int stores = docs.GameplaySlice.GetArrayOrEmpty("loot_containers").OfType<GdDict>().Count(c => c.GetString("id") == FirstRunAwayGate.FirstWreckStoresId);
                    if (stores != 1) failures.Add(label + ": expected exactly one " + FirstRunAwayGate.FirstWreckStoresId + ", found " + stores);
                }
            }
            TestContext.WriteLine("contact sweep: " + seeds + " worlds, " + checkedContacts + " contacts, " + cells.Count + " size/condition cells, failures " + failures.Count);
            Assert.IsEmpty(failures, string.Join("\n", failures));
            Assert.Greater(checkedContacts, 0);
        }
    }
}
