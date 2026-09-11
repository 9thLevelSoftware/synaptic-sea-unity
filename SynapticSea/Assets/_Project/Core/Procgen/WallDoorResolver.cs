// Ported from scripts/procgen/wall_door_resolver.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Compatibility facade for callers that still provide the pre-canonical cell grid. StructuralEdgeCompiler is
    /// the only wall/portal boundary authority; this class only adapts its validated records to the legacy
    /// per-room geometry shape (<c>{room_id: {wall_segments, portals, interior_zones}}</c>).
    /// </summary>
    public sealed class WallDoorResolver
    {
        public GdDict Resolve(GdDict cellGrid, IList<GdDict> roomPlan)
        {
            GdDict layout = LegacyCellGridToLayout(cellGrid, roomPlan);
            GdDict plan = new StructuralEdgeCompiler().Compile(layout);
            GdDict verdict = new StructuralPlanValidator().Validate(plan, layout);
            if (!V.Bool(verdict.Get("ok", false)))
            {
                CoreServices.Log.Error("WALL DOOR RESOLVER FAIL structural plan validation failed: " + V.Str(verdict.Get("errors", new GdArray())));
                return new GdDict();
            }
            return AdaptValidatedPlanToLegacyGeometry(plan, layout);
        }

        static GdDict LegacyCellGridToLayout(GdDict cellGrid, IList<GdDict> roomPlan)
        {
            var roomRoles = new GdDict();
            foreach (var room in roomPlan)
                roomRoles[V.Str(room.Get("id", ""))] = V.Str(room.Get("role", room.Get("room_role", "")));

            var canonicalRooms = new GdArray();
            var roomDecks = new GdDict();
            if (!(cellGrid.Get("rooms", null) is GdDict rooms))
                return new GdDict { { "rooms", canonicalRooms }, { "portals", new GdArray() } };
            foreach (var kv in rooms)
            {
                string roomId = V.Str(kv.Key);
                if (!(kv.Value is GdDict room))
                {
                    canonicalRooms.Append(new GdDict { { "id", roomId }, { "deck", -1L }, { "cells", new GdArray() } });
                    continue;
                }
                object cellsVariant = room.Get("cells", null);
                GdArray cells = cellsVariant is GdArray ca ? ca.DeepCopy() : GdArray.Of(new object[] { cellsVariant });
                object deckValue = room.Get("deck", 0L);
                roomDecks[roomId] = deckValue;
                canonicalRooms.Append(new GdDict
                {
                    { "id", roomId },
                    { "room_role", V.Str(roomRoles.Get(roomId, room.Get("role", ""))) },
                    { "deck", deckValue },
                    { "cells", cells },
                    { "footprint", room.Get("footprint", new GdArray()) },
                });
            }

            var portals = new GdArray();
            var verticalConnections = new GdArray();
            if (cellGrid.Get("adjacencies", new GdArray()) is GdArray adjacencies)
            {
                foreach (var adjacencyVariant in adjacencies)
                {
                    if (!(adjacencyVariant is GdDict adjacency))
                    {
                        portals.Append(adjacencyVariant);
                        continue;
                    }
                    string fromRoom = V.Str(adjacency.Get("from_room", ""));
                    string toRoom = V.Str(adjacency.Get("to_room", ""));
                    var portal = new GdDict
                    {
                        { "id", V.Str(adjacency.Get("id", fromRoom + "_to_" + toRoom)) },
                        { "from_room", fromRoom },
                        { "to_room", toRoom },
                        { "from_cell", adjacency.Get("from_cell", null) },
                        { "to_cell", adjacency.Get("to_cell", null) },
                        { "module_id", V.Str(adjacency.Get("module_id", "bulkhead_portal_2x1")) },
                        { "state", V.Str(adjacency.Get("state", adjacency.Get("portal_type", "DOOR"))).ToUpperInvariant() },
                    };
                    if (adjacency.Has("exterior")) portal["exterior"] = adjacency["exterior"];
                    object fromDeck = roomDecks.Get(fromRoom, null);
                    object toDeck = roomDecks.Get(toRoom, null);
                    if (fromDeck != null && toDeck != null && StructuralEdgeCompiler.IsInteger(fromDeck) &&
                        StructuralEdgeCompiler.IsInteger(toDeck) && V.I64(fromDeck) != V.I64(toDeck))
                    {
                        verticalConnections.Append(new GdDict
                        {
                            { "id", portal["id"] },
                            { "from_room", fromRoom },
                            { "to_room", toRoom },
                            { "from_cell", portal["from_cell"] },
                            { "to_cell", portal["to_cell"] },
                        });
                    }
                    else
                    {
                        portals.Append(portal);
                    }
                }
            }

            return new GdDict
            {
                { "cell_size", 4.0 },
                { "rooms", canonicalRooms },
                { "portals", portals },
                { "vertical_connections", verticalConnections },
            };
        }

        sealed class RoomState
        {
            public readonly GdArray Walls = new GdArray();
            public readonly GdArray Portals = new GdArray();
            public readonly GdArray Reserved = new GdArray();
        }

        static GdDict AdaptValidatedPlanToLegacyGeometry(GdDict plan, GdDict layout)
        {
            var geometry = new GdDict();
            var roomCells = new Dictionary<string, GdArray>();
            var roomData = new GdDict(); // room_id -> index into states (insertion order)
            var states = new Dictionary<string, RoomState>();
            foreach (var roomVariant in layout.GetArrayOrEmpty("rooms"))
            {
                if (!(roomVariant is GdDict room)) continue;
                string roomId = V.Str(room.Get("id", ""));
                roomCells[roomId] = new GdArray();
                roomData[roomId] = true;
                states[roomId] = new RoomState();
                foreach (var cellVariant in room.GetArrayOrEmpty("cells"))
                {
                    if (LegacyCell(cellVariant, out Vec2i cell)) roomCells[roomId].Append(cell);
                }
            }

            foreach (var occupancyVariant in plan.GetDictOrEmpty("occupancy").Values)
            {
                if (!(occupancyVariant is GdDict record)) continue;
                string ownerRoom = V.Str(record.Get("room_id", ""));
                if (!roomData.Has(ownerRoom)) continue;
                if (LegacyCell(record.Get("cell", null), out Vec2i cell) && !roomCells[ownerRoom].Contains(cell))
                    roomCells[ownerRoom].Append(cell);
            }

            foreach (var edgeVariant in plan.GetDictOrEmpty("edges").Values)
            {
                if (!(edgeVariant is GdDict edge)) continue;
                string ownerRoom = V.Str(edge.Get("owner_room", ""));
                if (!roomData.Has(ownerRoom)) continue;
                string kind = V.Str(edge.Get("kind", edge.Get("state", "SOLID")));
                if (kind == "SOLID")
                {
                    states[ownerRoom].Walls.Append(LegacyWall(edge, ownerRoom));
                }
                else if (V.Bool(edge.Get("portal", false)))
                {
                    GdDict portal = LegacyPortal(edge, ownerRoom);
                    states[ownerRoom].Portals.Append(portal);
                    object portalCell = portal.Get("from_cell", null);
                    if (portalCell is Vec2i && !states[ownerRoom].Reserved.Contains(portalCell))
                        states[ownerRoom].Reserved.Append(portalCell);
                }
            }

            foreach (var roomIdVariant in roomData.Keys)
            {
                string roomId = V.Str(roomIdVariant);
                RoomState state = states[roomId];
                var wallSlotCells = new GdArray();
                var centerCells = new GdArray();
                foreach (var cellVariant in roomCells.TryGetValue(roomId, out GdArray cells) ? cells : new GdArray())
                {
                    bool hasWall = HasCellRecord(state.Walls, cellVariant);
                    bool hasPortal = HasCellRecord(state.Portals, cellVariant, "from_cell");
                    if (hasWall && !hasPortal) wallSlotCells.Append(new GdDict { { "cell", cellVariant }, { "against_wall", true } });
                    else if (!hasWall && !hasPortal) centerCells.Append(cellVariant);
                }
                geometry[roomId] = new GdDict
                {
                    { "wall_segments", state.Walls },
                    { "portals", state.Portals },
                    {
                        "interior_zones", new GdDict
                        {
                            { "reserved_cells", state.Reserved },
                            { "wall_slots", wallSlotCells },
                            { "center_slots", centerCells },
                        }
                    },
                };
            }
            return geometry;
        }

        static GdDict LegacyWall(GdDict edge, string roomId)
        {
            Vec2i cell = edge.Get("cell") is Vec2i c ? c : Vec2i.Zero;
            string direction = V.Str(edge.Get("direction", ""));
            return new GdDict
            {
                { "name", "wall_" + roomId + "_" + direction + "_x" + GdString.FormatInt(cell.X) + "_z" + GdString.FormatInt(cell.Y) },
                { "module_id", V.Str(edge.Get("module_id", "wall_straight_1x1")) },
                { "position", edge.Get("position", Vec3.Zero) },
                { "yaw_degrees", V.F64(edge.Get("yaw_degrees", 0.0)) },
                { "cell", cell },
                { "direction", direction },
            };
        }

        static GdDict LegacyPortal(GdDict edge, string ownerRoom)
        {
            GdArray sourceCells = edge.Get("source_cells", new GdArray()) as GdArray ?? new GdArray();
            Vec2i fromCell = edge.Get("cell") is Vec2i c ? c : Vec2i.Zero;
            Vec2i toCell = Vec2i.Zero;
            if (sourceCells.Count >= 2)
            {
                if (LegacyCell(sourceCells[0], out Vec2i first)) fromCell = first;
                if (LegacyCell(sourceCells[1], out Vec2i second)) toCell = second;
            }
            return new GdDict
            {
                { "id", V.Str(edge.Get("id", "edge:" + V.Str(edge.Get("edge_key", "")))) },
                { "wall", V.Str(edge.Get("direction", "")) },
                { "module_id", V.Str(edge.Get("module_id", "bulkhead_portal_2x1")) },
                { "position", edge.Get("position", Vec3.Zero) },
                { "yaw_degrees", V.F64(edge.Get("yaw_degrees", 0.0)) },
                { "to_room", V.Str(edge.Get("other_room", "")) },
                { "from_room", ownerRoom },
                { "from_cell", fromCell },
                { "to_cell", toCell },
                { "edge_key", V.Str(edge.Get("edge_key", "")) },
            };
        }

        static bool HasCellRecord(GdArray records, object cell, string cellKey = "cell")
        {
            foreach (var recordVariant in records)
            {
                if (!(recordVariant is GdDict record)) continue;
                if (V.VariantEquals(record.Get(cellKey, null), cell)) return true;
            }
            return false;
        }

        static bool LegacyCell(object value, out Vec2i cell)
        {
            cell = Vec2i.Zero;
            if (value is Vec2i v)
            {
                cell = v;
                return true;
            }
            if (!(value is GdArray values)) return false;
            if (values.Count < 2 || !StructuralEdgeCompiler.IsInteger(values[0]) || !StructuralEdgeCompiler.IsInteger(values[1])) return false;
            cell = new Vec2i(V.I32(values[0]), V.I32(values[1]));
            return true;
        }
    }
}
