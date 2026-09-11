// Ported from scripts/procgen/cell_layout_engine.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Grows rooms onto a 4 m grid from template connections / attach_to connectors. Same-deck links must share a
    /// cardinal cell edge; cross-deck links stay vertical. Fully deterministic: the seeded <see cref="Rng"/> is
    /// reseeded per call but never drawn from, and every ordering follows the GDScript (insertion-ordered
    /// dictionaries, explicit insertion sorts).
    /// </summary>
    public sealed class CellLayoutEngine
    {
        public const double CELL_SIZE = 4.0;
        public const double DECK_HEIGHT = 4.0;

        public static readonly Vec2i DIR_NORTH = new Vec2i(0, -1);
        public static readonly Vec2i DIR_EAST = new Vec2i(1, 0);
        public static readonly Vec2i DIR_SOUTH = new Vec2i(0, 1);
        public static readonly Vec2i DIR_WEST = new Vec2i(-1, 0);
        public static readonly IReadOnlyList<Vec2i> ALL_DIRS = new[] { DIR_NORTH, DIR_EAST, DIR_SOUTH, DIR_WEST };

        public static readonly Vec2i SENTINEL = new Vec2i(-99999, -99999);
        public const long MAX_GROW_STEPS = 24;

        // Ship axis: bow = +X (east), stern = -X (west). Lateral = north/south (port/starboard).
        static readonly Dictionary<string, Vec2i[]> HintDirs = new Dictionary<string, Vec2i[]>(StringComparer.Ordinal)
        {
            { "bow", new[] { DIR_EAST, DIR_NORTH, DIR_SOUTH, DIR_WEST } },
            { "stern", new[] { DIR_WEST, DIR_NORTH, DIR_SOUTH, DIR_EAST } },
            { "lateral", new[] { DIR_SOUTH, DIR_NORTH, DIR_EAST, DIR_WEST } },
            { "center", new[] { DIR_EAST, DIR_WEST, DIR_SOUTH, DIR_NORTH } },
        };

        static readonly Vec2i[] AllDirsArray = { DIR_NORTH, DIR_EAST, DIR_SOUTH, DIR_WEST };

        /// <summary>GDScript <c>HINT_DIRECTIONS</c> (read-only view of the same table the engine uses).</summary>
        public static readonly GdDict HINT_DIRECTIONS = BuildHintDirections();

        static GdDict BuildHintDirections()
        {
            var d = new GdDict();
            foreach (string key in new[] { "bow", "stern", "lateral", "center" }) d[key] = new GdArray(HintDirs[key]);
            return d;
        }

        /// <summary>Connective roles form the spine/skeleton; functional rooms attach to these.</summary>
        public static readonly IReadOnlyList<string> CONNECTIVE_ROLES = new[]
        {
            "corridor", "main_spine", "hub", "ramp", "elevator", "airlock", "dock",
        };

        /// <summary>Hazardous roles must NOT share a wall with crew comfort roles.</summary>
        public static readonly IReadOnlyList<string> HAZARDOUS_ROLES = new[] { "reactor", "engineering" };

        /// <summary>Crew comfort roles must be kept away from hazardous areas.</summary>
        public static readonly IReadOnlyList<string> CREW_COMFORT_ROLES = new[]
        {
            "crew_quarters", "medical", "mess_hall", "bridge",
        };

        public GodotRandom Rng = new GodotRandom();

        sealed class ConnectorGraph
        {
            /// <summary>rid -&gt; {neighbor rid: true}.</summary>
            public GdDict Neighbors = new GdDict();

            /// <summary>[{"a": zone, "b": zone}].</summary>
            public List<GdDict> ZonePairs = new List<GdDict>();
        }

        /// <summary>GDScript <c>{"origin", "footprint", "cells"}</c> candidate; null stands for <c>{}</c>.</summary>
        sealed class Candidate
        {
            public Vec2i Origin;
            public Vec2i Footprint;
            public List<Vec2i> Cells;
        }

        struct ZoneRef
        {
            public string Id;
            public string Kind;
            public long Index;
        }

        /// <summary><c>layout()</c> with a GDScript <c>Array[Dictionary]</c> room plan.</summary>
        public GdDict Layout(GdArray roomPlan, TopologyTemplate template, long seedValue)
        {
            var plan = new List<GdDict>();
            if (roomPlan != null)
                foreach (var item in roomPlan)
                    if (item is GdDict room) plan.Add(room);
            return Layout(plan, template, seedValue);
        }

        /// <summary>
        /// Returns <c>{"rooms": {rid: {cells, origin, footprint, deck, role}}, "adjacencies": [...]}</c>; cells,
        /// origin, footprint, from_cell and to_cell are <see cref="Vec2i"/> values.
        /// </summary>
        public GdDict Layout(IList<GdDict> roomPlan, TopologyTemplate template, long seedValue)
        {
            Rng.Seed = seedValue;

            var zoneRoomsMap = new GdDict();
            foreach (var room in roomPlan)
            {
                string rid = V.Str(room["id"]);
                string zid = V.Str(room.Get("zone_id", ""));
                if (!zoneRoomsMap.Has(zid)) zoneRoomsMap[zid] = new GdArray();
                ((GdArray)zoneRoomsMap[zid]).Append(rid);
            }

            List<GdDict> zoneOrder = BuildZoneOrder(template);
            ConnectorGraph graph = BuildConnectorGraph(roomPlan, template, zoneRoomsMap);

            // deck -> (cell -> room_id)
            var occupiedPerDeck = new Dictionary<long, Dictionary<Vec2i, string>>();
            // room_id -> {cells, origin, footprint, deck, role}
            var placed = new GdDict();

            foreach (var zoneInfo in zoneOrder)
            {
                string zoneId = V.Str(zoneInfo["id"]);
                string parentZoneId = V.Str(zoneInfo.Get("attach_to", ""));
                GdArray zoneRoomIds = ZoneRooms(zoneRoomsMap, zoneId);
                string lastInZone = "";

                foreach (var roomIdVariant in zoneRoomIds)
                {
                    string rid = V.Str(roomIdVariant);
                    GdDict room = RoomById(roomPlan, rid);
                    if (room.IsEmpty) continue;
                    bool committed = PlaceOneRoom(
                        room, zoneId, parentZoneId, lastInZone, graph,
                        zoneRoomsMap, occupiedPerDeck, placed);
                    if (committed) lastInZone = rid;
                    else CoreServices.Log.Error("CellLayoutEngine: could not place room " + rid);
                }
            }

            RealizeMissingConnectors(graph, zoneRoomsMap, occupiedPerDeck, placed);

            List<GdDict> adjacencies = DiscoverAdjacencies(placed);
            AddVerticalAdjacencies(adjacencies, placed, graph, zoneRoomsMap, template);

            return new GdDict { { "rooms", placed }, { "adjacencies", new GdArray(adjacencies) } };
        }

        static GdArray ZoneRooms(GdDict zoneRoomsMap, string zoneId) =>
            zoneRoomsMap.Get(zoneId) as GdArray ?? new GdArray();

        List<GdDict> BuildZoneOrder(TopologyTemplate template)
        {
            var order = new List<GdDict>();
            var visited = new HashSet<string>();
            var queue = new List<GdDict>();

            foreach (var zone in template.Zones)
            {
                string attach = V.Str(zone.Get("attach_to", ""));
                if (attach.Length == 0)
                {
                    queue.Add(zone);
                    visited.Add(V.Str(zone["id"]));
                }
            }

            while (queue.Count > 0)
            {
                GdDict zone = queue[0];
                queue.RemoveAt(0);
                order.Add(zone);
                List<GdDict> children = template.GetZonesAttachedTo(V.Str(zone["id"]));
                foreach (var child in children)
                {
                    string cid = V.Str(child["id"]);
                    if (!visited.Contains(cid))
                    {
                        visited.Add(cid);
                        queue.Add(child);
                    }
                }
            }

            return order;
        }

        ConnectorGraph BuildConnectorGraph(IList<GdDict> roomPlan, TopologyTemplate template, GdDict zoneRoomsMap)
        {
            var graph = new ConnectorGraph();
            GdDict neighbors = graph.Neighbors;
            List<GdDict> zonePairs = graph.ZonePairs;
            var zonePairSeen = new HashSet<string>();

            foreach (var conn in template.Connections)
            {
                if (conn == null) continue;
                ZoneRef fromRef = ParseZoneRef(V.Str(conn.Get("from", "")));
                ZoneRef toRef = ParseZoneRef(V.Str(conn.Get("to", "")));
                string fromZone = fromRef.Id;
                string toZone = toRef.Id;
                if (fromZone.Length == 0 || toZone.Length == 0) continue;
                GdArray fromRooms = ZoneRooms(zoneRoomsMap, fromZone);
                GdArray toRooms = ZoneRooms(zoneRoomsMap, toZone);
                string distribution = V.Str(conn.Get("distribution", "adjacent"));
                string fromKind = fromRef.Kind;
                string toKind = toRef.Kind;

                if (fromKind == "next" || toKind == "next")
                {
                    string chainZone = fromKind == "next" ? fromZone : toZone;
                    if (fromZone == toZone) chainZone = fromZone;
                    GdArray chainRooms = ZoneRooms(zoneRoomsMap, chainZone);
                    for (int i = 0; i < Math.Max(chainRooms.Count - 1, 0); i++)
                        AddSpecificNeighbor(neighbors, V.Str(chainRooms[i]), V.Str(chainRooms[i + 1]));
                    continue;
                }

                if (fromZone != toZone) AddZonePair(zonePairs, zonePairSeen, fromZone, toZone);

                List<string> fromIds = ResolveRefRooms(fromRef, fromRooms);
                List<string> toIds = ResolveRefRooms(toRef, toRooms);
                if (fromIds.Count == 0 || toIds.Count == 0) continue;

                if (distribution == "spread")
                {
                    for (int i = 0; i < toIds.Count; i++)
                        AddSpecificNeighbor(neighbors, toIds[i], fromIds[i % fromIds.Count]);
                }
                else if (fromKind == "index" || toKind == "index")
                {
                    foreach (string fr in fromIds)
                        foreach (string tr in toIds)
                            AddSpecificNeighbor(neighbors, fr, tr);
                }
                else
                {
                    AddSpecificNeighbor(neighbors, fromIds[fromIds.Count - 1], toIds[0]);
                }
            }

            var zoneLayout = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var zone in template.Zones)
            {
                zoneLayout[V.Str(zone.Get("id", ""))] = V.Str(zone.Get("layout", "single"));
                string childZone = V.Str(zone.Get("id", ""));
                string parentZone = V.Str(zone.Get("attach_to", ""));
                if (parentZone.Length == 0 || childZone.Length == 0) continue;
                if (zonePairSeen.Contains(PairKey(parentZone, childZone))) continue;
                AddZonePair(zonePairs, zonePairSeen, parentZone, childZone);
                GdArray parentRooms = ZoneRooms(zoneRoomsMap, parentZone);
                GdArray childRooms = ZoneRooms(zoneRoomsMap, childZone);
                if (parentRooms.IsEmpty || childRooms.IsEmpty) continue;
                AddSpecificNeighbor(neighbors, V.Str(childRooms[0]), V.Str(parentRooms[parentRooms.Count - 1]));
            }

            foreach (var zidVariant in zoneRoomsMap.Keys)
            {
                var zRooms = (GdArray)zoneRoomsMap[zidVariant];
                if (zRooms.Count < 2) continue;
                for (int i = 0; i < zRooms.Count - 1; i++)
                    AddSpecificNeighbor(neighbors, V.Str(zRooms[i]), V.Str(zRooms[i + 1]));
                string zid = V.Str(zidVariant);
                if ((zoneLayout.TryGetValue(zid, out string layoutKind) ? layoutKind : "") == "clustered")
                {
                    for (int i = 1; i < zRooms.Count; i++)
                        AddSpecificNeighbor(neighbors, V.Str(zRooms[i]), V.Str(zRooms[0]));
                }
            }

            return graph;
        }

        static ZoneRef ParseZoneRef(string zoneRef)
        {
            string raw = ProcgenCompat.StripEdges(zoneRef);
            int bracket = raw.IndexOf('[');
            if (bracket < 0) return new ZoneRef { Id = raw, Kind = "all", Index = 0 };
            int close = raw.IndexOf(']');
            string zoneId = raw.Substring(0, bracket);
            string inner = close > bracket ? raw.Substring(bracket + 1, close - bracket - 1) : "*";
            if (inner == "*" || inner.Length == 0) return new ZoneRef { Id = zoneId, Kind = "all", Index = 0 };
            if (inner.StartsWith("*", StringComparison.Ordinal)) return new ZoneRef { Id = zoneId, Kind = "next", Index = 1 };
            return new ZoneRef { Id = zoneId, Kind = "index", Index = V.StringToInt(inner) };
        }

        static List<string> ResolveRefRooms(ZoneRef parsed, GdArray zoneRooms)
        {
            var output = new List<string>();
            if (zoneRooms.IsEmpty) return output;
            if (parsed.Kind == "index")
            {
                string rid = IndexRoom(zoneRooms, parsed.Index);
                if (rid.Length != 0) output.Add(rid);
                return output;
            }
            foreach (var entry in zoneRooms) output.Add(V.Str(entry));
            return output;
        }

        static string IndexRoom(GdArray zoneRooms, long index)
        {
            if (zoneRooms.IsEmpty) return "";
            long i = index;
            if (i < 0) i = zoneRooms.Count + i;
            if (i < 0 || i >= zoneRooms.Count) return "";
            return V.Str(zoneRooms[(int)i]);
        }

        static void AddSpecificNeighbor(GdDict neighbors, string a, string b)
        {
            if (a.Length == 0 || b.Length == 0 || a == b) return;
            if (!neighbors.Has(a)) neighbors[a] = new GdDict();
            ((GdDict)neighbors[a])[b] = true;
            if (!neighbors.Has(b)) neighbors[b] = new GdDict();
            ((GdDict)neighbors[b])[a] = true;
        }

        static void AddZonePair(List<GdDict> zonePairs, HashSet<string> seen, string a, string b)
        {
            if (a.Length == 0 || b.Length == 0 || a == b) return;
            string key = PairKey(a, b);
            if (seen.Contains(key)) return;
            seen.Add(key);
            zonePairs.Add(new GdDict { { "a", a }, { "b", b } });
        }

        static List<string> NeighborIds(GdDict neighbors, string rid)
        {
            var output = new List<string>();
            if (!(neighbors.Get(rid, new GdDict()) is GdDict raw)) return output;
            var keys = new List<object>(raw.Keys);
            GdSort.Sort(keys);
            foreach (var key in keys) output.Add(V.Str(key));
            return output;
        }

        bool PlaceOneRoom(
            GdDict room,
            string zoneId,
            string parentZoneId,
            string lastInZone,
            ConnectorGraph graph,
            GdDict zoneRoomsMap,
            Dictionary<long, Dictionary<Vec2i, string>> occupiedPerDeck,
            GdDict placed)
        {
            string rid = V.Str(room["id"]);
            Vec2i fp = CoerceFootprint(room.Get("footprint", new Vec2i(2, 2)));
            long deck = V.I64(room.Get("deck", 0L));
            string hint = V.Str(room.Get("position_hint", "center"));
            string role = V.Str(room.Get("role", ""));
            long targetCells = V.I64(room.Get("target_cells", (long)fp.X * fp.Y));
            if (targetCells <= 0) targetCells = Math.Max((long)fp.X * fp.Y, 1L);

            if (!occupiedPerDeck.ContainsKey(deck)) occupiedPerDeck[deck] = new Dictionary<Vec2i, string>();
            Dictionary<Vec2i, string> occupied = occupiedPerDeck[deck];

            if (placed.IsEmpty || occupied.Count == 0)
            {
                List<string> verticalIdsFirst = FilterPlaced(
                    DesiredAnchors(rid, zoneId, parentZoneId, lastInZone, graph, zoneRoomsMap, placed),
                    placed, deck, false);
                Candidate aligned = BestAlignedRect(fp, hint, occupied, role, placed, verticalIdsFirst, targetCells);
                if (aligned == null && occupied.Count == 0)
                {
                    aligned = new Candidate { Origin = Vec2i.Zero, Footprint = fp, Cells = ComputeCells(Vec2i.Zero, fp) };
                }
                if (aligned == null) return false;
                CommitRoom(placed, occupied, rid, new List<Vec2i>(aligned.Cells), deck, role, aligned.Footprint);
                return true;
            }

            List<string> desired = DesiredAnchors(rid, zoneId, parentZoneId, lastInZone, graph, zoneRoomsMap, placed);
            List<string> sameDeck = FilterPlaced(desired, placed, deck, true);
            if (sameDeck.Count == 0) sameDeck = PlacedIdsOnDeck(placed, deck);
            List<string> verticalIds = FilterPlaced(desired, placed, deck, false);

            Candidate best = BestRectAgainstAnchors(fp, hint, occupied, role, placed, sameDeck, verticalIds, targetCells, true, true);
            if (best == null)
                best = BestGrownAgainstAnchors(targetCells, hint, occupied, role, placed, sameDeck, true, true);
            if (best == null)
                best = BestRectAgainstAnchors(fp, hint, occupied, role, placed, sameDeck, verticalIds, targetCells, true, false);
            if (best == null)
                best = BestRectAgainstAnchors(fp, hint, occupied, role, placed, sameDeck, verticalIds, targetCells, false, false);
            if (best == null)
                best = BestGrownAgainstAnchors(targetCells, hint, occupied, role, placed, sameDeck, true, false);
            if (best == null)
                best = BestGrownAgainstAnchors(targetCells, hint, occupied, role, placed, sameDeck, false, false);
            List<string> allDeck = PlacedIdsOnDeck(placed, deck);
            if (best == null && allDeck.Count > sameDeck.Count)
                best = BestRectAgainstAnchors(fp, hint, occupied, role, placed, allDeck, verticalIds, targetCells, true, true);
            if (best == null && allDeck.Count > sameDeck.Count)
                best = BestRectAgainstAnchors(fp, hint, occupied, role, placed, allDeck, verticalIds, targetCells, true, false);
            if (best == null && allDeck.Count > sameDeck.Count)
                best = BestRectAgainstAnchors(fp, hint, occupied, role, placed, allDeck, verticalIds, targetCells, false, false);
            if (best == null && allDeck.Count > sameDeck.Count)
                best = BestGrownAgainstAnchors(targetCells, hint, occupied, role, placed, allDeck, false, false);
            if (best == null && verticalIds.Count > 0)
                best = BestAlignedRect(fp, hint, occupied, role, placed, verticalIds, targetCells);
            if (best == null) return false;
            CommitRoom(placed, occupied, rid, new List<Vec2i>(best.Cells), deck, role, best.Footprint);
            return true;
        }

        List<string> DesiredAnchors(
            string rid,
            string zoneId,
            string parentZoneId,
            string lastInZone,
            ConnectorGraph graph,
            GdDict zoneRoomsMap,
            GdDict placed)
        {
            // Declared graph neighbors are the only same-deck attach targets when any of them are already placed.
            GdDict neighbors = graph.Neighbors;
            var neighborPlaced = new List<string>();
            var neighborSeen = new HashSet<string>();
            foreach (string nid in NeighborIds(neighbors, rid))
            {
                if (nid == rid || !placed.Has(nid) || neighborSeen.Contains(nid)) continue;
                neighborSeen.Add(nid);
                neighborPlaced.Add(nid);
            }
            if (neighborPlaced.Count > 0) return neighborPlaced;

            var output = new List<string>();
            var seen = new HashSet<string>();
            foreach (var pair in graph.ZonePairs)
            {
                string otherZone = "";
                if (V.Str(pair.Get("a", "")) == zoneId) otherZone = V.Str(pair.Get("b", ""));
                else if (V.Str(pair.Get("b", "")) == zoneId) otherZone = V.Str(pair.Get("a", ""));
                if (otherZone.Length == 0) continue;
                foreach (var otherRid in ZoneRooms(zoneRoomsMap, otherZone)) AppendUnique(output, seen, V.Str(otherRid));
            }
            AppendUnique(output, seen, lastInZone);
            foreach (var parentRid in ZoneRooms(zoneRoomsMap, parentZoneId)) AppendUnique(output, seen, V.Str(parentRid));
            var filtered = new List<string>();
            foreach (string candidate in output)
            {
                if (candidate == rid) continue;
                if (placed.Has(candidate)) filtered.Add(candidate);
            }
            return filtered;
        }

        static void AppendUnique(List<string> output, HashSet<string> seen, string rid)
        {
            if (rid.Length == 0 || seen.Contains(rid)) return;
            seen.Add(rid);
            output.Add(rid);
        }

        static long DeckOf(GdDict placed, object rid) => V.I64(((GdDict)placed[rid]).Get("deck", 0L));

        static GdArray CellsOf(GdDict placed, object rid) => ((GdDict)placed[rid]).Get("cells", new GdArray()) as GdArray;

        static List<string> FilterPlaced(List<string> ids, GdDict placed, long deck, bool sameDeck)
        {
            var output = new List<string>();
            foreach (string rid in ids)
            {
                if (!placed.Has(rid)) continue;
                long otherDeck = DeckOf(placed, rid);
                if (sameDeck && otherDeck == deck) output.Add(rid);
                else if (!sameDeck && otherDeck != deck) output.Add(rid);
            }
            return output;
        }

        static List<string> PlacedIdsOnDeck(GdDict placed, long deck)
        {
            var output = new List<string>();
            foreach (var rid in placed.Keys)
                if (DeckOf(placed, rid) == deck) output.Add(V.Str(rid));
            return output;
        }

        Candidate BestRectAgainstAnchors(
            Vec2i fp,
            string hint,
            Dictionary<Vec2i, string> occupied,
            string role,
            GdDict placed,
            List<string> anchors,
            List<string> verticalIds,
            long targetCells,
            bool requireCompat,
            bool requireTouch)
        {
            if (anchors.Count == 0) return null;
            var fps = new List<Vec2i> { fp };
            if (fp.X != fp.Y) fps.Add(new Vec2i(fp.Y, fp.X));
            List<Vec2i> anchorCells = ConcatRoomCells(placed, anchors);
            List<Vec2i> seeds = EmptySeeds(anchorCells, occupied);
            SortSeeds(seeds, anchorCells, hint);

            Candidate best = null;
            long bestScore = -1;
            var seen = new HashSet<(int, int, int, int)>();
            foreach (Vec2i tryFp in fps)
            {
                foreach (Vec2i seed in seeds)
                {
                    foreach (Vec2i origin in OriginsCovering(seed, tryFp))
                    {
                        var key = (origin.X, origin.Y, tryFp.X, tryFp.Y);
                        if (seen.Contains(key)) continue;
                        seen.Add(key);
                        if (!CanPlace(origin, tryFp, occupied)) continue;
                        List<Vec2i> cells = ComputeCells(origin, tryFp);
                        if (requireCompat && !CellsCompatible(cells, role, occupied, placed)) continue;
                        if (requireTouch && !CellSetsShareEdge(CellSet(cells), anchorCells)) continue;
                        long score = PlacementScore(cells, anchors, verticalIds, placed, targetCells);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new Candidate { Origin = origin, Footprint = tryFp, Cells = cells };
                        }
                    }
                }
            }
            return best;
        }

        Candidate BestGrownAgainstAnchors(
            long targetCells,
            string hint,
            Dictionary<Vec2i, string> occupied,
            string role,
            GdDict placed,
            List<string> anchors,
            bool requireCompat,
            bool requireTouch)
        {
            if (anchors.Count == 0) return null;
            List<Vec2i> anchorCells = ConcatRoomCells(placed, anchors);
            List<Vec2i> seeds = EmptySeeds(anchorCells, occupied);
            SortSeeds(seeds, anchorCells, hint);
            Candidate best = null;
            long bestScore = -1;
            foreach (Vec2i seed in seeds)
            {
                List<Vec2i> grown = GrowFromSeed(seed, targetCells, occupied, role, placed, requireCompat, hint);
                if (grown.Count == 0) continue;
                if (requireTouch && !CellSetsShareEdge(CellSet(grown), anchorCells)) continue;
                long score = PlacementScore(grown, anchors, new List<string>(), placed, targetCells);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new Candidate { Origin = OriginOf(grown), Footprint = BboxFp(grown), Cells = grown };
                }
            }
            return best;
        }

        Candidate BestAlignedRect(
            Vec2i fp,
            string hint,
            Dictionary<Vec2i, string> occupied,
            string role,
            GdDict placed,
            List<string> verticalIds,
            long targetCells)
        {
            var fps = new List<Vec2i> { fp };
            if (fp.X != fp.Y) fps.Add(new Vec2i(fp.Y, fp.X));
            List<Vec2i> partnerCells = ConcatRoomCells(placed, verticalIds);
            var seeds = new List<Vec2i>();
            var seenSeed = new HashSet<Vec2i>();
            foreach (Vec2i cell in partnerCells)
            {
                var xz = new Vec2i(cell.X, cell.Y);
                if (seenSeed.Contains(xz)) continue;
                seenSeed.Add(xz);
                seeds.Add(xz);
            }
            if (seeds.Count == 0) seeds.Add(Vec2i.Zero);
            // GDScript passes the same array as both the list to sort and the anchor set; the aliasing is
            // intentional here (the insertion sort's in-flight shifts are visible to _seed_rank).
            SortSeeds(seeds, seeds, hint);

            Candidate best = null;
            long bestScore = -1;
            var seen = new HashSet<(int, int, int, int)>();
            foreach (Vec2i tryFp in fps)
            {
                foreach (Vec2i seed in seeds)
                {
                    foreach (Vec2i origin in OriginsCovering(seed, tryFp))
                    {
                        var key = (origin.X, origin.Y, tryFp.X, tryFp.Y);
                        if (seen.Contains(key)) continue;
                        seen.Add(key);
                        if (!CanPlace(origin, tryFp, occupied)) continue;
                        List<Vec2i> cells = ComputeCells(origin, tryFp);
                        if (!CellsCompatible(cells, role, occupied, placed)) continue;
                        long score = PlacementScore(cells, new List<string>(), verticalIds, placed, targetCells);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new Candidate { Origin = origin, Footprint = tryFp, Cells = cells };
                        }
                    }
                }
                if (best != null) continue;
                Vec2i fallbackOrigin = seeds[0];
                if (CanPlace(fallbackOrigin, tryFp, occupied))
                {
                    List<Vec2i> cells = ComputeCells(fallbackOrigin, tryFp);
                    if (CellsCompatible(cells, role, occupied, placed))
                        return new Candidate { Origin = fallbackOrigin, Footprint = tryFp, Cells = cells };
                }
            }
            return best;
        }

        long PlacementScore(List<Vec2i> cells, List<string> sameDeckIds, List<string> verticalIds, GdDict placed, long targetCells)
        {
            long score = ConnectorScore(cells, sameDeckIds, placed) * 1000;
            score += XzOverlapCount(cells, verticalIds, placed) * 10;
            score += targetCells - Math.Abs(cells.Count - targetCells);
            return score;
        }

        long ConnectorScore(List<Vec2i> cells, List<string> desired, GdDict placed)
        {
            HashSet<Vec2i> cellSet = CellSet(cells);
            long score = 0;
            foreach (string aid in desired)
            {
                if (!placed.Has(aid)) continue;
                if (CellSetsShareEdge(cellSet, AsCells(CellsOf(placed, aid)))) score += 1;
            }
            return score;
        }

        long XzOverlapCount(List<Vec2i> cells, List<string> verticalIds, GdDict placed)
        {
            if (verticalIds.Count == 0) return 0;
            var partner = new HashSet<Vec2i>();
            foreach (string vid in verticalIds)
            {
                if (!placed.Has(vid)) continue;
                foreach (Vec2i cell in AsCells(CellsOf(placed, vid))) partner.Add(new Vec2i(cell.X, cell.Y));
            }
            long count = 0;
            foreach (Vec2i cell in cells)
                if (partner.Contains(new Vec2i(cell.X, cell.Y))) count += 1;
            return count;
        }

        static List<Vec2i> ConcatRoomCells(GdDict placed, List<string> ids)
        {
            var cells = new List<Vec2i>();
            foreach (string rid in ids)
            {
                if (!placed.Has(rid)) continue;
                cells.AddRange(AsCells(CellsOf(placed, rid)));
            }
            return cells;
        }

        static List<Vec2i> EmptySeeds(List<Vec2i> anchorCells, Dictionary<Vec2i, string> occupied)
        {
            var seeds = new List<Vec2i>();
            var seen = new HashSet<Vec2i>();
            foreach (Vec2i cell in anchorCells)
            {
                foreach (Vec2i dir in AllDirsArray)
                {
                    var seed = new Vec2i(cell.X + dir.X, cell.Y + dir.Y);
                    if (occupied.ContainsKey(seed) || seen.Contains(seed)) continue;
                    seen.Add(seed);
                    seeds.Add(seed);
                }
            }
            return seeds;
        }

        /// <summary>The GDScript insertion sort (stable); <paramref name="anchorCells"/> may alias <paramref name="seeds"/>.</summary>
        static void SortSeeds(List<Vec2i> seeds, List<Vec2i> anchorCells, string hint)
        {
            for (int i = 1; i < seeds.Count; i++)
            {
                Vec2i key = seeds[i];
                int j = i;
                while (j > 0 && SeedLess(key, seeds[j - 1], anchorCells, hint))
                {
                    seeds[j] = seeds[j - 1];
                    j -= 1;
                }
                seeds[j] = key;
            }
        }

        static bool SeedLess(Vec2i a, Vec2i b, List<Vec2i> anchorCells, string hint)
        {
            int ra = SeedRank(a, anchorCells, hint);
            int rb = SeedRank(b, anchorCells, hint);
            if (ra != rb) return ra < rb;
            if (a.X != b.X) return a.X < b.X;
            return a.Y < b.Y;
        }

        static Vec2i[] Preferred(string hint) => HintDirs.TryGetValue(hint, out var dirs) ? dirs : AllDirsArray;

        static int SeedRank(Vec2i seed, List<Vec2i> anchorCells, string hint)
        {
            if (anchorCells.Count == 0) return 0;
            Vec2i nearest = NearestCell(seed, anchorCells);
            var delta = new Vec2i(seed.X - nearest.X, seed.Y - nearest.Y);
            Vec2i[] preferred = Preferred(hint);
            for (int i = 0; i < preferred.Length; i++)
                if (preferred[i] == delta) return i;
            return preferred.Length;
        }

        static Vec2i NearestCell(Vec2i seed, List<Vec2i> cells)
        {
            Vec2i best = cells[0];
            long bestD = 999999;
            foreach (Vec2i cell in cells)
            {
                long d = Math.Abs((long)cell.X - seed.X) + Math.Abs((long)cell.Y - seed.Y);
                if (d < bestD)
                {
                    bestD = d;
                    best = cell;
                }
            }
            return best;
        }

        static List<Vec2i> OriginsCovering(Vec2i seed, Vec2i fp)
        {
            var origins = new List<Vec2i>();
            for (int dx = 0; dx < fp.X; dx++)
                for (int dy = 0; dy < fp.Y; dy++)
                    origins.Add(new Vec2i(seed.X - dx, seed.Y - dy));
            return origins;
        }

        List<Vec2i> GrowFromSeed(
            Vec2i seed,
            long targetCells,
            Dictionary<Vec2i, string> occupied,
            string role,
            GdDict placed,
            bool requireCompat,
            string hint)
        {
            if (occupied.ContainsKey(seed)) return new List<Vec2i>();
            if (requireCompat && !CellCompatible(seed, role, occupied, placed)) return new List<Vec2i>();
            var cells = new List<Vec2i> { seed };
            var inSet = new HashSet<Vec2i> { seed };
            long want = Math.Max(targetCells, 1L);
            while (cells.Count < want)
            {
                Vec2i best = SENTINEL;
                long bestScore = -999999;
                foreach (Vec2i cell in cells)
                {
                    foreach (Vec2i dir in AllDirsArray)
                    {
                        var nxt = new Vec2i(cell.X + dir.X, cell.Y + dir.Y);
                        if (inSet.Contains(nxt) || occupied.ContainsKey(nxt)) continue;
                        if (requireCompat && !CellCompatible(nxt, role, occupied, placed)) continue;
                        long score = GrowthCellScore(nxt, inSet, seed, hint);
                        if (score > bestScore || (score == bestScore && VecLess(nxt, best)))
                        {
                            bestScore = score;
                            best = nxt;
                        }
                    }
                }
                if (best == SENTINEL) break;
                cells.Add(best);
                inSet.Add(best);
            }
            if (cells.Count == 0) return new List<Vec2i>();
            SortCells(cells);
            return cells;
        }

        static long GrowthCellScore(Vec2i cell, HashSet<Vec2i> inSet, Vec2i seed, string hint)
        {
            long neighborsInSet = 0;
            foreach (Vec2i dir in AllDirsArray)
                if (inSet.Contains(new Vec2i(cell.X + dir.X, cell.Y + dir.Y))) neighborsInSet += 1;
            Vec2i[] preferred = Preferred(hint);
            long hintBonus = 0;
            var delta = new Vec2i(SignInt(cell.X - seed.X), SignInt(cell.Y - seed.Y));
            if (preferred.Length > 0 && preferred[0] == delta) hintBonus = 2;
            long dist = Math.Abs((long)cell.X - seed.X) + Math.Abs((long)cell.Y - seed.Y);
            return neighborsInSet * 100 + hintBonus * 10 - dist;
        }

        static int SignInt(int value)
        {
            if (value > 0) return 1;
            if (value < 0) return -1;
            return 0;
        }

        static bool VecLess(Vec2i a, Vec2i b)
        {
            if (b == SENTINEL) return true;
            if (a.X != b.X) return a.X < b.X;
            return a.Y < b.Y;
        }

        static Vec2i CoerceFootprint(object raw)
        {
            if (raw is Vec2i v) return v;
            if (raw is GdArray arr && arr.Count >= 2)
                return new Vec2i((int)Math.Max(V.I64(arr[0]), 1L), (int)Math.Max(V.I64(arr[1]), 1L));
            return new Vec2i(2, 2);
        }

        static long MinManhattan(Vec2i cell, List<Vec2i> cells)
        {
            long best = 999999;
            foreach (Vec2i other in cells)
            {
                long d = Math.Abs((long)cell.X - other.X) + Math.Abs((long)cell.Y - other.Y);
                if (d < best) best = d;
            }
            return best;
        }

        static void CommitRoom(
            GdDict placed,
            Dictionary<Vec2i, string> occupied,
            string rid,
            List<Vec2i> cells,
            long deck,
            string role,
            Vec2i fp)
        {
            var owned = new List<Vec2i>();
            var seen = new HashSet<Vec2i>();
            foreach (Vec2i cell in cells)
            {
                if (seen.Contains(cell)) continue;
                seen.Add(cell);
                owned.Add(cell);
            }
            SortCells(owned);
            foreach (Vec2i cell in owned) occupied[cell] = rid;
            Vec2i origin = OriginOf(owned);
            Vec2i storedFp = fp;
            if (storedFp.X <= 0 || storedFp.Y <= 0) storedFp = BboxFp(owned);
            placed[rid] = new GdDict
            {
                { "cells", new GdArray(owned) },
                { "origin", origin },
                { "footprint", storedFp },
                { "deck", deck },
                { "role", role },
            };
        }

        void RealizeMissingConnectors(
            ConnectorGraph graph,
            GdDict zoneRoomsMap,
            Dictionary<long, Dictionary<Vec2i, string>> occupiedPerDeck,
            GdDict placed)
        {
            GdDict neighbors = graph.Neighbors;
            var pending = new List<string[]>();
            var seenPairs = new HashSet<string>();
            foreach (var ridVariant in placed.Keys)
            {
                string rid = V.Str(ridVariant);
                foreach (string other in NeighborIds(neighbors, rid))
                {
                    if (!placed.Has(other)) continue;
                    string key = PairKey(rid, other);
                    if (seenPairs.Contains(key)) continue;
                    seenPairs.Add(key);
                    pending.Add(new[] { rid, other });
                }
            }
            foreach (string[] pair in pending) TryGrowPair(pair[0], pair[1], occupiedPerDeck, placed);

            foreach (var pair in graph.ZonePairs)
            {
                string zoneA = V.Str(pair.Get("a", ""));
                string zoneB = V.Str(pair.Get("b", ""));
                if (ZonesShareEdgeOrVertical(zoneA, zoneB, zoneRoomsMap, placed)) continue;
                GdArray aIds = ZoneRooms(zoneRoomsMap, zoneA);
                GdArray bIds = ZoneRooms(zoneRoomsMap, zoneB);
                if (aIds.IsEmpty || bIds.IsEmpty) continue;
                TryGrowPair(V.Str(aIds[aIds.Count - 1]), V.Str(bIds[0]), occupiedPerDeck, placed);
            }
        }

        void TryGrowPair(string a, string b, Dictionary<long, Dictionary<Vec2i, string>> occupiedPerDeck, GdDict placed)
        {
            if (!placed.Has(a) || !placed.Has(b)) return;
            long deckA = DeckOf(placed, a);
            long deckB = DeckOf(placed, b);
            if (deckA != deckB) return;
            if (RoomsShareEdge(placed, a, b)) return;
            Dictionary<Vec2i, string> occupied = occupiedPerDeck[deckA];
            if (GrowRoomToTouch(b, a, occupied, placed)) return;
            GrowRoomToTouch(a, b, occupied, placed);
        }

        bool ZonesShareEdgeOrVertical(string zoneA, string zoneB, GdDict zoneRoomsMap, GdDict placed)
        {
            foreach (var arVariant in ZoneRooms(zoneRoomsMap, zoneA))
            {
                foreach (var brVariant in ZoneRooms(zoneRoomsMap, zoneB))
                {
                    string ar = V.Str(arVariant), br = V.Str(brVariant);
                    if (!placed.Has(ar) || !placed.Has(br)) continue;
                    long deckA = DeckOf(placed, ar);
                    long deckB = DeckOf(placed, br);
                    if (deckA != deckB) return true;
                    if (RoomsShareEdge(placed, ar, br)) return true;
                }
            }
            return false;
        }

        bool GrowRoomToTouch(string fromId, string toId, Dictionary<Vec2i, string> occupied, GdDict placed)
        {
            List<Vec2i> fromCells = AsCells(CellsOf(placed, fromId));
            List<Vec2i> toCells = AsCells(CellsOf(placed, toId));
            if (fromCells.Count == 0 || toCells.Count == 0) return false;
            HashSet<Vec2i> toSet = CellSet(toCells);
            if (CellSetsShareEdge(CellSet(fromCells), toCells)) return true;
            HashSet<Vec2i> fromSet = CellSet(fromCells);
            var prev = new Dictionary<Vec2i, Vec2i>();
            var seen = new HashSet<Vec2i>();
            var queue = new Queue<Vec2i>();
            foreach (Vec2i cell in fromCells)
            {
                queue.Enqueue(cell);
                seen.Add(cell);
            }
            Vec2i hit = SENTINEL;
            while (queue.Count > 0)
            {
                Vec2i cur = queue.Dequeue();
                foreach (Vec2i dir in AllDirsArray)
                {
                    var nxt = new Vec2i(cur.X + dir.X, cur.Y + dir.Y);
                    if (toSet.Contains(nxt))
                    {
                        hit = cur;
                        queue.Clear();
                        break;
                    }
                    if (seen.Contains(nxt)) continue;
                    if (occupied.TryGetValue(nxt, out string owner) && owner != fromId) continue;
                    if (MinManhattan(nxt, fromCells) > MAX_GROW_STEPS) continue;
                    seen.Add(nxt);
                    prev[nxt] = cur;
                    queue.Enqueue(nxt);
                }
            }
            if (hit == SENTINEL) return false;
            if (fromSet.Contains(hit)) return RoomsShareEdge(placed, fromId, toId);
            var path = new List<Vec2i>();
            Vec2i walk = hit;
            long guard = 0;
            while (!fromSet.Contains(walk) && guard < MAX_GROW_STEPS)
            {
                path.Add(walk);
                if (!prev.ContainsKey(walk)) break;
                walk = prev[walk];
                guard += 1;
            }
            if (path.Count == 0 || path.Count > MAX_GROW_STEPS) return false;
            var merged = new List<Vec2i>(fromCells);
            foreach (Vec2i cell in path)
            {
                if (occupied.TryGetValue(cell, out string owner) && owner != fromId) return false;
                if (fromSet.Contains(cell)) continue;
                merged.Add(cell);
                occupied[cell] = fromId;
                fromSet.Add(cell);
            }
            SortCells(merged);
            var record = (GdDict)placed[fromId];
            record["cells"] = new GdArray(merged);
            record["origin"] = OriginOf(merged);
            record["footprint"] = BboxFp(merged);
            return RoomsShareEdge(placed, fromId, toId);
        }

        static bool CanPlace(Vec2i origin, Vec2i fp, Dictionary<Vec2i, string> occupied)
        {
            for (int dx = 0; dx < fp.X; dx++)
                for (int dz = 0; dz < fp.Y; dz++)
                    if (occupied.ContainsKey(new Vec2i(origin.X + dx, origin.Y + dz))) return false;
            return true;
        }

        static bool In(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return true;
            return false;
        }

        static bool CellsCompatible(List<Vec2i> cells, string newRole, Dictionary<Vec2i, string> occupied, GdDict placed)
        {
            if (newRole.Length == 0 || In(CONNECTIVE_ROLES, newRole)) return true;
            bool isHazardous = In(HAZARDOUS_ROLES, newRole);
            bool isComfort = In(CREW_COMFORT_ROLES, newRole);
            if (!isHazardous && !isComfort) return true;
            foreach (Vec2i cell in cells)
                if (!CellCompatible(cell, newRole, occupied, placed)) return false;
            return true;
        }

        static bool CellCompatible(Vec2i cell, string newRole, Dictionary<Vec2i, string> occupied, GdDict placed)
        {
            if (newRole.Length == 0 || In(CONNECTIVE_ROLES, newRole)) return true;
            bool isHazardous = In(HAZARDOUS_ROLES, newRole);
            bool isComfort = In(CREW_COMFORT_ROLES, newRole);
            if (!isHazardous && !isComfort) return true;
            foreach (Vec2i dir in AllDirsArray)
            {
                var neighbor = new Vec2i(cell.X + dir.X, cell.Y + dir.Y);
                if (!occupied.TryGetValue(neighbor, out string neighborRid)) continue;
                if (!placed.Has(neighborRid)) continue;
                string neighborRole = V.Str(((GdDict)placed[neighborRid]).Get("role", ""));
                if (isHazardous && In(CREW_COMFORT_ROLES, neighborRole)) return false;
                if (isComfort && In(HAZARDOUS_ROLES, neighborRole)) return false;
            }
            return true;
        }

        static List<Vec2i> ComputeCells(Vec2i origin, Vec2i fp)
        {
            var cells = new List<Vec2i>();
            for (int dx = 0; dx < fp.X; dx++)
                for (int dz = 0; dz < fp.Y; dz++)
                    cells.Add(new Vec2i(origin.X + dx, origin.Y + dz));
            return cells;
        }

        /// <summary>The GDScript insertion sort by (x, y).</summary>
        static void SortCells(List<Vec2i> cells)
        {
            for (int i = 1; i < cells.Count; i++)
            {
                Vec2i key = cells[i];
                int j = i;
                while (j > 0 && VecLess(key, cells[j - 1]))
                {
                    cells[j] = cells[j - 1];
                    j -= 1;
                }
                cells[j] = key;
            }
        }

        static Vec2i OriginOf(List<Vec2i> cells)
        {
            if (cells.Count == 0) return Vec2i.Zero;
            int minX = cells[0].X;
            int minY = cells[0].Y;
            foreach (Vec2i cell in cells)
            {
                if (cell.X < minX) minX = cell.X;
                if (cell.Y < minY) minY = cell.Y;
            }
            return new Vec2i(minX, minY);
        }

        static Vec2i BboxFp(List<Vec2i> cells)
        {
            if (cells.Count == 0) return Vec2i.Zero;
            Vec2i origin = OriginOf(cells);
            int maxX = origin.X;
            int maxY = origin.Y;
            foreach (Vec2i cell in cells)
            {
                if (cell.X > maxX) maxX = cell.X;
                if (cell.Y > maxY) maxY = cell.Y;
            }
            return new Vec2i(maxX - origin.X + 1, maxY - origin.Y + 1);
        }

        /// <summary><c>_as_cells</c>: the Vector2i entries of an array (others skipped).</summary>
        static List<Vec2i> AsCells(object raw)
        {
            var cells = new List<Vec2i>();
            if (raw is GdArray arr)
                foreach (var item in arr)
                    if (item is Vec2i cell) cells.Add(cell);
            return cells;
        }

        static HashSet<Vec2i> CellSet(List<Vec2i> cells) => new HashSet<Vec2i>(cells);

        static bool CellSetsShareEdge(HashSet<Vec2i> aSet, List<Vec2i> bCells)
        {
            foreach (Vec2i cell in bCells)
                foreach (Vec2i dir in AllDirsArray)
                    if (aSet.Contains(new Vec2i(cell.X + dir.X, cell.Y + dir.Y))) return true;
            return false;
        }

        static bool RoomsShareEdge(GdDict placed, string a, string b)
        {
            if (!placed.Has(a) || !placed.Has(b)) return false;
            return CellSetsShareEdge(CellSet(AsCells(CellsOf(placed, a))), AsCells(CellsOf(placed, b)));
        }

        static List<GdDict> DiscoverAdjacencies(GdDict placed)
        {
            var cellToRoom = new Dictionary<(int, int, long), string>();
            foreach (var rid in placed.Keys)
            {
                var roomData = (GdDict)placed[rid];
                long deck = V.I64(roomData.Get("deck", 0L));
                foreach (Vec2i cell in AsCells(roomData.Get("cells", new GdArray())))
                    cellToRoom[(cell.X, cell.Y, deck)] = V.Str(rid);
            }

            var adjacencies = new List<GdDict>();
            var seenPairs = new HashSet<string>();

            foreach (var rid in placed.Keys)
            {
                string roomId = V.Str(rid);
                var roomData = (GdDict)placed[rid];
                long deck = V.I64(roomData.Get("deck", 0L));
                foreach (Vec2i cell in AsCells(roomData.Get("cells", new GdArray())))
                {
                    foreach (Vec2i dir in AllDirsArray)
                    {
                        var neighborCell = new Vec2i(cell.X + dir.X, cell.Y + dir.Y);
                        if (!cellToRoom.TryGetValue((neighborCell.X, neighborCell.Y, deck), out string neighborId)) continue;
                        if (neighborId == roomId) continue;
                        string pairKey = PairKey(roomId, neighborId);
                        if (seenPairs.Contains(pairKey)) continue;
                        seenPairs.Add(pairKey);
                        adjacencies.Add(new GdDict
                        {
                            { "from_room", roomId },
                            { "to_room", neighborId },
                            { "from_cell", cell },
                            { "to_cell", neighborCell },
                        });
                    }
                }
            }

            return adjacencies;
        }

        void AddVerticalAdjacencies(
            List<GdDict> adjacencies,
            GdDict placed,
            ConnectorGraph graph,
            GdDict zoneRoomsMap,
            TopologyTemplate template)
        {
            var existingPairs = new HashSet<string>();
            foreach (var adj in adjacencies)
                existingPairs.Add(PairKey(V.Str(adj["from_room"]), V.Str(adj["to_room"])));

            GdDict neighbors = graph.Neighbors;
            foreach (var rid in placed.Keys)
                foreach (string other in NeighborIds(neighbors, V.Str(rid)))
                    MaybeAddVertical(adjacencies, existingPairs, placed, V.Str(rid), other);

            foreach (var pair in graph.ZonePairs)
            {
                string fromZone = V.Str(pair.Get("a", ""));
                string toZone = V.Str(pair.Get("b", ""));
                if (ZonePairAlreadyLinked(fromZone, toZone, zoneRoomsMap, existingPairs)) continue;
                string fromRid = LastPlacedInZone(fromZone, zoneRoomsMap, placed);
                string toRid = FirstPlacedInZone(toZone, zoneRoomsMap, placed);
                MaybeAddVertical(adjacencies, existingPairs, placed, fromRid, toRid);
            }

            // attach_to remains a vertical fallback when a child zone sits on another deck and connections
            // already covered the zone pair as a same-deck miss.
            foreach (var zone in template.Zones)
            {
                string childZoneId = V.Str(zone.Get("id", ""));
                string parentZoneId = V.Str(zone.Get("attach_to", ""));
                if (parentZoneId.Length == 0) continue;
                if (ZonePairAlreadyLinked(parentZoneId, childZoneId, zoneRoomsMap, existingPairs)) continue;
                string parentRid = LastPlacedInZone(parentZoneId, zoneRoomsMap, placed);
                string childRid = FirstPlacedInZone(childZoneId, zoneRoomsMap, placed);
                MaybeAddVertical(adjacencies, existingPairs, placed, parentRid, childRid);
            }
        }

        static void MaybeAddVertical(
            List<GdDict> adjacencies,
            HashSet<string> existingPairs,
            GdDict placed,
            string fromRid,
            string toRid)
        {
            if (fromRid.Length == 0 || toRid.Length == 0) return;
            if (!placed.Has(fromRid) || !placed.Has(toRid)) return;
            string pk = PairKey(fromRid, toRid);
            if (existingPairs.Contains(pk)) return;
            long fromDeck = DeckOf(placed, fromRid);
            long toDeck = DeckOf(placed, toRid);
            // Same-deck declared connections must already be real shared edges; never invent a doorway.
            if (fromDeck == toDeck) return;
            existingPairs.Add(pk);
            Vec2i[] pairCells = VerticalCellPair(AsCells(CellsOf(placed, fromRid)), AsCells(CellsOf(placed, toRid)));
            adjacencies.Add(new GdDict
            {
                { "from_room", fromRid },
                { "to_room", toRid },
                { "from_cell", pairCells[0] },
                { "to_cell", pairCells[1] },
            });
        }

        static bool ZonePairAlreadyLinked(string zoneA, string zoneB, GdDict zoneRoomsMap, HashSet<string> existingPairs)
        {
            foreach (var ar in ZoneRooms(zoneRoomsMap, zoneA))
                foreach (var br in ZoneRooms(zoneRoomsMap, zoneB))
                    if (existingPairs.Contains(PairKey(V.Str(ar), V.Str(br)))) return true;
            return false;
        }

        static string LastPlacedInZone(string zoneId, GdDict zoneRoomsMap, GdDict placed)
        {
            string best = "";
            foreach (var rid in ZoneRooms(zoneRoomsMap, zoneId))
                if (placed.Has(V.Str(rid))) best = V.Str(rid);
            return best;
        }

        static string FirstPlacedInZone(string zoneId, GdDict zoneRoomsMap, GdDict placed)
        {
            foreach (var rid in ZoneRooms(zoneRoomsMap, zoneId))
                if (placed.Has(V.Str(rid))) return V.Str(rid);
            return "";
        }

        /// <summary>First xz-coincident (from, to) pair, else the first cell of each (or zero).</summary>
        static Vec2i[] VerticalCellPair(List<Vec2i> fromCells, List<Vec2i> toCells)
        {
            foreach (Vec2i fromCell in fromCells)
                foreach (Vec2i toCell in toCells)
                    if (fromCell.X == toCell.X && fromCell.Y == toCell.Y) return new[] { fromCell, toCell };
            return new[]
            {
                fromCells.Count > 0 ? fromCells[0] : Vec2i.Zero,
                toCells.Count > 0 ? toCells[0] : Vec2i.Zero,
            };
        }

        static GdDict RoomById(IList<GdDict> roomPlan, string rid)
        {
            foreach (var room in roomPlan)
                if (V.Str(room["id"]) == rid) return room;
            return new GdDict();
        }

        /// <summary>"spine", "spine[0]", "spine[*]", "spine[*+1]" all refer to zone "spine".</summary>
        static string ZoneRefId(string zoneRef) => ParseZoneRef(zoneRef).Id;

        static string PairKey(string a, string b)
        {
            if (ProcgenCompat.StringLess(a, b)) return a + "|" + b;
            return b + "|" + a;
        }
    }
}
