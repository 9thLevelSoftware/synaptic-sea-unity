// Port deviation (no Godot source): corrects where the multi-wing vertex wrappers are instantiated.
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Places the multi-wing vertex wrappers (<c>wall_inner_corner</c>, <c>wall_outer_corner</c>,
    /// <c>wall_t_junction</c>) where their geometry belongs. This is a PORT DEVIATION, applied when the scene is
    /// built and never to the plan itself (<see cref="StructuralPlanValidator"/> requires every edge placement to
    /// keep its canonical edge pose, and the plan is compared bit-exact with Godot's).
    ///
    /// Godot's <c>ApplyVertexModules</c> stamps a vertex module onto one EDGE record and leaves that edge's pose
    /// (the edge midpoint) alone. But those three wrappers are authored around a CELL CENTRE: their wings are whole
    /// 4 m walls 2 m out on the cell's north and east sides (the T-junction adds the west side). Instantiated at an
    /// edge midpoint, each wing lands half a cell off, so walls appear inside rooms — golden coherent_ship_001 gets
    /// a wall across the middle of life_support_01, which blocks the player and cuts the room off the NavMesh.
    ///
    /// The fix moves the wrapper to the cell centre whose own edges its wings cover, and yaws it so they do. The
    /// invariant: every wing lands on a SOLID non-portal edge that has geometry coming anyway, so no opening is
    /// sealed and no wall appears in open space. The edges a wing covers are reported in <see cref="Result.Covered"/>
    /// and must not build their own wrapper, which is what keeps one wall per edge. Where no placement satisfies the
    /// invariant, the edge is reported in <see cref="Result.Fallbacks"/> and takes a plain straight wall instead.
    /// </summary>
    public static class VertexWrapperPlacement
    {
        /// <summary>The wrappers authored around a cell centre, with the wing directions they cover at yaw 0.</summary>
        public static readonly IReadOnlyList<string> VERTEX_MODULES = new[]
        {
            StructuralEdgeCompiler.WALL_INNER_CORNER_MODULE,
            StructuralEdgeCompiler.WALL_OUTER_CORNER_MODULE,
            StructuralEdgeCompiler.WALL_T_JUNCTION_MODULE,
        };

        /// <summary>
        /// Wing directions by yaw: the north wing, the east wing, then the west wing (T-junction only). A yaw of
        /// <c>t</c> rotates the authored -Z / +X / -X wings, so yaw 0 covers the cell's north, east and west edges.
        /// </summary>
        static readonly Dictionary<double, string[]> WINGS_BY_YAW = new Dictionary<double, string[]>
        {
            { 0.0, new[] { "north", "east", "west" } },
            { 90.0, new[] { "west", "north", "south" } },
            { 180.0, new[] { "south", "west", "east" } },
            { 270.0, new[] { "east", "south", "north" } },
        };

        static readonly double[] YAW_ORDER = { 0.0, 90.0, 180.0, 270.0 };

        public struct Pose
        {
            public Vec3 Position;
            public double YawDegrees;
        }

        public sealed class Result
        {
            /// <summary>edge_key -> the cell-centre pose its vertex wrapper is built at.</summary>
            public readonly Dictionary<string, Pose> Poses = new Dictionary<string, Pose>(System.StringComparer.Ordinal);

            /// <summary>edge_key -> covered by another placement's wing; it must not build its own wrapper.</summary>
            public readonly HashSet<string> Covered = new HashSet<string>(System.StringComparer.Ordinal);

            /// <summary>edge_key -> no wing layout was valid; build a plain straight wall at the edge pose.</summary>
            public readonly HashSet<string> Fallbacks = new HashSet<string>(System.StringComparer.Ordinal);
        }

        public static bool IsVertexModule(string moduleId)
        {
            foreach (string id in VERTEX_MODULES)
                if (id == moduleId) return true;
            return false;
        }

        /// <summary>
        /// Resolves every vertex wrapper in <paramref name="plan"/> (a <c>structural_plan</c>) in placement order,
        /// which makes the outcome deterministic for a given plan.
        /// </summary>
        public static Result Resolve(GdDict plan)
        {
            var result = new Result();
            GdDict edges = plan.GetDictOrEmpty("edges");
            GdDict occupancy = plan.GetDictOrEmpty("occupancy");
            GdArray placements = plan.GetArrayOrEmpty("placements");
            if (edges.IsEmpty || placements.IsEmpty) return result;

            foreach (object placementVariant in placements)
            {
                if (!(placementVariant is GdDict placement)) continue;
                string moduleId = placement.GetString("module_id");
                if (!IsVertexModule(moduleId)) continue;
                string edgeKey = placement.GetString("edge_key");
                if (edgeKey.Length == 0) continue;
                // An earlier wrapper's wing already walls this edge, so this one is not built at all.
                if (result.Covered.Contains(edgeKey)) continue;
                bool tJunction = moduleId == StructuralEdgeCompiler.WALL_T_JUNCTION_MODULE;
                if (!TryChoose(placement, edges, occupancy, result, edgeKey, tJunction, moduleId))
                    result.Fallbacks.Add(edgeKey);
            }
            return result;
        }

        /// <summary>
        /// The wrapper's cell is one of the two the edge separates, at one of the two yaws that put the edge under a
        /// wing. A candidate holds only when every OTHER wing lands on a solid, non-portal edge that is still free.
        /// </summary>
        static bool TryChoose(
            GdDict placement,
            GdDict edges,
            GdDict occupancy,
            Result result,
            string edgeKey,
            bool tJunction,
            string moduleId)
        {
            string direction = placement.GetString("direction");
            if (!StructuralEdgeCompiler.DIRECTIONS.Has(direction)) return false;
            long deck = V.I64(placement.Get("deck", -1L));
            var parsed = ReadCell(placement.Get("cell", null), deck);
            if (!parsed.Ok) return false;
            Vec2i cell = parsed.Cell;
            deck = parsed.Deck;
            Vec2i neighbor = cell + (Vec2i)StructuralEdgeCompiler.DIRECTIONS[direction];
            // A wrapper in the cell on the far side covers the same edge from its own opposite direction.
            string opposite = V.Str(StructuralEdgeCompiler.OPPOSITE[direction]);

            // wall_outer_corner / wall_t_junction wrap an occupied cell's outside; wall_inner_corner wraps the notch.
            bool preferOccupied = moduleId != StructuralEdgeCompiler.WALL_INNER_CORNER_MODULE;
            List<string> best = null;
            Vec2i bestCell = cell;
            double bestYaw = 0.0;
            bool bestPreferred = false;

            foreach (var candidate in new[] { (Cell: cell, Dir: direction), (Cell: neighbor, Dir: opposite) })
            {
                bool occupied = occupancy.Has(StructuralEdgeCompiler.CellKey(deck, candidate.Cell));
                foreach (double yaw in YAW_ORDER)
                {
                    string[] wings = WINGS_BY_YAW[yaw];
                    int wingCount = tJunction ? 3 : 2;
                    bool covers = false;
                    for (int i = 0; i < wingCount; i++)
                        if (wings[i] == candidate.Dir) covers = true;
                    if (!covers) continue;
                    List<string> covered = CoveredKeys(edges, result, deck, candidate.Cell, wings, wingCount, edgeKey);
                    if (covered == null) continue;
                    bool preferred = occupied == preferOccupied;
                    // First valid candidate wins, unless a later one sits on the side the wrapper is authored for.
                    if (best != null && !(preferred && !bestPreferred)) continue;
                    best = covered;
                    bestCell = candidate.Cell;
                    bestYaw = yaw;
                    bestPreferred = preferred;
                }
            }

            if (best == null) return false;
            result.Poses[edgeKey] = new Pose
            {
                Position = StructuralEdgeCompiler.CellWorldPosition(deck, bestCell),
                YawDegrees = bestYaw,
            };
            foreach (string covered in best) result.Covered.Add(covered);
            return true;
        }

        /// <summary>
        /// The edges this candidate's wings would cover, or null when any wing misses a solid wall that is still
        /// free (which would seal an opening, double a wrapper, or leave a wall standing in open space). A wing may
        /// take over another vertex wrapper's edge as long as that one has not been placed yet: it then never
        /// builds, and the edges it would have covered keep their own wrappers.
        /// </summary>
        static List<string> CoveredKeys(
            GdDict edges,
            Result result,
            long deck,
            Vec2i cell,
            string[] wings,
            int wingCount,
            string edgeKey)
        {
            var covered = new List<string>();
            for (int i = 0; i < wingCount; i++)
            {
                string wingKey = StructuralEdgeCompiler.EdgeKey(deck, cell, wings[i]);
                if (wingKey == edgeKey) continue;
                if (!(edges.Get(wingKey, null) is GdDict edge)) return null;
                if (V.Str(edge.Get("kind", edge.Get("state", ""))) != "SOLID") return null;
                if (V.Bool(edge.Get("portal", false))) return null;
                if (!V.Bool(edge.Get("wrapper_required", edge.Get("placement_required", true)))) return null;
                if (result.Covered.Contains(wingKey) || result.Poses.ContainsKey(wingKey) || result.Fallbacks.Contains(wingKey))
                    return null;
                covered.Add(wingKey);
            }
            return covered;
        }

        static StructuralEdgeCompiler.CellInfo ReadCell(object value, long defaultDeck) =>
            StructuralEdgeCompiler.ReadCell(value, defaultDeck);
    }
}
