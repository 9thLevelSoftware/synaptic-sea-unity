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

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// Phase 1.10: the document-level seed sweep behind <c>docs/playtest/home-objectives-1.10.md</c>. For every seed the generated New Run home must come
    /// out of <see cref="StartSceneBuilder.BuildHomeStart"/> (Pristine, exterior dock, guarantee) with the four typed onboarding objectives placed by room role,
    /// both pickups on its floor, every cell reachable from the dock, and the gates' own independent checks passing. SYNAPTICSEA_SEED_SWEEP sets the seed count
    /// (default 200); SYNAPTICSEA_OBJECTIVES_REPORT writes the markdown tables. The booted-session seam is <c>GeneratedHomeSessionTests</c>.
    /// </summary>
    public class HomeObjectiveSweepTests
    {
        const string Biome = "breach_field";
        const string Difficulty = "standard";
        /// <summary>The share of seeds that must yield a home within the retry budget.</summary>
        const double RequiredSuccess = 0.95;

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
            if (reason.StartsWith("the life boat would overlap", StringComparison.Ordinal)) reason = "the life boat would overlap other rooms";
            if (reason.StartsWith("dock room", StringComparison.Ordinal)) reason = "no exterior edge for the life boat";
            return reason.Trim();
        }

        static string RoleOf(GdDict layout, string roomId)
        {
            foreach (GdDict room in layout.GetArrayOrEmpty("rooms").OfType<GdDict>())
                if (room.GetString("id") == roomId) return V.Str(room.Get("room_role", room.Get("role", "")));
            return "";
        }

        /// <summary>Mirrors <see cref="HomeJoinPlanner.Sites"/> for the host side of a wreck mooring: an exposed west edge of a deck-0 floor cell away from the dock.</summary>
        static bool HasWestMooringSite(GdDict layout)
        {
            List<Vec3> floors = AssemblyMobility.Floors(layout);
            if (floors.Count == 0) return false;
            var seen = new HashSet<Vec3>(floors);
            Vec3 reserved = DockPorts.ForDerelict(layout).Get("position") is Vec3 v ? v : Vec3.Inf;
            double west = floors.Where(c => Math.Abs(c.Y) < 0.05).Select(c => (double)c.X).DefaultIfEmpty(double.NaN).Min();
            foreach (Vec3 cell in floors.Where(c => Math.Abs(c.Y) < 0.05 && Math.Abs(c.X - west) < 0.05))
            {
                if (seen.Contains(cell + (-Vec3.Right) * 4)) continue;
                if ((cell + (-Vec3.Right) * 2).DistanceTo(reserved) <= 4.05) continue;
                return true;
            }
            return false;
        }

        static string Pct(int n, int d) => d == 0 ? "n/a" : (100.0 * n / d).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        static string Histogram(IEnumerable<string> keys)
        {
            var groups = keys.GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            return groups.Count == 0 ? "none" : string.Join("; ", groups.Select(g => g.Key + " x" + g.Count()));
        }
        static string Spread(IEnumerable<double> values)
        {
            List<double> v = values.OrderBy(x => x).ToList();
            return v.Count == 0 ? "n/a" : v[0].ToString("0", CultureInfo.InvariantCulture) + " / " + v[v.Count / 2].ToString("0", CultureInfo.InvariantCulture) + " / " + v[v.Count - 1].ToString("0", CultureInfo.InvariantCulture);
        }

        sealed class Row
        {
            public long Seed;
            public int Attempts;
            public StartSceneBuilder.HomeStart Home;
            public readonly List<string> Rejections = new List<string>();
            public int Usable;
            public bool JoinSite;
            public int RoleMatches;
        }

        [Test]
        public void EverySeedGetsTheOnboardingChainOnReachableFloor()
        {
            int seeds = int.TryParse(Environment.GetEnvironmentVariable("SYNAPTICSEA_SEED_SWEEP"), out int n) && n > 0 ? n : 200;
            var spec = new StartingHomeGuarantee.Spec();
            var rows = new List<Row>();
            var problems = new List<string>();
            var objectiveRoles = new List<string>();
            var pickupRoles = new List<string>();
            var allRejections = new List<string>();

            for (long seed = 1; seed <= seeds; seed++)
            {
                var row = new Row { Seed = seed };
                int before = _log.Warnings.Count;
                StartSceneBuilder.HomeStart home = StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty,
                    condition: (long)ShipBlueprint.Condition.Pristine, exteriorDock: true, guarantee: spec);
                foreach (string w in _log.Warnings.Skip(before)) { string b = Bucket(w); if (b.Length != 0) { row.Rejections.Add(b); allRejections.Add(b); } }
                rows.Add(row);
                if (home == null) { row.Attempts = StartSceneBuilder.MAX_START_ATTEMPTS; continue; }
                row.Home = home;
                row.Attempts = home.Attempts;
                ShipDocuments docs = home.Documents;
                GdDict layout = docs.Layout;
                GdDict slice = docs.GameplaySlice;

                string validation = HomeObjectiveComposer.Validate(docs);
                if (validation.Length != 0) problems.Add("seed " + seed + ": composer validation: " + validation);
                string reach = HomeObjectiveComposer.ReachabilityReason(docs);
                if (reach.Length != 0) problems.Add("seed " + seed + ": " + reach);
                row.Usable = StartingHomeGuarantee.EligibleRooms(layout, slice).Count;
                row.JoinSite = HasWestMooringSite(layout);

                // Every typed objective and pickup has a floor position in the loader's frame, and its room is a real, non-passage room.
                foreach (GdDict objective in slice.GetArrayOrEmpty("objectives").OfType<GdDict>())
                {
                    string roomId = objective.GetString("room_id");
                    string role = RoleOf(layout, roomId);
                    objectiveRoles.Add(objective.GetString("type") + "@" + role);
                    string[] preferred = StationPlacer.PREFERRED_ROOM_ROLES[StationPlacer.ObjectiveKindPrefix + objective.GetString("type")];
                    if (Array.IndexOf(preferred, role) >= 0) row.RoleMatches++;
                    GdDict room = layout.GetArrayOrEmpty("rooms").OfType<GdDict>().First(r => r.GetString("id") == roomId);
                    if (GeneratedShipLayout.RoomCellWorld(layout, room, objective.GetArrayOrEmpty("approach_cell")) == Vec3.Inf)
                        problems.Add("seed " + seed + ": " + objective.GetString("type") + " has no floor position");
                }
                foreach (GdDict pickup in slice.GetArrayOrEmpty(HomeObjectiveComposer.PickupsKey).OfType<GdDict>())
                {
                    string roomId = pickup.GetString("room_id");
                    pickupRoles.Add(pickup.GetString("tool_id") + "@" + RoleOf(layout, roomId));
                    GdDict room = layout.GetArrayOrEmpty("rooms").OfType<GdDict>().First(r => r.GetString("id") == roomId);
                    if (GeneratedShipLayout.RoomCellWorld(layout, room, pickup.GetArrayOrEmpty("approach_cell")) == Vec3.Inf)
                        problems.Add("seed " + seed + ": pickup " + pickup.GetString("tool_id") + " has no floor position");
                }

                // The dock room's id carries the role prefix the runtime's dock-overlap suppression keys on.
                string dock = HomeDockPlanner.DockRoomId(layout);
                if (!dock.StartsWith("airlock", StringComparison.Ordinal) && !dock.StartsWith("dock", StringComparison.Ordinal))
                    problems.Add("seed " + seed + ": the dock room id '" + dock + "' has no airlock/dock prefix");

                // The boat still fits, and the home is still calm.
                HomeDockPlanner.Plan plan = HomeDockPlanner.TryPlan(layout, out _);
                if (plan == null || plan.OtherRoomOverlap != 0) problems.Add("seed " + seed + ": the life boat no longer fits");
                if (!layout.GetArrayOrEmpty("encounters").IsEmpty || !slice.GetArrayOrEmpty("breach_zones").IsEmpty) problems.Add("seed " + seed + ": the home is not calm");

                // Determinism: the same seed yields the same slice.
                StartSceneBuilder.HomeStart again = StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty,
                    condition: (long)ShipBlueprint.Condition.Pristine, exteriorDock: true, guarantee: new StartingHomeGuarantee.Spec());
                if (again == null || GdJson.Stringify(again.Documents.GameplaySlice) != GdJson.Stringify(slice)) problems.Add("seed " + seed + ": generation is not deterministic");
            }

            int ok = rows.Count(r => r.Home != null);
            string reportPath = Environment.GetEnvironmentVariable("SYNAPTICSEA_OBJECTIVES_REPORT");
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, Report(rows, objectiveRoles, pickupRoles, allRejections, problems, seeds));
            Assert.IsEmpty(problems, string.Join("\n", problems.Take(15)));
            Assert.GreaterOrEqual(ok, (int)Math.Ceiling(seeds * RequiredSuccess),
                "homes within " + StartSceneBuilder.MAX_START_ATTEMPTS + " attempts: " + ok + "/" + seeds + " (" + Histogram(allRejections) + ")");
        }

        static string Report(List<Row> rows, List<string> objectiveRoles, List<string> pickupRoles, List<string> rejections, List<string> problems, int seeds)
        {
            var ok = rows.Where(r => r.Home != null).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("# Generated-home objectives sweep (Phase 1.10)");
            sb.AppendLine();
            sb.AppendLine("Seeds 1.." + seeds + ", `breach_field` / `standard`, Pristine, exterior dock, starting-home guarantee, onboarding composer.");
            sb.AppendLine();
            sb.AppendLine("| Measure | Result |");
            sb.AppendLine("| --- | --- |");
            sb.AppendLine("| Homes within " + StartSceneBuilder.MAX_START_ATTEMPTS + " attempts | " + ok.Count + "/" + rows.Count + " (" + Pct(ok.Count, rows.Count) + ") |");
            sb.AppendLine("| First attempt | " + rows.Count(r => r.Home != null && r.Attempts == 1) + " |");
            sb.AppendLine("| Attempts histogram | " + Histogram(ok.Select(r => "attempt " + r.Attempts)) + " |");
            sb.AppendLine("| Rejection reasons (all attempts) | " + Histogram(rejections) + " |");
            sb.AppendLine("| Usable rooms (min / median / max) | " + Spread(ok.Select(r => (double)r.Usable)) + " |");
            sb.AppendLine("| Objectives in a room whose role the type prefers | " + ok.Sum(r => r.RoleMatches) + "/" + (ok.Count * 4) + " (" + Pct(ok.Sum(r => r.RoleMatches), ok.Count * 4) + ") |");
            sb.AppendLine("| Wreck-mooring site on the west edge | " + ok.Count(r => r.JoinSite) + "/" + ok.Count + " (" + Pct(ok.Count(r => r.JoinSite), ok.Count) + ") |");
            sb.AppendLine("| Invariant problems | " + problems.Count + " |");
            sb.AppendLine();
            sb.AppendLine("## Objective placement by room role");
            sb.AppendLine();
            sb.AppendLine(Histogram(objectiveRoles));
            sb.AppendLine();
            sb.AppendLine("## Pickup placement by room role");
            sb.AppendLine();
            sb.AppendLine(Histogram(pickupRoles));
            sb.AppendLine();
            sb.AppendLine("## Seeds without a home");
            sb.AppendLine();
            foreach (Row r in rows.Where(r => r.Home == null).Take(20))
                sb.AppendLine("- seed " + r.Seed + ": " + Histogram(r.Rejections));
            return sb.ToString();
        }
    }
}
