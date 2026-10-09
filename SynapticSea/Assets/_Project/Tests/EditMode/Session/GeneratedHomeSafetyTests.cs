using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Phase 1.11: the safety proof behind making the generated home the Title New Run default. One seam, as in
    /// <see cref="GeneratedHomeSessionTests"/>: a booted headless generated-home session driven across a seed sweep. Only external behaviour
    /// is asserted. (1) An idle survivor is untouched for 30 simulated minutes at every supported time scale. (2) The survivor can loot the
    /// guaranteed kit, repair the lifeboat at every repair skill and leave on a real trip, and the first wreck still qualifies.
    /// (3) The guaranteed stores keep the survivor fed over a 30 game hour excursion. SYNAPTICSEA_SEED_SWEEP scales the sweeps;
    /// SYNAPTICSEA_SAFETY_REPORT writes the markdown tables behind docs/playtest/home-safety-1.11.md.
    /// </summary>
    public class GeneratedHomeSafetyTests
    {
        const double Step = 0.25;
        const double IdleSeconds = 1800.0;
        const double HungerFloor = 25.0;
        const double ThirstFloor = 40.0;

        /// <summary>The 11 starting classes; repair skill 4 mechanic, 3 engineer, 2 scientist, 1 medic/pilot/security/salvage_captain, 0 cook/communications/field_medic/signal_specialist.</summary>
        static readonly string[] Classes =
        {
            "cook", "communications", "field_medic", "signal_specialist", // repair 0
            "medic", "pilot", "security", "salvage_captain",             // repair 1
            "scientist",                                                  // repair 2
            "engineer",                                                   // repair 3
            "mechanic",                                                   // repair 4
        };

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

        static int SweepCount(int fallback)
        {
            string raw = Environment.GetEnvironmentVariable("SYNAPTICSEA_SEED_SWEEP");
            return int.TryParse(raw, out int parsed) && parsed > 0 ? Math.Min(parsed, fallback * 10) : fallback;
        }

        static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- 1. the idle proof

        /// <summary>The first thing that goes wrong while an idle survivor sits at the spawn pose, or "" when nothing does.</summary>
        static string IdleFor30Minutes(long seed, double scale, out int ticks)
        {
            RunSession s = GeneratedHomeSessionTests.BootGeneratedHome(seed, out SessionHarness.Rig rig, out _, scale, null, false);
            ticks = 0;
            if (s == null) return "no viable home";
            Vec3 pose = rig.Scene.PlayerPosition;
            double health = s.VitalsState.Health, oxygen = s.OxygenState.Oxygen, hunger = s.VitalsState.Hunger, thirst = s.VitalsState.Thirst;
            if (s.ThreatManager.Threats.Count != 0) return "the home spawned " + s.ThreatManager.Threats.Count + " threat(s)";
            if (s.AwayFromStart) return "the run starts away from the home";
            if (s.PlayerFireIntensity() > 1e-12) return "the spawn pose is inside a fire volume";
            if (s.GetActiveFireState().GetActiveFireCount() != 0) return "a fire burns at start";
            if (s.BreachZoneNodes.Count != 0 || s.OxygenState.BreachOpen) return "an oxygen hazard exists at start";
            int total = (int)(IdleSeconds / Step);
            for (int i = 0; i < total; i++)
            {
                rig.Clock.Advance(Step);
                s.Tick(TickContext.Frame(Step, pose));
                ticks = i + 1;
                double t = (i + 1) * Step;
                if (s.VitalsState.IsIncapacitated() || s.SliceComplete) return "incapacitated or finished at t=" + Fmt(t) + "s";
                if (s.ThreatManager.Threats.Count != 0) return "a threat appeared at t=" + Fmt(t) + "s";
                if (s.PlayerFireIntensity() > 1e-12 || s.GetActiveFireState().GetActiveFireCount() != 0) return "fire at t=" + Fmt(t) + "s";
                if (s.VitalsState.Health < health - 1e-9) return "health fell " + Fmt(health) + " -> " + Fmt(s.VitalsState.Health) + " at t=" + Fmt(t) + "s";
                if (s.OxygenState.Oxygen < oxygen - 1e-9) return "suit oxygen fell " + Fmt(oxygen) + " -> " + Fmt(s.OxygenState.Oxygen) + " at t=" + Fmt(t) + "s";
                if (s.RadiationState != null && s.RadiationState.Radiation > 1e-9) return "radiation " + Fmt(s.RadiationState.Radiation) + " at t=" + Fmt(t) + "s";
                if (s.VitalsState.Hunger < hunger - 1e-9 || s.VitalsState.Thirst < thirst - 1e-9)
                    return "hunger/thirst moved at home (" + Fmt(s.VitalsState.Hunger) + "/" + Fmt(s.VitalsState.Thirst) + ") at t=" + Fmt(t) + "s";
                if (s.HomeAtmosphereSeverity() > 0.0) return "home atmosphere severity " + Fmt(s.HomeAtmosphereSeverity()) + " at t=" + Fmt(t) + "s";
                if (s.SanityState != null && s.SanityState.Sanity < s.SanityState.MaxSanity - 1e-6) return "sanity fell to " + Fmt(s.SanityState.Sanity) + " at t=" + Fmt(t) + "s";
            }
            return "";
        }

        [TestCase(60.0, 20)]  // the New Run default
        [TestCase(30.0, 8)]
        [TestCase(120.0, 8)]
        public void AnIdleSurvivorIsUntouchedForThirtyMinutes_AtEverySupportedTimeScale(double scale, int defaultSeeds)
        {
            var failures = new List<string>();
            int n = SweepCount(defaultSeeds);
            for (long seed = 1; seed <= n; seed++)
            {
                string problem = IdleFor30Minutes(seed, scale, out _);
                if (problem.Length != 0) failures.Add("seed " + seed + " @" + scale + "x: " + problem);
            }
            Assert.IsEmpty(failures, string.Join("\n", failures.Take(10)));
        }

        [Test]
        public void ScaleOff_IsRefusedBecauseTheGuaranteeNeedsScaledPacing()
        {
            // Time scale off (1x) has no safe food/water guarantee: the generator refuses it so the bootstrap falls back to the golden hub.
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            CoreServices.UserStorage = rig.Storage;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot, rig.Storage);
            CoreServices.Log = new CollectingLog();
            StartSceneBuilder.HomeStart start = StartSceneBuilder.BuildHomeStart(7, "breach_field", "standard",
                condition: (long)ShipBlueprint.Condition.Pristine, exteriorDock: true,
                guarantee: new StartingHomeGuarantee.Spec { ClockScale = WorldClock.DefaultScale });
            Assert.IsNull(start, "a generated home must not be offered at 1x pacing");
        }

        [Test]
        public void DifferentSeedsGiveDifferentHomes_TheStartIsVaried()
        {
            int n = SweepCount(30);
            var hashes = new HashSet<long>();
            var failures = new List<string>();
            for (long seed = 1; seed <= n; seed++)
            {
                RunSession s = GeneratedHomeSessionTests.BootGeneratedHome(seed, out _, out StartSceneBuilder.HomeStart start, WorldClock.DefaultNewRunScale, null, false);
                if (s == null) { failures.Add("seed " + seed + ": no viable home"); continue; }
                hashes.Add(SeedDeterminismContract.Fnv1a64(start.Documents.LayoutJson ?? GdJson.Stringify(start.Documents.Layout, "  ")));
            }
            Assert.IsEmpty(failures, string.Join("\n", failures.Take(10)));
            // Not every seed is its own home: a seed the gate rejects re-rolls to seed+1, so neighbouring seeds can converge on one home.
            // Measured 23 distinct layouts in the first 30 seeds; require well over half to be distinct.
            Assert.GreaterOrEqual(hashes.Count, (int)Math.Ceiling(n * 0.6), "the home varies with the seed: " + hashes.Count + " distinct layouts in " + n + " seeds");
        }

        // ---------------------------------------------------------------- 2. the full route

        static IEnumerable<LootContainer> KitContainers(RunSession s) =>
            s.LootContainers.Where(c => c.ContainerId == StartingHomeGuarantee.RepairCacheAId
                || c.ContainerId == StartingHomeGuarantee.RepairCacheBId
                || c.ContainerId == StartingHomeGuarantee.SurvivalStoresId);

        static ShipSubcomponent SubOf(RepairPoint r) => r.TargetManager.GetSystem(r.SystemId).GetSubcomponent(r.SubcomponentId);

        /// <summary>Boots, loots the kit, (optionally) finishes the onboarding, repairs the flight path from the kit alone and leaves for a wreck. "" on success.</summary>
        static string FullRoute(long seed, string classId, bool onboarding, out string wreck)
        {
            wreck = "";
            RunSession s = GeneratedHomeSessionTests.BootGeneratedHome(seed, out SessionHarness.Rig rig, out _, WorldClock.DefaultNewRunScale, classId, false);
            if (s == null) return "no viable home";
            if (s.ThreatManager.Threats.Count != 0) return "the home spawned threats";
            var kit = KitContainers(s).ToList();
            if (kit.Count < 3) return "only " + kit.Count + " of 3 guarantee containers exist";
            foreach (LootContainer c in kit)
                if (!c.TryInteract(c.GlobalPosition)) return "could not open " + c.ContainerId;
            if (onboarding && !s.CompleteAllObjectives()) return "the onboarding chain could not be completed";
            var required = new[] { "power", "navigation", "propulsion" };
            for (int guard = 0; guard < 24; guard++)
            {
                RepairPoint next = s.RepairPoints.Where(r => required.Contains(r.SystemId) && r.CanBeginRepair() && !SubOf(r).IsFunctional())
                    .OrderBy(r => r.MinSkill).FirstOrDefault();
                if (next == null) break;
                if (!next.TryStart(next.GlobalPosition)) return "could not start repairing " + next.SystemId + "." + next.SubcomponentId;
                next.AdvanceChannel(240.0);
            }
            foreach (string id in required)
                if (!s.ShipSystemsManager.IsOperational(id))
                {
                    var missing = s.RepairPoints.Where(r => r.SystemId == id && !SubOf(r).IsFunctional()).Select(r => r.SubcomponentId + (r.CanBeginRepair() ? "" : "(blocked)"));
                    return id + " is not operational after the kit: " + string.Join(",", missing);
                }
            // The survivor boards the lifeboat to depart (the travel gate needs them aboard the piloted ship).
            Vec3 boatFloor = AssemblyMobility.Floors(s.LifeboatShip.BuiltLayout)[0];
            rig.Scene.PlayerPosition = s.LifeboatShip.SceneRoot.GlobalTransform * (boatFloor + new Vec3(0, .55, 0));
            GdDict capacity = s.TravelCapability();
            if (!capacity.GetBool("success"))
                return "capacity " + capacity.GetString("reason") + " supported " + Fmt(capacity.GetFloat("supported_kg")) + " for " + Fmt(capacity.GetFloat("total_mass_kg"));
            GdDict travelled = null;
            foreach (string id in s.ScannableMarkerIds()) { travelled = s.TravelToMarkerId(id); if (travelled.GetBool("success")) { wreck = id; break; } }
            if (travelled == null) return "no wreck in scanner range";
            if (!travelled.GetBool("success")) return "travel refused: " + V.Str(travelled.Get("reason", ""));
            if (!s.AwayFromStart) return "travel reported success but the survivor is still at the home";
            if (!s.LootContainers.Any(c => c.ContainerId == "first_wreck_stores")) return "the first wreck has no emergency stores";
            return "";
        }

        [Test]
        public void EveryClassCanLootTheKit_RepairTheLifeboatAndLeaveForAQualifiedFirstWreck()
        {
            var failures = new List<string>();
            int n = SweepCount(22);
            for (long seed = 1; seed <= n; seed++)
            {
                string classId = Classes[(int)((seed - 1) % Classes.Length)];
                bool onboarding = seed % 2 == 0; // odd seeds prove the harder case: the kit alone, no free objective repairs
                string problem = FullRoute(seed, classId, onboarding, out _);
                if (problem.Length != 0) failures.Add("seed " + seed + " " + classId + (onboarding ? " +onboarding" : " kit-only") + ": " + problem);
            }
            Assert.IsEmpty(failures, string.Join("\n", failures.Take(10)));
        }

        [Test]
        public void TheWeakestSurvivor_CanLeaveWithOnlyTheKit_AcrossSeeds()
        {
            var failures = new List<string>();
            int n = SweepCount(12);
            for (long seed = 1; seed <= n; seed++)
            {
                string problem = FullRoute(seed * 7 + 3, "cook", false, out _); // repair skill 0, no objective repairs
                if (problem.Length != 0) failures.Add("seed " + (seed * 7 + 3) + " cook kit-only: " + problem);
            }
            Assert.IsEmpty(failures, string.Join("\n", failures.Take(10)));
        }

        // ---------------------------------------------------------------- 3. the stores

        static void SimulateSurvival(long rations, long water, double efficiency, double hours, SurvivalTuning tuning, GdDict defs, out double minHunger, out double minThirst)
        {
            double hunger = 100, thirst = 100;
            minHunger = hunger; minThirst = thirst;
            double rationHunger = ItemDefs.GetDefinition(defs, "ration_pack").GetFloat("hunger_restore") * efficiency;
            double rationThirst = ItemDefs.GetDefinition(defs, "ration_pack").GetFloat("thirst_restore") * efficiency;
            double waterThirst = ItemDefs.GetDefinition(defs, "purified_water").GetFloat("thirst_restore") * efficiency;
            long rationsLeft = rations, waterLeft = water;
            for (double t = 0; t < hours; t += 1.0 / 60.0)
            {
                hunger -= tuning.HungerPerGameHour / 60.0;
                thirst -= tuning.ThirstPerGameHour / 60.0;
                if (hunger <= 100 - rationHunger / efficiency && rationsLeft > 0) { hunger = Math.Min(100, hunger + rationHunger); thirst = Math.Min(100, thirst + rationThirst); rationsLeft--; }
                if (thirst <= 100 - waterThirst / efficiency && waterLeft > 0) { thirst = Math.Min(100, thirst + waterThirst); waterLeft--; }
                minHunger = Math.Min(minHunger, hunger);
                minThirst = Math.Min(minThirst, thirst);
            }
        }

        [Test]
        public void TheStoresALootedSurvivorCarries_KeepThemFedForThirtyGameHours()
        {
            var failures = new List<string>();
            int n = SweepCount(20);
            double worstFull = 100, worstStale = 100, worstThirstFull = 100, worstThirstStale = 100;
            for (long seed = 1; seed <= n; seed++)
            {
                RunSession s = GeneratedHomeSessionTests.BootGeneratedHome(seed, out _, out _, WorldClock.DefaultNewRunScale, null, false);
                if (s == null) { failures.Add("seed " + seed + ": no viable home"); continue; }
                foreach (LootContainer c in KitContainers(s)) c.TryInteract(c.GlobalPosition);
                long rations = s.InventoryState.GetQuantity("ration_pack"), water = s.InventoryState.GetQuantity("purified_water");
                SurvivalTuning tuning = SurvivalTuning.FromDict(CatalogRegistry.LoadDict(SurvivalTuning.Path));
                GdDict defs = ItemDefs.LoadDefinitions();
                SimulateSurvival(rations, water, 1.0, 30, tuning, defs, out double h100, out double t100);
                SimulateSurvival(rations, water, 0.6, 30, tuning, defs, out double h60, out double t60);
                worstFull = Math.Min(worstFull, h100); worstThirstFull = Math.Min(worstThirstFull, t100);
                worstStale = Math.Min(worstStale, h60); worstThirstStale = Math.Min(worstThirstStale, t60);
                if (h100 < HungerFloor || t100 < ThirstFloor) failures.Add("seed " + seed + ": full effect hunger " + Fmt(h100) + " thirst " + Fmt(t100) + " with " + rations + " rations, " + water + " water");
                if (h60 < HungerFloor || t60 < ThirstFloor) failures.Add("seed " + seed + ": 60% effect hunger " + Fmt(h60) + " thirst " + Fmt(t60) + " with " + rations + " rations, " + water + " water");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures.Take(10)));
            TestContext.Out.WriteLine("worst hunger/thirst (full) " + Fmt(worstFull) + "/" + Fmt(worstThirstFull) + "; (60%) " + Fmt(worstStale) + "/" + Fmt(worstThirstStale));
        }

        // ---------------------------------------------------------------- the report

        [Test]
        public void WriteSafetyReport_WhenAsked()
        {
            string path = Environment.GetEnvironmentVariable("SYNAPTICSEA_SAFETY_REPORT");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("SYNAPTICSEA_SAFETY_REPORT not set");
            var sb = new StringBuilder();
            sb.AppendLine("| Scale | Seeds | Idle 30 min clean | Failures |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach ((double scale, int seeds) in new[] { (60.0, 30), (30.0, 12), (120.0, 12) })
            {
                var failures = new List<string>();
                for (long seed = 1; seed <= seeds; seed++)
                {
                    string problem = IdleFor30Minutes(seed, scale, out _);
                    if (problem.Length != 0) failures.Add("seed " + seed + ": " + problem);
                }
                sb.AppendLine("| " + scale + "x | " + seeds + " | " + (seeds - failures.Count) + " | " + (failures.Count == 0 ? "none" : string.Join("; ", failures.Take(5))) + " |");
            }
            sb.AppendLine();
            sb.AppendLine("| Class | Repair skill | Seeds | Route clean (kit-only) | Route clean (with onboarding) | Failures |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            int[] skills = { 0, 0, 0, 0, 1, 1, 1, 1, 2, 3, 4 };
            for (int i = 0; i < Classes.Length; i++)
            {
                var fails = new List<string>();
                int kitOk = 0, onbOk = 0, seeds = 0;
                foreach (long seed in new long[] { 11 + i, 31 + i * 3, 59 + i * 5 })
                {
                    seeds++;
                    string a = FullRoute(seed, Classes[i], false, out _);
                    string b = FullRoute(seed, Classes[i], true, out _);
                    if (a.Length == 0) kitOk++; else fails.Add("seed " + seed + " kit-only: " + a);
                    if (b.Length == 0) onbOk++; else fails.Add("seed " + seed + " +onboarding: " + b);
                }
                sb.AppendLine("| " + Classes[i] + " | " + skills[i] + " | " + seeds + " | " + kitOk + "/" + seeds + " | " + onbOk + "/" + seeds + " | " + (fails.Count == 0 ? "none" : string.Join("; ", fails.Take(3))) + " |");
            }
            File.WriteAllText(path, sb.ToString());
        }
    }
}
