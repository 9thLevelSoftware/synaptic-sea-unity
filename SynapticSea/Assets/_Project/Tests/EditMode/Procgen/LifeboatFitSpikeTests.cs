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

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// Phase 1.7c spike: can the lifeboat-fit pass rate of generated homes be raised cheaply? Measures the real docking transform
    /// (<see cref="DockingManager.ComputeMobileTransform"/> applied to the real lifeboat layout) against each generated home's
    /// occupancy, for the current port and for the candidate ports. Measurement only; no production behaviour is asserted beyond
    /// determinism and the model check. Set SYNAPTICSEA_SEED_SWEEP (default 200) and SYNAPTICSEA_SPIKE_REPORT (a file path) to
    /// write the tables used in <c>docs/playtest/lifeboat-fit-spike-1.7c.md</c>.
    /// </summary>
    public class LifeboatFitSpikeTests
    {
        const string Biome = "breach_field";
        const string Difficulty = "standard";
        const double Cell = StructuralEdgeCompiler.CELL_SIZE;

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

        struct HomeCell
        {
            public Vec3 Pos;
            public string Room;
        }

        static readonly (double dx, double dz, string name)[] Directions =
        {
            (1.0, 0.0, "+X"), (-1.0, 0.0, "-X"), (0.0, 1.0, "+Z"), (0.0, -1.0, "-Z"),
        };

        static string RoleOf(GdDict room)
        {
            string role = V.Str(room.Get("room_role", ""));
            return role.Length != 0 ? role : V.Str(room.Get("role", ""));
        }

        static List<HomeCell> HomeCells(GdDict layout)
        {
            var list = new List<HomeCell>();
            foreach (object recordV in layout.GetDictOrEmpty("structural_plan").GetDictOrEmpty("occupancy").Values)
            {
                if (!(recordV is GdDict record)) continue;
                Vec3 pos = WalkabilityContract.OccupancyWorldPosition(record);
                if (pos == Vec3.Inf) continue;
                list.Add(new HomeCell { Pos = pos, Room = WalkabilityContract.OccupancyRoomId(record) });
            }
            return list;
        }

        static string DockRoomId(GdArray rooms)
        {
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room)) continue;
                if (RoleOf(room) == "dock" || V.Str(room.Get("id", "")).StartsWith("dock", StringComparison.Ordinal)) return V.Str(room.Get("id", ""));
            }
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room)) continue;
                if (RoleOf(room) == "airlock" || V.Str(room.Get("id", "")).StartsWith("airlock", StringComparison.Ordinal)) return V.Str(room.Get("id", ""));
            }
            return "";
        }

        /// <summary>The boat's three cell centres in home coordinates, through the real transform.</summary>
        static List<Vec3> BoatCells(GdDict hostPort, out bool ok)
        {
            ok = false;
            var cells = new List<Vec3>();
            GdDict boat = LifeBoatBuilder.BuildLayout();
            GdDict mobile = DockPorts.ForLifeboat(boat);
            if (mobile.IsEmpty) return cells;
            Xform3 t = DockingManager.ComputeMobileTransform(hostPort, mobile);
            foreach (object recordV in boat.GetDictOrEmpty("structural_plan").GetDictOrEmpty("occupancy").Values)
            {
                if (!(recordV is GdDict record)) continue;
                Vec3 pos = WalkabilityContract.OccupancyWorldPosition(record);
                if (pos == Vec3.Inf) continue;
                cells.Add(t * pos);
            }
            ok = cells.Count == 3;
            return cells;
        }

        /// <summary>Home cells (by room) that a boat cell's 4 m square overlaps by more than half a metre on both axes.</summary>
        static void Overlaps(List<HomeCell> home, List<Vec3> boat, string allowedRoom, out int other, out int allowed)
        {
            other = 0;
            allowed = 0;
            var seen = new HashSet<int>();
            foreach (Vec3 b in boat)
            {
                for (int i = 0; i < home.Count; i++)
                {
                    HomeCell h = home[i];
                    if (Math.Abs(b.Y - h.Pos.Y) >= 1.0) continue;
                    if (Cell - Math.Abs(b.X - h.Pos.X) <= 0.5 || Cell - Math.Abs(b.Z - h.Pos.Z) <= 0.5) continue;
                    if (!seen.Add(i)) continue;
                    if (h.Room == allowedRoom) allowed++; else other++;
                }
            }
        }

        static GdDict Port(Vec3 position, Vec3 facing) => new GdDict { { "position", position }, { "facing", facing } };

        sealed class Option
        {
            public GdDict Port;
            public string Name = "";
            public int Other;
            public int Allowed;
        }

        /// <summary>Every exterior-facing floor cell of <paramref name="roomId"/>, with the port on that cell's outward edge.</summary>
        static IEnumerable<Option> ExteriorOptions(List<HomeCell> home, string roomId, double portYOffset, string allowedRoom)
        {
            foreach (HomeCell c in home.Where(h => h.Room == roomId))
            {
                foreach (var d in Directions)
                {
                    bool occupied = home.Any(h => Math.Abs(h.Pos.Y - c.Pos.Y) < 1.0 && Math.Abs(h.Pos.X - (c.Pos.X + d.dx * Cell)) < 1.0 && Math.Abs(h.Pos.Z - (c.Pos.Z + d.dz * Cell)) < 1.0);
                    if (occupied) continue;
                    var port = Port(new Vec3(c.Pos.X + d.dx * Cell * 0.5, c.Pos.Y + portYOffset, c.Pos.Z + d.dz * Cell * 0.5), new Vec3(d.dx, 0.0, d.dz));
                    List<Vec3> boat = BoatCells(port, out bool ok);
                    if (!ok) continue;
                    Overlaps(home, boat, allowedRoom, out int other, out int allowed);
                    yield return new Option { Port = port, Name = d.name, Other = other, Allowed = allowed };
                }
            }
        }

        /// <summary>The model the 1.7b sweep used: a fixed strip from the port centroid, +X.</summary>
        static int AnalyticOverlap(GdDict layout, string dockRoom)
        {
            GdDict port = DockPorts.ForDerelict(layout);
            if (port.IsEmpty || !(port.Get("position", null) is Vec3 p)) return -1;
            double xMin = p.X - Cell, xMax = p.X + 2.0 * Cell;
            int other = 0;
            foreach (object recordV in layout.GetDictOrEmpty("structural_plan").GetDictOrEmpty("occupancy").Values)
            {
                if (!(recordV is GdDict record)) continue;
                Vec3 pos = WalkabilityContract.OccupancyWorldPosition(record);
                if (pos == Vec3.Inf) continue;
                if (Math.Abs(pos.Z - p.Z) > Cell * 0.5 || Math.Abs(pos.Y - p.Y) > 1.0) continue;
                double overlap = Math.Min(pos.X + Cell * 0.5, xMax) - Math.Max(pos.X - Cell * 0.5, xMin);
                if (overlap <= 0.5) continue;
                if (WalkabilityContract.OccupancyRoomId(record) == dockRoom) continue;
                other++;
            }
            return other;
        }

        sealed class Result
        {
            public long Seed;
            public string Template = "";
            public string DockRoom = "";
            public bool HasDockRoom;
            public int AnalyticOther = -1;
            public int RealOther = -1;       // current port, real transform
            public int RealInDock;
            public int ExteriorOptionCount;  // candidate A: dock-room exterior options
            public int BestOther = -1;       // candidate A best option
            public string BestDirection = "";
            public int AnyRoomBestOther = -1; // candidate D: best over every room
            public string AnyRoomBestRoom = "";
            public int BoatCellsOutsideHull; // for candidate A best option: boat cells that overlap no home cell
        }

        static string Pct(int n, int d) => d == 0 ? "n/a" : (100.0 * n / d).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        static string Histogram(IEnumerable<string> keys)
        {
            var groups = keys.GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            return groups.Count == 0 ? "none" : string.Join("; ", groups.Select(g => g.Key + " x" + g.Count()));
        }

        static Result Evaluate(long seed, GdDict layout, string dockRoom, bool hasDock)
        {
            var r = new Result { Seed = seed, Template = V.Str(layout.Get("template_id", "")), DockRoom = dockRoom, HasDockRoom = hasDock };
            List<HomeCell> home = HomeCells(layout);
            GdDict basePort = DockPorts.ForDerelict(layout);
            if (basePort.IsEmpty || !(basePort.Get("position", null) is Vec3 bp)) return r;
            r.AnalyticOther = AnalyticOverlap(layout, dockRoom);
            List<Vec3> boat = BoatCells(basePort, out bool ok);
            if (ok) { Overlaps(home, boat, dockRoom, out r.RealOther, out r.RealInDock); }

            // Candidate A: the dock/airlock room's own exterior edge.
            var dockCells = home.Where(h => h.Room == dockRoom).ToList();
            double offsetY = dockCells.Count == 0 ? 0.0 : bp.Y - dockCells.Average(h => h.Pos.Y);
            List<Option> options = ExteriorOptions(home, dockRoom, offsetY, dockRoom).ToList();
            r.ExteriorOptionCount = options.Count;
            if (options.Count > 0)
            {
                // Deterministic: fewest other-room overlaps, then most dock-room overlap (a tight fit), then direction order.
                Option best = options.OrderBy(o => o.Other).ThenBy(o => Array.FindIndex(Directions, d => d.name == o.Name)).First();
                r.BestOther = best.Other;
                r.BestDirection = best.Name;
                List<Vec3> bb = BoatCells(best.Port, out _);
                int touching = 0;
                foreach (Vec3 b in bb)
                    if (home.Any(h => Math.Abs(b.Y - h.Pos.Y) < 1.0 && Cell - Math.Abs(b.X - h.Pos.X) > 0.5 && Cell - Math.Abs(b.Z - h.Pos.Z) > 0.5)) touching++;
                r.BoatCellsOutsideHull = bb.Count - touching;
            }

            // Candidate D: any room could host the dock (upper bound: ignores that the start/boarding cell is the dock room's).
            int bestAny = int.MaxValue;
            string bestRoom = "";
            foreach (string roomId in home.Select(h => h.Room).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                double rowOffset = bp.Y - home.Where(h => h.Room == roomId).Average(h => h.Pos.Y);
                foreach (Option o in ExteriorOptions(home, roomId, rowOffset, roomId))
                    if (o.Other < bestAny) { bestAny = o.Other; bestRoom = roomId; }
            }
            if (bestAny != int.MaxValue) { r.AnyRoomBestOther = bestAny; r.AnyRoomBestRoom = bestRoom; }
            return r;
        }

        [Test]
        public void RealDockingTransform_PlacesTheBoatWhereTheSweepModelSaid_AndExteriorPortsFitMore()
        {
            int seeds = int.TryParse(Environment.GetEnvironmentVariable("SYNAPTICSEA_SEED_SWEEP"), out int n) && n > 0 ? n : 200;

            // Golden hub through the real transform: the one home the game is known to dock correctly.
            string dir = Path.Combine(Fixtures.StreamingDataRoot, "data", "procgen", "golden", "coherent_ship_001");
            var goldenLayout = (GdDict)GdJson.Parse(File.ReadAllText(Path.Combine(dir, "layout.json")));
            string goldenDock = DockRoomId(goldenLayout.GetArrayOrEmpty("rooms"));
            List<HomeCell> goldenHome = HomeCells(goldenLayout);
            GdDict goldenPort = DockPorts.ForDerelict(goldenLayout);
            Assert.IsFalse(goldenPort.IsEmpty, "the golden hub has a dock port");
            List<Vec3> goldenBoat = BoatCells(goldenPort, out bool goldenOk);
            Assert.IsTrue(goldenOk, "the lifeboat layout exposes three occupied cells and an airlock port");
            Overlaps(goldenHome, goldenBoat, goldenDock, out int goldenOther, out int goldenInDock);

            var results = new List<Result>();
            int firstAttempt = 0;
            for (long seed = 1; seed <= seeds; seed++)
            {
                StartSceneBuilder.HomeStart hs = StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty, condition: (long)ShipBlueprint.Condition.Pristine);
                if (hs == null) continue;
                if (hs.Attempts == 1) firstAttempt++;
                GdDict layout = hs.Documents.Layout;
                string dockRoom = DockRoomId(layout.GetArrayOrEmpty("rooms"));
                bool hasDock = layout.GetArrayOrEmpty("rooms").OfType<GdDict>().Any(rm => RoleOf(rm) == "dock" || V.Str(rm.Get("id", "")).StartsWith("dock", StringComparison.Ordinal));
                results.Add(Evaluate(seed, layout, dockRoom, hasDock));
            }
            // Determinism: the same seed, the same candidate choice.
            if (results.Count > 0)
            {
                Result again = Evaluate(results[0].Seed, StartSceneBuilder.BuildHomeStart(results[0].Seed, Biome, Difficulty, condition: (long)ShipBlueprint.Condition.Pristine).Documents.Layout, results[0].DockRoom, results[0].HasDockRoom);
                Assert.AreEqual(results[0].BestOther, again.BestOther);
                Assert.AreEqual(results[0].BestDirection, again.BestDirection);
            }

            List<Result> valid = results.Where(r => r.RealOther >= 0).ToList();
            int analyticAgree = valid.Count(r => r.AnalyticOther == r.RealOther);
            int analyticPassStrict = valid.Count(r => r.AnalyticOther == 0);
            int realPassStrict = valid.Count(r => r.RealOther == 0);
            int realPassGolden = valid.Count(r => r.RealOther <= goldenOther);
            int aStrict = valid.Count(r => r.BestOther == 0);
            int aGolden = valid.Count(r => r.BestOther >= 0 && r.BestOther <= goldenOther);
            int aHasOption = valid.Count(r => r.ExteriorOptionCount > 0);
            int dStrict = valid.Count(r => r.AnyRoomBestOther == 0);
            string[] whitelist = { "spine", "bifurcated", "stacked" };
            List<Result> whitelisted = valid.Where(r => whitelist.Contains(r.Template)).ToList();

            double PerAttempt(int pass) => valid.Count == 0 ? 0.0 : (double)pass / valid.Count;
            string Within8(int pass) => (100.0 * (1.0 - Math.Pow(1.0 - PerAttempt(pass), 8))).ToString("0.0", CultureInfo.InvariantCulture) + "%";

            var sb = new StringBuilder();
            sb.AppendLine("## Lifeboat-fit spike results (" + seeds + " seeds, Pristine, `" + Biome + "` / `" + Difficulty + "`)");
            sb.AppendLine();
            sb.AppendLine("Homes measured: " + valid.Count + " (first-attempt successes " + firstAttempt + "). Golden hub through the real transform: other-room overlap " + goldenOther + ", dock-room overlap " + goldenInDock + ".");
            sb.AppendLine();
            sb.AppendLine("Real boat cell centres for the golden hub (home coordinates): " + string.Join(", ", goldenBoat.Select(b => "(" + b.X.ToString("0.#", CultureInfo.InvariantCulture) + "," + b.Z.ToString("0.#", CultureInfo.InvariantCulture) + ")")) + "; dock port " + goldenPort.Get("position", null));
            sb.AppendLine();
            sb.AppendLine("### Model check: the 1.7b analytic strip versus the real transform");
            sb.AppendLine();
            sb.AppendLine("| Measure | Result |");
            sb.AppendLine("| --- | --- |");
            sb.AppendLine("| Analytic and real-transform overlap counts agree | " + analyticAgree + " / " + valid.Count + " (" + Pct(analyticAgree, valid.Count) + ") |");
            sb.AppendLine("| Strict pass (0 other-room cells), analytic / real | " + analyticPassStrict + " (" + Pct(analyticPassStrict, valid.Count) + ") / " + realPassStrict + " (" + Pct(realPassStrict, valid.Count) + ") |");
            sb.AppendLine("| No worse than the golden hub (" + goldenOther + "), real transform | " + realPassGolden + " (" + Pct(realPassGolden, valid.Count) + ") |");
            sb.AppendLine();
            sb.AppendLine("### Candidates (per-attempt pass rate; P(8) = chance at least one of 8 independent attempts passes)");
            sb.AppendLine();
            sb.AppendLine("| Candidate | Strict (0 other-room cells) | P(8), strict | No worse than golden | P(8), golden-calibrated |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            sb.AppendLine("| Current port (room centroid, +X), reject by fit | " + realPassStrict + " (" + Pct(realPassStrict, valid.Count) + ") | " + Within8(realPassStrict) + " | " + realPassGolden + " (" + Pct(realPassGolden, valid.Count) + ") | " + Within8(realPassGolden) + " |");
            sb.AppendLine("| A. Dock room's exterior edge, best direction (needs a free outward neighbour) | " + aStrict + " (" + Pct(aStrict, valid.Count) + ") | " + Within8(aStrict) + " | " + aGolden + " (" + Pct(aGolden, valid.Count) + ") | " + Within8(aGolden) + " |");
            sb.AppendLine("| D. Best exterior edge of any room (upper bound; moves the dock room) | " + dStrict + " (" + Pct(dStrict, valid.Count) + ") | " + Within8(dStrict) + " | - | - |");
            int wlPass = whitelisted.Count(r => r.RealOther <= goldenOther);
            sb.AppendLine("| C. Template whitelist (" + string.Join(", ", whitelist) + "), current port | " + whitelisted.Count(r => r.RealOther == 0) + " strict, " + wlPass + " golden-calibrated of " + whitelisted.Count + " whitelisted homes (" + Pct(whitelisted.Count, valid.Count) + " of seeds) | - | - | whitelist gate passes " + Pct(wlPass, valid.Count) + " of seeds per attempt |");
            sb.AppendLine();
            sb.AppendLine("Homes whose dock room has at least one exterior-facing edge: " + aHasOption + " / " + valid.Count + " (" + Pct(aHasOption, valid.Count) + ").");
            sb.AppendLine();
            sb.AppendLine("Candidate A best direction chosen: " + Histogram(valid.Where(r => r.BestDirection.Length != 0).Select(r => r.BestDirection)) + ".");
            sb.AppendLine();
            sb.AppendLine("Candidate A, boat cells that overlap no home cell at all (outside the hull), for homes with an option: " + Histogram(valid.Where(r => r.BestOther >= 0).Select(r => r.BoatCellsOutsideHull.ToString(CultureInfo.InvariantCulture))) + ".");
            sb.AppendLine();
            sb.AppendLine("Templates (all homes): " + Histogram(valid.Select(r => r.Template)) + ".");
            sb.AppendLine();
            sb.AppendLine("By template, current port (real transform) overlap, A strict pass:");
            sb.AppendLine();
            sb.AppendLine("| Template | Homes | Dedicated dock room | Current strict | Current golden-calibrated | A strict |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var g in valid.GroupBy(r => r.Template).OrderBy(g => g.Key, StringComparer.Ordinal))
                sb.AppendLine("| " + g.Key + " | " + g.Count() + " | " + g.Count(r => r.HasDockRoom) + " | " + g.Count(r => r.RealOther == 0) + " | " + g.Count(r => r.RealOther <= goldenOther) + " | " + g.Count(r => r.BestOther == 0) + " |");
            sb.AppendLine();
            List<Result> aMiss = valid.Where(r => r.BestOther != 0).ToList();
            sb.AppendLine("Seeds where candidate A does not reach 0 other-room cells: " + (aMiss.Count == 0 ? "none" : aMiss.Count + " (first: " + string.Join(", ", aMiss.Take(15).Select(r => r.Seed.ToString(CultureInfo.InvariantCulture) + " [" + r.Template + (r.ExteriorOptionCount == 0 ? ", no exterior edge" : ", best " + r.BestOther) + "]")) + ")"));
            sb.AppendLine();

            string report = sb.ToString();
            TestContext.WriteLine(report);
            string path = Environment.GetEnvironmentVariable("SYNAPTICSEA_SPIKE_REPORT");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, report);

            Assert.Greater(valid.Count, 0, "at least one generated home was measured");
        }
    }
}
