using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// Phase 1.9: the seed sweep behind <c>docs/playtest/home-guarantee-1.9.md</c>. For every seed a generated home must come out of
    /// <see cref="StartSceneBuilder.BuildHomeStart"/> with the guarantee, keep every promise on an independent check, and be survivable and
    /// leavable by the weakest survivor. SYNAPTICSEA_SEED_SWEEP sets the seed count (default 200); SYNAPTICSEA_GUARANTEE_REPORT writes the markdown tables.
    /// </summary>
    public class StartingHomeGuaranteeSweepTests
    {
        const string Biome = "breach_field";
        const string Difficulty = "standard";
        static readonly string[] TravelSystems = { "power", "navigation", "propulsion" };

        CollectingLog _log;

        [SetUp]
        public void SetUp()
        {
            _log = new CollectingLog();
            CoreServices.Log = _log;
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

        sealed class Row
        {
            public long Seed;
            public int Attempts;
            public StartSceneBuilder.HomeStart Home;
            public readonly List<string> Rejections = new List<string>();
            public int BrokenTravel;
            public int ExtraBreaks;
            public double RepairKg, StoresKg;
            public long Rations, Water;
            public double CapacityMargin;
            public int Rooms;
            public int[] WorstSkillFailures = new int[5];
            public string Failure = "";
        }

        static string Bucket(string warning)
        {
            int idx = warning.IndexOf("rejected, seed ", StringComparison.Ordinal);
            if (idx < 0) return "";
            int colon = warning.IndexOf(": ", idx, StringComparison.Ordinal);
            if (colon < 0) return "";
            string reason = warning.Substring(colon + 2);
            int cut = reason.IndexOfAny(new[] { ':', '[', '{', '(' });
            if (cut > 0) reason = reason.Substring(0, cut);
            if (reason.StartsWith("room ", StringComparison.Ordinal) && reason.Contains("not walkable")) reason = "an objective room is not walkable from the start room";
            return reason.Trim();
        }

        /// <summary>The kit exactly as the home holds it: item id -> quantity across the three guarantee containers.</summary>
        static Dictionary<string, long> HeldItems(GdDict slice)
        {
            var held = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (GdDict container in slice.GetArrayOrEmpty("loot_containers").OfType<GdDict>())
            {
                if (Array.IndexOf(StartingHomeGuarantee.ContainerIds, V.Str(container.Get("id", ""))) < 0) continue;
                foreach (GdDict stack in container.GetArrayOrEmpty("contents").OfType<GdDict>())
                {
                    string id = V.Str(stack.Get("item_id", ""));
                    held[id] = (held.TryGetValue(id, out long n) ? n : 0) + V.I64(stack.Get("qty", 0L));
                }
            }
            return held;
        }

        /// <summary>
        /// The weakest-possible walk: a survivor of repair <paramref name="skill"/> repairs every broken travel part using ONLY the home's parts and tools
        /// (consuming one part per repair, as the game does), then the real lifeboat capacity check runs with everything the home gave carried as cargo.
        /// Returns "" when the lifeboat could leave.
        /// </summary>
        static string LeaveWith(ShipBlueprint blueprint, Dictionary<string, long> held, long skill)
        {
            ShipSystemsManager manager = HomeOpeningState.Build(blueprint);
            var parts = new Dictionary<string, long>(held, StringComparer.Ordinal);
            foreach (string systemId in TravelSystems)
                foreach (ShipSubcomponent sub in manager.GetSystem(systemId).Subcomponents)
                {
                    if (sub.IsFunctional()) continue;
                    foreach (string tool in sub.RequiredTools)
                        if (!held.TryGetValue(tool, out long tools) || tools < 1) return "missing tool " + tool + " for " + sub.SubcomponentId;
                    foreach (string part in sub.RequiredParts)
                    {
                        if (!parts.TryGetValue(part, out long have) || have < 1) return "missing part " + part + " for " + sub.SubcomponentId;
                        parts[part] = have - 1;
                    }
                    sub.Health = sub.RepairQuality(skill);
                }
            if (!manager.IsOperational("propulsion")) return "flight path not operational";
            GdDict defs = ItemDefs.LoadDefinitions();
            double cargo = held.Sum(kv => kv.Value * ItemDefs.GetDefinition(defs, kv.Key).GetFloat("weight"));
            var ship = ShipInstance.Create("boat", "", null, manager, new FakeRoot());
            var floors = new GdArray();
            for (int i = 0; i < 3; i++) floors.Add(new GdDict { { "position", new Vec3(i * 4, 0, 0) } });
            ship.BuiltLayout = new GdDict { { "structural_plan", new GdDict { { "floor_placements", floors } } } };
            ship.Mobility = AssemblyMobility.CreateSpecification(ship, true);
            GdDict report = AssemblyMobility.Evaluate(ship, cargo);
            return report.GetBool("success") ? "" : "capacity " + report.GetString("reason");
        }

        /// <summary>Hunger/thirst minimum over the first <paramref name="hours"/> game hours, eating and drinking from <paramref name="efficiency"/> of the stores.</summary>
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
                // Eat when a full ration fits without waste; drink likewise.
                if (hunger <= 100 - rationHunger / efficiency && rationsLeft > 0) { hunger = Math.Min(100, hunger + rationHunger); thirst = Math.Min(100, thirst + rationThirst); rationsLeft--; }
                if (thirst <= 100 - waterThirst / efficiency && waterLeft > 0) { thirst = Math.Min(100, thirst + waterThirst); waterLeft--; }
                minHunger = Math.Min(minHunger, hunger);
                minThirst = Math.Min(minThirst, thirst);
            }
        }

        static string Pct(int n, int d) => d == 0 ? "n/a" : (100.0 * n / d).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        static string Spread(IEnumerable<double> values, string format = "0.0")
        {
            List<double> v = values.OrderBy(x => x).ToList();
            return v.Count == 0 ? "n/a" : v[0].ToString(format, CultureInfo.InvariantCulture) + " / " + v[v.Count / 2].ToString(format, CultureInfo.InvariantCulture) + " / " + v[v.Count - 1].ToString(format, CultureInfo.InvariantCulture);
        }
        static string Histogram(IEnumerable<string> keys)
        {
            var groups = keys.GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            return groups.Count == 0 ? "none" : string.Join("; ", groups.Select(g => g.Key + " x" + g.Count()));
        }

        [Test]
        public void EverySeedGetsAGuaranteedSafeHome()
        {
            int seeds = int.TryParse(Environment.GetEnvironmentVariable("SYNAPTICSEA_SEED_SWEEP"), out int n) && n > 0 ? n : 200;
            var spec = new StartingHomeGuarantee.Spec();
            SurvivalTuning tuning = SurvivalTuning.FromDict(CatalogRegistry.LoadDict(SurvivalTuning.Path));
            GdDict defs = ItemDefs.LoadDefinitions();
            var rows = new List<Row>();
            var problems = new List<string>();
            double worst100Hunger = 100, worst100Thirst = 100, worst60Hunger = 100, worst60Thirst = 100;

            for (long seed = 1; seed <= seeds; seed++)
            {
                var row = new Row { Seed = seed };
                int before = _log.Warnings.Count;
                StartSceneBuilder.HomeStart home = StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty,
                    condition: (long)ShipBlueprint.Condition.Pristine, exteriorDock: true, guarantee: spec);
                foreach (string w in _log.Warnings.Skip(before)) { string b = Bucket(w); if (b.Length != 0) row.Rejections.Add(b); }
                rows.Add(row);
                if (home == null) { row.Attempts = StartSceneBuilder.MAX_START_ATTEMPTS; row.Failure = "no viable home"; problems.Add("seed " + seed + ": no viable home (" + Histogram(row.Rejections) + ")"); continue; }
                row.Home = home;
                row.Attempts = home.Attempts;

                string validation = StartingHomeGuarantee.Validate(home.Documents, home.Blueprint, spec);
                if (validation.Length != 0) { problems.Add("seed " + seed + ": validation " + validation); continue; }

                StartingHomeGuarantee.Plan plan = home.Guarantee;
                row.BrokenTravel = plan.BrokenSubs.Count;
                row.ExtraBreaks = HomeOpeningState.ExtraBreaks(home.Blueprint).Count;
                row.RepairKg = plan.RepairKitKg; row.StoresKg = plan.StoresKg; row.Rations = plan.Rations; row.Water = plan.Water;
                row.CapacityMargin = plan.CapacityMarginKg;
                row.Rooms = home.Documents.Layout.GetArrayOrEmpty("rooms").Count;

                // The independent check also ran in BuildHomeStart; here the four mirrors must additionally agree with each other.
                GdDict textSlice = GdJson.ParseDict(home.Documents.GameplaySliceJson);
                if (GdJson.Stringify(textSlice) != GdJson.Stringify(home.Documents.GameplaySlice)) problems.Add("seed " + seed + ": slice text mirror differs from the slice document");
                GdDict textLayout = GdJson.ParseDict(home.Documents.LayoutJson);
                if (GdJson.Stringify(textLayout) != GdJson.Stringify(home.Documents.Layout)) problems.Add("seed " + seed + ": layout text mirror differs from the layout document");
                if (home.Documents.SourceLayout != null && GdJson.Stringify(home.Documents.SourceLayout.GetArrayOrEmpty("encounters")) != "[]")
                    problems.Add("seed " + seed + ": source layout still has encounters");

                // Life support, scanners and gravity open healthy.
                ShipSystemsManager opening = HomeOpeningState.Build(home.Blueprint);
                foreach (string systemId in new[] { "life_support", "scanners", "gravity" })
                    if (opening.GetSystem(systemId).Subcomponents.Any(s => !s.IsFunctional())) problems.Add("seed " + seed + ": " + systemId + " opens broken");

                // Class matrix at repair skills 0..4 using only the home's own parts.
                Dictionary<string, long> held = HeldItems(home.Documents.GameplaySlice);
                for (long skill = 0; skill <= 4; skill++)
                {
                    string leave = LeaveWith(home.Blueprint, held, skill);
                    if (leave.Length != 0) { row.WorstSkillFailures[skill]++; problems.Add("seed " + seed + " skill " + skill + ": cannot leave (" + leave + ")"); }
                }

                // Survival sufficiency over the excursion, at full and at 60% efficiency.
                long heldRations = held.TryGetValue("ration_pack", out long r) ? r : 0, heldWater = held.TryGetValue("purified_water", out long w2) ? w2 : 0;
                SimulateSurvival(heldRations, heldWater, 1.0, spec.GameHours, tuning, defs, out double h100, out double t100);
                SimulateSurvival(heldRations, heldWater, 0.6, spec.GameHours, tuning, defs, out double h60, out double t60);
                worst100Hunger = Math.Min(worst100Hunger, h100); worst100Thirst = Math.Min(worst100Thirst, t100);
                worst60Hunger = Math.Min(worst60Hunger, h60); worst60Thirst = Math.Min(worst60Thirst, t60);
                if (h100 < spec.HungerFloorPercent - 1e-6 || t100 < spec.ThirstFloorPercent - 1e-6)
                    problems.Add("seed " + seed + ": at full efficiency hunger fell to " + h100.ToString("0.0") + " and thirst to " + t100.ToString("0.0"));
                if (h60 < spec.HungerFloorPercent - 1e-6 || t60 < spec.ThirstFloorPercent - 1e-6)
                    problems.Add("seed " + seed + ": at 60% efficiency hunger fell to " + h60.ToString("0.0") + " and thirst to " + t60.ToString("0.0"));
            }

            List<Row> ok = rows.Where(r => r.Home != null).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("| Measure | Result |");
            sb.AppendLine("| --- | --- |");
            sb.AppendLine("| Seeds swept | " + seeds + " |");
            sb.AppendLine("| `BuildHomeStart` with the guarantee succeeds within " + StartSceneBuilder.MAX_START_ATTEMPTS + " attempts | " + ok.Count + " / " + seeds + " (" + Pct(ok.Count, seeds) + ") |");
            sb.AppendLine("| Attempts used (successes) | " + Histogram(ok.Select(r => r.Attempts.ToString(CultureInfo.InvariantCulture))) + " |");
            sb.AppendLine("| Rejection reasons (all attempts) | " + Histogram(rows.SelectMany(r => r.Rejections)) + " |");
            sb.AppendLine("| Rooms per home, min / median / max | " + Spread(ok.Select(r => (double)r.Rooms), "0") + " |");
            sb.AppendLine("| Broken power/navigation/propulsion parts at New Run, min / median / max | " + Spread(ok.Select(r => (double)r.BrokenTravel), "0") + " |");
            sb.AppendLine("| Extra breaks beyond nav_linkage (0 / 1 / 2) | " + Histogram(ok.Select(r => r.ExtraBreaks.ToString(CultureInfo.InvariantCulture))) + " |");
            sb.AppendLine("| Repair kit weight kg, min / median / max | " + Spread(ok.Select(r => r.RepairKg)) + " |");
            sb.AppendLine("| Survival stores weight kg, min / median / max | " + Spread(ok.Select(r => r.StoresKg)) + " |");
            sb.AppendLine("| Rations, min / median / max | " + Spread(ok.Select(r => (double)r.Rations), "0") + " |");
            sb.AppendLine("| Water, min / median / max | " + Spread(ok.Select(r => (double)r.Water), "0") + " |");
            sb.AppendLine("| Lifeboat capacity margin kg after skill-0 repairs and the whole kit aboard, min / median / max | " + Spread(ok.Select(r => r.CapacityMargin), "0") + " |");
            sb.AppendLine("| Guarantee containers per home | 3 (`" + string.Join("`, `", StartingHomeGuarantee.ContainerIds) + "`), each in a different non-start, non-dock room |");
            for (int skill = 0; skill <= 4; skill++)
                sb.AppendLine("| Homes whose skill-" + skill + " survivor cannot leave using only the home's parts | " + ok.Sum(r => r.WorstSkillFailures[skill]) + " / " + ok.Count + " |");
            sb.AppendLine("| Worst hunger / thirst over " + spec.GameHours + " game hours, all stores used | " + worst100Hunger.ToString("0.0", CultureInfo.InvariantCulture) + " / " + worst100Thirst.ToString("0.0", CultureInfo.InvariantCulture) + " |");
            sb.AppendLine("| Worst hunger / thirst, only 60% of each item's effect (stale food) | " + worst60Hunger.ToString("0.0", CultureInfo.InvariantCulture) + " / " + worst60Thirst.ToString("0.0", CultureInfo.InvariantCulture) + " |");
            sb.AppendLine("| Problems | " + (problems.Count == 0 ? "none" : problems.Count.ToString(CultureInfo.InvariantCulture)) + " |");
            string table = sb.ToString();
            TestContext.WriteLine(table);
            string report = Environment.GetEnvironmentVariable("SYNAPTICSEA_GUARANTEE_REPORT");
            if (!string.IsNullOrEmpty(report)) File.WriteAllText(report, table);

            Assert.IsEmpty(problems, string.Join("\n", problems.Take(25)) + (problems.Count > 25 ? "\n... " + problems.Count + " total" : ""));
            Assert.AreEqual(seeds, ok.Count, "every seed yields a guaranteed home within " + StartSceneBuilder.MAX_START_ATTEMPTS + " attempts");
        }
    }
}
