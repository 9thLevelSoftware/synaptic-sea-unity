// Ported from scripts/systems/synaptic_sea_world.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The infinite Synaptic Sea: a world_seed, the player's position, and the set of markers already
    /// materialized into ships. Markers themselves are not stored — they are regenerated deterministically
    /// from world_seed on each query.
    /// </summary>
    public class SynapticSeaWorld : IMarkerWorld
    {
        public long WorldSeed = 0;
        public Vec3 PlayerPosition = Vec3.Zero;

        /// <summary>marker_id -> true (insertion-ordered, like the GDScript Dictionary).</summary>
        public GdDict GeneratedMarkerIds = new GdDict();

        readonly MarkerGenerator _generator;

        public SynapticSeaWorld() : this(0, Vec3.Zero) { }

        public SynapticSeaWorld(long pWorldSeed, Vec3 pPlayerPosition = default)
        {
            WorldSeed = pWorldSeed;
            PlayerPosition = pPlayerPosition;
            _generator = new MarkerGenerator();
        }

        Vec3 IMarkerWorld.PlayerPosition => PlayerPosition;

        IReadOnlyList<ShipMarker> IMarkerWorld.MarkersInRange(double radius) => MarkersInRange(radius);

        /// <summary>
        /// Distinct markers within <paramref name="radius"/> of player_position, sorted ascending by distance.
        /// Regenerates every cell overlapping the radius bounding box.
        /// </summary>
        public List<ShipMarker> MarkersInRange(double radius)
        {
            // GDScript assert() (debug builds halt).
            if (!(radius >= 0.0))
                throw new ArgumentOutOfRangeException(nameof(radius), "SynapticSeaWorld.markers_in_range: radius must be non-negative");
            var output = new List<ShipMarker>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            double cs = MarkerGenerator.CELL_SIZE;
            long minX = (long)Math.Floor((PlayerPosition.X - radius) / cs);
            long maxX = (long)Math.Floor((PlayerPosition.X + radius) / cs);
            long minY = (long)Math.Floor((PlayerPosition.Z - radius) / cs);
            long maxY = (long)Math.Floor((PlayerPosition.Z + radius) / cs);
            for (long cx = minX; cx < maxX + 1; cx++)
            {
                for (long cy = minY; cy < maxY + 1; cy++)
                {
                    foreach (ShipMarker m in _generator.MarkersForCell(WorldSeed, new Vec2i(unchecked((int)cx), unchecked((int)cy))))
                    {
                        if (seen.Contains(m.MarkerId))
                            continue;
                        if (m.Position.DistanceTo(PlayerPosition) <= radius)
                        {
                            seen.Add(m.MarkerId);
                            output.Add(m);
                        }
                    }
                }
            }
            GdSort.SortCustom(output, CloserToPlayer);
            return output;
        }

        bool CloserToPlayer(ShipMarker a, ShipMarker b) =>
            a.Position.DistanceTo(PlayerPosition) < b.Position.DistanceTo(PlayerPosition);

        public void MarkGenerated(string markerId)
        {
            GeneratedMarkerIds[markerId] = true;
        }

        public bool IsGenerated(string markerId) => GeneratedMarkerIds.Has(markerId);

        /// <summary>
        /// Reverses mark_generated — used to roll back a travel that materialized a target but was then rejected
        /// (e.g. an incompatible dock port) so the world does not retain a generated mark for a derelict the
        /// player never actually traveled to.
        /// </summary>
        public void UnmarkGenerated(string markerId)
        {
            GeneratedMarkerIds.Erase(markerId);
        }

        public void SetPlayerPosition(Vec3 pos)
        {
            PlayerPosition = pos;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "world_seed", WorldSeed },
                { "player_position", GdArray.Of((double)PlayerPosition.X, (double)PlayerPosition.Y, (double)PlayerPosition.Z) },
                { "generated_marker_ids", new GdArray(GeneratedMarkerIds.Keys) },
            };
        }

        public bool ApplySummary(object summary)
        {
            if (!(summary is GdDict dict) || dict.IsEmpty)
                return false;
            WorldSeed = V.I64(dict.Get("world_seed", WorldSeed));
            object p = dict.Get("player_position", null);
            if (p is GdArray pa && pa.Count >= 3)
                PlayerPosition = new Vec3(V.F64(pa[0]), V.F64(pa[1]), V.F64(pa[2]));
            GeneratedMarkerIds.Clear();
            object ids = dict.Get("generated_marker_ids", new GdArray());
            if (ids is GdArray idArr)
            {
                foreach (object mid in idArr)
                    GeneratedMarkerIds[V.Str(mid)] = true;
            }
            return true;
        }
    }
}
