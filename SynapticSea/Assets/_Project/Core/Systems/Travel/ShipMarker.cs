// Ported from scripts/systems/ship_marker.gd @ 96ecb2b0

using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Lightweight pre-generation descriptor of a ship in Synaptic Sea space. Pure data; the actual ship is
    /// materialized on demand from seed_value via ShipGenerator.
    /// </summary>
    public class ShipMarker
    {
        public string MarkerId = "";

        /// <summary>y is always 0 (planar SynapticSea grid).</summary>
        public Vec3 Position = Vec3.Zero;

        /// <summary>ShipBlueprint.Size.</summary>
        public long SizeClass = 0;

        /// <summary>ShipBlueprint.Condition.</summary>
        public long Condition = 1;

        public string ShipType = "";
        public long SeedValue = 0;

        public GdDict ToDict() =>
            new GdDict
            {
                { "marker_id", MarkerId },
                { "position", Position.ToArray() },
                { "size_class", SizeClass },
                { "condition", Condition },
                { "ship_type", ShipType },
                { "seed_value", SeedValue },
            };

        public static ShipMarker FromDict(GdDict d)
        {
            d = d ?? new GdDict();
            var m = new ShipMarker();
            m.MarkerId = V.Str(d.Get("marker_id", ""));
            object p = d.Get("position", GdArray.Of(0.0, 0.0, 0.0));
            if (p is GdArray arr && arr.Count >= 3)
                m.Position = new Vec3(V.F64(arr[0]), V.F64(arr[1]), V.F64(arr[2]));
            m.SizeClass = V.I64(d.Get("size_class", 0L));
            m.Condition = V.I64(d.Get("condition", 1L));
            m.ShipType = V.Str(d.Get("ship_type", ""));
            m.SeedValue = V.I64(d.Get("seed_value", 0L));
            return m;
        }
    }
}
