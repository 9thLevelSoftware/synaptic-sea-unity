// Ported from scripts/procgen/ship_blueprint.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Data class describing a procedurally generated ship layout seed. The blueprint is the single source of
    /// truth that downstream generators (room graph, system placement, encounter rolls) consume; it carries no
    /// scene nodes and only stores the inputs needed to deterministically reproduce a ship.
    /// </summary>
    public sealed class ShipBlueprint
    {
        public enum Size
        {
            LifeBoat = 0,
            Small = 1,
            Medium = 2,
        }

        public enum Condition
        {
            Pristine = 0,
            Damaged = 1,
            Wrecked = 2,
        }

        // GDScript fields `size`, `condition`, `seed_value`, `room_count_range`. They are plain ints in GDScript
        // (from_dict accepts any int), so they are stored as long rather than as the enums.
        public long ShipSize = (long)Size.Medium;
        public long ShipCondition = (long)Condition.Pristine;
        public long SeedValue = 0;

        /// <summary>Inclusive (min, max) room count. Recomputed from size in the constructor; writable for overrides.</summary>
        public Vec2i RoomCountRange = new Vec2i(8, 12);

        public ShipBlueprint(long pSize = (long)Size.Medium, long pCondition = (long)Condition.Pristine, long pSeed = 0)
        {
            ShipSize = pSize;
            ShipCondition = pCondition;
            SeedValue = pSeed;
            RoomCountRange = GetRoomCountRange();
        }

        public ShipBlueprint(Size pSize, Condition pCondition, long pSeed = 0)
            : this((long)pSize, (long)pCondition, pSeed) { }

        /// <summary>GDScript <c>_get_room_count_range_for</c>.</summary>
        public static Vec2i GetRoomCountRangeFor(long pSize)
        {
            switch (pSize)
            {
                case (long)Size.LifeBoat: return new Vec2i(2, 4);
                case (long)Size.Small: return new Vec2i(4, 8);
                case (long)Size.Medium: return new Vec2i(8, 12);
                default: return new Vec2i(8, 12);
            }
        }

        /// <summary>GDScript <c>_get_room_count_range</c>: recomputed from the current size each call.</summary>
        public Vec2i GetRoomCountRange() => GetRoomCountRangeFor(ShipSize);

        public double GetSystemOnlineChance()
        {
            switch (ShipCondition)
            {
                case (long)Condition.Pristine: return 0.9;
                case (long)Condition.Damaged: return 0.5;
                case (long)Condition.Wrecked: return 0.2;
                default: return 0.5;
            }
        }

        public GdDict ToDict()
        {
            return new GdDict
            {
                { "size", ShipSize },
                { "condition", ShipCondition },
                { "seed_value", SeedValue },
                {
                    "room_count_range", new GdDict
                    {
                        { "min", (long)RoomCountRange.X },
                        { "max", (long)RoomCountRange.Y },
                    }
                },
            };
        }

        public static ShipBlueprint FromDict(GdDict data)
        {
            var bp = new ShipBlueprint();
            if (data.Has("size")) bp.ShipSize = V.I64(data["size"]);
            if (data.Has("condition")) bp.ShipCondition = V.I64(data["condition"]);
            if (data.Has("seed_value")) bp.SeedValue = V.I64(data["seed_value"]);
            if (data.Has("room_count_range") && data["room_count_range"] is GdDict r)
            {
                Vec2i derived = bp.GetRoomCountRange();
                int rmin = unchecked((int)V.I64(r.Get("min", (long)derived.X)));
                int rmax = unchecked((int)V.I64(r.Get("max", (long)derived.Y)));
                bp.RoomCountRange = new Vec2i(rmin, rmax);
            }
            else
            {
                bp.RoomCountRange = bp.GetRoomCountRange();
            }
            return bp;
        }
    }
}
