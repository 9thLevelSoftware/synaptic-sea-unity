// Unity port (no Godot source). Godot put the home crafting and production stations on the first nodes of the home
// ShipStructure (playable_generated_ship.gd _home_local_station_positions): the airlock and corridor floor-cell centres,
// so the recipe picker claimed interact next to the spawn point, ahead of objectives and pickups.
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// Places home stations by room role. Each station kind has preferred room roles; a station goes on the first free spot
    /// of the first preferred room that has one, else on any room that is not a passage, else on the legacy positions. A
    /// spot is free when its interaction radius does not overlap any occupied radius (existing interactables, the player
    /// start, stations already placed) and it is not on a doorway (portal) cell. Deterministic: rooms, cells and spots are
    /// visited in layout order. Positions are ship-local (the Godot frame, like every Core position).
    /// </summary>
    public static class StationPlacer
    {
        /// <summary>Station kind to preferred room roles, best first.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> PREFERRED_ROOM_ROLES = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "fabricator", new[] { "maintenance", "engineering", "tool_storage", "storage", "cargo" } },
            { "workbench", new[] { "maintenance", "tool_storage", "engineering", "storage", "cargo" } },
            { "salvage", new[] { "cargo", "storage", "hangar", "maintenance" } },
            { "medbay", new[] { "medical", "crew_quarters", "quarters" } },
            { "kitchen", new[] { "crew_quarters", "quarters", "storage", "cargo" } },
            { "synthesizer", new[] { "medical", "reactor", "engineering", "maintenance" } },
            { "hydroponics", new[] { "storage", "cargo", "crew_quarters", "quarters", "compartment" } },
            { "water_recycler", new[] { "maintenance", "reactor", "engineering", "storage" } },
        };

        /// <summary>Room roles that are passages or entries: stations never go there, except through the legacy fallback.</summary>
        public static readonly HashSet<string> PASSAGE_ROLES = new HashSet<string>(StringComparer.Ordinal)
        {
            "airlock", "corridor", "main_spine", "main_hub", "ramp", "elevator_shaft", "dock",
        };

        /// <summary>Spot offsets inside a 4 m floor cell, tried in order: the centre, then the four inner corners.</summary>
        static readonly Vec3[] CELL_SPOTS =
        {
            new Vec3(0.0f, 0.0f, 0.0f),
            new Vec3(-1.2f, 0.0f, -1.2f),
            new Vec3(1.2f, 0.0f, -1.2f),
            new Vec3(-1.2f, 0.0f, 1.2f),
            new Vec3(1.2f, 0.0f, 1.2f),
        };

        const double DEFAULT_CELL_SIZE = 4.0;

        /// <summary>An interaction area already in the ship: a position and the radius it claims interact in.</summary>
        public readonly struct Occupied
        {
            public readonly Vec3 Position;
            public readonly double Radius;

            public Occupied(Vec3 position, double radius)
            {
                Position = position;
                Radius = radius;
            }
        }

        public sealed class Placement
        {
            public string Kind = "";
            public Vec3 LocalPosition;
            /// <summary>The room the station stands in; "" for a legacy fallback position.</summary>
            public string RoomId = "";
        }

        /// <summary>
        /// Places <paramref name="kinds"/> in order. <paramref name="occupied"/> is extended with every placed station.
        /// <paramref name="fallbackPositions"/> (already at standing height) are the legacy positions, used when no room
        /// spot is free; when none of them is free either, the legacy <c>positions[i % count]</c> choice is kept.
        /// </summary>
        public static List<Placement> Place(GdDict layout, IReadOnlyList<string> kinds, List<Occupied> occupied,
            IReadOnlyList<Vec3> fallbackPositions, double stationRadius, double spawnHeight)
        {
            List<RoomSpots> rooms = RoomSpotsFor(layout, spawnHeight);
            var output = new List<Placement>();
            for (int i = 0; i < kinds.Count; i++)
            {
                string kind = kinds[i];
                Placement placement = PlaceInPreferredRoom(kind, rooms, occupied, stationRadius)
                    ?? PlaceInAnyRoom(kind, rooms, occupied, stationRadius)
                    ?? PlaceOnFallback(kind, i, fallbackPositions, occupied, stationRadius);
                if (placement == null)
                    continue;
                occupied.Add(new Occupied(placement.LocalPosition, stationRadius));
                output.Add(placement);
            }
            return output;
        }

        sealed class RoomSpots
        {
            public string RoomId = "";
            public string Role = "";
            public readonly List<Vec3> Spots = new List<Vec3>();
        }

        static Placement PlaceInPreferredRoom(string kind, List<RoomSpots> rooms, List<Occupied> occupied, double radius)
        {
            if (!PREFERRED_ROOM_ROLES.TryGetValue(kind, out string[] roles))
                return null;
            foreach (string role in roles)
            {
                foreach (RoomSpots room in rooms)
                {
                    if (room.Role != role)
                        continue;
                    Placement p = FirstFreeSpot(kind, room, occupied, radius);
                    if (p != null)
                        return p;
                }
            }
            return null;
        }

        static Placement PlaceInAnyRoom(string kind, List<RoomSpots> rooms, List<Occupied> occupied, double radius)
        {
            foreach (RoomSpots room in rooms)
            {
                if (PASSAGE_ROLES.Contains(room.Role))
                    continue;
                Placement p = FirstFreeSpot(kind, room, occupied, radius);
                if (p != null)
                    return p;
            }
            return null;
        }

        static Placement PlaceOnFallback(string kind, int index, IReadOnlyList<Vec3> fallbackPositions, List<Occupied> occupied, double radius)
        {
            if (fallbackPositions == null || fallbackPositions.Count == 0)
                return null;
            foreach (Vec3 pos in fallbackPositions)
            {
                if (IsFree(pos, occupied, radius))
                    return new Placement { Kind = kind, LocalPosition = pos };
            }
            return new Placement { Kind = kind, LocalPosition = fallbackPositions[index % fallbackPositions.Count] };
        }

        static Placement FirstFreeSpot(string kind, RoomSpots room, List<Occupied> occupied, double radius)
        {
            foreach (Vec3 spot in room.Spots)
            {
                if (IsFree(spot, occupied, radius))
                    return new Placement { Kind = kind, LocalPosition = spot, RoomId = room.RoomId };
            }
            return null;
        }

        /// <summary>True when an interaction area of <paramref name="radius"/> at <paramref name="pos"/> overlaps none of <paramref name="occupied"/>.</summary>
        public static bool IsFree(Vec3 pos, IReadOnlyList<Occupied> occupied, double radius)
        {
            foreach (Occupied o in occupied)
            {
                if (pos.DistanceTo(o.Position) < radius + o.Radius)
                    return false;
            }
            return true;
        }

        /// <summary>Every room's candidate spots at standing height, skipping doorway (portal) cells.</summary>
        static List<RoomSpots> RoomSpotsFor(GdDict layout, double spawnHeight)
        {
            var output = new List<RoomSpots>();
            if (layout == null)
                return output;
            double cellSize = V.F64(layout.Get("cell_size", DEFAULT_CELL_SIZE));
            if (cellSize <= 0.0)
                cellSize = DEFAULT_CELL_SIZE;
            HashSet<string> portalCells = PortalCells(layout);
            if (!(layout.Get("rooms", new GdArray()) is GdArray rooms))
                return output;
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room))
                    continue;
                var entry = new RoomSpots { RoomId = V.Str(room.Get("id", "")), Role = V.Str(room.Get("room_role", "")) };
                if (room.Get("structural_placements", new GdArray()) is GdArray placements)
                {
                    foreach (object pV in placements)
                    {
                        if (!(pV is GdDict p) || !IsFloorModule(V.Str(p.Get("module", ""))))
                            continue;
                        if (!(p.Get("world_position", null) is GdArray wp) || wp.Count < 3)
                            continue;
                        var center = new Vec3(V.F64(wp[0]), V.F64(wp[1]) + spawnHeight, V.F64(wp[2]));
                        string cellKey = (long)GdMath.Round(V.F64(wp[0]) / cellSize) + "," + (long)GdMath.Round(V.F64(wp[2]) / cellSize);
                        if (portalCells.Contains(cellKey))
                            continue;
                        foreach (Vec3 offset in CELL_SPOTS)
                            entry.Spots.Add(center + offset);
                    }
                }
                output.Add(entry);
            }
            return output;
        }

        static bool IsFloorModule(string module) => module == "floor_1x1" || module == "corridor_floor_1x1";

        static HashSet<string> PortalCells(GdDict layout)
        {
            var cells = new HashSet<string>(StringComparer.Ordinal);
            if (!(layout.Get("portals", new GdArray()) is GdArray portals))
                return cells;
            foreach (object portalV in portals)
            {
                if (!(portalV is GdDict portal))
                    continue;
                foreach (string key in new[] { "from_cell", "to_cell" })
                {
                    if (portal.Get(key, null) is GdArray c && c.Count >= 2)
                        cells.Add(V.I64(c[0]) + "," + V.I64(c[1]));
                }
            }
            return cells;
        }
    }
}
