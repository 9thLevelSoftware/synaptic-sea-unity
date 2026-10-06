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
            Assert.AreEqual(MilestoneALaunch.HubBlueprintPath, deps.BlueprintPath);
            Assert.AreEqual(RunSession.DEFAULT_KIT_PATH, deps.KitPath);
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

        [Test]
        public void FirstRunPatch_IsDeterministic()
        {
            var contract = new FirstRunContract();
            Assert.IsTrue(contract.LoadContract());
            string Patched()
            {
                var generator = new ShipGenerator();
                generator.ConfigureRunContext("breach_field", "standard");
                var docs = generator.GenerateFromSeed(777, 1, (long)ShipBlueprint.Condition.Damaged);
                FirstRunAwayGate.Patch(contract, docs);
                return GdJson.Stringify(docs.Layout, "  ");
            }
            Assert.AreEqual(Patched(), Patched());
        }

        [TestCase(42L, 9)]
        [TestCase(777L, 9)]
        public void PreferredAwaySeedMatrixSatisfiesCompleteContractGate(long seed, int expectedAccepted)
        {
            var contract = new FirstRunContract();
            Assert.IsTrue(contract.LoadContract());
            var generator = new ShipGenerator();
            generator.ConfigureRunContext("breach_field", "standard");
            int accepted = 0;
            for (long size = 0; size <= 2; size++)
                for (long condition = 0; condition <= 2; condition++)
                {
                    var docs = generator.GenerateFromSeed(seed, size, condition);
                    string reason = docs == null ? "generation" : FirstRunAwayGate.RejectReason(contract, docs.Layout, docs.GameplaySlice, condition);
                    if (seed == 777 && docs != null)
                    {
                        Assert.AreEqual(contract.Contract.GetString("biome_id"), docs.Layout.GetString("biome_id"));
                        Assert.AreEqual(contract.Contract.GetString("difficulty_id"), docs.Layout.GetString("difficulty_id"));
                        Assert.GreaterOrEqual(docs.GameplaySlice.GetArrayOrEmpty("loot_containers").Count, contract.Contract.GetInt("require_min_loot_containers"));
                        Assert.GreaterOrEqual(docs.Layout.GetArrayOrEmpty("encounters").Count, contract.Contract.GetInt("require_min_encounters"));
                        Assert.IsFalse(contract.Validate(docs.Layout, docs.GameplaySlice), "all non-hazard requirements pass; preserve the missing-hazard rejection");
                    }
                    contract.Contract["preferred_seeds"] = GdArray.Of(seed);
                    var pick = FirstRunAwayGate.EvaluateCandidates(contract, size, condition, (a, b, c) => docs);
                    Assert.IsTrue(pick.Success, "gate must accept every size/condition cell (ordinary generator reject: " + reason + "): " + pick.Reason);
                    Assert.AreEqual("", FirstRunAwayGate.RejectReason(contract, docs.Layout, docs.GameplaySlice, condition), "the patched candidate satisfies the complete contract");
                    if (pick.Success) accepted++;
                    else StringAssert.Contains(FirstRunAwayGate.UnsatisfiedReason, pick.Reason);
                    TestContext.WriteLine("away seed=" + seed + " size=" + size + " condition=" + condition + " result=" + (pick.Success ? "accepted" : reason)
                        + " loot=" + (docs?.GameplaySlice.GetArrayOrEmpty("loot_containers").Count ?? 0)
                        + " encounters=" + (docs?.Layout.GetArrayOrEmpty("encounters").Count ?? 0));
                }
            Assert.AreEqual(expectedAccepted, accepted, "D9: the first wreck always qualifies, on every size and condition");
            Assert.IsFalse(MilestoneALaunch.TryAccept(seed, "breach_field", "standard", out _), "away candidates are not supported title seeds");
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
        public void FiniteHubMaintenanceCacheCoversDamagedFlightPartsAndTools()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            MilestoneALaunch.ApplyHubPaths(deps);
            var session = RunSession.Create(deps);
            Assert.IsTrue(session.InventoryState.Items.IsEmpty, "New Run grants no flight-repair inventory");
            var cache = session.LootContainers.Single(c => c.ContainerId == "start_supply_a");
            Assert.IsTrue(cache.TryInteract(cache.GlobalPosition));
            var requirements = new Dictionary<string, int>();
            var tools = new HashSet<string>();
            foreach (var point in session.RepairPoints.Where(p => new[] { "power", "navigation", "scanners", "propulsion" }.Contains(p.SystemId)))
            {
                var sub = point.TargetManager.GetSystem(point.SystemId).GetSubcomponent(point.SubcomponentId);
                foreach (string part in sub.RequiredParts) requirements[part] = requirements.TryGetValue(part, out int n) ? n + 1 : 1;
                foreach (string tool in sub.RequiredTools) tools.Add(tool);
            }
            foreach (var part in requirements) Assert.GreaterOrEqual(session.InventoryState.GetQuantity(part.Key), part.Value, part.Key);
            foreach (string tool in tools) Assert.Greater(session.InventoryState.GetQuantity(tool), 0, tool);
            string before = GdJson.Stringify(session.InventoryState.Items);
            Assert.IsFalse(cache.TryInteract(cache.GlobalPosition), "the finite supply cache pays only once");
            Assert.AreEqual(before, GdJson.Stringify(session.InventoryState.Items));
        }

        [TestCase("engineer", false)] [TestCase("engineer", true)]
        [TestCase("mechanic", false)] [TestCase("mechanic", true)]
        [TestCase("medic", false)] [TestCase("medic", true)]
        [TestCase("pilot", false)] [TestCase("pilot", true)]
        [TestCase("scientist", false)] [TestCase("scientist", true)]
        [TestCase("cook", false)] [TestCase("cook", true)]
        [TestCase("security", false)] [TestCase("security", true)]
        [TestCase("communications", false)] [TestCase("communications", true)]
        public void StartingClassRepairPathPreservesGatesAndReportsReachability(string classId, bool hubTraining)
        {
            var deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            MilestoneALaunch.ApplyHubPaths(deps);
            deps.StartingClassId = classId;
            var s = RunSession.Create(deps);
            Assert.AreEqual(classId, s.PlayerProgression.ClassId);
            long initial = s.PlayerProgression.GetSkillLevel("repair");
            Assert.AreEqual(V.I64(ClassDefinition.LoadAll()[classId].StartingSkills.Get("repair", 0L)), initial);
            Assert.IsTrue(s.LootContainers.Single(c => c.ContainerId == "start_supply_a").TryInteract(s.LootContainers.Single(c => c.ContainerId == "start_supply_a").GlobalPosition));
            Assert.AreEqual("crowbar", s.EquipmentState.GetEquipped("primary_hand"), "normal loot auto-equips the acquired tool into the empty hand");
            if (hubTraining)
                for (int guard = 0; guard < 12 && s.CurrentObjectiveSequence <= 3; guard++)
                {
                    var objective = s.Interactables.First(o => o.Active && !o.Completed);
                    Assert.IsTrue(objective.TryInteract(objective.GlobalPosition), "existing hub objective interaction");
                }
            if (hubTraining) Assert.AreEqual(4, s.CurrentObjectiveSequence, "stop before reactor objective ends extraction");
            var required = new[] { "power", "navigation", "scanners", "propulsion" };
            var completed = new List<string>();
            for (int guard = 0; guard < 24; guard++)
            {
                var next = s.RepairPoints.Where(r => required.Contains(r.SystemId) && r.CanBeginRepair())
                    .OrderBy(r => r.MinSkill).FirstOrDefault();
                next ??= s.RepairPoints.Where(r => r.CanBeginRepair()).OrderBy(r => r.MinSkill).FirstOrDefault();
                if (next == null) break;
                Assert.GreaterOrEqual(s.PlayerProgression.GetSkillLevel("repair"), next.MinSkill);
                Assert.IsTrue(next.TryStart(next.GlobalPosition));
                next.AdvanceChannel(120.0);
                Assert.IsTrue(next.Repaired, "normal resource/skill-gated channel finishes");
                completed.Add(next.SystemId + "." + next.SubcomponentId);
            }
            bool ready = required.All(id => s.ShipSystemsManager.IsOperational(id));
            Assert.AreEqual(classId == "engineer" || classId == "mechanic", ready,
                "current finite hub route: preserve and expose class-specific first-away limitations rather than granting skill");
            TestContext.WriteLine(classId + ": hubTraining=" + hubTraining + " initial=" + initial + " earned=" + s.PlayerProgression.GetSkillLevel("repair")
                + " ready=" + ready + " completed=" + string.Join(",", completed));
            if (ready) Assert.IsFalse(s.SliceComplete, "repairing for travel is not extraction");
            else Assert.IsFalse(s.RepairPoints.Any(r => r.CanBeginRepair()), "a stopped path has no eligible remaining repair, including side work");
            for (int guard = 0; guard < 24 && !s.HomeObjectivesComplete && !s.SliceComplete; guard++)
            {
                var objective = s.Interactables.First(o => o.Active && !o.Completed);
                Assert.IsTrue(objective.TryInteract(objective.GlobalPosition));
            }
            Assert.IsTrue(s.HomeObjectivesComplete, "all classes can complete the existing onboarding tasks");
            Assert.IsFalse(s.SliceComplete, "onboarding does not terminate survival");
            Assert.AreEqual(classId, s.PlayerProgression.ClassId, "onboarding retains the chosen class");
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
