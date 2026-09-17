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
            var defaults = new RunSessionDeps();
            deps.HomeLifeSupportPowerFloor = defaults.HomeLifeSupportPowerFloor;
            deps.HomeSuitAirReserveSeconds = defaults.HomeSuitAirReserveSeconds;
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

        [Test]
        public void GodotHarness_KeepsTheSuitOutOfShipAtmosphere()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig _);
            Assert.AreEqual(0.0, deps.HomeSuitAirReserveSeconds, "parity sessions run Godot's behaviour");
            Assert.Greater(new RunSessionDeps().HomeSuitAirReserveSeconds, 0.0, "the game default carries the reserve");
        }
    }
}
