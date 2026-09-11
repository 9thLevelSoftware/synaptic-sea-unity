// Ported from scripts/procgen/structural_edge_plan.gd @ 96ecb2b0
using System.Globalization;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Canonical, data-only structural grid helpers. Cells are addressed by integer (x, y) coordinates on an
    /// integer deck. A boundary is identified geometrically, so both sides of a shared edge produce one edge ID.
    /// </summary>
    public static class StructuralEdgePlan
    {
        public const double CELL_SIZE = 4.0;
        public const double DECK_HEIGHT = 4.0;

        public static readonly GdDict DIRECTIONS = new GdDict
        {
            { "north", new Vec2i(0, -1) },
            { "east", new Vec2i(1, 0) },
            { "south", new Vec2i(0, 1) },
            { "west", new Vec2i(-1, 0) },
        };

        public static readonly GdDict OPPOSITE = new GdDict
        {
            { "north", "south" }, { "east", "west" }, { "south", "north" }, { "west", "east" },
        };

        public static readonly GdDict YAW_DEGREES = new GdDict
        {
            { "south", 0.0 }, { "west", 90.0 }, { "north", 180.0 }, { "east", 270.0 },
        };

        static string D(long v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Stable identity of an occupied grid cell: <c>"deck|x|y"</c>.</summary>
        public static string CellKey(long deck, Vec2i cell) => D(deck) + "|" + D(cell.X) + "|" + D(cell.Y);

        /// <summary>
        /// Geometry-derived identity for a cardinal cell boundary. The edge from (x, y) east equals the edge from
        /// (x + 1, y) west, and likewise for north/south.
        /// </summary>
        public static string EdgeKey(long deck, Vec2i cell, string direction)
        {
            // GDScript: assert(DIRECTIONS.has(direction)) — debug builds abort the call; release falls through.
            if (!DIRECTIONS.Has(direction))
            {
                CoreServices.Log.Error("unknown edge direction: " + direction);
                return "";
            }
            Vec2i delta = (Vec2i)DIRECTIONS[direction];
            Vec2i neighbor = cell + delta;
            if (direction == "north" || direction == "south")
                return D(deck) + "|h|" + D(System.Math.Min(cell.Y, neighbor.Y)) + "|" + D(cell.X);
            return D(deck) + "|v|" + D(cell.Y) + "|" + D(System.Math.Min(cell.X, neighbor.X));
        }

        /// <summary>Center of a cell's requested boundary in world coordinates (south is the zero-yaw pose).</summary>
        public static Vec3 EdgeWorldPosition(long deck, Vec2i cell, string direction)
        {
            if (!DIRECTIONS.Has(direction))
            {
                CoreServices.Log.Error("unknown edge direction: " + direction);
                return Vec3.Zero;
            }
            var center = new Vec3(
                (double)cell.X * CELL_SIZE,
                (double)deck * DECK_HEIGHT,
                (double)cell.Y * CELL_SIZE);
            Vec2i delta = (Vec2i)DIRECTIONS[direction];
            return center + new Vec3(
                (double)delta.X * CELL_SIZE * 0.5,
                0.0,
                (double)delta.Y * CELL_SIZE * 0.5);
        }

        /// <summary>Normalized occupied-cell record for compiler/validator consumers.</summary>
        public static GdDict MakeCell(long deck, Vec2i cell, string roomId)
        {
            string key = CellKey(deck, cell);
            return new GdDict
            {
                { "id", "cell:" + key },
                { "key", key },
                { "deck", deck },
                { "cell", cell },
                { "room_id", roomId },
                {
                    "position", new Vec3(
                        (double)cell.X * CELL_SIZE,
                        (double)deck * DECK_HEIGHT,
                        (double)cell.Y * CELL_SIZE)
                },
            };
        }

        /// <summary>
        /// Canonical edge/placement record. <paramref name="key"/> is expected to come from <see cref="EdgeKey"/>;
        /// its deck component is decoded only to derive the world position.
        /// </summary>
        public static GdDict MakeEdge(string key, string kind, string ownerRoom, string otherRoom, Vec2i cell, string direction)
        {
            if (!DIRECTIONS.Has(direction))
            {
                CoreServices.Log.Error("unknown edge direction: " + direction);
                return new GdDict();
            }
            long deck = DeckFromEdgeKey(key);
            Vec2i delta = (Vec2i)DIRECTIONS[direction];
            Vec2i neighbor = cell + delta;
            var roomIds = GdArray.Of(ownerRoom, otherRoom);
            var sourceCells = GdArray.Of(cell, neighbor);
            return new GdDict
            {
                { "id", "edge:" + key },
                { "edge_key", key },
                { "key", key },
                { "module_id", ModuleIdForKind(kind) },
                { "kind", kind },
                { "position", EdgeWorldPosition(deck, cell, direction) },
                { "yaw_degrees", V.F64(YAW_DEGREES[direction]) },
                { "direction", direction },
                { "opposite_direction", OPPOSITE[direction] },
                { "deck", deck },
                { "cell", cell },
                { "room_ids", roomIds },
                { "owner_room", ownerRoom },
                { "other_room", otherRoom },
                { "source_cells", sourceCells },
            };
        }

        static long DeckFromEdgeKey(string key)
        {
            // GDScript String.split("|") keeps empty parts; "".split("|") yields [""] (never empty).
            string[] parts = (key ?? "").Split('|');
            if (parts.Length < 4) CoreServices.Log.Error("invalid canonical edge key: " + key);
            if (parts.Length == 0) return 0;
            return V.StringToInt(parts[0]);
        }

        static string ModuleIdForKind(string kind)
        {
            switch (kind)
            {
                case "SOLID": return "wall_straight_1x1";
                case "DOOR": return "bulkhead_portal_2x1";
                case "LOCKED": return "doorway_frame_blocked_1x1";
                case "HATCH": return "doorway_frame_open_1x1";
                case "BREACH": return "";
                case "OPEN": return "";
                default: return "";
            }
        }
    }
}
