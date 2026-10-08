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
            Assert.IsFalse(MilestoneALaunch.TryAccept(0, "breach_field", "standard", out string seedReason));
            StringAssert.Contains("non_slice_launch", seedReason);
            StringAssert.Contains("seed=0", seedReason);

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
            Assert.IsTrue(MilestoneALaunch.TryAccept(seed, "breach_field", "standard", out _), "any seed is a valid New Run seed (Phase 1.5)");
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

        [Test]
        public void HomeEmergencyStoresHoldFoodWaterAndMedicineSeparateFromTheRepairCache()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            MilestoneALaunch.ApplyHubPaths(deps);
            var session = RunSession.Create(deps);
            var stores = session.LootContainers.Single(c => c.ContainerId == "start_supply_c");
            Assert.AreNotEqual(session.LootContainers.Single(c => c.ContainerId == "start_supply_a").GlobalPosition, stores.GlobalPosition);
            Assert.IsTrue(stores.TryInteract(stores.GlobalPosition));
            Assert.AreEqual(5, session.InventoryState.GetQuantity("ration_pack"));
            Assert.AreEqual(4, session.InventoryState.GetQuantity("purified_water"));
            Assert.AreEqual(1, session.InventoryState.GetQuantity("field_medkit"));
            Assert.AreEqual(2, session.InventoryState.GetQuantity("bandage_kit"));
            Assert.AreEqual(2, session.InventoryState.GetQuantity("rad_patch"), "a radiation cure is on the hub");
            Assert.Less(session.InventoryState.GetLoadRatio(), 0.2, "the stores are light enough that the repair haul still fits the bag");
            Assert.AreEqual(0, session.InventoryState.GetQuantity("welder"), "the repair tools stay in the maintenance cache");
            Assert.IsFalse(stores.TryInteract(stores.GlobalPosition), "authored stores pay once");
        }

        [TestCase(42L)]
        [TestCase(777L)]
        public void FirstWreckAlwaysCarriesDeterministicEmergencyStores(long seed)
        {
            var contract = new FirstRunContract();
            Assert.IsTrue(contract.LoadContract());
            for (long size = 0; size <= 2; size++)
                for (long condition = 0; condition <= 2; condition++)
                {
                    GdDict Stores(out ShipDocuments docs)
                    {
                        var generator = new ShipGenerator();
                        generator.ConfigureRunContext("breach_field", "standard");
                        docs = (ShipDocuments)new FirstRunAwayGate.PatchedGenerator(generator, contract).GenerateFromSeed(seed, size, condition);
                        var found = docs.GameplaySlice.GetArrayOrEmpty("loot_containers").OfType<GdDict>()
                            .Where(c => c.GetString("id") == FirstRunAwayGate.FirstWreckStoresId).ToList();
                        Assert.AreEqual(1, found.Count, "seed " + seed + " size " + size + " condition " + condition + ": exactly one stores container");
                        return found[0];
                    }
                    GdDict first = Stores(out ShipDocuments docs1);
                    GdDict second = Stores(out _);
                    Assert.AreEqual(GdJson.Stringify(first), GdJson.Stringify(second), "the patched stores are deterministic");
                    foreach (string item in new[] { "ration_pack", "purified_water", "field_medkit", "bandage_kit", "rad_patch" })
                        Assert.IsTrue(first.GetArrayOrEmpty("contents").OfType<GdDict>().Any(c => c.GetString("item_id") == item), item);
                    string room = first.GetString("room_id");
                    string cell = GdJson.Stringify(LayoutSerializer.ParseSlotCell(first.Get("approach_cell")));
                    Assert.AreNotEqual(docs1.GameplaySlice.GetString("start_room"), room, "the stores are not in the arrival room");
                    foreach (GdDict other in docs1.GameplaySlice.GetArrayOrEmpty("loot_containers").OfType<GdDict>().Where(c => c != first && c.GetString("room_id") == room))
                        Assert.AreNotEqual(cell, GdJson.Stringify(LayoutSerializer.ParseSlotCell(other.Get("approach_cell"))), "no two containers share a cell");
                    Assert.AreEqual("", FirstRunAwayGate.RejectReason(contract, docs1.Layout, docs1.GameplaySlice, condition), "the stores do not disturb the first-run contract");
                }
        }

        [Test]
        public void SurvivalLootOverlayAddsRadPatchesWithoutChangingTheSyncedTables()
        {
            GdDict plain = LootRoller.LoadTables(), overlaid = LootRoller.LoadTablesWithOverlays();
            bool Has(GdDict tables, string table, string item) => tables.GetDictOrEmpty(table).GetArrayOrEmpty("entries").OfType<GdDict>().Any(e => e.GetString("item_id") == item);
            Assert.IsFalse(Has(plain, "generic_locker", "rad_patch"), "loot_tables.json stays identical to the Godot parity fixtures");
            Assert.IsTrue(Has(overlaid, "generic_locker", "rad_patch"));
            Assert.IsTrue(Has(overlaid, "hidden_cache", "rad_patch"));
            Assert.IsTrue(Has(overlaid, "generic_locker", "bandage_kit"));
            Assert.IsTrue(Has(overlaid, "generic_locker", "field_surgery_manual"), "the book overlay still applies");
        }

        [Test]
        public void OverlaysNeverTouchTheProgressionSalvageTables()
        {
            // The propulsion parts (thruster_nozzle, fuel_line) come from fixed rolls of salvage_engineering. Adding entries to
            // a table changes its total weight and so every pick, which silently removes parts the journey depends on.
            GdDict plain = LootRoller.LoadTables(), overlaid = LootRoller.LoadTablesWithOverlays();
            foreach (var pair in plain)
            {
                string key = V.Str(pair.Key);
                if (!key.StartsWith("salvage_")) continue;
                Assert.AreEqual(GdJson.Stringify(plain.GetDictOrEmpty(key)), GdJson.Stringify(overlaid.GetDictOrEmpty(key)),
                    key + " must stay identical under the Unity-only overlays");
            }
        }

        [Test]
        public void RadPatchCanBeCompoundedAtTheMedbay()
        {
            GdDict recipe = CatalogRegistry.LoadDict("res://data/recipes/recipe_definitions.json").GetArrayOrEmpty("recipes").OfType<GdDict>()
                .Single(r => r.GetString("recipe_id") == "craft_rad_patch");
            Assert.AreEqual("medbay", recipe.GetString("station_kind"));
            Assert.AreEqual("rad_patch", recipe.GetDictOrEmpty("produces").GetString("item_id"));
            Assert.AreEqual(0, recipe.GetInt("required_skill_level"), "no class is locked out of the radiation cure");
        }

        [TestCase("engineer", false)] [TestCase("engineer", true)]
        [TestCase("mechanic", false)] [TestCase("mechanic", true)]
        [TestCase("medic", false)] [TestCase("medic", true)]
        [TestCase("pilot", false)] [TestCase("pilot", true)]
        [TestCase("scientist", false)] [TestCase("scientist", true)]
        [TestCase("cook", false)] [TestCase("cook", true)]
        [TestCase("security", false)] [TestCase("security", true)]
        [TestCase("communications", false)] [TestCase("communications", true)]
        [TestCase("salvage_captain", false)] [TestCase("salvage_captain", true)]
        [TestCase("field_medic", false)] [TestCase("field_medic", true)]
        [TestCase("signal_specialist", false)] [TestCase("signal_specialist", true)]
        public void EveryStartingClassCanRepairTheFlightPathAtSkillDependentQuality(string classId, bool hubTraining)
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
            double lowest = 1.0;
            for (int guard = 0; guard < 24; guard++)
            {
                // Broken parts first, lowest requirement first. Improving an earlier reduced-quality repair would spend parts the rest need.
                var next = s.RepairPoints.Where(r => required.Contains(r.SystemId) && r.CanBeginRepair() && !SubOf(r).IsFunctional())
                    .OrderBy(r => r.MinSkill).FirstOrDefault();
                next ??= s.RepairPoints.Where(r => r.CanBeginRepair() && !SubOf(r).IsFunctional()).OrderBy(r => r.MinSkill).FirstOrDefault();
                if (next == null) break;
                long skill = s.PlayerProgression.GetSkillLevel("repair");
                Assert.IsTrue(next.TryStart(next.GlobalPosition));
                Assert.IsTrue(next.Channeling, "repair is universal: skill " + skill + " may start a skill-" + next.MinSkill + " repair");
                next.AdvanceChannel(240.0);
                Assert.IsTrue(SubOf(next).IsFunctional(), "the channel finishes and the part works");
                Assert.AreEqual(ShipSubcomponent.QualityFor(skill, next.MinSkill), SubOf(next).Health, 1e-9,
                    "health follows the skill deficit at the time the repair started");
                lowest = System.Math.Min(lowest, SubOf(next).Health);
                completed.Add(next.SystemId + "." + next.SubcomponentId);
            }
            bool ready = required.All(id => s.ShipSystemsManager.IsOperational(id));
            TestContext.WriteLine(classId + ": hubTraining=" + hubTraining + " initial=" + initial + " earned=" + s.PlayerProgression.GetSkillLevel("repair")
                + " ready=" + ready + " lowestHealth=" + lowest + " completed=" + string.Join(",", completed));
            Assert.IsTrue(ready, "every class reaches travel readiness; skill changes speed and quality, not access");
            GdDict capacity = s.TravelCapability();
            Assert.IsTrue(capacity.GetBool("success"), classId + ": an operational flight path must also pass the capacity check ("
                + capacity.GetString("reason") + ", supported " + capacity.GetFloat("supported_kg") + " kg for " + capacity.GetFloat("total_mass_kg") + " kg)");
            Assert.IsFalse(s.SliceComplete, "repairing for travel is not extraction");
            for (int guard = 0; guard < 24 && !s.HomeObjectivesComplete && !s.SliceComplete; guard++)
            {
                var objective = s.Interactables.First(o => o.Active && !o.Completed);
                Assert.IsTrue(objective.TryInteract(objective.GlobalPosition));
            }
            Assert.IsTrue(s.HomeObjectivesComplete, "all classes can complete the existing onboarding tasks");
            Assert.IsFalse(s.SliceComplete, "onboarding does not terminate survival");
            Assert.AreEqual(classId, s.PlayerProgression.ClassId, "onboarding retains the chosen class");
        }

        [TestCase("engineer", false)] [TestCase("engineer", true)]
        [TestCase("mechanic", false)] [TestCase("mechanic", true)]
        [TestCase("medic", false)] [TestCase("medic", true)]
        [TestCase("pilot", false)] [TestCase("pilot", true)]
        [TestCase("scientist", false)] [TestCase("scientist", true)]
        [TestCase("cook", false)] [TestCase("cook", true)]
        [TestCase("security", false)] [TestCase("security", true)]
        [TestCase("communications", false)] [TestCase("communications", true)]
        [TestCase("salvage_captain", false)] [TestCase("salvage_captain", true)]
        [TestCase("field_medic", false)] [TestCase("field_medic", true)]
        [TestCase("signal_specialist", false)] [TestCase("signal_specialist", true)]
        public void EveryStartingClassCanActuallyTravelToAWreckAfterRepairingTheFlightPath(string classId, bool hubTraining)
        {
            var deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            MilestoneALaunch.ApplyHubPaths(deps);
            deps.StartingClassId = classId;
            var s = RunSession.Create(deps);
            var cache = s.LootContainers.Single(c => c.ContainerId == "start_supply_a");
            Assert.IsTrue(cache.TryInteract(cache.GlobalPosition));
            if (hubTraining)
                for (int guard = 0; guard < 12 && s.CurrentObjectiveSequence <= 3; guard++)
                {
                    var objective = s.Interactables.First(o => o.Active && !o.Completed);
                    Assert.IsTrue(objective.TryInteract(objective.GlobalPosition));
                }
            var required = new[] { "power", "navigation", "scanners", "propulsion" };
            for (int guard = 0; guard < 24; guard++)
            {
                var next = s.RepairPoints.Where(r => required.Contains(r.SystemId) && r.CanBeginRepair() && !SubOf(r).IsFunctional())
                    .OrderBy(r => r.MinSkill).FirstOrDefault();
                if (next == null) break;
                Assert.IsTrue(next.TryStart(next.GlobalPosition));
                next.AdvanceChannel(240.0);
            }
            Assert.IsTrue(required.All(id => s.ShipSystemsManager.IsOperational(id)), classId + ": flight path is operational");
            s.ThreatManager.Threats.Clear();
            GdDict capacity = s.TravelCapability();
            Assert.IsTrue(capacity.GetBool("success"), classId + ": capacity " + capacity.GetString("reason") + " supported "
                + capacity.GetFloat("supported_kg") + " kg for " + capacity.GetFloat("total_mass_kg") + " kg");
            GdDict travelled = null;
            foreach (string id in s.ScannableMarkerIds()) { travelled = s.TravelToMarkerId(id); if (travelled.GetBool("success")) break; }
            Assert.IsNotNull(travelled, classId + ": a wreck is in scanner range");
            Assert.IsTrue(travelled.GetBool("success"), classId + ": travel refused: " + V.Str(travelled.Get("reason", "")));
            Assert.IsTrue(s.AwayFromStart);
        }

        [TestCase("mechanic")] // control: repair 4 meets every requirement, so the reactor is repaired to full health
        [TestCase("engineer")] [TestCase("medic")] [TestCase("pilot")] [TestCase("scientist")] [TestCase("cook")]
        [TestCase("security")] [TestCase("communications")] [TestCase("salvage_captain")] [TestCase("field_medic")] [TestCase("signal_specialist")]
        public void EveryClassCanStartFoodProductionAfterRepairingTheReactor(string classId)
        {
            var deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            MilestoneALaunch.ApplyHubPaths(deps);
            deps.StartingClassId = classId;
            var s = RunSession.Create(deps);
            var cache = s.LootContainers.Single(c => c.ContainerId == "start_supply_a");
            Assert.IsTrue(cache.TryInteract(cache.GlobalPosition));
            for (int guard = 0; guard < 24; guard++)
            {
                var next = s.RepairPoints.Where(r => r.CanBeginRepair() && !SubOf(r).IsFunctional()).OrderBy(r => r.MinSkill).FirstOrDefault();
                if (next == null) break;
                Assert.IsTrue(next.TryStart(next.GlobalPosition));
                next.AdvanceChannel(240.0);
            }
            for (int i = 0; i < 40; i++) s.Tick(TickContext.Frame(.05, rig.Scene.PlayerPosition)); // past the slow-band recompute
            Assert.IsTrue(s.SustenanceStationsPowered(), classId + ": a reduced-quality reactor repair must still power hydroponics and the recycler");
            Assert.IsTrue(s.ProductionStations.Any(p => p.StationKind == "hydroponics" || p.StationKind == "water_recycler"), "the home has production stations to power");
        }

        static ShipSubcomponent SubOf(RepairPoint r) => r.TargetManager.GetSystem(r.SystemId).GetSubcomponent(r.SubcomponentId);

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
