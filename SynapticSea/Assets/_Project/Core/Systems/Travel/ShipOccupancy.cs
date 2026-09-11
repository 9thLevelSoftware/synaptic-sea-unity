// Ported from scripts/systems/ship_occupancy.gd @ 96ecb2b0

using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// One occupancy entry: the GDScript <c>{"inst": ShipInstance, "aabb": AABB}</c> dictionary. A GdDict cannot hold
    /// a ShipInstance reference, so the entry is a typed pair; <see cref="Aabb"/> stays <c>object</c> so the
    /// "wrong-type aabb is skipped" guard keeps its meaning.
    /// </summary>
    public sealed class OccupancyEntry
    {
        public object Inst;
        public object Aabb;

        public OccupancyEntry() { }

        public OccupancyEntry(object inst, object aabb)
        {
            Inst = inst;
            Aabb = aabb;
        }
    }

    /// <summary>
    /// Pure spatial-containment resolver: returns the ShipInstance whose world-space interior AABB contains the
    /// player. Entry ORDER is priority: list the host (home) ship first so a dock-seam overlap deterministically
    /// resolves to it.
    /// </summary>
    public static class ShipOccupancy
    {
        public static object Resolve(Vec3 playerPos, IEnumerable<object> entries)
        {
            if (entries == null)
                return null;
            foreach (object entry in entries)
            {
                if (!(entry is OccupancyEntry e))
                    continue;
                if (!(e.Aabb is Aabb3 aabb))
                    continue;
                // AABB.has_point is inclusive; grow a hair so a player exactly on a shared seam still counts as inside.
                if (aabb.Grow(0.001f).HasPoint(playerPos))
                    return e.Inst;
            }
            return null;
        }
    }
}
