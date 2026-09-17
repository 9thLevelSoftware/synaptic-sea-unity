using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// Milestone A New Run / first-away launch contract (REQ-SLICE-001). Headless: no scene flag-flip of
    /// <c>away_from_start</c>. Title New Run must resolve golden hub; first away uses production ShipGenerator.
    /// </summary>
    public class MilestoneALaunchContractTests
    {
        IEngineInfo _previousEngine;
        CollectingLog _log;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            _log = new CollectingLog();
            CoreServices.Log = _log;
            CatalogRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Engine = _previousEngine;
            CoreServices.Log = NullLog.Instance;
        }

        [Test]
        public void NewRunDefaults_AcceptAndResolveGoldenHub()
        {
            Assert.IsTrue(MilestoneALaunch.TryAccept(17, "breach_field", "standard", out string reason), reason);
            Assert.AreEqual("", reason);
            var deps = new RunSessionDeps();
            MilestoneALaunch.ApplyHubPaths(deps);
            Assert.AreEqual(MilestoneALaunch.HubLayoutPath, deps.LayoutPath);
            StringAssert.Contains("coherent_ship_001", deps.LayoutPath);
            StringAssert.Contains("coherent_ship_001", deps.GameplaySlicePath);
            StringAssert.Contains("coherent_ship_001", deps.BlueprintPath);
            Assert.AreNotEqual(RunSession.DEFAULT_LAYOUT_PATH, deps.LayoutPath);
        }

        [Test]
        public void NonSliceSeedBiomeDifficulty_FailClosed()
        {
            Assert.IsFalse(MilestoneALaunch.TryAccept(99, "breach_field", "standard", out string seedReason));
            StringAssert.Contains("non_slice_launch", seedReason);
            StringAssert.Contains("seed=99", seedReason);

            Assert.IsFalse(MilestoneALaunch.TryAccept(17, "dead_fleet", "standard", out string biomeReason));
            StringAssert.Contains("biome=dead_fleet", biomeReason);

            Assert.IsFalse(MilestoneALaunch.TryAccept(17, "breach_field", "hardened", out string diffReason));
            StringAssert.Contains("difficulty=hardened", diffReason);
            StringAssert.DoesNotContain("seed_000017", seedReason + biomeReason + diffReason);
        }

        [Test]
        public void StartSceneBuilder_StillNullWithLog_WhenNoDock()
        {
            StartSceneBuilder.StartSceneDocuments built = StartSceneBuilder.Build(42);
            Assert.IsNull(built, "legacy life-boat+derelict path stays fail-closed; do not invent a dock");
            Assert.That(_log.Errors, Has.Some.Contains("no dock room found"));
        }

        [Test]
        public void FirstAwayGate_EvaluatesPreferredSeedsInAuthoredOrder_AndPicksFirstPass()
        {
            var contract = new FirstRunContract();
            Assert.IsTrue(contract.LoadContract());
            CollectionAssert.AreEqual(new object[] { 42L, 777L }, PreferredSeeds(contract));

            var seen = new List<long>();
            FirstRunAwayGate.Result pick = FirstRunAwayGate.EvaluateCandidates(
                contract, 1, (long)ShipBlueprint.Condition.Pristine, (seed, size, cond) =>
                {
                    seen.Add(seed);
                    return seed == 777 ? PassingDocuments() : null;
                });
            CollectionAssert.AreEqual(new[] { 42L, 777L }, seen);
            Assert.IsTrue(pick.Success, pick.Reason);
            Assert.AreEqual(777L, pick.Seed);

            seen.Clear();
            FirstRunAwayGate.Result first = FirstRunAwayGate.EvaluateCandidates(
                contract, 1, (long)ShipBlueprint.Condition.Pristine, (seed, size, cond) =>
                {
                    seen.Add(seed);
                    return PassingDocuments();
                });
            CollectionAssert.AreEqual(new[] { 42L }, seen, "stops at the first passing seed");
            Assert.AreEqual(42L, first.Seed);
        }

        static ShipDocuments PassingDocuments()
        {
            var occupancy = new GdDict
            {
                {
                    "0|0|0", new GdDict
                    {
                        { "room_id", "start" }, { "cell", GdArray.Of(0L, 0L) }, { "deck", 0L },
                        { "position", new Vec3(0f, 0f, 0f) }, { "cell_key", "0|0|0" },
                    }
                },
            };
            var layout = new GdDict
            {
                { "biome_id", "breach_field" },
                { "difficulty_id", "standard" },
                { "encounters", GdArray.Of(new GdDict { { "id", "e1" } }) },
                { "fire_zones", GdArray.Of(new GdDict { { "id", "f1" } }) },
                { "prototype", new GdDict { { "start_room", "start" }, { "goal_room", "start" } } },
                { "structural_plan", new GdDict { { "occupancy", occupancy }, { "edges", new GdDict() } } },
                {
                    "rooms", GdArray.Of(new GdDict
                    {
                        { "id", "start" },
                        {
                            "interior_zones", new GdDict
                            {
                                { "center_slots", GdArray.Of(GdArray.Of(1L, 2L)) },
                                { "wall_slots", new GdArray() },
                            }
                        },
                    })
                },
            };
            var slice = new GdDict
            {
                {
                    "loot_containers", GdArray.Of(new GdDict
                    {
                        { "room_id", "start" }, { "approach_cell", GdArray.Of(1L, 2L) },
                    })
                },
                { "objectives", GdArray.Of(new GdDict { { "id", "o1" }, { "sequence", 1L } }) },
            };
            return new ShipDocuments { Layout = layout, GameplaySlice = slice };
        }

        [Test]
        public void FirstAwayGate_DeniesWhenNoCandidatePasses()
        {
            var contract = new FirstRunContract();
            Assert.IsTrue(contract.LoadContract());
            var seen = new List<long>();
            FirstRunAwayGate.Result pick = FirstRunAwayGate.EvaluateCandidates(contract, 1, 2, (seed, size, cond) =>
            {
                seen.Add(seed);
                return null;
            });
            CollectionAssert.AreEqual(new[] { 42L, 777L }, seen);
            Assert.IsFalse(pick.Success);
            StringAssert.Contains(FirstRunAwayGate.UnsatisfiedReason, pick.Reason);
        }

        [Test]
        public void HeadlessNewRunHub_IsCoherentShip001()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            MilestoneALaunch.ApplyHubPaths(deps);
            rig.Session = RunSession.Create(deps);
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            StringAssert.Contains("coherent_ship_001", rig.Session.LayoutPath);
            Assert.IsFalse(rig.Session.AwayFromStart);
            Assert.AreSame(rig.Session.HomeShip, rig.Session.CurrentShip);
        }

        [Test]
        public void FirstAway_BoardsGeneratedWreckViaAttachPath()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            s.ForceRepairAll();

            Assert.IsFalse(s.AwayFromStart, "precondition: still on hub");
            string hubProgram = V.Str(s.HomeShip.BuiltLayout.Get("program_id", ""));
            string markerId = "";
            GdDict travel = null;
            foreach (string id in s.ScannableMarkerIds())
            {
                if (MarkerById(s, id) == null) continue;
                markerId = id;
                travel = s.TravelToMarkerId(id);
                if (travel.GetBool("success")) break;
            }
            Assert.IsNotNull(travel, "a marker was in range");
            Assert.IsTrue(travel.GetBool("success"), "travel: " + V.Str(travel.Get("reason", "")));
            Assert.IsTrue(s.AwayFromStart, "away_from_start comes from attach-derelict, not a test flag");
            Assert.AreNotSame(s.HomeShip, s.CurrentShip);
            GdDict layout = s.CurrentShip.BuiltLayout;
            string programId = V.Str(layout.Get("program_id", ""));
            Assert.AreNotEqual("coherent-proof-ship-001", programId);
            StringAssert.StartsWith("procgen-", programId);
            Assert.AreNotEqual(hubProgram, programId);

            long boardedSeed = s.CurrentShip.Blueprint.SeedValue;
            Assert.That(boardedSeed == 42L || boardedSeed == 777L, "boarded preferred seed, got " + boardedSeed);
            Assert.IsTrue(FirstRunAwayGate.HasStandingStartToGoal(layout), "standing start→goal");
            var loader = s.CurrentShip.SceneRoot as IShipLoaderView;
            Assert.IsNotNull(loader, "attach path left a loader view on current_ship");
            Assert.GreaterOrEqual(loader.GetObjectiveSpecsCopy().Count, 1, "≥1 objective");
            Assert.IsTrue(FirstRunAwayGate.HasInteriorLootSlot(layout, loader.GameplayDoc), "≥1 interior loot slot");
            if (FirstRunAwayGate.RequiresWreckOverlay(s.CurrentShip.Blueprint.ShipCondition))
                Assert.IsTrue(FirstRunAwayGate.HasWreckOverlay(layout), "DAMAGED/WRECKED wreck overlay");
            Assert.IsTrue(s.FirstRunContract.Validate(layout, loader.GameplayDoc));
            Assert.AreEqual(markerId, s.CurrentShip.MarkerId);
        }

        [Test]
        public void FirstAway_DeniesTravelWhenNoSeedPasses_HubUnchanged()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            s.ForceRepairAll();
            s.FirstRunContract.Contract["preferred_seeds"] = new GdArray();

            Vec3 playerBefore = rig.Scene.PlayerPosition;
            string homeId = s.HomeShip.ShipId;
            bool generatedBefore = false;
            string markerId = s.ScannableMarkerIds()[0];
            ShipMarker marker = MarkerById(s, markerId);
            long seedBefore = marker.SeedValue;
            generatedBefore = s.SynapticSeaWorld.IsGenerated(markerId);

            GdDict travel = s.TravelToMarkerId(markerId);
            Assert.IsFalse(travel.GetBool("success"));
            StringAssert.Contains(FirstRunAwayGate.UnsatisfiedReason, V.Str(travel.Get("reason", "")));
            Assert.IsFalse(s.AwayFromStart);
            Assert.AreSame(s.HomeShip, s.CurrentShip);
            Assert.AreEqual(homeId, s.CurrentShip.ShipId);
            Assert.AreEqual(playerBefore, rig.Scene.PlayerPosition);
            Assert.AreEqual(seedBefore, MarkerById(s, markerId).SeedValue, "marker seed unchanged");
            Assert.AreEqual(generatedBefore, s.SynapticSeaWorld.IsGenerated(markerId), "world generated mark unchanged");
        }

        static GdArray PreferredSeeds(FirstRunContract contract) =>
            contract.Contract.GetArrayOrEmpty("preferred_seeds");

        static ShipMarker MarkerById(RunSession session, string markerId)
        {
            foreach (ShipMarker m in session.SynapticSeaWorld.MarkersInRange(session.ScannerState.RangeRadius))
            {
                if (m.MarkerId == markerId) return m;
            }
            return null;
        }
    }
}
