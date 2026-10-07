using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>Versioned service-loop expedition geometry. Legacy templates and first-away generation stay unchanged.</summary>
    public static class ExpeditionLayoutEngine
    {
        public const string Profile = "service_loop_v1";

        public static GdDict Layout(ShipBlueprint blueprint, out List<GdDict> roomPlan)
        {
            var rng = GodotRandom.FromSeed(blueprint.SeedValue);
            int sectors = (int)rng.RandiRange(blueprint.ShipSize == 1 ? 3 : 5, blueprint.ShipSize == 1 ? 4 : 6);
            int height = (int)rng.RandiRange(blueprint.ShipSize == 1 ? 2 : 3, blueprint.ShipSize == 1 ? 3 : 5);
            var rooms = new GdDict();
            roomPlan = new List<GdDict>();
            var plan = roomPlan;
            var counts = new Dictionary<string, int>();
            var owners = new Dictionary<Vec2i, string>();
            var order = new List<Vec2i>();
            int quarter = (int)rng.RandiRange(0, 3);
            void Add(string role, int x, int y, int width, int depth)
            {
                counts.TryGetValue(role, out int count); counts[role] = ++count;
                string id = role + "_" + count.ToString("D2");
                var cells = new GdArray();
                for (int cy = y; cy < y + depth; cy++) for (int cx = x; cx < x + width; cx++)
                {
                    var cell = new Vec2i(cx, cy);
                    for (int r = 0; r < quarter; r++) cell = new Vec2i(-cell.Y, cell.X);
                    owners.Add(cell, id); order.Add(cell); cells.Append(cell);
                }
                var footprint = quarter % 2 == 0 ? new Vec2i(width, depth) : new Vec2i(depth, width);
                var data = new GdDict { { "cells", cells }, { "origin", cells[0] }, { "footprint", footprint }, { "deck", 0L }, { "role", role } };
                rooms[id] = data;
                plan.Add(new GdDict { { "id", id }, { "role", role }, { "variant", "standard" }, { "deck", 0L }, { "footprint", footprint }, { "target_cells", (long)width * depth } });
            }
            Add("dock", -2, 1, 2, 2);
            Add("corridor", 0, 0, 1, height + 2);
            string[] roles = { "crew_quarters", "medical", "cargo", "bridge", "engineering", "reactor" };
            int start = 1;
            for (int i = 0; i < sectors; i++)
            {
                int width = (int)rng.RandiRange(2, 4);
                Add("corridor", start, 0, width, 1);
                string role = i == 0 ? "crew_quarters" : i == sectors - 1 ? "engineering" : roles[(int)rng.RandiRange(1, roles.Length - 2)];
                if (i == sectors / 2) role = "bridge";
                Add(role, start, 1, width, height);
                Add("corridor", start, height + 1, width, 1);
                // Optional exploration pockets alter the silhouette and place supplies off the service loop.
                if (i == 0 || i == sectors - 2 || (blueprint.ShipSize == 2 && i == sectors / 2))
                {
                    int pocketDepth = (int)rng.RandiRange(1, 3);
                    bool north = rng.RandiRange(0, 1) == 0;
                    Add(i == 0 ? "cargo" : i == sectors - 2 ? "maintenance" : "medical",
                        start, north ? -pocketDepth : height + 2, width, pocketDepth);
                }
                start += width;
            }
            Add("corridor", start, 0, 1, height + 2);
            var adjacencies = new GdArray();
            var pairs = new HashSet<string>();
            foreach (var cell in order)
                foreach (var dir in CellLayoutEngine.ALL_DIRS)
                {
                    var neighbor = new Vec2i(cell.X + dir.X, cell.Y + dir.Y);
                    if (!owners.TryGetValue(neighbor, out string other) || other == owners[cell]) continue;
                    string from = owners[cell];
                    string key = string.CompareOrdinal(from, other) < 0 ? from + "|" + other : other + "|" + from;
                    if (!pairs.Add(key)) continue;
                    adjacencies.Append(new GdDict { { "from_room", from }, { "to_room", other }, { "from_cell", cell }, { "to_cell", neighbor } });
                }
            return new GdDict { { "rooms", rooms }, { "adjacencies", adjacencies } };
        }
    }
}
