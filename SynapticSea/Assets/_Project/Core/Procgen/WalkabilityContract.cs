// Ported from scripts/procgen/walkability_contract.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Player-capsule and compiler-edge walkability numbers (REQ-WALK-001). Enclosure flood is non-SOLID;
    /// standing-play is OPEN/DOOR/HATCH only. Geometry follows Godot's float32 Vector2/Vector3/Basis maths.
    /// </summary>
    public static class WalkabilityContract
    {
        public const double PLAYER_RADIUS_M = 0.35;
        public const double PLAYER_HEIGHT_M = 1.6;
        public const double CLEARANCE_MARGIN_M = 0.10;
        public const double STANDING_OPENING_WIDTH_M = 0.80;
        public const double STANDING_OPENING_HEIGHT_M = 1.70;
        public const double SLAB_THICKNESS_M = 0.20;
        public const double DOOR_OPENING_WIDTH_M = 1.20;
        public const double CAPSULE_FLOOR_OFFSET_M = 0.12;
        public const double WALL_HEIGHT_M = 3.0;
        public const double DOOR_HEIGHT_M = 3.2;
        public const double WALL_HALF_SPAN_M = 2.0;
        public const double HEADER_CLEARANCE_M = 0.10;
        public const double SLAB_INTERIOR_T_EPS = 0.05;
        /// <summary>Live wrapper proxies (REQ-DECAY-002). Inner post faces at ±0.6 m = opening 1.20 m.</summary>
        public const double DOOR_POST_WIDTH_M = 1.4;
        public const double DOOR_POST_OFFSET_X_M = 1.3;
        public const double DOOR_HEADER_HEIGHT_M = 1.0;
        public const double DOOR_HEADER_BOTTOM_Y_M = 2.2;

        public static readonly IReadOnlyList<string> STANDING_KINDS = new[] { "OPEN", "DOOR", "HATCH" };

        static readonly Vec2i Sentinel = new Vec2i(-99999, -99999);

        public static bool EnclosurePassable(string kind) => (kind ?? "").ToUpperInvariant() != "SOLID";

        public static bool StandingPassable(string kind)
        {
            string k = (kind ?? "").ToUpperInvariant();
            foreach (string s in STANDING_KINDS)
                if (s == k) return true;
            return false;
        }

        public static string EdgeKind(GdDict edge) => V.Str(edge.Get("kind", edge.Get("state", "SOLID"))).ToUpperInvariant();

        public static string OccupancyCellKey(GdDict record, string occupancyKey)
        {
            string declared = V.Str(record.Get("cell_key", occupancyKey));
            return declared.Length != 0 ? declared : occupancyKey;
        }

        public static long OccupancyDeck(GdDict record) => V.I64(record.Get("deck", 0L));

        public static Vec2i OccupancyCell(GdDict record) => ReadCellXz(record.Get("cell", null));

        public static Vec3 OccupancyWorldPosition(GdDict record)
        {
            object raw = record.Get("position", record.Get("world_position", null));
            if (raw is Vec3 v) return v;
            if (raw is GdArray values && values.Count >= 3) return new Vec3(V.F64(values[0]), V.F64(values[1]), V.F64(values[2]));
            return StructuralEdgeCompiler.CellWorldPosition(OccupancyDeck(record), OccupancyCell(record));
        }

        public static string OccupancyRoomId(GdDict record) => V.Str(record.Get("room_id", ""));

        /// <summary>Cell-key adjacency (<c>{cell_key: [neighbor keys]}</c>) through passable edges and vertical links.</summary>
        public static GdDict BuildAdjacency(GdDict occupancy, GdDict edges, GdDict topology, bool standing)
        {
            var adjacency = new GdDict();
            foreach (var key in occupancy.Keys) adjacency[V.Str(key)] = new GdArray();
            foreach (var edgeVariant in edges.Values)
            {
                if (!(edgeVariant is GdDict edge)) continue;
                string kind = EdgeKind(edge);
                if (standing)
                {
                    if (!StandingPassable(kind)) continue;
                }
                else if (!EnclosurePassable(kind))
                {
                    continue;
                }
                List<string> pair = OccupiedEdgeKeys(edge, occupancy, topology);
                if (pair.Count != 2) continue;
                Link(adjacency, pair[0], pair[1]);
            }
            AddVerticalLinks(adjacency, occupancy, topology);
            if (standing) OverlayBlockedLinks(adjacency, occupancy, topology);
            return adjacency;
        }

        public static GdDict FloodVisited(GdDict adjacency, string startKey)
        {
            var visited = new GdDict();
            if (string.IsNullOrEmpty(startKey) || !adjacency.Has(startKey)) return visited;
            var queue = new List<string> { startKey };
            visited[startKey] = true;
            int head = 0;
            while (head < queue.Count)
            {
                string current = queue[head++];
                foreach (var neighborVariant in adjacency.GetArrayOrEmpty(current))
                {
                    string neighbor = V.Str(neighborVariant);
                    if (visited.Has(neighbor)) continue;
                    visited[neighbor] = true;
                    queue.Add(neighbor);
                }
            }
            return visited;
        }

        public static bool Reachable(GdDict adjacency, string startKey, string goalKey)
        {
            if (startKey == goalKey && adjacency.Has(startKey)) return true;
            return FloodVisited(adjacency, startKey).Has(goalKey);
        }

        public static bool RoomsReachable(GdDict adjacency, GdDict occupancy, string startRoom, string goalRoom)
        {
            List<string> startCells = RoomCellKeys(occupancy, startRoom);
            var goalLookup = new GdDict();
            foreach (string goalKey in RoomCellKeys(occupancy, goalRoom)) goalLookup[goalKey] = true;
            if (startCells.Count == 0 || goalLookup.IsEmpty) return false;
            foreach (string startKey in startCells)
            {
                GdDict visited = FloodVisited(adjacency, startKey);
                foreach (var goalKey in goalLookup.Keys)
                    if (visited.Has(V.Str(goalKey))) return true;
            }
            return false;
        }

        public static List<string> StandingPathKeys(GdDict adjacency, GdDict occupancy, string startRoom, string goalRoom)
        {
            List<string> startCells = RoomCellKeys(occupancy, startRoom);
            var goalLookup = new GdDict();
            foreach (string goalKey in RoomCellKeys(occupancy, goalRoom)) goalLookup[goalKey] = true;
            if (startCells.Count == 0 || goalLookup.IsEmpty) return new List<string>();
            foreach (string startKey in startCells)
            {
                var cameFrom = new GdDict { { startKey, startKey } };
                var queue = new List<string> { startKey };
                string found = "";
                int head = 0;
                while (head < queue.Count)
                {
                    string current = queue[head++];
                    if (goalLookup.Has(current))
                    {
                        found = current;
                        break;
                    }
                    foreach (var neighborVariant in adjacency.GetArrayOrEmpty(current))
                    {
                        string neighbor = V.Str(neighborVariant);
                        if (cameFrom.Has(neighbor)) continue;
                        cameFrom[neighbor] = current;
                        queue.Add(neighbor);
                    }
                }
                if (found.Length == 0) continue;
                var path = new List<string>();
                string cursor = found;
                while (true)
                {
                    path.Insert(0, cursor);
                    if (cursor == startKey) break;
                    cursor = V.Str(cameFrom.Get(cursor, ""));
                    if (cursor.Length == 0) return new List<string>();
                }
                return path;
            }
            return new List<string>();
        }

        public static List<string> RoomCellKeys(GdDict occupancy, string roomId)
        {
            var cells = new List<string>();
            if (string.IsNullOrEmpty(roomId)) return cells;
            foreach (var kv in occupancy)
            {
                if (!(kv.Value is GdDict record)) continue;
                if (OccupancyRoomId(record) == roomId) cells.Add(V.Str(kv.Key));
            }
            return cells;
        }

        public static GdDict FloorCellKeys(GdDict plan)
        {
            var keys = new GdDict();
            if (!(plan.Get("floor_placements", new GdArray()) is GdArray floors)) return keys;
            foreach (var floorVariant in floors)
            {
                if (!(floorVariant is GdDict floor)) continue;
                string cellKeyValue = V.Str(floor.Get("cell_key", ""));
                if (cellKeyValue.Length != 0) keys[cellKeyValue] = true;
            }
            return keys;
        }

        public static bool CapsuleHitsSolidSlab(GdDict edge, GdDict occupancy) =>
            CapsuleEntersExtrudedSlab(edge, occupancy, SLAB_THICKNESS_M, Vec3.Inf);

        /// <summary>Fail-closed fixture: a zero-thickness plane at the edge must not count as a wall hit.</summary>
        public static bool CapsuleHitsZeroThicknessFixture(GdDict edge, GdDict occupancy) =>
            CapsuleEntersExtrudedSlab(edge, occupancy, 0.0, Vec3.Inf);

        /// <summary>Fail-closed fixture: a 0.20 m AABB at the source cell center must not count as the wall.</summary>
        public static bool CapsuleHitsCellCenterAabbFixture(GdDict edge, GdDict occupancy)
        {
            if (!CapsuleSweepSegment(edge, occupancy, out Sweep sweep)) return false;
            return CapsuleEntersExtrudedSlab(edge, occupancy, SLAB_THICKNESS_M, sweep.From);
        }

        public static bool CapsulePassesDoorOpening(GdDict edge, GdDict occupancy)
        {
            if (!CapsuleSweepSegment(edge, occupancy, out Sweep sweep)) return false;
            Vec3 fromLocal = WorldToLocal(sweep.From, sweep.Origin, sweep.Yaw);
            Vec3 toLocal = WorldToLocal(sweep.To, sweep.Origin, sweep.Yaw);
            double halfW = DOOR_OPENING_WIDTH_M * 0.5;
            double halfT = SLAB_THICKNESS_M * 0.5;
            double headerMinY = STANDING_OPENING_HEIGHT_M + HEADER_CLEARANCE_M;
            var boxes = new List<(Vec3 Min, Vec3 Max)>
            {
                (new Vec3(-WALL_HALF_SPAN_M, 0.0, -halfT), new Vec3(-halfW, DOOR_HEIGHT_M, halfT)),
                (new Vec3(halfW, 0.0, -halfT), new Vec3(WALL_HALF_SPAN_M, DOOR_HEIGHT_M, halfT)),
                (new Vec3(-WALL_HALF_SPAN_M, headerMinY, -halfT), new Vec3(WALL_HALF_SPAN_M, DOOR_HEIGHT_M, halfT)),
            };
            if (EdgeKind(edge) == "LOCKED")
                boxes.Add((new Vec3(-halfW, 0.0, -halfT), new Vec3(halfW, headerMinY, halfT)));
            foreach (var box in boxes)
            {
                if (HorizontalCapsuleHitsAabbLocal(fromLocal, toLocal, box.Min, box.Max)) return false;
            }
            if (EdgeKind(edge) == "LOCKED") return false;
            return SegmentCrossesOpeningPlane(fromLocal, toLocal, halfW, STANDING_OPENING_HEIGHT_M);
        }

        public static string StandingVoidReason(GdDict plan, GdDict occupancy, IList<string> pathKeys, GdDict topology = null)
        {
            topology = topology ?? new GdDict();
            GdDict floors = FloorCellKeys(plan);
            foreach (string pathKey in pathKeys)
            {
                if (!occupancy.Has(pathKey)) return pathKey;
                if (!floors.Has(pathKey)) return pathKey;
                if (!(occupancy[pathKey] is GdDict record)) return pathKey;
                long deck = OccupancyDeck(record);
                Vec2i cell = OccupancyCell(record);
                foreach (var direction in StructuralEdgeCompiler.DIRECTIONS.Keys)
                {
                    Vec2i neighbor = cell + (Vec2i)StructuralEdgeCompiler.DIRECTIONS[direction];
                    string neighborKey = StructuralEdgeCompiler.CellKey(deck, neighbor);
                    if (occupancy.Has(neighborKey)) continue;
                    if (StandingEdgeBetween(plan, pathKey, neighborKey, deck, topology)) return neighborKey;
                }
            }
            return "";
        }

        static bool StandingEdgeBetween(GdDict plan, string firstKey, string secondKey, long deck, GdDict topology)
        {
            if (!(plan.Get("edges", new GdDict()) is GdDict edges)) return false;
            foreach (var edgeVariant in edges.Values)
            {
                if (!(edgeVariant is GdDict edge)) continue;
                if (!StandingPassable(EdgeKind(edge))) continue;
                if (V.I64(edge.Get("deck", deck)) != deck) continue;
                GdArray cells = FloodEndpointCells(edge, topology);
                if (cells.Count < 2) continue;
                if (!CellKeyFromValue(cells[0], deck, out string aKey)) continue;
                if (!CellKeyFromValue(cells[1], deck, out string bKey)) continue;
                if ((aKey == firstKey && bKey == secondKey) || (aKey == secondKey && bKey == firstKey)) return true;
            }
            return false;
        }

        static List<string> OccupiedEdgeKeys(GdDict edge, GdDict occupancy, GdDict topology)
        {
            long deck = V.I64(edge.Get("deck", -1L));
            GdArray cells = FloodEndpointCells(edge, topology ?? new GdDict());
            if (cells.Count < 2) return new List<string>();
            if (!CellKeyFromValue(cells[0], deck, out string firstKey)) return new List<string>();
            if (!CellKeyFromValue(cells[1], deck, out string secondKey)) return new List<string>();
            if (!occupancy.Has(firstKey) || !occupancy.Has(secondKey)) return new List<string>();
            return new List<string> { firstKey, secondKey };
        }

        static GdArray FloodEndpointCells(GdDict edge, GdDict topology)
        {
            object lf = edge.Get("logical_from_cell", null);
            object lt = edge.Get("logical_to_cell", null);
            if (lf != null && lt != null) return GdArray.Of(lf, lt);
            if (V.Bool(edge.Get("logical_boundary", false)) || V.Bool(edge.Get("portal", false)))
            {
                if (topology.Get("portals", new GdArray()) is GdArray portals)
                {
                    string edgeKeyValue = V.Str(edge.Get("key", edge.Get("edge_key", "")));
                    object edgeCell = edge.Get("cell", null);
                    string direction = V.Str(edge.Get("direction", ""));
                    foreach (var portalVariant in portals)
                    {
                        if (!(portalVariant is GdDict portal)) continue;
                        if (!V.Bool(portal.Get("logical_boundary", false))) continue;
                        if (edgeKeyValue.Length != 0 && V.Str(portal.Get("edge_key", "")) == edgeKeyValue)
                            return GdArray.Of(portal.Get("from_cell", null), portal.Get("to_cell", null));
                        string portalDir = V.Str(portal.Get("edge_direction", portal.Get("direction", "")));
                        if (portalDir == direction)
                        {
                            Vec2i pec = ReadCellXz(portal.Get("edge_cell", null));
                            Vec2i ecell = ReadCellXz(edgeCell);
                            if (pec != Sentinel && pec == ecell)
                                return GdArray.Of(portal.Get("from_cell", null), portal.Get("to_cell", null));
                        }
                    }
                }
            }
            if (edge.Get("source_cells", new GdArray()) is GdArray sourceCells) return sourceCells;
            return new GdArray();
        }

        static void OverlayBlockedLinks(GdDict adjacency, GdDict occupancy, GdDict topology)
        {
            if (!(topology.Get("blocked_links", new GdArray()) is GdArray blocked)) return;
            GdDict roomDecks = RoomDecks(topology);
            foreach (var linkVariant in blocked)
            {
                if (!(linkVariant is GdDict link)) continue;
                long fromDeck = V.I64(roomDecks.Get(V.Str(link.Get("from_room", "")), V.I64(link.Get("from_deck", 0L))));
                long toDeck = V.I64(roomDecks.Get(V.Str(link.Get("to_room", "")), V.I64(link.Get("to_deck", 0L))));
                if (!CellKeyFromValue(link.Get("from_cell", null), fromDeck, out string a)) continue;
                if (!CellKeyFromValue(link.Get("to_cell", null), toDeck, out string b)) continue;
                Unlink(adjacency, a, b);
            }
        }

        static void AddVerticalLinks(GdDict adjacency, GdDict occupancy, GdDict topology)
        {
            if (!(topology.Get("vertical_connections", new GdArray()) is GdArray vertical)) return;
            GdDict roomDecks = RoomDecks(topology);
            foreach (var linkVariant in vertical)
            {
                if (!(linkVariant is GdDict link)) continue;
                string fromRoom = V.Str(link.Get("from_room", ""));
                string toRoom = V.Str(link.Get("to_room", ""));
                long fromDeck = V.I64(roomDecks.Get(fromRoom, V.I64(link.Get("from_deck", -1L))));
                long toDeck = V.I64(roomDecks.Get(toRoom, V.I64(link.Get("to_deck", -1L))));
                if (!CellKeyFromValue(link.Get("from_cell", null), fromDeck, out string fromKey)) continue;
                if (!CellKeyFromValue(link.Get("to_cell", null), toDeck, out string toKey)) continue;
                if (occupancy.Has(fromKey) && occupancy.Has(toKey)) Link(adjacency, fromKey, toKey);
            }
        }

        static void Link(GdDict adjacency, string a, string b)
        {
            if (!adjacency.Has(a) || !adjacency.Has(b) || a == b) return;
            ((GdArray)adjacency[a]).Append(b);
            ((GdArray)adjacency[b]).Append(a);
        }

        static void Unlink(GdDict adjacency, string a, string b)
        {
            if (a.Length == 0 || b.Length == 0 || a == b) return;
            if (adjacency.Has(a)) ((GdArray)adjacency[a]).Remove(b);
            if (adjacency.Has(b)) ((GdArray)adjacency[b]).Remove(a);
        }

        struct Sweep
        {
            public Vec3 From;
            public Vec3 To;
            public Vec3 Origin;
            public double Yaw;
        }

        static bool CapsuleSweepSegment(GdDict edge, GdDict occupancy, out Sweep sweep)
        {
            sweep = default;
            if (!(edge.Get("source_cells", new GdArray()) is GdArray sourceCells) || sourceCells.Count < 2) return false;
            long deck = V.I64(edge.Get("deck", 0L));
            Vec3 fromPos = CellWorldFromValue(sourceCells[0], deck, occupancy);
            Vec3 toPos = CellWorldFromValue(sourceCells[1], deck, occupancy);
            if (fromPos == Vec3.Inf || toPos == Vec3.Inf) return false;
            fromPos = new Vec3(fromPos.X, (float)(fromPos.Y + CAPSULE_FLOOR_OFFSET_M), fromPos.Z);
            toPos = new Vec3(toPos.X, (float)(toPos.Y + CAPSULE_FLOOR_OFFSET_M), toPos.Z);
            sweep = new Sweep { From = fromPos, To = toPos, Origin = EdgeOrigin(edge), Yaw = V.F64(edge.Get("yaw_degrees", 0.0)) };
            return true;
        }

        static Vec3 CellWorldFromValue(object value, long defaultDeck, GdDict occupancy)
        {
            if (!CellKeyFromValue(value, defaultDeck, out string key, out Vec2i cell, out long deck)) return Vec3.Inf;
            if (occupancy.Has(key) && occupancy[key] is GdDict record) return OccupancyWorldPosition(record);
            return StructuralEdgeCompiler.CellWorldPosition(deck, cell);
        }

        static Vec3 EdgeOrigin(GdDict edge)
        {
            object raw = edge.Get("position", null);
            if (raw is Vec3 v) return v;
            if (raw is GdArray values && values.Count >= 3) return new Vec3(V.F64(values[0]), V.F64(values[1]), V.F64(values[2]));
            long deck = V.I64(edge.Get("deck", 0L));
            Vec2i cell = ReadCellXz(edge.Get("cell", null));
            string direction = V.Str(edge.Get("direction", "south"));
            return StructuralEdgeCompiler.EdgeWorldPosition(deck, cell, direction);
        }

        static bool CapsuleEntersExtrudedSlab(GdDict edge, GdDict occupancy, double thicknessM, Vec3 originOverride)
        {
            if (!CapsuleSweepSegment(edge, occupancy, out Sweep sweep)) return false;
            Vec3 origin = originOverride;
            if (origin == Vec3.Inf) origin = sweep.Origin;
            Vec3 fromLocal = WorldToLocal(sweep.From, origin, sweep.Yaw);
            Vec3 toLocal = WorldToLocal(sweep.To, origin, sweep.Yaw);
            SlabLocalBox(WALL_HEIGHT_M, thicknessM, out Vec3 slabMin, out Vec3 slabMax);
            if (!CapsuleYOverlapsAabb(fromLocal, toLocal, slabMin, slabMax)) return false;
            Vec2f interval = SegmentAabb2Interval(
                new Vec2f(fromLocal.X, fromLocal.Z),
                new Vec2f(toLocal.X, toLocal.Z),
                new Vec2f(slabMin.X, slabMin.Z),
                new Vec2f(slabMax.X, slabMax.Z));
            if (interval.X > interval.Y) return false;
            double pathLen = new Vec2f(fromLocal.X, fromLocal.Z).DistanceTo(new Vec2f(toLocal.X, toLocal.Z));
            double overlapM = ((double)interval.Y - interval.X) * pathLen;
            double minOverlap = thicknessM > 0.0 ? thicknessM * 0.5 : 0.05;
            if (overlapM < minOverlap) return false;
            return interval.X < (1.0 - SLAB_INTERIOR_T_EPS) && interval.Y > SLAB_INTERIOR_T_EPS;
        }

        static void SlabLocalBox(double heightM, double thicknessM, out Vec3 min, out Vec3 max)
        {
            double halfT = thicknessM * 0.5;
            min = new Vec3(-WALL_HALF_SPAN_M, 0.0, -halfT);
            max = new Vec3(WALL_HALF_SPAN_M, heightM, halfT);
        }

        static bool CapsuleYOverlapsAabb(Vec3 fromLocal, Vec3 toLocal, Vec3 boxMin, Vec3 boxMax)
        {
            double capY0 = Math.Min((double)fromLocal.Y, toLocal.Y);
            double capY1 = capY0 + PLAYER_HEIGHT_M;
            return capY1 > boxMin.Y && capY0 < boxMax.Y;
        }

        static bool HorizontalCapsuleHitsAabbLocal(Vec3 fromLocal, Vec3 toLocal, Vec3 boxMin, Vec3 boxMax)
        {
            if (!CapsuleYOverlapsAabb(fromLocal, toLocal, boxMin, boxMax)) return false;
            var expandedMin = new Vec2f(boxMin.X - PLAYER_RADIUS_M, boxMin.Z - PLAYER_RADIUS_M);
            var expandedMax = new Vec2f(boxMax.X + PLAYER_RADIUS_M, boxMax.Z + PLAYER_RADIUS_M);
            return SegmentHitsAabb2(new Vec2f(fromLocal.X, fromLocal.Z), new Vec2f(toLocal.X, toLocal.Z), expandedMin, expandedMax);
        }

        static bool SegmentCrossesOpeningPlane(Vec3 fromLocal, Vec3 toLocal, double halfW, double openingTop)
        {
            double z0 = fromLocal.Z;
            double z1 = toLocal.Z;
            if ((z0 < 0.0 && z1 < 0.0) || (z0 > 0.0 && z1 > 0.0)) return false;
            double denom = z1 - z0;
            double t = GdMath.IsZeroApprox(denom) ? 0.5 : (-z0 / denom);
            if (t < 0.0 || t > 1.0) return false;
            Vec3 hit = fromLocal.Lerp(toLocal, (float)t);
            if (Math.Abs((double)hit.X) > halfW - PLAYER_RADIUS_M) return false;
            // Named opening is measured from floor Y=0; floor-plate offset is not counted.
            double heightFromFloor = hit.Y - CAPSULE_FLOOR_OFFSET_M + PLAYER_HEIGHT_M;
            return hit.Y >= 0.0 && heightFromFloor <= openingTop + 0.0001;
        }

        /// <summary>
        /// <c>Basis.from_euler(Vector3(0, deg_to_rad(yaw), 0)).inverse() * (world - origin)</c> with Godot's float32
        /// Basis::from_euler (YXZ), Basis::inverse (cofactors / determinant) and Basis::xform.
        /// </summary>
        static Vec3 WorldToLocal(Vec3 world, Vec3 origin, double yawDegrees)
        {
            float yaw = (float)GdMath.DegToRad(yawDegrees);
            float c = ProcgenMath.CosF(yaw);
            float s = ProcgenMath.SinF(yaw);
            // from_euler YXZ with x = z = 0: ymat * identity * identity == ymat exactly.
            float[,] r = { { c, 0f, s }, { 0f, 1f, 0f }, { -s, 0f, c } };
            float co0 = (float)((float)(r[1, 1] * r[2, 2]) - (float)(r[1, 2] * r[2, 1]));
            float co1 = (float)((float)(r[1, 2] * r[2, 0]) - (float)(r[1, 0] * r[2, 2]));
            float co2 = (float)((float)(r[1, 0] * r[2, 1]) - (float)(r[1, 1] * r[2, 0]));
            float det = (float)((float)((float)(r[0, 0] * co0) + (float)(r[0, 1] * co1)) + (float)(r[0, 2] * co2));
            float inv = (float)(1.0f / det);
            float[,] m =
            {
                { (float)(co0 * inv), (float)((float)((float)(r[0, 2] * r[2, 1]) - (float)(r[0, 1] * r[2, 2])) * inv), (float)((float)((float)(r[0, 1] * r[1, 2]) - (float)(r[0, 2] * r[1, 1])) * inv) },
                { (float)(co1 * inv), (float)((float)((float)(r[0, 0] * r[2, 2]) - (float)(r[0, 2] * r[2, 0])) * inv), (float)((float)((float)(r[0, 2] * r[1, 0]) - (float)(r[0, 0] * r[1, 2])) * inv) },
                { (float)(co2 * inv), (float)((float)((float)(r[0, 1] * r[2, 0]) - (float)(r[0, 0] * r[2, 1])) * inv), (float)((float)((float)(r[0, 0] * r[1, 1]) - (float)(r[0, 1] * r[1, 0])) * inv) },
            };
            Vec3 d = world - origin;
            return new Vec3(Dot(m, 0, d), Dot(m, 1, d), Dot(m, 2, d));
        }

        static float Dot(float[,] m, int row, Vec3 p) =>
            (float)((float)((float)(m[row, 0] * p.X) + (float)(m[row, 1] * p.Y)) + (float)(m[row, 2] * p.Z));

        static bool SegmentHitsAabb2(Vec2f p0, Vec2f p1, Vec2f boxMin, Vec2f boxMax)
        {
            Vec2f interval = SegmentAabb2Interval(p0, p1, boxMin, boxMax);
            return interval.X <= interval.Y;
        }

        static Vec2f SegmentAabb2Interval(Vec2f p0, Vec2f p1, Vec2f boxMin, Vec2f boxMax)
        {
            var dir = new Vec2f((float)(p1.X - p0.X), (float)(p1.Y - p0.Y));
            double tMin = 0.0;
            double tMax = 1.0;
            for (int axis = 0; axis < 2; axis++)
            {
                double originA = axis == 0 ? p0.X : p0.Y;
                double dirA = axis == 0 ? dir.X : dir.Y;
                double minA = axis == 0 ? boxMin.X : boxMin.Y;
                double maxA = axis == 0 ? boxMax.X : boxMax.Y;
                if (GdMath.IsZeroApprox(dirA))
                {
                    if (originA < minA || originA > maxA) return new Vec2f(1.0, 0.0);
                    continue;
                }
                double t1 = (minA - originA) / dirA;
                double t2 = (maxA - originA) / dirA;
                if (t1 > t2)
                {
                    double tmp = t1;
                    t1 = t2;
                    t2 = tmp;
                }
                tMin = Math.Max(tMin, t1);
                tMax = Math.Min(tMax, t2);
                if (tMin > tMax) return new Vec2f(1.0, 0.0);
            }
            return new Vec2f(tMin, tMax);
        }

        static bool CellKeyFromValue(object value, long defaultDeck, out string key) =>
            CellKeyFromValue(value, defaultDeck, out key, out _, out _);

        static bool CellKeyFromValue(object value, long defaultDeck, out string key, out Vec2i cell, out long deck)
        {
            key = "";
            cell = ReadCellXz(value);
            deck = defaultDeck;
            if (value is GdArray arr && arr.Count >= 3) deck = V.I64(arr[2]);
            if (deck < 0) return false;
            if (cell == Sentinel) return false;
            key = StructuralEdgeCompiler.CellKey(deck, cell);
            return true;
        }

        static Vec2i ReadCellXz(object value)
        {
            if (value is Vec2i v) return v;
            if (value is GdArray arr && arr.Count >= 2) return new Vec2i(V.I32(arr[0]), V.I32(arr[1]));
            if (value is string s)
            {
                string text = GdString.StripEdges(s);
                if (GdString.BeginsWith(text, "(") && GdString.EndsWith(text, ")")) text = text.Length >= 2 ? text.Substring(1, text.Length - 2) : "";
                List<string> pieces = GdString.Split(text, ",");
                if (pieces.Count >= 2)
                    return new Vec2i((int)V.StringToInt(GdString.StripEdges(pieces[0])), (int)V.StringToInt(GdString.StripEdges(pieces[1])));
            }
            return Sentinel;
        }

        static GdDict RoomDecks(GdDict topology)
        {
            var output = new GdDict();
            if (!(topology.Get("rooms", new GdArray()) is GdArray rooms)) return output;
            foreach (var roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room)) continue;
                string roomId = V.Str(room.Get("id", ""));
                if (roomId.Length != 0) output[roomId] = V.I64(room.Get("deck", 0L));
            }
            return output;
        }

        /// <summary>Godot <c>Vector2</c> (real_t = float32): only what the capsule sweeps need.</summary>
        readonly struct Vec2f
        {
            public readonly float X, Y;

            public Vec2f(double x, double y)
            {
                X = (float)x;
                Y = (float)y;
            }

            /// <summary><c>Vector2::distance_to</c>: <c>sqrt((x - b.x)^2 + (y - b.y)^2)</c> in float32.</summary>
            public float DistanceTo(Vec2f b)
            {
                float dx = (float)(X - b.X);
                float dy = (float)(Y - b.Y);
                return (float)Math.Sqrt((float)((float)(dx * dx) + (float)(dy * dy)));
            }
        }
    }
}
