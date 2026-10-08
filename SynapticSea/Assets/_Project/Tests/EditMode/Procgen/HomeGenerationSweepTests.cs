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
    /// Phase 1.7b: measurement only. How often does <see cref="StartSceneBuilder.BuildHomeStart"/> produce a home that could host a New Run, with
    /// today's Damaged layout condition versus Pristine? Nothing here changes production behaviour. The test asserts only loose invariants
    /// (no exceptions, determinism, at least one success per condition) and records everything else for the report in
    /// <c>docs/playtest/home-generation-sweep.md</c>.
    /// Set SYNAPTICSEA_SEED_SWEEP (default 200) for the number of seeds, and SYNAPTICSEA_SWEEP_REPORT to a file path to write the markdown tables.
    /// </summary>
    public class HomeGenerationSweepTests
    {
        const string Biome = "breach_field";
        const string Difficulty = "standard";
        const double CellSize = StructuralEdgeCompiler.CELL_SIZE;

        // The bar proposed for the go/no-go in the report.
        const int BarNonConnectiveRooms = 3;
        const int BarFreeSlots = 3;

        CollectingLog _log;
        int _goldenOverlap;

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
            public bool Success;
            public int Attempts;
            public long UsedSeed;
            public string Template = "";
            public int Rooms;
            public int NonConnectiveRooms;
            public int FloorCells;
            public int FreeSlotsOutsideStart;
            public int RoomsWithFreeSlot;
            public bool HasDockRoom;
            public string AnchorSource = "";
            public int BreachZones;
            public int Encounters;
            public int ArcZones;
            public int BlockedLinks;
            public int ModuleDamage;
            public int NonStandardRooms;
            public int LockedOrBreachEdges;
            public bool StartToDockEnclosure;
            public bool StartToDockStanding;
            public bool LifeboatFits;
            public int LifeboatOverlapOtherRoomCells;
            public int BrokenTravelSubs;
            public int BrokenOtherSubs;
            public int BrokenSurvivalSubs;
            public bool OpeningDamageProtectsSurvivalSubs;
            public bool MeetsBar;
            public bool MeetsBarWithoutLifeboatFit;
            public bool MeetsBarCalibrated;
            public bool StartIsDockRoom;
            public string StartRoomVariant = "";
            public int EncountersInStartRoom;
            public int HazardVariantRooms;
            public readonly List<string> Variants = new List<string>();
            public readonly List<string> RejectionReasons = new List<string>();
            public string Failure = "";
        }

        static readonly HashSet<string> HazardVariants = new HashSet<string> { "breached", "flooded", "collapsed", "burned_out", "biomatter_crusted" };

        static HashSet<string> Connective => new HashSet<string>(GameplaySliceBuilder.CONNECTIVE_ROLES);

        static string RoleOf(GdDict room)
        {
            string role = V.Str(room.Get("room_role", ""));
            return role.Length != 0 ? role : V.Str(room.Get("role", ""));
        }

        static string ReasonBucket(string warning)
        {
            // "StartSceneBuilder: home start rejected, seed 17: <reason>"
            int idx = warning.IndexOf("rejected, seed ", StringComparison.Ordinal);
            if (idx < 0) return "";
            int colon = warning.IndexOf(": ", idx, StringComparison.Ordinal);
            if (colon < 0) return "";
            string reason = warning.Substring(colon + 2);
            int cut = reason.IndexOfAny(new[] { ':', '[', '{' });
            if (cut > 0) reason = reason.Substring(0, cut);
            // Keep "room X is not walkable" lines in one bucket.
            if (reason.StartsWith("room ", StringComparison.Ordinal) && reason.Contains("not walkable")) reason = "an objective room is not walkable from the start room";
            return reason.Trim();
        }

        static string DockRoomId(GdArray rooms, out bool hasDockRoom)
        {
            hasDockRoom = false;
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room)) continue;
                string role = RoleOf(room), id = V.Str(room.Get("id", ""));
                if (role == "dock" || id.StartsWith("dock", StringComparison.Ordinal)) { hasDockRoom = true; return id; }
            }
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room)) continue;
                string role = RoleOf(room), id = V.Str(room.Get("id", ""));
                if (role == "airlock" || id.StartsWith("airlock", StringComparison.Ordinal)) return id;
            }
            return "";
        }

        static void BlockApproach(GdDict row, string roomId, HashSet<string> blocked)
        {
            if (row == null || V.Str(row.Get("room_id", "")) != roomId) return;
            GdArray cell = LayoutSerializer.ParseSlotCell(row.Get("approach_cell", new GdArray()));
            if (cell.Count >= 2) blocked.Add(V.I64(cell[0]) + "," + V.I64(cell[1]));
        }

        /// <summary>The free interior loot slots of every room except the start room, by the AddFirstWreckStores rules.</summary>
        static void FreeSlots(GdDict layout, GdDict slice, string startRoom, out int total, out int roomsWithFree)
        {
            total = 0;
            roomsWithFree = 0;
            GdArray containers = slice.GetArrayOrEmpty("loot_containers");
            foreach (object roomV in layout.GetArrayOrEmpty("rooms"))
            {
                if (!(roomV is GdDict room)) continue;
                string roomId = V.Str(room.Get("id", ""));
                if (roomId.Length == 0 || roomId == startRoom) continue;
                GdDict interior = room.GetDictOrEmpty("interior_zones");
                var blocked = new HashSet<string>();
                foreach (object r in interior.GetArrayOrEmpty("reserved_cells"))
                {
                    GdArray cell = LayoutSerializer.ParseSlotCell(r);
                    if (cell.Count >= 2) blocked.Add(V.I64(cell[0]) + "," + V.I64(cell[1]));
                }
                foreach (object other in containers) BlockApproach(other as GdDict, roomId, blocked);
                foreach (object objective in slice.GetArrayOrEmpty("objectives")) BlockApproach(objective as GdDict, roomId, blocked);
                int free = 0;
                foreach (string bucket in new[] { "center_slots", "wall_slots" })
                    foreach (object slot in interior.GetArrayOrEmpty(bucket))
                    {
                        GdArray cell = LayoutSerializer.ParseSlotCell(slot);
                        if (cell.Count >= 2 && !blocked.Contains(V.I64(cell[0]) + "," + V.I64(cell[1]))) free++;
                    }
                total += free;
                if (free > 0) roomsWithFree++;
            }
        }

        /// <summary>
        /// Best-effort lifeboat footprint: the docked boat's airlock sits half a cell east of the home port, with its engine bay one cell west and its
        /// cockpit one cell east of that (3 cells in a row, facing +X). The boat may overlap the dock/airlock room (decision 55) and nothing else.
        /// </summary>
        static bool LifeboatFit(GdDict layout, string dockRoomId, out int overlapOther)
        {
            overlapOther = -1;
            GdDict port = DockPorts.ForDerelict(layout);
            if (port.IsEmpty || !(port.Get("position", null) is Vec3 p)) return false;
            double xMin = p.X - CellSize, xMax = p.X + 2.0 * CellSize;
            GdDict occupancy = layout.GetDictOrEmpty("structural_plan").GetDictOrEmpty("occupancy");
            int other = 0;
            foreach (object recordV in occupancy.Values)
            {
                if (!(recordV is GdDict record)) continue;
                Vec3 pos = WalkabilityContract.OccupancyWorldPosition(record);
                if (pos == Vec3.Inf) continue;
                if (Math.Abs(pos.Z - p.Z) > CellSize * 0.5 || Math.Abs(pos.Y - p.Y) > 1.0) continue;
                double overlap = Math.Min(pos.X + CellSize * 0.5, xMax) - Math.Max(pos.X - CellSize * 0.5, xMin);
                if (overlap <= 0.5) continue;
                if (WalkabilityContract.OccupancyRoomId(record) == dockRoomId) continue;
                other++;
            }
            overlapOther = other;
            return other == 0;
        }

        static void OpeningDamage(StartSceneBuilder.HomeStart hs, Row row)
        {
            var manager = new ShipSystemsManager();
            manager.Configure(manager.LoadDefinitions(), hs.Blueprint.ShipCondition, hs.Blueprint.SeedValue);
            // RunSession.ApplyLifeboatOpeningDamage: every propulsion sub healthy except nav_linkage.
            ShipSystem prop = manager.GetSystem("propulsion");
            if (prop != null)
            {
                foreach (ShipSubcomponent sub in prop.Subcomponents) sub.Health = 1.0;
                ShipSubcomponent blocker = prop.GetSubcomponent("nav_linkage");
                if (blocker != null) blocker.Health = ShipSystemsManager.DAMAGED_HEALTH;
            }
            foreach (var entry in manager.Systems)
            {
                int broken = entry.Value.Subcomponents.Count(s => !s.IsFunctional());
                switch (entry.Key)
                {
                    case "power": case "navigation": case "propulsion": row.BrokenTravelSubs += broken; break;
                    case "life_support": case "scanners": case "gravity": row.BrokenSurvivalSubs += broken; row.BrokenOtherSubs += broken; break;
                    default: row.BrokenOtherSubs += broken; break;
                }
            }
            row.OpeningDamageProtectsSurvivalSubs = row.BrokenSurvivalSubs == 0;
        }

        static void Analyze(Row row, GdDict layout, GdDict slice, int calibratedOverlap = 0)
        {
            row.Template = V.Str(layout.Get("template_id", ""));
            GdArray rooms = layout.GetArrayOrEmpty("rooms");
            HashSet<string> connective = Connective;
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room)) continue;
                row.Rooms++;
                if (!connective.Contains(RoleOf(room))) row.NonConnectiveRooms++;
                string variant = V.Str(room.Get("variant", "standard"));
                row.Variants.Add(variant);
                if (variant != "standard") row.NonStandardRooms++;
                if (HazardVariants.Contains(variant)) row.HazardVariantRooms++;
                if (V.Str(room.Get("id", "")) == V.Str(slice.Get("start_room", ""))) row.StartRoomVariant = variant;
            }
            GdDict plan = layout.GetDictOrEmpty("structural_plan");
            GdDict occupancy = plan.GetDictOrEmpty("occupancy");
            row.FloorCells = occupancy.Count;
            string startRoom = V.Str(slice.Get("start_room", ""));
            FreeSlots(layout, slice, startRoom, out row.FreeSlotsOutsideStart, out row.RoomsWithFreeSlot);
            row.BreachZones = layout.GetArrayOrEmpty("breach_zones").Count + slice.GetArrayOrEmpty("breach_zones").Count;
            row.Encounters = layout.GetArrayOrEmpty("encounters").Count;
            foreach (object encV in layout.GetArrayOrEmpty("encounters"))
                if (encV is GdDict enc && V.Str(enc.Get("room_id", "")) == startRoom) row.EncountersInStartRoom++;
            row.ArcZones = layout.GetArrayOrEmpty("arc_zones").Count + slice.GetArrayOrEmpty("arc_zones").Count;
            row.BlockedLinks = layout.GetArrayOrEmpty("blocked_links").Count;
            row.ModuleDamage = layout.GetArrayOrEmpty("module_damage").Count;
            foreach (object edgeV in plan.GetDictOrEmpty("edges").Values)
            {
                if (!(edgeV is GdDict edge)) continue;
                string kind = WalkabilityContract.EdgeKind(edge);
                if (kind == "LOCKED" || kind == "BREACH") row.LockedOrBreachEdges++;
            }
            string dockRoom = DockRoomId(rooms, out row.HasDockRoom);
            row.StartIsDockRoom = dockRoom.Length != 0 && startRoom == dockRoom;
            if (dockRoom.Length != 0 && startRoom.Length != 0)
            {
                GdDict edges = plan.GetDictOrEmpty("edges");
                row.StartToDockEnclosure = startRoom == dockRoom ||
                    WalkabilityContract.RoomsReachable(WalkabilityContract.BuildAdjacency(occupancy, edges, layout, false), occupancy, startRoom, dockRoom);
                row.StartToDockStanding = startRoom == dockRoom ||
                    WalkabilityContract.RoomsReachable(WalkabilityContract.BuildAdjacency(occupancy, edges, layout, true), occupancy, startRoom, dockRoom);
            }
            row.LifeboatFits = LifeboatFit(layout, dockRoom, out row.LifeboatOverlapOtherRoomCells);
            row.MeetsBarWithoutLifeboatFit = row.NonConnectiveRooms >= BarNonConnectiveRooms && row.FreeSlotsOutsideStart >= BarFreeSlots && row.StartToDockStanding;
            row.MeetsBar = row.MeetsBarWithoutLifeboatFit && row.LifeboatFits;
            row.MeetsBarCalibrated = row.MeetsBarWithoutLifeboatFit && row.LifeboatOverlapOtherRoomCells >= 0 && row.LifeboatOverlapOtherRoomCells <= calibratedOverlap;
        }

        Row Measure(long seed, long condition)
        {
            var row = new Row { Seed = seed };
            int warningsBefore = _log.Warnings.Count;
            StartSceneBuilder.HomeStart hs = StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty, condition: condition);
            foreach (string w in _log.Warnings.Skip(warningsBefore))
            {
                string bucket = ReasonBucket(w);
                if (bucket.Length != 0) row.RejectionReasons.Add(bucket);
            }
            if (hs == null)
            {
                row.Success = false;
                row.Attempts = StartSceneBuilder.MAX_START_ATTEMPTS;
                row.Failure = "no viable home in " + StartSceneBuilder.MAX_START_ATTEMPTS + " attempts";
                return row;
            }

            row.Success = true;
            row.Attempts = hs.Attempts;
            row.UsedSeed = hs.Seed;
            row.AnchorSource = hs.AnchorSource;
            Analyze(row, hs.Documents.Layout, hs.Documents.GameplaySlice ?? new GdDict(), _goldenOverlap);
            OpeningDamage(hs, row);
            return row;
        }

        static string Pct(int n, int d) => d == 0 ? "n/a" : (100.0 * n / d).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        static string Spread(IEnumerable<int> values)
        {
            List<int> v = values.OrderBy(x => x).ToList();
            if (v.Count == 0) return "n/a";
            return v[0] + " / " + v[v.Count / 2] + " / " + v[v.Count - 1];
        }

        static string Histogram(IEnumerable<string> keys)
        {
            var groups = keys.GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            return groups.Count == 0 ? "none" : string.Join("; ", groups.Select(g => g.Key + " x" + g.Count()));
        }

        static void Section(StringBuilder sb, string title, List<Row> rows, int seeds, int goldenOverlap)
        {
            List<Row> ok = rows.Where(r => r.Success).ToList();
            sb.AppendLine("### " + title);
            sb.AppendLine();
            sb.AppendLine("| Measure | Result |");
            sb.AppendLine("| --- | --- |");
            sb.AppendLine("| Seeds swept | " + seeds + " |");
            sb.AppendLine("| `BuildHomeStart` succeeds within " + StartSceneBuilder.MAX_START_ATTEMPTS + " attempts | " + ok.Count + " / " + seeds + " (" + Pct(ok.Count, seeds) + ") |");
            sb.AppendLine("| Succeeds on the first attempt | " + ok.Count(r => r.Attempts == 1) + " / " + seeds + " (" + Pct(ok.Count(r => r.Attempts == 1), seeds) + ") |");
            sb.AppendLine("| Attempts used (successes) | " + Histogram(ok.Select(r => r.Attempts.ToString(CultureInfo.InvariantCulture))) + " |");
            sb.AppendLine("| Rejection reasons (all attempts) | " + Histogram(rows.SelectMany(r => r.RejectionReasons)) + " |");
            sb.AppendLine("| Templates chosen | " + Histogram(ok.Select(r => r.Template)) + " |");
            sb.AppendLine("| Rooms, min / median / max | " + Spread(ok.Select(r => r.Rooms)) + " |");
            sb.AppendLine("| Non-connective rooms, min / median / max | " + Spread(ok.Select(r => r.NonConnectiveRooms)) + " |");
            sb.AppendLine("| Floor cells, min / median / max | " + Spread(ok.Select(r => r.FloorCells)) + " |");
            sb.AppendLine("| Free interior loot slots outside the start room, min / median / max | " + Spread(ok.Select(r => r.FreeSlotsOutsideStart)) + " |");
            sb.AppendLine("| Rooms with a free slot, min / median / max | " + Spread(ok.Select(r => r.RoomsWithFreeSlot)) + " |");
            sb.AppendLine("| Dedicated dock room (else airlock / boarding fallback) | " + ok.Count(r => r.HasDockRoom) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.HasDockRoom), ok.Count) + ") |");
            sb.AppendLine("| Anchor source | " + Histogram(ok.Select(r => r.AnchorSource)) + " |");
            sb.AppendLine("| Start room reaches the dock/airlock room (enclosure rules, the gate's model) | " + ok.Count(r => r.StartToDockEnclosure) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.StartToDockEnclosure), ok.Count) + ") |");
            sb.AppendLine("| Start room reaches the dock/airlock room (standing rules: OPEN/DOOR/HATCH only) | " + ok.Count(r => r.StartToDockStanding) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.StartToDockStanding), ok.Count) + ") |");
            sb.AppendLine("| Lifeboat footprint overlaps only the dock/airlock room | " + ok.Count(r => r.LifeboatFits) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.LifeboatFits), ok.Count) + ") |");
            sb.AppendLine("| Lifeboat overlap with other rooms' cells, min / median / max | " + Spread(ok.Select(r => Math.Max(0, r.LifeboatOverlapOtherRoomCells))) + " |");
            sb.AppendLine("| Homes with any breach zone | " + ok.Count(r => r.BreachZones > 0) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.BreachZones > 0), ok.Count) + "); zones min / median / max " + Spread(ok.Select(r => r.BreachZones)) + " |");
            sb.AppendLine("| Homes with authored encounters | " + ok.Count(r => r.Encounters > 0) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.Encounters > 0), ok.Count) + "); markers min / median / max " + Spread(ok.Select(r => r.Encounters)) + " |");
            sb.AppendLine("| Homes with an arc zone | " + ok.Count(r => r.ArcZones > 0) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.ArcZones > 0), ok.Count) + ") |");
            sb.AppendLine("| Homes with blocked links / module damage / LOCKED or BREACH edges | " + ok.Count(r => r.BlockedLinks > 0) + " / " + ok.Count(r => r.ModuleDamage > 0) + " / " + ok.Count(r => r.LockedOrBreachEdges > 0) + " of " + ok.Count + " |");
            sb.AppendLine("| Homes with a room variant other than standard | " + ok.Count(r => r.NonStandardRooms > 0) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.NonStandardRooms > 0), ok.Count) + ") |");
            sb.AppendLine("| Homes with no hazard of any kind (breach, encounter, arc, blocked link, module damage, LOCKED/BREACH edge, non-standard room) | " +
                ok.Count(r => r.BreachZones + r.Encounters + r.ArcZones + r.BlockedLinks + r.ModuleDamage + r.LockedOrBreachEdges + r.NonStandardRooms == 0) + " / " + ok.Count + " |");
            sb.AppendLine("| Broken power / navigation / propulsion subs at New Run (after the opening damage), min / median / max | " + Spread(ok.Select(r => r.BrokenTravelSubs)) + " |");
            sb.AppendLine("| Homes that start with a broken life_support / scanners / gravity sub | " + ok.Count(r => !r.OpeningDamageProtectsSurvivalSubs) + " / " + ok.Count + " (" + Pct(ok.Count(r => !r.OpeningDamageProtectsSurvivalSubs), ok.Count) + ") |");
            sb.AppendLine("| Lifeboat overlap no worse than the golden hub's (" + goldenOverlap + " other-room cell" + (goldenOverlap == 1 ? "" : "s") + ") | " + ok.Count(r => r.LifeboatOverlapOtherRoomCells >= 0 && r.LifeboatOverlapOtherRoomCells <= goldenOverlap) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.LifeboatOverlapOtherRoomCells >= 0 && r.LifeboatOverlapOtherRoomCells <= goldenOverlap), ok.Count) + ") |");
            sb.AppendLine("| Start room (the airlock/dock) has a non-standard variant | " + ok.Count(r => r.StartRoomVariant.Length != 0 && r.StartRoomVariant != "standard") + " / " + ok.Count + " (" + Pct(ok.Count(r => r.StartRoomVariant.Length != 0 && r.StartRoomVariant != "standard"), ok.Count) + ") |");
            sb.AppendLine("| Homes with a hazardous room variant (breached, flooded, collapsed, burned_out, biomatter_crusted); rooms per home min / median / max | " + ok.Count(r => r.HazardVariantRooms > 0) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.HazardVariantRooms > 0), ok.Count) + "); " + Spread(ok.Select(r => r.HazardVariantRooms)) + " |");
            sb.AppendLine("| Homes with an encounter marker in the start room | " + ok.Count(r => r.EncountersInStartRoom > 0) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.EncountersInStartRoom > 0), ok.Count) + ") |");
            sb.AppendLine("| Room variants (all rooms of all homes) | " + Histogram(ok.SelectMany(r => r.Variants)) + " |");
            sb.AppendLine("| Start room is the dock/airlock room itself | " + ok.Count(r => r.StartIsDockRoom) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.StartIsDockRoom), ok.Count) + ") |");
            sb.AppendLine("| Module-damage entries, min / median / max | " + Spread(ok.Select(r => r.ModuleDamage)) + " |");
            sb.AppendLine("| Homes with >= " + BarNonConnectiveRooms + " non-connective rooms | " + ok.Count(r => r.NonConnectiveRooms >= BarNonConnectiveRooms) + " / " + ok.Count + " (" + Pct(ok.Count(r => r.NonConnectiveRooms >= BarNonConnectiveRooms), ok.Count) + ") |");
            sb.AppendLine("| Meets the bar if the lifeboat-fit check is ignored | " + ok.Count(r => r.MeetsBarWithoutLifeboatFit) + " / " + seeds + " (" + Pct(ok.Count(r => r.MeetsBarWithoutLifeboatFit), seeds) + ") |");
            sb.AppendLine("| Meets the bar with the lifeboat check calibrated to the golden hub | " + ok.Count(r => r.MeetsBarCalibrated) + " / " + seeds + " (" + Pct(ok.Count(r => r.MeetsBarCalibrated), seeds) + ") |");
            sb.AppendLine("| **Meets the bar** (success, >= " + BarNonConnectiveRooms + " non-connective rooms, >= " + BarFreeSlots + " free slots outside the start room, start reaches the dock by standing rules, lifeboat fits) | **" + ok.Count(r => r.MeetsBar) + " / " + seeds + " (" + Pct(ok.Count(r => r.MeetsBar), seeds) + ")** |");
            sb.AppendLine();
            sb.AppendLine("By template (lifeboat overlap with other rooms' cells; calibrated pass = no worse than the golden hub's " + goldenOverlap + "):");
            sb.AppendLine();
            sb.AppendLine("| Template | Homes | Dedicated dock room | Overlap min / median / max | Calibrated pass |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var group in ok.GroupBy(r => r.Template).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                List<Row> g = group.ToList();
                sb.AppendLine("| " + group.Key + " | " + g.Count + " | " + g.Count(r => r.HasDockRoom) + " | " + Spread(g.Select(r => Math.Max(0, r.LifeboatOverlapOtherRoomCells))) + " | " +
                    g.Count(r => r.LifeboatOverlapOtherRoomCells >= 0 && r.LifeboatOverlapOtherRoomCells <= goldenOverlap) + " / " + g.Count + " |");
            }
            sb.AppendLine();
            sb.AppendLine("By anchor source (calibrated pass): dock room " + ok.Count(r => r.HasDockRoom && r.LifeboatOverlapOtherRoomCells >= 0 && r.LifeboatOverlapOtherRoomCells <= goldenOverlap) + " / " + ok.Count(r => r.HasDockRoom) +
                "; airlock/boarding fallback " + ok.Count(r => !r.HasDockRoom && r.LifeboatOverlapOtherRoomCells >= 0 && r.LifeboatOverlapOtherRoomCells <= goldenOverlap) + " / " + ok.Count(r => !r.HasDockRoom));
            sb.AppendLine();
            List<Row> fails = rows.Where(r => !r.Success).ToList();
            sb.AppendLine("Seeds with no viable home in " + StartSceneBuilder.MAX_START_ATTEMPTS + " attempts: " + (fails.Count == 0 ? "none" : string.Join(", ", fails.Take(30).Select(r => r.Seed.ToString(CultureInfo.InvariantCulture))) + (fails.Count > 30 ? ", ... (" + fails.Count + " total)" : "")));
            List<Row> fewRooms = ok.Where(r => r.NonConnectiveRooms < BarNonConnectiveRooms).ToList();
            sb.AppendLine();
            sb.AppendLine("Valid homes with fewer than " + BarNonConnectiveRooms + " non-connective rooms: " + (fewRooms.Count == 0 ? "none" : fewRooms.Count + " (seeds: " + string.Join(", ", fewRooms.Take(15).Select(r => r.Seed.ToString(CultureInfo.InvariantCulture) + " [" + r.Template + "]")) + (fewRooms.Count > 15 ? ", ..." : "") + ")"));
            List<Row> notBar = ok.Where(r => !r.MeetsBar).ToList();
            sb.AppendLine();
            sb.AppendLine("Valid homes that miss the bar: " + (notBar.Count == 0 ? "none" : notBar.Count + " (first seeds: " + string.Join(", ", notBar.Take(15).Select(r => r.Seed.ToString(CultureInfo.InvariantCulture))) + ")"));
            sb.AppendLine();
        }

        static string Signature(StartSceneBuilder.HomeStart hs) =>
            hs == null ? "null" : hs.Seed + "|" + hs.Documents.LayoutJson + "|" + GdJson.Stringify(hs.Documents.GameplaySlice ?? new GdDict());

        [Test]
        public void GeneratedHomesAcrossManySeeds_DamagedVersusPristine()
        {
            int seeds = int.TryParse(Environment.GetEnvironmentVariable("SYNAPTICSEA_SEED_SWEEP"), out int n) && n > 0 ? n : 200;
            // Calibrate the lifeboat-fit check on the one home the game is known to dock correctly.
            var goldenRow = new Row { Success = true, Attempts = 1 };
            string goldenError = "";
            try
            {
                string dir = Path.Combine(Fixtures.StreamingDataRoot, "data", "procgen", "golden", "coherent_ship_001");
                var goldenLayout = (GdDict)GdJson.Parse(File.ReadAllText(Path.Combine(dir, "layout.json")));
                var goldenSlice = (GdDict)GdJson.Parse(File.ReadAllText(Path.Combine(dir, "gameplay_slice.json")));
                Analyze(goldenRow, goldenLayout, goldenSlice, 0);
                _goldenOverlap = Math.Max(0, goldenRow.LifeboatOverlapOtherRoomCells);
            }
            catch (Exception e) { goldenError = e.GetType().Name + ": " + e.Message; }

            var damaged = new List<Row>();
            var pristine = new List<Row>();
            for (long seed = 1; seed <= seeds; seed++)
            {
                damaged.Add(Measure(seed, (long)ShipBlueprint.Condition.Damaged));
                pristine.Add(Measure(seed, (long)ShipBlueprint.Condition.Pristine));
            }

            // Determinism on a prefix of the sweep, for both conditions.
            int checkedSeeds = Math.Min(10, seeds);
            for (long seed = 1; seed <= checkedSeeds; seed++)
            {
                foreach (long condition in new[] { (long)ShipBlueprint.Condition.Damaged, (long)ShipBlueprint.Condition.Pristine })
                {
                    string a = Signature(StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty, condition: condition));
                    string b = Signature(StartSceneBuilder.BuildHomeStart(seed, Biome, Difficulty, condition: condition));
                    Assert.AreEqual(a, b, "seed " + seed + " condition " + condition + " must generate identical documents twice");
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("## Sweep results (" + seeds + " seeds, biome `" + Biome + "`, difficulty `" + Difficulty + "`, size Medium)");
            sb.AppendLine();
            Section(sb, "Damaged layout condition (today's `StartSceneBuilder.HOME_CONDITION`)", damaged, seeds, _goldenOverlap);
            Section(sb, "Pristine layout condition", pristine, seeds, _goldenOverlap);
            sb.AppendLine("### Baseline: the golden hub (`coherent_ship_001`) through the same checks");
            sb.AppendLine();
            if (goldenError.Length == 0)
            {
                Row golden = goldenRow;
                sb.AppendLine("| Measure | Golden hub |");
                sb.AppendLine("| --- | --- |");
                sb.AppendLine("| Rooms / non-connective rooms / floor cells | " + golden.Rooms + " / " + golden.NonConnectiveRooms + " / " + golden.FloorCells + " |");
                sb.AppendLine("| Free interior loot slots outside the start room | " + golden.FreeSlotsOutsideStart + " in " + golden.RoomsWithFreeSlot + " rooms (its supplies are authored containers) |");
                sb.AppendLine("| Dedicated dock room | " + golden.HasDockRoom + " (start room is the dock/airlock room: " + golden.StartIsDockRoom + ") |");
                sb.AppendLine("| Start reaches the dock/airlock room (enclosure / standing) | " + golden.StartToDockEnclosure + " / " + golden.StartToDockStanding + " |");
                sb.AppendLine("| Lifeboat footprint overlaps only the dock/airlock room (strict check) | " + golden.LifeboatFits + " (overlap with other rooms' cells: " + golden.LifeboatOverlapOtherRoomCells + ") |");
                sb.AppendLine("| Breach zones / encounters / arc zones / blocked links / module damage / LOCKED or BREACH edges | " +
                    golden.BreachZones + " / " + golden.Encounters + " / " + golden.ArcZones + " / " + golden.BlockedLinks + " / " + golden.ModuleDamage + " / " + golden.LockedOrBreachEdges + " |");
                sb.AppendLine("| Room variants | " + Histogram(golden.Variants) + " |");
                sb.AppendLine();
                sb.AppendLine("The golden hub is the only home the lifeboat is known to dock to correctly, and it fails the strict lifeboat-fit check (overlap " + _goldenOverlap + "). So the strict check is stricter than what the game accepts. The tables above also report a calibrated check: other-room overlap no worse than the golden hub's " + _goldenOverlap + ".");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("The golden baseline could not be computed: " + goldenError);
                sb.AppendLine();
            }
            string report = sb.ToString();
            TestContext.WriteLine(report);
            string path = Environment.GetEnvironmentVariable("SYNAPTICSEA_SWEEP_REPORT");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, report);

            Assert.Greater(damaged.Count(r => r.Success), 0, "at least one Damaged home is generated");
            Assert.Greater(pristine.Count(r => r.Success), 0, "at least one Pristine home is generated");
        }
    }
}
