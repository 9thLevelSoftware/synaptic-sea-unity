using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>Unwired finite topology prototype. It makes no hazard, threat, damage, clearance or complete-contract guarantee.</summary>
    public static class FirstAwaySalvageGeometry
    {
        sealed class Rect
        {
            public readonly string Id, Role; public readonly int X, Z, W, H;
            public Rect(string id, string role, int x, int z, int w = 2, int h = 2) { Id = id; Role = role; X = x; Z = z; W = w; H = h; }
            public IEnumerable<Vec2i> Cells() { for (int z = Z; z < Z + H; z++) for (int x = X; x < X + W; x++) yield return new Vec2i(x, z); }
        }
        public sealed class Choices
        {
            public string Family { get; }
            public int CargoWidth { get; }
            public int CargoHeight { get; }
            public int Orientation { get; }
            /// <summary>Two-choice offset applied to every declared shared boundary for explicit exhaustive geometry tests.</summary>
            public int BoundaryOffset { get; }
            public Choices(string family, int width, int height, int orientation, int boundaryOffset)
            { Family = family; CargoWidth = width; CargoHeight = height; Orientation = orientation; BoundaryOffset = boundaryOffset; }
        }
        public static string[] Families(long size) => size == 0 ? new[] { "compact_dogleg" }
            : size == 1 ? new[] { "service_loop", "split_service", "split_service_s" }
            : size == 2 ? new[] { "service_loop", "divided_service_hull", "divided_service_hull_p1", "divided_service_hull_p2" }
            : Array.Empty<string>();
        public static Choices Project(FirstAwayGenerationInputs inputs)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            string[] families = Families(inputs.Size); string family = families[inputs.Substream("geometry.family") % (uint)families.Length];
            uint footprint = inputs.Size == 0 ? 0 : inputs.Substream("geometry.cargo_footprint") % 3;
            return new Choices(family, footprint == 2 ? 4 : 3, footprint == 1 ? 4 : 3,
                (int)(inputs.Substream("geometry.orientation") % 8), (int)(inputs.Substream("geometry.boundary_offset") % 2));
        }
        /// <summary>Reflection of X followed by zero to three integer quarter-turns; directions use the identical map.</summary>
        public static Vec2i Transform(Vec2i cell, int orientation)
        {
            if (orientation < 0 || orientation > 7) throw new ArgumentException("unsupported orientation");
            if (orientation >= 4) cell = new Vec2i(-cell.X, cell.Y);
            for (int q = 0; q < orientation % 4; q++) cell = new Vec2i(-cell.Y, cell.X);
            return cell;
        }
        public static GdDict Compose(FirstAwayGenerationInputs inputs, out List<GdDict> plan) => Compose(inputs, Project(inputs), out plan);
        public static GdDict Compose(FirstAwayGenerationInputs inputs, Choices choices, out List<GdDict> plan)
        {
            if (inputs == null || choices == null) throw new ArgumentException("missing composition inputs");
            if (!Families(inputs.Size).Contains(choices.Family) || choices.Orientation < 0 || choices.Orientation > 7 || choices.BoundaryOffset < 0 || choices.BoundaryOffset > 1
                || !(choices.CargoWidth == 3 && (choices.CargoHeight == 3 || choices.CargoHeight == 4) || choices.CargoWidth == 4 && choices.CargoHeight == 3)
                || inputs.Size == 0 && (choices.CargoWidth != 3 || choices.CargoHeight != 3)) throw new ArgumentException("unsupported finite geometry choice");
            var rectangles = new List<Rect> { new Rect("dock", "dock", 0, 0), new Rect("corridor", "corridor", 2, 0),
                new Rect("bridge", "bridge", 4, 0), new Rect("recovery", "cargo", 4, 2, choices.CargoWidth, choices.CargoHeight) };
            var extensions = new[] { new Rect("Q", "crew_quarters", 0, -2), new Rect("M", "maintenance", 2, -2),
                new Rect("W", "engineering", 4, -2), new Rect("S", "maintenance", 6, -2), new Rect("N", "medical", 0, -4),
                new Rect("R", "maintenance", 2, -4), new Rect("P1", "crew_quarters", 4, -4), new Rect("P2", "maintenance", 6, -4) };
            int count = inputs.Size == 0 ? 0 : inputs.Size == 1 ? choices.Family == "service_loop" ? 2 : choices.Family == "split_service" ? 3 : 4
                : choices.Family == "service_loop" ? 4 : choices.Family == "divided_service_hull" ? 6 : choices.Family == "divided_service_hull_p1" ? 7 : 8;
            rectangles.AddRange(extensions.Take(count));
            var byId = rectangles.ToDictionary(room => room.Id); var rooms = new GdDict(); plan = new List<GdDict>();
            string RoomId(string semantic) => FirstAwayGenerationInputs.Profile + "/" + inputs.OwnerId + "/room/" + semantic;
            foreach (var room in rectangles.OrderBy(room => room.Id == "dock" ? 0 : room.Id == "bridge" ? 2 : 1))
            {
                var cells = room.Cells().Select(cell => Transform(cell, choices.Orientation)).OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();
                var fp = choices.Orientation % 2 == 0 ? new Vec2i(room.W, room.H) : new Vec2i(room.H, room.W);
                var origin = new Vec2i(cells.Min(cell => cell.X), cells.Min(cell => cell.Y));
                rooms[RoomId(room.Id)] = new GdDict { { "cells", new GdArray(cells) }, { "origin", origin }, { "footprint", fp },
                    { "deck", 0L }, { "role", room.Role }, { "semantic_id", room.Id }, { "owner_id", inputs.OwnerId } };
                plan.Add(new GdDict { { "id", RoomId(room.Id) }, { "role", room.Role }, { "variant", "standard" }, { "deck", 0L }, { "footprint", fp }, { "target_cells", (long)room.W * room.H } });
            }
            string[][] joins = { new[] { "dock", "corridor" }, new[] { "corridor", "bridge" }, new[] { "bridge", "recovery" },
                new[] { "dock", "Q" }, new[] { "corridor", "M" }, new[] { "Q", "M" }, new[] { "bridge", "W" }, new[] { "M", "W" },
                new[] { "W", "S" }, new[] { "Q", "N" }, new[] { "M", "R" }, new[] { "N", "R" }, new[] { "W", "P1" },
                new[] { "R", "P1" }, new[] { "S", "P2" }, new[] { "P1", "P2" } };
            var links = new GdArray();
            foreach (string[] pairIds in joins)
            {
                if (!byId.ContainsKey(pairIds[0]) || !byId.ContainsKey(pairIds[1])) continue;
                var boundary = (from a in byId[pairIds[0]].Cells() from b in byId[pairIds[1]].Cells() where a.ManhattanTo(b) == 1 select (a, b)).ToList();
                if (boundary.Count != 2) throw new InvalidOperationException("declared join must have exactly two owned cardinal boundary choices");
                var pair = boundary[choices.BoundaryOffset];
                var aCell = Transform(pair.a, choices.Orientation); var bCell = Transform(pair.b, choices.Orientation);
                links.Add(new GdDict { { "id", FirstAwayGenerationInputs.Profile + "/" + inputs.OwnerId + "/join/" + pairIds[0] + "-" + pairIds[1] },
                    { "from_room", RoomId(pairIds[0]) }, { "to_room", RoomId(pairIds[1]) }, { "from_cell", aCell }, { "to_cell", bCell },
                    { "normal", Transform(pair.b - pair.a, choices.Orientation) }, { "owner_id", inputs.OwnerId } });
            }
            var cargo = byId["recovery"];
            // Authoring publishes cells in z/x order; existing serializer preserves that order and EncounterInjector selects Count/2.
            var transformedCargo = cargo.Cells().Select(cell => Transform(cell, choices.Orientation)).OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();
            var midpoint = transformedCargo[transformedCargo.Length / 2];
            var reservations = new GdDict {
                { "branch_landing", Transform(new Vec2i(4, 2), choices.Orientation) },
                { "work_approach", Transform(new Vec2i(4, cargo.Z + cargo.H - 1), choices.Orientation) },
                { "threat_anchor", midpoint }, { "loot_approach", Transform(new Vec2i(cargo.X + cargo.W - 1, cargo.Z + cargo.H - 1), choices.Orientation) } };
            var graph = new GdDict { { "rooms", rooms }, { "adjacencies", links }, { "generation_profile", FirstAwayGenerationInputs.Profile },
                { "prototype_inputs", inputs.ToDict() }, { "prototype_choices", new GdDict { { "family", choices.Family }, { "cargo_width", (long)choices.CargoWidth },
                    { "cargo_height", (long)choices.CargoHeight }, { "orientation", (long)choices.Orientation }, { "boundary_offset", (long)choices.BoundaryOffset } } },
                { "reservations", reservations }, { "reservation_owner", RoomId("recovery") }, { "physical_acceptance", false } };
            string failure = Validate(graph); if (failure.Length != 0) throw new InvalidOperationException(failure);
            return graph;
        }
        public static string Validate(GdDict grid)
        {
            if (grid == null || !(grid.Get("rooms") is GdDict) || !(grid.Get("adjacencies") is GdArray)) return "missing_grid";
            var rooms = grid.GetDictOrEmpty("rooms"); var owners = new Dictionary<Vec2i, string>(); var graph = new Dictionary<string, HashSet<string>>();
            if (rooms.Count < 4) return "room_budget";
            string start = V.Str(rooms.Keys.First()), goal = V.Str(rooms.Keys.Last());
            if (rooms.GetDictOrEmpty(start).GetString("role") != "dock" || rooms.GetDictOrEmpty(goal).GetString("role") != "bridge") return "publication_order";
            foreach (var key in rooms.Keys)
            {
                string id = V.Str(key); var room = rooms.GetDictOrEmpty(id); graph[id] = new HashSet<string>();
                var cells = room.GetArrayOrEmpty("cells");
                if (!(room.Get("footprint") is Vec2i fp) || fp.X < 2 || fp.X > 4 || fp.Y < 2 || fp.Y > 4 || cells.Count != fp.X * fp.Y
                    || !(room.Get("origin") is Vec2i origin)) return "footprint";
                foreach (object value in cells)
                    if (!(value is Vec2i cell) || owners.ContainsKey(cell)) return "floor_owner";
                    else if (cell.X < origin.X || cell.X >= origin.X + fp.X || cell.Y < origin.Y || cell.Y >= origin.Y + fp.Y) return "nonrectangular_floor";
                    else owners[cell] = id;
            }
            foreach (object rawEdge in grid.GetArrayOrEmpty("adjacencies"))
            {
                if (!(rawEdge is GdDict edge)) return "invalid_portal";
                string a = edge.GetString("from_room"), b = edge.GetString("to_room");
                if (!(edge.Get("from_cell") is Vec2i ca) || !(edge.Get("to_cell") is Vec2i cb) || !owners.TryGetValue(ca, out string oa) || oa != a
                    || !owners.TryGetValue(cb, out string ob) || ob != b || ca.ManhattanTo(cb) != 1 || !(edge.Get("normal") is Vec2i normal) || normal != cb - ca) return "portal_ownership";
                if (!graph[a].Add(b)) return "duplicate_join"; graph[b].Add(a);
            }
            var depths = new Dictionary<string, int> { { start, 0 } }; var queue = new Queue<string>(); queue.Enqueue(start);
            while (queue.Count > 0) { string id = queue.Dequeue(); foreach (string next in graph[id]) if (!depths.ContainsKey(next)) { depths[next] = depths[id] + 1; queue.Enqueue(next); } }
            if (depths.Count != rooms.Count) return "disconnected";
            string branch = grid.GetString("reservation_owner");
            if (!depths.ContainsKey(branch) || depths[branch] < .55 * depths.Values.Max() || branch == goal || graph[branch].Count != 1) return "late_optional_branch";
            var reserved = grid.GetDictOrEmpty("reservations"); var unique = new HashSet<Vec2i>();
            if (reserved.Count != 4) return "reservation_budget";
            foreach (object value in reserved.Values) if (!(value is Vec2i cell) || !unique.Add(cell) || !owners.TryGetValue(cell, out string owner) || owner != branch) return "reservation_ownership";
            return "";
        }
    }
}
