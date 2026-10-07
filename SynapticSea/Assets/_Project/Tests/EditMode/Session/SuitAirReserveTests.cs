using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// Decision 56: on the home ship the suit supplies the player's air while the ship atmosphere is fouled. Generated
    /// New Run homes start with several breaches; without the reserve an idle player suffocated in 24–30 s with a full
    /// Suit O2 meter on screen.
    /// </summary>
    public class SuitAirReserveTests
    {
        const double Step = 0.25;

        IEngineInfo _previousEngine;
        IStorage _previousStorage;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            _previousStorage = CoreServices.UserStorage;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Engine = _previousEngine;
            CoreServices.UserStorage = _previousStorage;
        }

        /// <summary>Boots a generated New Run home ship (the Playable bootstrap path) with the game's default tuning.</summary>
        static RunSession BootGeneratedHome(long seed, string biome, string difficulty, out SessionHarness.Rig rig)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out rig);
            CoreServices.UserStorage = rig.Storage;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot, rig.Storage);
            SessionHarness.OverlayGamePlayability(deps);
            StartSceneBuilder.HomeStart start = StartSceneBuilder.BuildHomeStart(seed, biome, difficulty);
            Assert.IsNotNull(start, "a viable generated home");
            const string dir = "user://runs/suit-air-test/";
            rig.Storage.WriteText(dir + "layout.json", start.Documents.LayoutJson ?? GdJson.Stringify(start.Documents.Layout, "  "));
            rig.Storage.WriteText(dir + "gameplay_slice.json", start.Documents.GameplaySliceJson ?? GdJson.Stringify(start.Documents.GameplaySlice, "  "));
            rig.Storage.WriteText(dir + "blueprint.json", GdJson.Stringify(start.Blueprint.ToDict(), "  "));
            deps.LayoutPath = dir + "layout.json";
            deps.GameplaySlicePath = dir + "gameplay_slice.json";
            deps.BlueprintPath = dir + "blueprint.json";
            deps.KitPath = string.IsNullOrEmpty(start.Documents.KitPath) ? RunSession.DEFAULT_KIT_PATH : start.Documents.KitPath;
            deps.RunSeed = start.Seed;
            deps.BiomeId = biome;
            deps.DifficultyId = difficulty;
            RunSession s = RunSession.Create(deps);
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            s.ThreatManager.Threats.Clear();
            return s;
        }

        static void TickFor(RunSession s, SessionHarness.Rig rig, double seconds)
        {
            for (int i = 0; i < seconds / Step; i++)
            {
                rig.Clock.Advance(Step);
                s.Tick(TickContext.Frame(Step, rig.Scene.PlayerPosition));
            }
        }

        [Test]
        public void GeneratedHome_FouledAirDrainsTheSuitInsteadOfHealth()
        {
            RunSession s = BootGeneratedHome(3, "", "standard", out SessionHarness.Rig rig);
            TickFor(s, rig, 20.0);
            Assert.Greater(s.LifeSupportExpandedState.GetHealthDrainPerSecond(), 0.0, "the breached home's air is fouled by 20 s");
            Assert.IsTrue(s.SuitFilteringShipAir, "the suit supplies the player's air");
            Assert.Less(s.OxygenState.Oxygen, 100.0, "the Suit O2 meter shows the reserve being used");
            Assert.Greater(s.OxygenState.Oxygen, 50.0, "a 150 s reserve lasts well past 20 s");
            Assert.AreEqual(100.0, s.VitalsState.Health, 1e-9, "no atmosphere health drain while the suit has air (Godot: dead at about 24 s)");
            Assert.IsFalse(s.SliceComplete);
        }

        [Test]
        public void GeneratedHome_AnEmptySuitLetsTheFouledAirHurtAgain()
        {
            RunSession s = BootGeneratedHome(3, "", "standard", out SessionHarness.Rig rig);
            TickFor(s, rig, 15.0);
            Assert.Greater(s.HomeAtmosphereSeverity(), 0.0);
            s.OxygenState.Oxygen = 0.0;
            double health = s.VitalsState.Health;
            TickFor(s, rig, 1.0);
            Assert.IsFalse(s.SuitFilteringShipAir);
            Assert.Less(s.VitalsState.Health, health - 1.0, "with the suit empty the atmosphere drain applies");
        }

        [Test]
        public void HarderDifficulty_EmptiesTheSuitFaster()
        {
            RunSession standard = BootGeneratedHome(3, "", "standard", out SessionHarness.Rig rigStandard);
            TickFor(standard, rigStandard, 20.0);
            double standardSuit = standard.OxygenState.Oxygen;
            CatalogRegistry.Clear();
            RunSession deep = BootGeneratedHome(3, "", "deep_dive", out SessionHarness.Rig rigDeep);
            TickFor(deep, rigDeep, 20.0);
            Assert.Greater(deep.HomeAtmosphereSeverity(), 0.0);
            Assert.Less(deep.OxygenState.Oxygen, standardSuit, "the hazard dial scales the reserve drain");
        }

        static RunSession BootShelter(out SessionHarness.Rig rig)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out rig);
            CoreServices.UserStorage = rig.Storage;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot, rig.Storage);
            SessionHarness.OverlayGamePlayability(deps);
            var s = RunSession.Create(deps); rig.Session = s;
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            s.ForceRepairAll();
            s.LifeboatShip.SystemsManager.ApplySummary(s.ShipSystemsManager.GetSummary());
            s.LifeboatCommissioned = true;
            s.LifeSupportExpandedState.OxygenPercent = 0;
            s.LifeSupportExpandedState.Co2Percent = 100;
            return s;
        }

        static void StandOn(SessionHarness.Rig rig, ShipInstance ship)
        {
            var floor = AssemblyMobility.Floors(ship.BuiltLayout)[0];
            rig.Scene.PlayerPosition = ship.SceneRoot.GlobalTransform * (floor + new Vec3(0, .5, 0));
            rig.Session.CurrentOccupancy = ship;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void IndependentBoatUsesItsOwnAirInsteadOfFouledHome(bool away)
        {
            var s = BootShelter(out var rig);
            try
            {
                StandOn(rig, s.LifeboatShip); s.AwayFromStart = away;
                var homeAir = GdJson.Stringify(s.LifeSupportExpandedState.GetSummary());
                var boatAir = GdJson.Stringify(s.LifeboatShip.SystemsManager.GetSummary());
                s.OxygenState.Oxygen = 40;
                s.StageOxygen(1); s.StageSurvivalAttrition(1);
                Assert.AreEqual(43.5, s.OxygenState.Oxygen, 1e-9);
                Assert.AreEqual(100, s.VitalsState.Health, 1e-9);
                Assert.IsFalse(s.SuitFilteringShipAir);
                Assert.AreEqual(homeAir, GdJson.Stringify(s.LifeSupportExpandedState.GetSummary()), "selection does not alter unrelated gas");
                Assert.AreEqual(boatAir, GdJson.Stringify(s.LifeboatShip.SystemsManager.GetSummary()), "selection does not reseed local air");
            }
            finally { s.Dispose(); }
        }

        [TestCase("power")]
        [TestCase("life_support")]
        [TestCase("empty_air")]
        [TestCase("breach")]
        [TestCase("fire")]
        [TestCase("off_deck")]
        [TestCase("wrong_deck")]
        public void BoatCannotRefillWithoutActualLocalShelter(string fault)
        {
            var s = BootShelter(out var rig);
            try
            {
                StandOn(rig, s.LifeboatShip);
                s.LifeSupportExpandedState.OxygenPercent = 100; s.LifeSupportExpandedState.Co2Percent = 0;
                if (fault == "power") s.LifeboatShip.SystemsManager.DamageSubcomponent("power", "reactor_core", 1);
                if (fault == "life_support") s.LifeboatShip.SystemsManager.DamageSubcomponent("life_support", "air_recycler", 1);
                if (fault == "empty_air") ((LifeSupportSystem)s.LifeboatShip.SystemsManager.GetSystem("life_support")).OxygenState.Oxygen = 0;
                if (fault == "breach") s.LifeboatShip.GetHull().Configure(new GdDict { { "compartments", GdArray.Of(new GdDict { { "compartment_id", "boat" }, { "health", 1.0 }, { "breach_open", true } }) } });
                if (fault == "fire") s.LifeboatShip.GetFire().Ignite("boat", 1);
                if (fault == "off_deck") rig.Scene.PlayerPosition += new Vec3(400, 0, 400);
                if (fault == "wrong_deck") rig.Scene.PlayerPosition += new Vec3(0, 30, 0);
                s.OxygenState.Oxygen = 40; s.StageOxygen(1);
                Assert.Less(s.OxygenState.Oxygen, 40, "healthy home cannot supply " + fault + " boat");
            }
            finally { s.Dispose(); }
        }

        [Test]
        public void PhysicalHomeRetainsReserveAndDamageEvenWithAwayBoardedContext()
        {
            var s = BootShelter(out var rig);
            try
            {
                StandOn(rig, s.HomeShip); s.AwayFromStart = true;
                s.OxygenState.Oxygen = 40; s.StageOxygen(1);
                Assert.Less(s.OxygenState.Oxygen, 40); Assert.IsTrue(s.SuitFilteringShipAir);
                s.OxygenState.Oxygen = 0; double health = s.VitalsState.Health;
                s.StageSurvivalAttrition(1);
                Assert.Less(s.VitalsState.Health, health, "empty suit does not suppress physically owned fouled atmosphere");
            }
            finally { s.Dispose(); }
        }

        [Test]
        public void OrdinarySaveContinuePreservesDistinctHomeAndBoatAirAndResolvesBoatPose()
        {
            var s = BootShelter(out var rig);
            try
            {
                StandOn(rig, s.LifeboatShip); s.OxygenState.Oxygen = 40;
                Assert.IsTrue(s.RequestSave()); Assert.IsTrue(s.RequestLoad());
                Assert.AreEqual(0, s.LifeSupportExpandedState.OxygenPercent);
                Assert.AreEqual(100, s.LifeSupportExpandedState.Co2Percent);
                Assert.AreEqual(100, ((LifeSupportSystem)s.LifeboatShip.SystemsManager.GetSystem("life_support")).OxygenState.Oxygen);
                s.Tick(TickContext.Frame(.02, rig.Scene.PlayerPosition));
                Assert.AreSame(s.LifeboatShip, s.CurrentOccupancy);
                Assert.Greater(s.OxygenState.Oxygen, 40, "Continue rechecks actual boat services and deck");
                s.LifeboatShip.SystemsManager.DamageSubcomponent("life_support", "air_recycler", 1);
                Assert.IsTrue(s.RequestSave()); Assert.IsTrue(s.RequestLoad());
                double before = s.OxygenState.Oxygen;
                s.Tick(TickContext.Frame(.02, rig.Scene.PlayerPosition));
                Assert.Less(s.OxygenState.Oxygen, before, "Continue cannot refill an offline boat from saved home air");
            }
            finally { s.Dispose(); }
        }

        [TestCase("vented")]
        [TestCase("depressurized")]
        [TestCase("oxygen_bp")]
        public void BoatExplicitRoomPressureCannotBeOverriddenByHealthyLocalSystems(string pressureKey)
        {
            var s = BootShelter(out var rig);
            try
            {
                var boat = s.LifeboatShip;
                var loader = new FakeLoaderView(boat.BuiltLayout, new GdDict(), "")
                    { IsInsideTree = true, Transform = boat.SceneRoot.GlobalTransform };
                boat.SceneRoot = loader; StandOn(rig, boat);
                var floor = AssemblyMobility.Floors(boat.BuiltLayout)[0];
                var air = new GdDict { { "position", floor }, { "oxygen_source", "initial_hull_condition_v1" } };
                air[pressureKey] = pressureKey == "oxygen_bp" ? (object)0L : true;
                loader.Model.AuthoredAtmosphereSpecs = GdArray.Of(air);
                string beforeAir = GdJson.Stringify(air);
                s.OxygenState.Oxygen = 40; s.StageOxygen(1);
                Assert.AreEqual(34, s.OxygenState.Oxygen, 1e-9, "explicit boat pressure remains hazardous");
                Assert.AreEqual(beforeAir, GdJson.Stringify(air), "no room air reseeding");
            }
            finally { s.Dispose(); }
        }

        [Test]
        public void JoinedRecoveryHullUsesLocalAirAndServicesWithoutSharingHomeAtmosphere()
        {
            var s = BootShelter(out var rig);
            try
            {
                var boat = s.LifeboatShip;
                var joined = ShipInstance.Create("joined_air", "joined_air", new ShipBlueprint(1, 0, 17)
                    { GenerationProfile = ConstrainedExpedition.Profile }, boat.SystemsManager, boat.SceneRoot);
                joined.BuiltLayout = boat.BuiltLayout; joined.GetAccess().Claim("player_local");
                joined.ParentShip = s.HomeShip; s.HomeShip.DockedShips.Add(joined);
                s.VisitedShips[joined.MarkerId] = joined; s.CurrentShip = joined; s.AwayFromStart = false;
                StandOn(rig, joined); s.OxygenState.Oxygen = 40; s.StageOxygen(1);
                Assert.AreEqual(43.5, s.OxygenState.Oxygen, 1e-9);
                joined.SystemsManager.DamageSubcomponent("power", "reactor_core", 1);
                s.OxygenState.Oxygen = 40; s.StageOxygen(1);
                Assert.Less(s.OxygenState.Oxygen, 40, "a joined connection is not a power/air conduit");
                Assert.AreEqual(0, s.LifeSupportExpandedState.OxygenPercent);
                Assert.AreEqual(100, s.LifeSupportExpandedState.Co2Percent);
            }
            finally { s.Dispose(); }
        }

        [Test]
        public void AbsentInfrastructureKeepsLegacyHomeBehaviorButInvalidRealRootCannotRefill()
        {
            var s = BootShelter(out var rig);
            try
            {
                StandOn(rig, s.LifeboatShip);
                ((FakeShipRoot)s.LifeboatShip.SceneRoot).IsValid = false;
                s.OxygenState.Oxygen = 40; s.StageOxygen(1);
                Assert.Less(s.OxygenState.Oxygen, 40, "invalid real root is never a legacy fallback");
                s.HomeShip = null; s.LifeboatShip = null; s.CurrentShip = null; s.CurrentOccupancy = null;
                s.LifeSupportExpandedState.OxygenPercent = 100; s.LifeSupportExpandedState.Co2Percent = 0;
                s.OxygenState.Oxygen = 40; s.StageOxygen(1);
                Assert.AreEqual(43.5, s.OxygenState.Oxygen, 1e-9);
            }
            finally { s.Dispose(); }
        }

        [TestCase("vented")]
        [TestCase("depressurized")]
        public void ExplicitHomeRoomVacuumUsesFieldPressureWithoutDoubleChargingReserve(string key)
        {
            var s = BootShelter(out var rig);
            try
            {
                StandOn(rig, s.HomeShip);
                var floor = AssemblyMobility.Floors(s.HomeShip.BuiltLayout)[0];
                ((FakeLoaderView)s.Loader).Model.AuthoredAtmosphereSpecs = GdArray.Of(new GdDict
                    { { "position", floor }, { key, true }, { "oxygen_bp", 0L } });
                s.OxygenState.Oxygen = 40; s.StageOxygen(1);
                Assert.AreEqual(34, s.OxygenState.Oxygen, 1e-9, "field pressure is charged once, not plus home reserve");
            }
            finally { s.Dispose(); }
        }

        [Test]
        public void GodotHarness_KeepsTheSuitOutOfShipAtmosphere()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig _);
            Assert.AreEqual(0.0, deps.HomeLifeSupportPowerFloor, "parity sessions run Godot's behaviour");
            Assert.AreEqual(0.0, deps.HomeSuitAirReserveSeconds, "parity sessions run Godot's behaviour");
            Assert.AreEqual(0.0, deps.HomeSpawnSafety, "parity sessions run Godot's behaviour");
            var game = new RunSessionDeps();
            Assert.Greater(game.HomeLifeSupportPowerFloor, 0.0, "the game default carries the floor");
            Assert.Greater(game.HomeSuitAirReserveSeconds, 0.0, "the game default carries the reserve");
            Assert.Greater(game.HomeSpawnSafety, 0.0, "the game default carries hub spawn safety");
        }
    }
}
