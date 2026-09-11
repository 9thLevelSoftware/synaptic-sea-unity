// Ported from scripts/systems/dock_ports.gd @ 96ecb2b0

using System;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Derives dock-port descriptors {position: Vector3 (local), facing: Vector3} from a ship layout dict.
    /// The lifeboat docks at its airlock (-X side); the derelict exposes its guaranteed <c>dock</c> room opening
    /// (+X side outward). Position/facing values are stored as <see cref="Vec3"/> inside the <see cref="GdDict"/>,
    /// exactly like the Godot Vector3 Variants.
    /// </summary>
    public static class DockPorts
    {
        /// <summary>CELL_SIZE (4.0) / 2.</summary>
        public const double HALF_CELL = 2.0;
        public const long AIRLOCK_SIZE_CLASS = 1;
        /// <summary>Hangar floor cells budgeted per ship slot.</summary>
        public const long CELLS_PER_SLOT = 2;
        /// <summary>&gt;= this many cells -> a size-class-2 bay.</summary>
        public const long HANGAR_BIG_CELL_THRESHOLD = 4;

        static readonly string[] FLOOR_MODULES = { "floor_1x1", "corridor_floor_1x1" };

        public static GdDict ForLifeboat(GdDict layout)
        {
            Vec3 center = RoomFloorCenter(layout, "airlock", "airlock");
            if (center == Vec3.Inf)
                return new GdDict();
            // Airlock opening faces the dock (-X, away from the +X cockpit); nudge to the edge.
            return new GdDict
            {
                { "position", center + new Vec3(-HALF_CELL, 0.0, 0.0) },
                { "facing", new Vec3(-1.0, 0.0, 0.0) },
                { "type", "airlock" },
                { "size_class", AIRLOCK_SIZE_CLASS },
                { "condition", "intact" },
            };
        }

        public static GdDict ForDerelict(GdDict layout, long seedValue = 0, long conditionClass = 0)
        {
            Vec3 center = RoomFloorCenter(layout, "dock", "dock");
            // Fall back to the airlock room when no dock room exists (e.g. the home ship uses its airlock as the
            // docking attachment point rather than a dedicated dock room).
            if (center == Vec3.Inf)
                center = RoomFloorCenter(layout, "airlock", "airlock");
            if (center == Vec3.Inf)
                return new GdDict();
            return new GdDict
            {
                { "position", center },
                { "facing", new Vec3(1.0, 0.0, 0.0) },
                { "type", "airlock" },
                { "size_class", AIRLOCK_SIZE_CLASS },
                { "condition", ConditionFromSeed(seedValue, conditionClass) },
            };
        }

        /// <summary>Ship-local floor center of the <c>bridge</c> room, or <see cref="Vec3.Inf"/> if none.</summary>
        public static Vec3 BridgeCenter(GdDict layout) => RoomFloorCenter(layout, "bridge", "bridge");

        /// <summary>
        /// Derives a hangar-bay descriptor from a ship layout. Prefers a <c>hangar</c> room; falls back to the
        /// <c>cargo</c> room (the home ship's bay) when no hangar room exists.
        /// slot_count = floor(floor_cells / CELLS_PER_SLOT) (min 1); slot_size_class scales with the bay footprint;
        /// slot_anchors are ship-local floor-cell centers, one per slot. Returns {} only when neither exists.
        /// </summary>
        public static GdDict ForHangar(GdDict layout, long seedValue = 0)
        {
            GdArray cells = RoomFloorCells(layout, "hangar", "hangar");
            if (cells.IsEmpty)
                cells = RoomFloorCells(layout, "cargo", "cargo");
            if (cells.IsEmpty)
                return new GdDict();
            long slotCount = Math.Max(1L, cells.Count / CELLS_PER_SLOT);
            var slotAnchors = new GdArray();
            for (long i = 0; i < slotCount; i++)
                slotAnchors.Add(cells[(int)((i * CELLS_PER_SLOT) % cells.Count)]);
            long slotSizeClass = cells.Count >= HANGAR_BIG_CELL_THRESHOLD ? 2 : 1;
            return new GdDict
            {
                { "type", "hangar" },
                { "slot_count", slotCount },
                { "slot_size_class", slotSizeClass },
                { "slot_anchors", slotAnchors },
            };
        }

        /// <summary>
        /// True iff the two ports can dock. Airlock-to-airlock is symmetric (same type + same size_class).
        /// A hangar bay is asymmetric: it accepts any single ship whose size_class fits a slot (slot availability
        /// is gated separately by HangarBay). Two hangars cannot dock to each other. Missing size fields fail closed.
        /// </summary>
        public static bool PortsCompatible(GdDict a, GdDict b)
        {
            if (a == null || b == null || a.IsEmpty || b.IsEmpty)
                return false;
            bool aHangar = V.Str(a.Get("type", "")) == "hangar";
            bool bHangar = V.Str(b.Get("type", "")) == "hangar";
            if (aHangar || bHangar)
            {
                if (aHangar && bHangar)
                    return false; // a bay cannot be stored inside another bay this cycle
                GdDict bay = aHangar ? a : b;
                GdDict ship = aHangar ? b : a;
                if (!ship.Has("size_class") || !bay.Has("slot_size_class"))
                    return false;
                return V.I64(ship["size_class"]) <= V.I64(bay["slot_size_class"]);
            }
            if (!a.Has("size_class") || !b.Has("size_class"))
                return false;
            if (V.Str(a.Get("type", "")) != V.Str(b.Get("type", "")))
                return false;
            return V.I64(a["size_class"]) == V.I64(b["size_class"]);
        }

        /// <summary>
        /// Deterministic port condition from the derelict's condition tier + seed. Pristine/light tiers are always
        /// intact; wreck tier always broken; the middle tiers (1,2) are split by the derelict's seed.
        /// </summary>
        public static string ConditionFromSeed(long seedValue, long conditionClass)
        {
            if (conditionClass <= 0)
                return "intact";
            if (conditionClass >= 3)
                return "broken";
            var rng = GodotRandom.FromSeed(seedValue);
            double brokenChance = 0.25 * (double)conditionClass; // class1=0.25, class2=0.50
            return rng.Randf() < brokenChance ? "broken" : "intact";
        }

        static bool IsFloorModule(string module) => Array.IndexOf(FLOOR_MODULES, module) >= 0;

        static GdArray Rooms(GdDict layout) => layout?.Get("rooms", new GdArray()) as GdArray ?? new GdArray();

        /// <summary>Reads a placement's world_position (or position) as a Vector3, or null when malformed.</summary>
        static Vec3? PlacementCell(GdDict p)
        {
            string module = V.Str(p.Get("module_id", p.Get("module", "")));
            if (!IsFloorModule(module))
                return null;
            object pos = p.Get("world_position", p.Get("position", null));
            if (!(pos is GdArray arr) || arr.Count < 3)
                return null;
            return new Vec3(V.F64(arr[0]), V.F64(arr[1]), V.F64(arr[2]));
        }

        static bool RoomMatches(GdDict room, string roleMatch, string idPrefix)
        {
            string role = V.Str(room.Get("room_role", ""));
            string rid = V.Str(room.Get("id", ""));
            return role == roleMatch || ShipCompat.BeginsWith(rid, idPrefix);
        }

        /// <summary>
        /// Ship-local floor-cell centers of the first room matching role_match / id_prefix. Returns [] if none.
        /// (Sibling of RoomFloorCenter, which averages them.)
        /// </summary>
        static GdArray RoomFloorCells(GdDict layout, string roleMatch, string idPrefix)
        {
            foreach (object roomV in Rooms(layout))
            {
                if (!(roomV is GdDict room))
                    continue;
                if (!RoomMatches(room, roleMatch, idPrefix))
                    continue;
                var cells = new GdArray();
                if (room.Get("structural_placements", new GdArray()) is GdArray placements)
                {
                    foreach (object pV in placements)
                    {
                        if (!(pV is GdDict p))
                            continue;
                        Vec3? cell = PlacementCell(p);
                        if (cell.HasValue)
                            cells.Add(cell.Value);
                    }
                }
                if (!cells.IsEmpty)
                    return cells;
            }
            return new GdArray();
        }

        /// <summary>
        /// Average world_position of floor placements in the first room whose room_role == role_match OR whose id
        /// begins with id_prefix. Returns <see cref="Vec3.Inf"/> if none.
        /// </summary>
        static Vec3 RoomFloorCenter(GdDict layout, string roleMatch, string idPrefix)
        {
            foreach (object roomV in Rooms(layout))
            {
                if (!(roomV is GdDict room))
                    continue;
                if (!RoomMatches(room, roleMatch, idPrefix))
                    continue;
                Vec3 sum = Vec3.Zero;
                long count = 0;
                if (room.Get("structural_placements", new GdArray()) is GdArray placements)
                {
                    foreach (object pV in placements)
                    {
                        if (!(pV is GdDict p))
                            continue;
                        Vec3? cell = PlacementCell(p);
                        if (!cell.HasValue)
                            continue;
                        sum += cell.Value;
                        count += 1;
                    }
                }
                if (count > 0)
                    return sum / (float)count;
            }
            return Vec3.Inf;
        }
    }
}
