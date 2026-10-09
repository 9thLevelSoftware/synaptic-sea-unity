using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// Phase 1.10: the single test seam for generated New Run homes. A headless session is booted the way the playable bootstrap boots a
    /// generated home (<see cref="RunLaunchRequest.GeneratedHomeRun"/>: Pristine, exterior-edge dock, the starting-home guarantee) and driven
    /// across a seed sweep (SYNAPTICSEA_SEED_SWEEP, default 100). Only external behaviour is asserted: which objectives the survivor meets,
    /// whether the home is free of oxygen hazards, whether finishing the onboarding repairs the flight path and rewards the survivor, and
    /// whether the pickups the objectives hand out sit on reachable floor of the home.
    /// </summary>
    public class GeneratedHomeSessionTests
    {
        const string Biome = "breach_field";
        const string Difficulty = "standard";
        static readonly string[] OnboardingTypes = { "recover_supplies", "restore_systems", "download_logs", "stabilize_reactor" };

        /// <summary>The generator's warnings from the last <see cref="BootGeneratedHome"/> (one "home start rejected" line per rejected attempt).</summary>
        internal static CollectingLog LastBuildLog = new CollectingLog();
        IEngineInfo _previousEngine;
        IStorage _previousStorage;
        IResourceReader _previousResources;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            _previousStorage = CoreServices.UserStorage;
            _previousResources = CoreServices.Resources;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
            CoreServices.Log = NullLog.Instance;
            CoreServices.Engine = _previousEngine;
            CoreServices.UserStorage = _previousStorage;
            CoreServices.Resources = _previousResources;
        }

        static IEnumerable<long> Seeds()
        {
            string raw = Environment.GetEnvironmentVariable("SYNAPTICSEA_SEED_SWEEP");
            int n = int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : 100;
            for (long seed = 1; seed <= n; seed++) yield return seed;
        }

        /// <summary>Boots a generated New Run home exactly as <c>PlayableBootstrap.ApplyGeneratedHome</c> builds it. Null when no home is viable.</summary>
        internal static RunSession BootGeneratedHome(long seed, out SessionHarness.Rig rig, out StartSceneBuilder.HomeStart start)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out rig);
            CoreServices.UserStorage = rig.Storage;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot, rig.Storage);
            SessionHarness.OverlayGamePlayability(deps);
            var guarantee = new StartingHomeGuarantee.Spec { ClockScale = WorldClock.DefaultNewRunScale };
            LastBuildLog = new CollectingLog();
            CoreServices.Log = LastBuildLog;
            start = StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty, condition: (long)ShipBlueprint.Condition.Pristine,
                exteriorDock: true, guarantee: guarantee);
            if (start == null) return null;
            string dir = "user://runs/home-objectives-" + seed + "/";
            rig.Storage.WriteText(dir + "layout.json", start.Documents.LayoutJson ?? GdJson.Stringify(start.Documents.Layout, "  "));
            rig.Storage.WriteText(dir + "gameplay_slice.json", start.Documents.GameplaySliceJson ?? GdJson.Stringify(start.Documents.GameplaySlice, "  "));
            rig.Storage.WriteText(dir + "blueprint.json", GdJson.Stringify(start.Blueprint.ToDict(), "  "));
            deps.LayoutPath = dir + "layout.json";
            deps.GameplaySlicePath = dir + "gameplay_slice.json";
            deps.BlueprintPath = dir + "blueprint.json";
            deps.KitPath = string.IsNullOrEmpty(start.Documents.KitPath) ? RunSession.DEFAULT_KIT_PATH : start.Documents.KitPath;
            deps.RunSeed = seed;
            deps.BiomeId = Biome;
            deps.DifficultyId = Difficulty;
            deps.TimeScale = WorldClock.DefaultNewRunScale;
            RunSession s = RunSession.Create(deps);
            Assert.IsTrue(s.PlayableStarted, "seed " + seed + ": " + s.LastFailureReason);
            s.ThreatManager.Threats.Clear();
            return s;
        }

        static RunSession Boot(long seed, out SessionHarness.Rig rig)
        {
            RunSession s = BootGeneratedHome(seed, out rig, out _);
            Assert.IsNotNull(s, "seed " + seed + ": a viable generated home within the retry budget: " + string.Join(" | ", LastBuildLog.Warnings));
            return s;
        }

        [Test]
        public void TheHomeOffersTheFourOnboardingObjectivesInOrder_OnReachableFloor()
        {
            var failures = new List<string>();
            foreach (long seed in Seeds())
            {
                RunSession s = Boot(seed, out _);
                GdArray specs = s.Loader.GetObjectiveSpecsCopy();
                var types = specs.OfType<GdDict>().Select(o => o.GetString("type")).ToList();
                if (!types.SequenceEqual(OnboardingTypes)) { failures.Add("seed " + seed + ": objective types [" + string.Join(",", types) + "]"); continue; }
                GdDict junction = (GdDict)specs[1];
                GdArray steps = junction.GetArrayOrEmpty("steps");
                if (junction.GetString("kind") != "repair_junction" || steps.Count < 2) { failures.Add("seed " + seed + ": junction is not a two-step repair_junction"); continue; }
                var stepPositions = steps.OfType<GdDict>().Select(st => st.Get("position", Vec3.Inf)).OfType<Vec3>().Where(p => p != Vec3.Inf).ToList();
                if (stepPositions.Count < 2 || stepPositions.Distinct().Count() != stepPositions.Count) failures.Add("seed " + seed + ": junction steps share a position");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures.Take(10)));
        }

        [Test]
        public void TheHomeStartsWithoutAnOxygenHazard_AndAnIdleSurvivorKeepsTheirAir()
        {
            var failures = new List<string>();
            foreach (long seed in Seeds())
            {
                RunSession s = Boot(seed, out SessionHarness.Rig rig);
                if (s.BreachZoneNodes.Count != 0) failures.Add("seed " + seed + ": " + s.BreachZoneNodes.Count + " breach zone node(s) at start");
                if (s.OxygenState.BreachOpen) failures.Add("seed " + seed + ": the breach is open at start");
                double oxygenBefore = s.OxygenState.Oxygen;
                for (int i = 0; i < 240; i++)
                {
                    rig.Clock.Advance(0.25);
                    s.Tick(TickContext.Frame(0.25, rig.Scene.PlayerPosition));
                }
                if (s.OxygenState.Oxygen < oxygenBefore - 1e-6) failures.Add("seed " + seed + ": idle survivor lost suit oxygen (" + oxygenBefore + " -> " + s.OxygenState.Oxygen + ")");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures.Take(10)));
        }

        [Test]
        public void FinishingTheOnboarding_RepairsTheFlightPathRewardsTheSurvivorAndCompletesTheChain()
        {
            var failures = new List<string>();
            foreach (long seed in Seeds())
            {
                RunSession s = Boot(seed, out _);
                long repairXpBefore = s.PlayerProgression.GetSkillXp("repair");
                if (!s.CompleteAllObjectives()) { failures.Add("seed " + seed + ": CompleteAllObjectives failed at sequence " + s.CurrentObjectiveSequence); continue; }
                foreach (string type in OnboardingTypes)
                    if (!s.CompletedObjectiveTypes.Has(type)) failures.Add("seed " + seed + ": " + type + " never completed");
                foreach ((string system, string sub) in new[] { ("power", "power_distribution"), ("power", "battery_cells"), ("navigation", "nav_computer"), ("power", "reactor_core") })
                {
                    ShipSubcomponent sc = s.ShipSystemsManager.GetSystem(system)?.GetSubcomponent(sub);
                    if (sc == null || sc.Health < 1.0 - 1e-9) failures.Add("seed " + seed + ": " + system + "." + sub + " health " + (sc?.Health.ToString() ?? "missing"));
                }
                if (!s.HomeObjectivesComplete) failures.Add("seed " + seed + ": the home chain is not complete");
                // Finishing the chain hands the home its own controls (commissioning the engine, mooring); each must stand on a floor cell of the home.
                List<Vec3> floors = AssemblyMobility.Floors(s.Loader.LayoutDoc);
                foreach (SessionInteractable control in s.HomeJoinControls)
                {
                    Vec3 at = control.LocalPosition;
                    if (!floors.Any(f => Math.Abs(f.X - at.X) <= StructuralEdgeCompiler.CELL_SIZE * 0.5 + 1e-6 && Math.Abs(f.Z - at.Z) <= StructuralEdgeCompiler.CELL_SIZE * 0.5 + 1e-6))
                        failures.Add("seed " + seed + ": home control " + control.NodeName + " at " + at + " is not over a floor cell");
                }
                if (s.PlayerProgression.GetSkillXp("repair") <= repairXpBefore && s.TrainingEventBus.GetTotalXpDelivered() <= 0)
                    failures.Add("seed " + seed + ": no experience was granted for the repairs");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures.Take(10)));
        }

        [Test]
        public void ThePickupsTheObjectivesHandOut_SitOnTheHomesFloor_NotNextToTheSurvivor()
        {
            var failures = new List<string>();
            foreach (long seed in Seeds())
            {
                RunSession s = Boot(seed, out SessionHarness.Rig rig);
                GdDict layout = s.Loader.LayoutDoc;
                var floor = new List<Vec3>();
                foreach (object recordV in layout.GetDictOrEmpty("structural_plan").GetDictOrEmpty("occupancy").Values)
                    if (recordV is GdDict record && WalkabilityContract.OccupancyWorldPosition(record) is Vec3 p && p != Vec3.Inf) floor.Add(p);
                foreach ((string name, SessionInteractable pickup) in new[] { ("tool pickup", (SessionInteractable)s.ToolPickup), ("calibrator", s.JunctionCalibratorPickup) })
                {
                    if (pickup == null) { failures.Add("seed " + seed + ": no " + name); continue; }
                    Vec3 g = pickup.GlobalPosition;
                    bool onFloor = floor.Any(f => Math.Abs(f.X - g.X) <= StructuralEdgeCompiler.CELL_SIZE * 0.5 + 1e-6 && Math.Abs(f.Z - g.Z) <= StructuralEdgeCompiler.CELL_SIZE * 0.5 + 1e-6);
                    if (!onFloor) failures.Add("seed " + seed + ": " + name + " at " + g + " is not over a floor cell of the home");
                }
            }
            Assert.IsEmpty(failures, string.Join("\n", failures.Take(10)));
        }
    }
}
