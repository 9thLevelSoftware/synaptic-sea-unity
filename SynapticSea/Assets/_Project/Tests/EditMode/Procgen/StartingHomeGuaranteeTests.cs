using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Session;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>Phase 1.9: the starting-home guarantee, its shared opening damage, and the first-wreck text-mirror fix.</summary>
    public class StartingHomeGuaranteeTests
    {
        const string Biome = "breach_field";
        const string Difficulty = "standard";

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

        static ShipBlueprint HomeBlueprint(long seed) =>
            new ShipBlueprint(StartSceneBuilder.HOME_SIZE, (long)ShipBlueprint.Condition.Pristine, seed) { StartKind = HomeOpeningState.GeneratedHomeKind };

        static StartSceneBuilder.HomeStart Home(long seed, StartingHomeGuarantee.Spec spec = null) =>
            StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty, condition: (long)ShipBlueprint.Condition.Pristine, exteriorDock: true,
                guarantee: spec ?? new StartingHomeGuarantee.Spec());

        static List<string> Broken(ShipSystemsManager m) =>
            m.SystemOrder.SelectMany(id => m.Systems[id].Subcomponents.Where(s => !s.IsFunctional()).Select(s => id + "." + s.SubcomponentId)).ToList();

        // ---------------------------------------------------------------- HomeOpeningState

        [Test]
        public void OpeningStateIsExactlyWhatTheGoldenSessionRolls()
        {
            var rig = SessionHarness.CreateGolden();
            GdDict blueprint = GdJson.ParseDict(File.ReadAllText(Path.Combine(Fixtures.StreamingDataRoot, "data", "procgen", "golden", "coherent_ship_001", "blueprint.json")));
            ShipSystemsManager expected = HomeOpeningState.Build(ShipBlueprint.FromDict(blueprint));
            Assert.AreEqual(GdJson.Stringify(expected.GetSummary()), GdJson.Stringify(rig.Session.ShipSystemsManager.GetSummary()),
                "RunSession and the guarantee must share one opening-damage definition");
            Assert.That(Broken(expected), Does.Contain("propulsion.nav_linkage"));
            Assert.That(Broken(expected).Where(b => b.StartsWith("propulsion.", StringComparison.Ordinal)).ToList(), Is.EqualTo(new[] { "propulsion.nav_linkage" }));
        }

        [Test]
        public void GoldenStyleBlueprintsGetNoExtraBreaks()
        {
            var plain = new ShipBlueprint(ShipBlueprint.Size.Medium, ShipBlueprint.Condition.Pristine, 5);
            Assert.AreEqual(new[] { "propulsion.nav_linkage" }, Broken(HomeOpeningState.Build(plain)));
            Assert.IsEmpty(HomeOpeningState.ExtraBreaks(plain));
        }

        [Test]
        public void GeneratedHomeOpensWithNavLinkageAndAtMostTwoCheapExtraBreaksNeverSurvivalSystems()
        {
            var histogram = new int[HomeOpeningState.MaxExtraBreaks + 1];
            for (long seed = 1; seed <= 200; seed++)
            {
                ShipBlueprint bp = HomeBlueprint(seed);
                List<string> broken = Broken(HomeOpeningState.Build(bp));
                Assert.That(broken, Does.Contain("propulsion.nav_linkage"), "seed " + seed);
                int extras = broken.Count - 1;
                Assert.That(extras, Is.InRange(0, HomeOpeningState.MaxExtraBreaks), "seed " + seed + ": " + string.Join(",", broken));
                histogram[extras]++;
                foreach (string b in broken)
                    Assert.IsFalse(b.StartsWith("life_support.") || b.StartsWith("scanners.") || b.StartsWith("gravity."), "seed " + seed + " broke " + b);
                Assert.AreEqual(broken, Broken(HomeOpeningState.Build(HomeBlueprint(seed))), "deterministic, seed " + seed);
            }
            TestContext.WriteLine("extra breaks 0/1/2: " + string.Join("/", histogram));
            Assert.That(histogram.Count(n => n > 0), Is.EqualTo(histogram.Length), "all of 0, 1 and 2 extra breaks occur across 200 seeds");
        }

        [Test]
        public void StartKindOnlyPersistsWhenSet()
        {
            Assert.IsFalse(new ShipBlueprint().ToDict().Has("start_kind"));
            GdDict withKind = HomeBlueprint(3).ToDict();
            Assert.AreEqual(HomeOpeningState.GeneratedHomeKind, ShipBlueprint.FromDict(withKind).StartKind);
        }

        // ---------------------------------------------------------------- Compute

        static ShipSystemsManager Manager(params (string System, string Sub)[] broken)
        {
            var manager = new ShipSystemsManager();
            manager.Configure(manager.LoadDefinitions(), 0, 0);
            foreach ((string system, string sub) in broken) manager.GetSystem(system).GetSubcomponent(sub).Health = ShipSystemsManager.DAMAGED_HEALTH;
            return manager;
        }

        static long Count(StartingHomeGuarantee.Plan plan, string id) => plan.Totals().TryGetValue(id, out long n) ? n : 0;

        [Test]
        public void KitCoversEveryBrokenPartWithSparesAndToolsOnce()
        {
            var plan = StartingHomeGuarantee.Compute(Manager(("propulsion", "nav_linkage"), ("power", "power_distribution"), ("power", "reactor_core")), new StartingHomeGuarantee.Spec());
            Assert.IsTrue(plan.Ok, plan.Reason);
            Assert.AreEqual(2, Count(plan, "circuit_board"), "nav_linkage needs one, one spare");
            Assert.AreEqual(2, Count(plan, "power_cell"), "power_distribution needs one, one spare");
            Assert.AreEqual(1, Count(plan, "reactor_core"), "heavy parts get no spare");
            Assert.AreEqual(1, Count(plan, "welder"));
            Assert.AreEqual(1, Count(plan, "plasma_cutter"));
            Assert.AreEqual(1, Count(plan, "thruster_nozzle"), "OPEN-3: one flight part beyond what the repairs consume");
            Assert.AreEqual(1, Count(plan, "fuel_line"));
            Assert.AreEqual(0, Count(plan, "data_core"), "nothing broken needs one");
        }

        [Test]
        public void FlightPartsAreAlwaysGuaranteedEvenWhenNothingNeedsThem()
        {
            var plan = StartingHomeGuarantee.Compute(Manager(("propulsion", "nav_linkage")), new StartingHomeGuarantee.Spec());
            Assert.IsTrue(plan.Ok, plan.Reason);
            Assert.AreEqual(1, Count(plan, "thruster_nozzle"));
            Assert.AreEqual(1, Count(plan, "fuel_line"));
            Assert.AreEqual(0, Count(plan, "welder"), "nav_linkage needs no tool");
        }

        [Test]
        public void ABrokenFlightPartKeepsItsOwnPartAndTheExtra()
        {
            var plan = StartingHomeGuarantee.Compute(Manager(("propulsion", "thruster_array"), ("propulsion", "fuel_injection")), new StartingHomeGuarantee.Spec());
            Assert.IsTrue(plan.Ok, plan.Reason);
            Assert.AreEqual(2, Count(plan, "thruster_nozzle"));
            Assert.AreEqual(2, Count(plan, "fuel_line"), "one consumed by the repair, plus one spare (the light-part spare and the flight extra coincide)");
        }

        [Test]
        public void FoodAndWaterFollowTheLiveSurvivalRates()
        {
            var spec = new StartingHomeGuarantee.Spec();
            var plan = StartingHomeGuarantee.Compute(Manager(("propulsion", "nav_linkage")), spec);
            Assert.IsTrue(plan.Ok, plan.Reason);

            // Independent derivation from the data files.
            SurvivalTuning tuning = SurvivalTuning.FromDict(CatalogRegistry.LoadDict(SurvivalTuning.Path));
            GdDict defs = ItemDefs.LoadDefinitions();
            double hunger = tuning.HungerPerGameHour, thirst = tuning.ThirstPerGameHour;
            double rationHunger = ItemDefs.GetDefinition(defs, "ration_pack").GetFloat("hunger_restore");
            double rationThirst = ItemDefs.GetDefinition(defs, "ration_pack").GetFloat("thirst_restore");
            double waterThirst = ItemDefs.GetDefinition(defs, "purified_water").GetFloat("thirst_restore");
            long rations = (long)Math.Ceiling(Math.Max(0, hunger * 30 - 75) / rationHunger * spec.Margin);
            long water = (long)Math.Ceiling(Math.Max(0, thirst * 30 - 60 - rations * rationThirst) / waterThirst * spec.Margin);
            Assert.AreEqual(rations, plan.Rations);
            Assert.AreEqual(water, plan.Water);
            Assert.AreEqual(rations, Count(plan, "ration_pack"));
            Assert.AreEqual(water, Count(plan, "purified_water"));
            TestContext.WriteLine("30 game hours: " + plan.Rations + " rations + " + plan.Water + " water, stores " + plan.StoresKg.ToString("0.0") + " kg");

            // A hungrier game needs more food: the rates are read, not hard-coded.
            var hungrier = new SurvivalTuning { HungerPerGameHour = tuning.HungerPerGameHour * 2, ThirstPerGameHour = tuning.ThirstPerGameHour * 2 };
            var more = StartingHomeGuarantee.Compute(Manager(("propulsion", "nav_linkage")), new StartingHomeGuarantee.Spec { Tuning = hungrier });
            Assert.IsTrue(more.Ok, more.Reason);
            Assert.Greater(more.Rations, plan.Rations);
            Assert.Greater(more.Water, plan.Water);
        }

        [Test]
        public void RealTimePacingIsRefused()
        {
            var plan = StartingHomeGuarantee.Compute(Manager(("propulsion", "nav_linkage")), new StartingHomeGuarantee.Spec { ClockScale = WorldClock.DefaultScale });
            Assert.IsFalse(plan.Ok);
            Assert.AreEqual(StartingHomeGuarantee.ReasonScaleUnsupported, plan.Reason);
        }

        [Test]
        public void AnOverweightKitFailsClosed()
        {
            var plan = StartingHomeGuarantee.Compute(Manager(("propulsion", "nav_linkage")), new StartingHomeGuarantee.Spec { MaxContainerKg = 1.0 });
            Assert.IsFalse(plan.Ok);
            Assert.AreEqual(StartingHomeGuarantee.ReasonOverweight, plan.Reason);
        }

        [Test]
        public void ACapacityShortfallFailsClosed()
        {
            var plan = StartingHomeGuarantee.Compute(Manager(("propulsion", "nav_linkage")), new StartingHomeGuarantee.Spec { ExtraCargoKg = 100000.0 });
            Assert.IsFalse(plan.Ok);
            Assert.AreEqual(StartingHomeGuarantee.ReasonCapacity, plan.Reason);
        }

        [Test]
        public void CapacityBackstopMatchesTheRealLifeboatCheck()
        {
            // Skill 0 repairs of every broken travel part, then the real AssemblyMobility.Evaluate on a 3-floor boat must agree with the plan's margin.
            ShipSystemsManager manager = HomeOpeningState.Build(HomeBlueprint(11));
            var plan = StartingHomeGuarantee.Compute(manager, new StartingHomeGuarantee.Spec());
            Assert.IsTrue(plan.Ok, plan.Reason);
            foreach (string systemId in new[] { "power", "navigation", "propulsion" })
                foreach (ShipSubcomponent sub in manager.GetSystem(systemId).Subcomponents)
                    if (!sub.IsFunctional()) sub.Health = sub.RepairQuality(0);
            var ship = ShipInstance.Create("boat", "", null, manager, new FakeRoot());
            var floors = new GdArray();
            for (int i = 0; i < StartingHomeGuarantee.LifeboatFloorCells; i++) floors.Add(new GdDict { { "position", new Vec3(i * 4, 0, 0) } });
            ship.BuiltLayout = new GdDict { { "structural_plan", new GdDict { { "floor_placements", floors } } } };
            ship.Mobility = AssemblyMobility.CreateSpecification(ship, true);
            GdDict report = AssemblyMobility.Evaluate(ship, plan.RepairKitKg + plan.StoresKg);
            Assert.IsTrue(report.GetBool("success"), report.GetString("reason"));
            Assert.AreEqual(report.GetFloat("margin_kg"), plan.CapacityMarginKg, 1e-6);
            Assert.AreEqual(3, StartingHomeGuarantee.LifeboatFloorCells);
        }

        // ---------------------------------------------------------------- placement

        [Test]
        public void PlacementIsDeterministicAndByteIdentical()
        {
            for (long seed = 1; seed <= 6; seed++)
            {
                StartSceneBuilder.HomeStart a = Home(seed), b = Home(seed);
                Assert.IsNotNull(a, "seed " + seed);
                Assert.AreEqual(a.Seed, b.Seed);
                Assert.AreEqual(GdJson.Stringify(a.Documents.GameplaySlice), GdJson.Stringify(b.Documents.GameplaySlice));
                Assert.AreEqual(a.Documents.GameplaySliceJson, b.Documents.GameplaySliceJson);
                Assert.AreEqual(a.Documents.LayoutJson, b.Documents.LayoutJson);
            }
        }

        [Test]
        public void AcceptedHomesPassTheIndependentValidationAndCarryTheGeneratedHomeKind()
        {
            for (long seed = 1; seed <= 10; seed++)
            {
                StartSceneBuilder.HomeStart home = Home(seed);
                Assert.IsNotNull(home, "seed " + seed);
                Assert.AreEqual(HomeOpeningState.GeneratedHomeKind, home.Blueprint.StartKind);
                Assert.IsNotNull(home.Guarantee);
                Assert.AreEqual("", StartingHomeGuarantee.Validate(home.Documents, home.Blueprint, new StartingHomeGuarantee.Spec()), "seed " + seed);
                Assert.AreEqual(HomeOpeningState.GeneratedHomeKind, ShipBlueprint.FromDict(home.Blueprint.ToDict()).StartKind, "the run-directory blueprint keeps the kind");
            }
        }

        [Test]
        public void ValidateCatchesATamperedHome()
        {
            StartSceneBuilder.HomeStart home = Home(3);
            Assert.IsNotNull(home);
            // Remove a required part from a cache.
            GdDict cache = home.Documents.GameplaySlice.GetArrayOrEmpty("loot_containers").OfType<GdDict>().First(c => V.Str(c.Get("id", "")) == StartingHomeGuarantee.RepairCacheAId);
            GdArray contents = cache.GetArrayOrEmpty("contents");
            contents.RemoveAt(0);
            home.Documents.GameplaySliceJson = GdJson.Stringify(home.Documents.GameplaySlice, "  ");
            Assert.AreNotEqual("", StartingHomeGuarantee.Validate(home.Documents, home.Blueprint, new StartingHomeGuarantee.Spec()));
        }

        [Test]
        public void ARealTimeHomeBootRejectsEverySeed()
        {
            StartSceneBuilder.HomeStart home = Home(3, new StartingHomeGuarantee.Spec { ClockScale = WorldClock.DefaultScale });
            Assert.IsNull(home, "scale 1.0 cannot supply the food and water, so no seed is viable");
        }

        // ---------------------------------------------------------------- the first-wreck text mirror

        [Test]
        public void FirstRunPatchKeepsTheSliceTextMirrorInSync()
        {
            var contract = new FirstRunContract();
            Assert.IsTrue(contract.LoadContract());
            var generator = new ShipGenerator();
            generator.ConfigureRunContext(Biome, Difficulty);
            ShipDocuments docs = generator.GenerateFromSeed(42, (long)ShipBlueprint.Size.Small, (long)ShipBlueprint.Condition.Damaged);
            Assert.IsNotNull(docs);
            FirstRunAwayGate.Patch(contract, docs);
            Assert.That(docs.GameplaySlice.GetArrayOrEmpty("loot_containers").OfType<GdDict>().Any(c => V.Str(c.Get("id", "")) == FirstRunAwayGate.FirstWreckStoresId),
                "the stores are on the slice document");
            Assert.AreEqual(GdJson.Stringify(docs.GameplaySlice, "  "), docs.GameplaySliceJson, "RunSession.Generation archives GameplaySliceJson, so it must match");
        }
    }
}
