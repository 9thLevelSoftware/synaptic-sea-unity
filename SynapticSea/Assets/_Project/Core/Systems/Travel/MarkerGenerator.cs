// Ported from scripts/systems/marker_generator.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Deterministic, infinite marker field. (world_seed, grid cell) -> a fixed set of <see cref="ShipMarker"/>s.
    /// Same inputs always yield identical markers (bit-exact with Godot's RandomNumberGenerator via GodotRandom).
    /// </summary>
    public class MarkerGenerator
    {
        public const double CELL_SIZE = 100.0;
        public const long MARKERS_PER_CELL = 3;
        public static readonly GdArray SHIP_TYPES = GdArray.Of("shuttle", "freighter", "science_vessel", "derelict_hauler");

        /// <summary>
        /// Stable spatial hash (NOT Godot's hash()). The large primes decorrelate adjacent cells. Not collision-free
        /// for very distant cells; harmless because a marker's identity is its marker_id, not its seed.
        /// </summary>
        public static long CellSeed(long worldSeed, Vec2i cell)
        {
            return unchecked(worldSeed ^ ((long)cell.X * 73856093L) ^ ((long)cell.Y * 19349663L));
        }

        public List<ShipMarker> MarkersForCell(long worldSeed, Vec2i cell)
        {
            var output = new List<ShipMarker>();
            GodotRandom rng = GodotRandom.FromSeed(CellSeed(worldSeed, cell));
            double baseX = (double)cell.X * CELL_SIZE;
            double baseZ = (double)cell.Y * CELL_SIZE;
            for (long i = 0; i < MARKERS_PER_CELL; i++)
            {
                var m = new ShipMarker();
                m.MarkerId = GdString.FormatInt(cell.X) + ":" + GdString.FormatInt(cell.Y) + ":" + GdString.FormatInt(i);
                // Consume rng in a FIXED order so determinism holds.
                double lx = rng.Randf() * CELL_SIZE;
                double lz = rng.Randf() * CELL_SIZE;
                m.Position = new Vec3(baseX + lx, 0.0, baseZ + lz);
                m.SeedValue = rng.Randi();
                m.SizeClass = WeightedSize(rng);
                m.Condition = WeightedCondition(rng);
                m.ShipType = V.Str(SHIP_TYPES[(int)(rng.Randi() % SHIP_TYPES.Count)]);
                output.Add(m);
            }
            return output;
        }

        long WeightedSize(GodotRandom rng)
        {
            double r = rng.Randf();
            if (r < 0.4)
                return 0; // LIFE_BOAT
            else if (r < 0.8)
                return 1; // SMALL
            return 2;     // MEDIUM
        }

        long WeightedCondition(GodotRandom rng)
        {
            double r = rng.Randf();
            if (r < 0.15)
                return 0; // PRISTINE
            else if (r < 0.6)
                return 1; // DAMAGED
            return 2;     // WRECKED
        }
    }
}
