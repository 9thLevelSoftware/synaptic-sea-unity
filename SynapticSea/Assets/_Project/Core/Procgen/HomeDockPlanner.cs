using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Phase 1.7c spike. Picks where the always-attached life boat docks to a generated home so it overlaps only the home's own
    /// dock/airlock room. The default port (<see cref="DockPorts.ForDerelict"/>) is the room's floor centroid facing +X, which
    /// lays the 12 m boat across neighbouring rooms in most hulls (see docs/playtest/lifeboat-fit-spike-1.7c.md). This planner
    /// instead puts the port on the outward edge of one of the dock/airlock room's own exterior-facing cells, so the boat's engine
    /// bay coincides with that cell and its airlock and cockpit sit outside the hull. The choice is written to the layout as a
    /// <c>docking_port</c> contract, which <see cref="DockPorts.ForDerelict"/> honours.
    /// Not wired to New Run: <see cref="StartSceneBuilder.BuildHomeStart"/> only uses it when asked.
    /// </summary>
    public static class HomeDockPlanner
    {
        const double Cell = StructuralEdgeCompiler.CELL_SIZE;

        /// <summary>Outward directions in tie-break order.</summary>
        static readonly (double dx, double dz)[] Directions = { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) };

        struct HomeCell
        {
            public Vec3 Pos;
            public string Room;
        }

        /// <summary>The planned contract and how well the boat fits it.</summary>
        public sealed class Plan
        {
            public GdDict Contract;
            /// <summary>Home cells of other rooms the boat footprint overlaps (0 = fits).</summary>
            public int OtherRoomOverlap;
            public string RoomId = "";
        }

        static string RoleOf(GdDict room)
        {
            string role = V.Str(room.Get("room_role", ""));
            return role.Length != 0 ? role : V.Str(room.Get("role", ""));
        }

        /// <summary>The dock room, else the airlock room: the room <see cref="DockPorts.ForDerelict"/> anchors on.</summary>
        public static string DockRoomId(GdDict layout)
        {
            GdArray rooms = layout?.GetArrayOrEmpty("rooms") ?? new GdArray();
            foreach (object roomV in rooms)
                if (roomV is GdDict room && (RoleOf(room) == "dock" || V.Str(room.Get("id", "")).StartsWith("dock", StringComparison.Ordinal)))
                    return V.Str(room.Get("id", ""));
            foreach (object roomV in rooms)
                if (roomV is GdDict room && (RoleOf(room) == "airlock" || V.Str(room.Get("id", "")).StartsWith("airlock", StringComparison.Ordinal)))
                    return V.Str(room.Get("id", ""));
            return "";
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

        /// <summary>The boat's cell centres in home coordinates through the real docking transform; null when the boat layout is unusable.</summary>
        static List<Vec3> BoatCells(GdDict hostPort)
        {
            GdDict boat = LifeBoatBuilder.BuildLayout();
            GdDict mobile = DockPorts.ForLifeboat(boat);
            if (mobile.IsEmpty) return null;
            Xform3 transform = DockingManager.ComputeMobileTransform(hostPort, mobile);
            var cells = new List<Vec3>();
            foreach (object recordV in boat.GetDictOrEmpty("structural_plan").GetDictOrEmpty("occupancy").Values)
            {
                if (!(recordV is GdDict record)) continue;
                Vec3 pos = WalkabilityContract.OccupancyWorldPosition(record);
                if (pos == Vec3.Inf) continue;
                cells.Add(transform * pos);
            }
            return cells.Count == 0 ? null : cells;
        }

        /// <summary>Home cells of rooms other than <paramref name="ownRoom"/> that a boat cell's square overlaps by more than half a metre on both axes.</summary>
        static int OtherRoomOverlap(List<HomeCell> home, List<Vec3> boat, string ownRoom)
        {
            var seen = new HashSet<int>();
            int other = 0;
            foreach (Vec3 b in boat)
                for (int i = 0; i < home.Count; i++)
                {
                    HomeCell h = home[i];
                    if (Math.Abs(b.Y - h.Pos.Y) >= 1.0) continue;
                    if (Cell - Math.Abs(b.X - h.Pos.X) <= 0.5 || Cell - Math.Abs(b.Z - h.Pos.Z) <= 0.5) continue;
                    if (seen.Add(i) && h.Room != ownRoom) other++;
                }
            return other;
        }

        /// <summary>
        /// The best exterior-edge dock port of the dock/airlock room, or null (with <paramref name="reason"/>) when the room has no
        /// exterior-facing edge. Deterministic: fewest other-room overlaps, then direction order (+X, -X, +Z, -Z), then lowest cell X, then Z.
        /// </summary>
        public static Plan TryPlan(GdDict layout, out string reason)
        {
            reason = "";
            string dockRoom = DockRoomId(layout);
            if (dockRoom.Length == 0) { reason = "no dock or airlock room"; return null; }
            GdDict baseline = DockPorts.ForDerelict(layout);
            if (baseline.IsEmpty || !(baseline.Get("position", null) is Vec3 basePosition)) { reason = "no dock port to derive the deck height from"; return null; }
            List<HomeCell> home = HomeCells(layout);
            List<HomeCell> dockCells = home.Where(h => h.Room == dockRoom).ToList();
            if (dockCells.Count == 0) { reason = "dock room " + dockRoom + " has no occupied cells"; return null; }
            double offsetY = basePosition.Y - dockCells.Average(h => h.Pos.Y);

            Plan best = null;
            int bestDirection = int.MaxValue;
            foreach (HomeCell c in dockCells.OrderBy(h => h.Pos.X).ThenBy(h => h.Pos.Z))
            {
                for (int d = 0; d < Directions.Length; d++)
                {
                    (double dx, double dz) = Directions[d];
                    bool occupied = home.Any(h => Math.Abs(h.Pos.Y - c.Pos.Y) < 1.0
                        && Math.Abs(h.Pos.X - (c.Pos.X + dx * Cell)) < 1.0 && Math.Abs(h.Pos.Z - (c.Pos.Z + dz * Cell)) < 1.0);
                    if (occupied) continue;
                    var position = new Vec3(c.Pos.X + dx * Cell * 0.5, c.Pos.Y + offsetY, c.Pos.Z + dz * Cell * 0.5);
                    var port = new GdDict { { "position", position }, { "facing", new Vec3(dx, 0.0, dz) } };
                    List<Vec3> boat = BoatCells(port);
                    if (boat == null) { reason = "life boat layout unavailable"; return null; }
                    int overlap = OtherRoomOverlap(home, boat, dockRoom);
                    bool better = best == null || overlap < best.OtherRoomOverlap
                        || (overlap == best.OtherRoomOverlap && d < bestDirection);
                    if (!better) continue;
                    best = new Plan
                    {
                        OtherRoomOverlap = overlap,
                        RoomId = dockRoom,
                        Contract = new GdDict
                        {
                            { "contract_version", 1L },
                            { "room_id", dockRoom },
                            { "source", "exterior_edge" },
                            { "position", GdArray.Of(position.X, position.Y, position.Z) },
                            { "facing", GdArray.Of(dx, 0.0, dz) },
                        },
                    };
                    bestDirection = d;
                }
            }
            if (best == null) reason = "dock room " + dockRoom + " has no exterior-facing edge for the life boat";
            return best;
        }

        /// <summary>
        /// Plans the dock and, when the boat fits, stamps the contract on every mirror of the layout. "" on success, else the rejection
        /// reason (so <see cref="StartSceneBuilder.BuildHomeStart"/> can re-roll the seed).
        /// </summary>
        public static string Apply(ShipDocuments docs)
        {
            if (docs?.Layout == null) return "no layout";
            Plan plan = TryPlan(docs.Layout, out string reason);
            if (plan == null) return reason;
            if (plan.OtherRoomOverlap != 0)
                return "the life boat would overlap " + plan.OtherRoomOverlap + " cell(s) of other rooms at every exterior edge of " + plan.RoomId;
            docs.Layout["docking_port"] = plan.Contract.DeepCopy();
            if (docs.SourceLayout != null && !ReferenceEquals(docs.SourceLayout, docs.Layout))
                docs.SourceLayout["docking_port"] = plan.Contract.DeepCopy();
            if (docs.LayoutJson != null) docs.LayoutJson = GdJson.Stringify(docs.Layout, "  ");
            if (DockPorts.ForDerelict(docs.Layout).IsEmpty) return "the stamped dock contract is not a valid dock port";
            return "";
        }
    }
}
