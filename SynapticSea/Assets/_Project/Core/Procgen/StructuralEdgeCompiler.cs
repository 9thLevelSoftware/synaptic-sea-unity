// Ported from scripts/procgen/structural_edge_compiler.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Data-only compiler for the canonical ship boundary plan. Rooms own explicit integer occupancy cells. Floors
    /// are emitted once per occupied cell in <c>floor_placements</c>; walls and portals once per canonical shared
    /// edge in <c>placements</c>. Ceilings and socket_bindings live inside the same plan. Module ids are chosen by
    /// ModularAssetSpec socket match (<see cref="ModularSocketCatalog"/>). No scene or wrapper lookup belongs here.
    /// Positions are <see cref="Vec3"/> (Godot frame), cells <see cref="Vec2i"/>.
    /// </summary>
    public sealed class StructuralEdgeCompiler
    {
        public const double CELL_SIZE = 4.0;
        public const double DECK_HEIGHT = 4.0;
        public const string FLOOR_MODULE = "floor_1x1";
        public const string CORRIDOR_FLOOR_MODULE = "corridor_floor_1x1";
        public const string CEILING_MODULE = "ceiling_cap_1x1";
        public const string WALL_MODULE = "wall_straight_1x1";
        public const string WALL_END_CAP_MODULE = "wall_end_cap";
        public const string WALL_INNER_CORNER_MODULE = "wall_inner_corner";
        public const string WALL_OUTER_CORNER_MODULE = "wall_outer_corner";
        public const string WALL_T_JUNCTION_MODULE = "wall_t_junction";
        public const string DEFAULT_PORTAL_MODULE = "doorway_frame_open_1x1";
        public const string DOOR_MODULE = "doorway_frame_open_1x1";
        public const string LOCKED_MODULE = "doorway_frame_blocked_1x1";
        public const string HATCH_MODULE = "bulkhead_portal_2x1";
        public const string INNER_CORNER_MODULE = "wall_inner_corner";
        public const string OUTER_CORNER_MODULE = "wall_outer_corner";
        public const string T_JUNCTION_MODULE = "wall_t_junction";
        public const string DEFAULT_KIT_ID = "ship_structural_v0";

        public static readonly GdDict DIRECTIONS = new GdDict
        {
            { "north", new Vec2i(0, -1) },
            { "east", new Vec2i(1, 0) },
            { "south", new Vec2i(0, 1) },
            { "west", new Vec2i(-1, 0) },
        };

        public static readonly GdDict OPPOSITE = new GdDict
        {
            { "north", "south" },
            { "east", "west" },
            { "south", "north" },
            { "west", "east" },
        };

        public static readonly GdDict YAW_DEGREES = new GdDict
        {
            { "south", 0.0 },
            { "west", 90.0 },
            { "north", 180.0 },
            { "east", 270.0 },
        };

        public static readonly IReadOnlyList<string> CARDINALS = new[] { "north", "east", "south", "west" };
        public static readonly IReadOnlyList<string> SUPPORTED_EDGE_KINDS = new[] { "SOLID", "OPEN", "DOOR", "LOCKED", "HATCH", "BREACH" };

        static readonly Vec2i CellSentinel = new Vec2i(-99999, -99999);

        /// <summary>GDScript <c>_read_cell()</c> result (<c>{"ok", "cell", "deck"}</c>).</summary>
        internal struct CellInfo
        {
            public bool Ok;
            public Vec2i Cell;
            public long Deck;

            public static readonly CellInfo Fail = new CellInfo { Ok = false };
        }

        /// <summary>
        /// Compiles a layout.json-shaped dictionary into
        /// <c>{occupancy, edges, placements, floor_placements, ceiling_placements, socket_bindings, errors}</c>.
        /// <c>occupancy</c> / <c>edges</c> are keyed dictionaries in insertion order.
        /// </summary>
        public GdDict Compile(GdDict layout)
        {
            var occupancy = new GdDict();
            var roomByCell = new GdDict();
            var roomById = new GdDict();
            var roomRoleById = new GdDict();
            var errors = new List<string>();

            object roomsVariant = layout.Get("rooms", null);
            if (!(roomsVariant is GdArray rooms)) return EmptyPlan(new List<string> { "layout rooms must be an array" });
            if (rooms.IsEmpty) return EmptyPlan(new List<string> { "layout rooms must be non-empty" });

            var catalog = new ModularSocketCatalog();
            string kitId = V.Str(layout.Get("kit_id", DEFAULT_KIT_ID));
            if (kitId.Length == 0) kitId = DEFAULT_KIT_ID;
            if (!catalog.LoadKit(kitId)) errors.Add("structural kit contracts missing: " + kitId);

            string floorModuleDefault = catalog.ChooseModule(new[] { "floor_edge", "floor_top" }, FLOOR_MODULE);
            if (floorModuleDefault.Length == 0) floorModuleDefault = FLOOR_MODULE;
            string corridorFloorModule = catalog.ChooseModule(new[] { "floor_edge", "floor_top" }, CORRIDOR_FLOOR_MODULE);
            if (corridorFloorModule.Length == 0) corridorFloorModule = CORRIDOR_FLOOR_MODULE;
            string ceilingModule = catalog.ChooseModule(new[] { "ceiling_edge", "ceiling_bottom" }, CEILING_MODULE);
            if (ceilingModule.Length == 0) ceilingModule = CEILING_MODULE;
            string wallModule = catalog.ChooseModule(new[] { "wall_base", "wall_end" }, WALL_MODULE);
            if (wallModule.Length == 0) wallModule = WALL_MODULE;
            string innerCornerModule = catalog.ChooseModule(new[] { "inner_corner_vertex" }, INNER_CORNER_MODULE);
            if (innerCornerModule.Length == 0) innerCornerModule = INNER_CORNER_MODULE;
            string outerCornerModule = catalog.ChooseModule(new[] { "outer_corner_vertex" }, OUTER_CORNER_MODULE);
            if (outerCornerModule.Length == 0) outerCornerModule = OUTER_CORNER_MODULE;
            string tJunctionModule = catalog.HasModule(T_JUNCTION_MODULE)
                ? T_JUNCTION_MODULE
                : catalog.ChooseModule(new[] { "wall_face" }, T_JUNCTION_MODULE);

            foreach (var roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room))
                {
                    errors.Add("room record must be an object");
                    continue;
                }
                string roomId = V.Str(room.Get("id", ""));
                if (roomId.Length == 0)
                {
                    errors.Add("room is missing id");
                    continue;
                }
                if (roomById.Has(roomId))
                {
                    errors.Add("duplicate room id: " + roomId);
                    continue;
                }
                if (!IsInteger(room.Get("deck", null)))
                {
                    errors.Add("room " + roomId + " has invalid deck");
                    continue;
                }
                long deck = V.I64(room.Get("deck"));
                roomById[roomId] = room;
                roomRoleById[roomId] = V.Str(room.Get("room_role", room.Get("role", "")));
                object cellsVariant = room.Get("cells", null);
                if (!(cellsVariant is GdArray cells) || cells.IsEmpty)
                {
                    errors.Add("room " + roomId + " must declare non-empty cells");
                    continue;
                }
                foreach (var rawCell in cells)
                {
                    CellInfo cellInfo = ReadCell(rawCell, deck);
                    if (!cellInfo.Ok)
                    {
                        errors.Add("room " + roomId + " has invalid cell: " + V.Str(rawCell));
                        continue;
                    }
                    if (cellInfo.Deck != deck)
                    {
                        errors.Add("room " + roomId + " cell deck mismatch");
                        continue;
                    }
                    Vec2i cell = cellInfo.Cell;
                    string key = CellKey(deck, cell);
                    if (roomByCell.Has(key))
                    {
                        errors.Add("occupied-cell overlap: " + key + " owned by " + V.Str(roomByCell[key]) + " and " + roomId);
                        continue;
                    }
                    roomByCell[key] = roomId;
                    string roleName = V.Str(roomRoleById[roomId]);
                    string occupancyFloorModule = roleName == "corridor" || roleName == "main_spine" ? corridorFloorModule : floorModuleDefault;
                    occupancy[key] = new GdDict
                    {
                        { "cell_key", key },
                        { "deck", deck },
                        { "cell", cell },
                        { "room_id", roomId },
                        { "room_ids", GdArray.Of(roomId) },
                        { "position", CellWorldPosition(deck, cell) },
                        { "module_id", occupancyFloorModule },
                    };
                }
            }

            GdDict portalByEdge = IndexPortals(layout, roomById, roomByCell, errors);
            GdDict openingKeys = VerticalOpeningKeys(layout, roomById);
            var edgeMap = new GdDict();
            var edgePlacements = new GdArray();
            var floorPlacements = new GdArray();
            var ceilingPlacements = new GdArray();

            foreach (var occupancyKeyVariant in occupancy.Keys)
            {
                string occupancyKey = V.Str(occupancyKeyVariant);
                var cellRecord = (GdDict)occupancy[occupancyKey];
                long deck = V.I64(cellRecord["deck"]);
                var cell = (Vec2i)cellRecord["cell"];
                string roomId = V.Str(cellRecord["room_id"]);
                string role = V.Str(roomRoleById.Get(roomId, ""));
                string floorModule = role == "corridor" || role == "main_spine" ? corridorFloorModule : floorModuleDefault;
                floorPlacements.Append(new GdDict
                {
                    { "id", "floor:" + occupancyKey },
                    { "placement_id", "floor:" + occupancyKey },
                    { "module_id", floorModule },
                    { "position", CellWorldPosition(deck, cell) },
                    { "yaw_degrees", 0.0 },
                    { "deck", deck },
                    { "cell", cell },
                    { "cell_key", occupancyKey },
                    { "room_id", roomId },
                    { "room_ids", GdArray.Of(roomId) },
                });
                if (!openingKeys.Has(occupancyKey))
                {
                    ceilingPlacements.Append(new GdDict
                    {
                        { "id", "ceiling:" + occupancyKey },
                        { "placement_id", "ceiling:" + occupancyKey },
                        { "module_id", ceilingModule },
                        { "position", CellWorldPosition(deck, cell) },
                        { "yaw_degrees", 0.0 },
                        { "deck", deck },
                        { "cell", cell },
                        { "cell_key", occupancyKey },
                        { "room_id", roomId },
                        { "room_ids", GdArray.Of(roomId) },
                    });
                }

                foreach (string direction in CARDINALS)
                {
                    string edgeKeyValue = EdgeKey(deck, cell, direction);
                    if (edgeMap.Has(edgeKeyValue)) continue;
                    var delta = (Vec2i)DIRECTIONS[direction];
                    Vec2i neighbor = cell + delta;
                    string neighborKey = CellKey(deck, neighbor);
                    string otherRoom = V.Str(roomByCell.Get(neighborKey, ""));
                    object portalVariant = portalByEdge.Get(edgeKeyValue, null);
                    GdDict portal = portalVariant as GdDict ?? new GdDict();
                    string edgeState = "SOLID";
                    string moduleId = wallModule;
                    bool portalPresent = !portal.IsEmpty;
                    string edgeOtherRoom = otherRoom;
                    if (portalPresent && edgeOtherRoom.Length == 0)
                        edgeOtherRoom = V.Str(portal.Get("edge_other_room", ""));
                    bool wrapperRequired = true;
                    if (edgeOtherRoom.Length != 0 && edgeOtherRoom == roomId && !portalPresent)
                    {
                        edgeState = "OPEN";
                        moduleId = "";
                        wrapperRequired = false;
                    }
                    else if (portalPresent)
                    {
                        edgeState = PortalKind(portal, layout);
                        moduleId = PortalModuleFromCatalog(catalog, portal, edgeState);
                        wrapperRequired = edgeState != "BREACH" || moduleId.Length != 0;
                        if (edgeOtherRoom.Length == 0 && !V.Bool(portal.Get("exterior", false)))
                            errors.Add("portal endpoint is exterior without explicit exterior flag: " + edgeKeyValue);
                    }
                    else if (edgeOtherRoom.Length != 0 && edgeOtherRoom != roomId)
                    {
                        edgeState = "SOLID";
                        moduleId = wallModule;
                    }
                    else
                    {
                        edgeState = "SOLID";
                        moduleId = wallModule;
                    }

                    if (!Contains(SUPPORTED_EDGE_KINDS, edgeState))
                    {
                        errors.Add("unsupported edge kind " + edgeState + " at " + edgeKeyValue);
                        edgeState = "SOLID";
                        moduleId = wallModule;
                    }
                    var sourceCells = GdArray.Of(CellWithDeck(cell, deck), CellWithDeck(neighbor, deck));
                    var roomIds = GdArray.Of(roomId, edgeOtherRoom);
                    Vec3 edgePosition = EdgeWorldPosition(deck, cell, direction);
                    var edgeRecord = new GdDict
                    {
                        { "id", "edge:" + edgeKeyValue },
                        { "key", edgeKeyValue },
                        { "edge_key", edgeKeyValue },
                        { "deck", deck },
                        { "cell", cell },
                        { "direction", direction },
                        { "opposite_direction", V.Str(OPPOSITE[direction]) },
                        { "source_cells", sourceCells },
                        { "room_ids", roomIds },
                        { "owner_room", roomId },
                        { "other_room", edgeOtherRoom },
                        { "kind", edgeState },
                        { "state", edgeState },
                        { "module_id", moduleId },
                        { "position", edgePosition },
                        { "yaw_degrees", V.F64(YAW_DEGREES[direction]) },
                        { "portal", portalPresent },
                        { "exterior", edgeOtherRoom.Length == 0 },
                        { "placement_required", wrapperRequired },
                        { "wrapper_required", wrapperRequired },
                    };
                    if (portalPresent)
                    {
                        bool logicalBoundary = V.Bool(portal.Get("logical_boundary", false));
                        edgeRecord["logical_boundary"] = logicalBoundary;
                        if (logicalBoundary)
                        {
                            edgeRecord["logical_from_cell"] = portal.Get("logical_from_cell", portal.Get("from_cell", null));
                            edgeRecord["logical_to_cell"] = portal.Get("logical_to_cell", portal.Get("to_cell", null));
                        }
                    }
                    edgeMap[edgeKeyValue] = edgeRecord;
                }
            }

            ApplyVertexModules(occupancy, edgeMap, catalog, innerCornerModule, outerCornerModule, tJunctionModule, wallModule);

            // GDScript scans edge_placements for an existing edge_key; a set of emitted keys is equivalent.
            var emittedEdgeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var occupancyKeyVariant in occupancy.Keys)
            {
                var cellRecord = (GdDict)occupancy[occupancyKeyVariant];
                long deck = V.I64(cellRecord["deck"]);
                var cell = (Vec2i)cellRecord["cell"];
                foreach (string direction in CARDINALS)
                {
                    string edgeKeyValue = EdgeKey(deck, cell, direction);
                    if (!edgeMap.Has(edgeKeyValue)) continue;
                    var edgeRecord = (GdDict)edgeMap[edgeKeyValue];
                    if (V.Str(edgeRecord.Get("kind", "")) == "OPEN" || !V.Bool(edgeRecord.Get("wrapper_required", true))) continue;
                    if (emittedEdgeKeys.Contains(edgeKeyValue)) continue;
                    emittedEdgeKeys.Add(edgeKeyValue);
                    GdDict placement = edgeRecord.DeepCopy();
                    placement["placement_id"] = "edge:" + edgeKeyValue;
                    edgePlacements.Append(placement);
                }
            }

            RefineWallModules(edgeMap, edgePlacements);

            GdArray socketBindings = EmitSocketBindings(catalog, floorPlacements, ceilingPlacements, edgePlacements);
            AttachBindingsToRecords(floorPlacements, socketBindings);
            AttachBindingsToRecords(ceilingPlacements, socketBindings);
            AttachBindingsToRecords(edgePlacements, socketBindings);

            return new GdDict
            {
                { "occupancy", occupancy },
                { "edges", edgeMap },
                { "placements", edgePlacements },
                { "floor_placements", floorPlacements },
                { "ceiling_placements", ceilingPlacements },
                { "socket_bindings", socketBindings },
                { "errors", GdString.ToGdArray(errors) },
            };
        }

        /// <summary>Stable identity of an occupied grid cell: <c>"deck|x|y"</c>.</summary>
        public static string CellKey(long deck, Vec2i cell) =>
            GdString.FormatInt(deck) + "|" + GdString.FormatInt(cell.X) + "|" + GdString.FormatInt(cell.Y);

        /// <summary>Geometry-derived identity for a cardinal cell boundary; "" for an unknown direction.</summary>
        public static string EdgeKey(long deck, Vec2i cell, string direction)
        {
            if (!DIRECTIONS.Has(direction)) return "";
            Vec2i neighbor = cell + (Vec2i)DIRECTIONS[direction];
            if (direction == "north" || direction == "south")
                return GdString.FormatInt(deck) + "|h|" + GdString.FormatInt(Math.Min(cell.Y, neighbor.Y)) + "|" + GdString.FormatInt(cell.X);
            return GdString.FormatInt(deck) + "|v|" + GdString.FormatInt(cell.Y) + "|" + GdString.FormatInt(Math.Min(cell.X, neighbor.X));
        }

        public static Vec3 CellWorldPosition(long deck, Vec2i cell) =>
            new Vec3((double)cell.X * CELL_SIZE, (double)deck * DECK_HEIGHT, (double)cell.Y * CELL_SIZE);

        public static Vec3 EdgeWorldPosition(long deck, Vec2i cell, string direction)
        {
            Vec3 center = CellWorldPosition(deck, cell);
            Vec2i delta = DIRECTIONS.Get(direction) is Vec2i d ? d : Vec2i.Zero;
            return center + new Vec3((double)delta.X * CELL_SIZE * 0.5, 0.0, (double)delta.Y * CELL_SIZE * 0.5);
        }

        public static GdArray CellWithDeck(Vec2i cell, long deck) => GdArray.Of((long)cell.X, (long)cell.Y, deck);

        GdDict EmptyPlan(List<string> errors)
        {
            return new GdDict
            {
                { "occupancy", new GdDict() },
                { "edges", new GdDict() },
                { "placements", new GdArray() },
                { "floor_placements", new GdArray() },
                { "ceiling_placements", new GdArray() },
                { "socket_bindings", new GdArray() },
                { "errors", GdString.ToGdArray(errors) },
            };
        }

        GdDict VerticalOpeningKeys(GdDict layout, GdDict roomById)
        {
            var keys = new GdDict();
            object verticalVariant = layout.Get("vertical_connections", new GdArray());
            if (!(verticalVariant is GdArray vertical)) return keys;
            foreach (var linkVariant in vertical)
            {
                if (!(linkVariant is GdDict link)) continue;
                string fromRoom = V.Str(link.Get("from_room", ""));
                string toRoom = V.Str(link.Get("to_room", ""));
                long fromDeck = -1;
                long toDeck = -1;
                if (roomById.Has(fromRoom)) fromDeck = V.I64(((GdDict)roomById[fromRoom]).Get("deck", -1L));
                if (roomById.Has(toRoom)) toDeck = V.I64(((GdDict)roomById[toRoom]).Get("deck", -1L));
                CellInfo fromInfo = ReadCell(link.Get("from_cell", null), fromDeck);
                CellInfo toInfo = ReadCell(link.Get("to_cell", null), toDeck);
                if (fromInfo.Ok) keys[CellKey(fromInfo.Deck, fromInfo.Cell)] = true;
                if (toInfo.Ok) keys[CellKey(toInfo.Deck, toInfo.Cell)] = true;
            }
            return keys;
        }

        void ApplyVertexModules(
            GdDict occupancy,
            GdDict edgeMap,
            ModularSocketCatalog catalog,
            string innerCornerModule,
            string outerCornerModule,
            string tJunctionModule,
            string wallModule)
        {
            var seenVertices = new HashSet<string>(StringComparer.Ordinal);
            foreach (var occupancyKey in occupancy.Keys)
            {
                var cellRecord = (GdDict)occupancy[occupancyKey];
                long deck = V.I64(cellRecord["deck"]);
                var cell = (Vec2i)cellRecord["cell"];
                for (int dx = 0; dx < 2; dx++)
                {
                    for (int dz = 0; dz < 2; dz++)
                    {
                        int vx = cell.X + dx;
                        int vz = cell.Y + dz;
                        string vertexKey = GdString.FormatInt(deck) + "|" + GdString.FormatInt(vx) + "|" + GdString.FormatInt(vz);
                        if (seenVertices.Contains(vertexKey)) continue;
                        seenVertices.Add(vertexKey);
                        var solidKeys = new List<string>();
                        string[] candidateEdges =
                        {
                            EdgeKey(deck, new Vec2i(vx - 1, vz - 1), "east"),
                            EdgeKey(deck, new Vec2i(vx - 1, vz), "east"),
                            EdgeKey(deck, new Vec2i(vx - 1, vz - 1), "south"),
                            EdgeKey(deck, new Vec2i(vx, vz - 1), "south"),
                        };
                        foreach (string candidate in candidateEdges)
                        {
                            if (candidate.Length == 0 || !edgeMap.Has(candidate)) continue;
                            var edge = (GdDict)edgeMap[candidate];
                            string kind = V.Str(edge.Get("kind", ""));
                            if (kind == "SOLID") solidKeys.Add(candidate);
                        }
                        if (solidKeys.Count == 0) continue;
                        long occupiedCount = 0;
                        foreach (Vec2i cellOffset in new[]
                                 {
                                     new Vec2i(vx - 1, vz - 1), new Vec2i(vx, vz - 1), new Vec2i(vx - 1, vz), new Vec2i(vx, vz),
                                 })
                        {
                            if (occupancy.Has(CellKey(deck, cellOffset))) occupiedCount += 1;
                        }
                        string assignedModule = "";
                        if (solidKeys.Count >= 3 && catalog.HasModule(tJunctionModule))
                            assignedModule = tJunctionModule;
                        else if (occupiedCount == 3 && solidKeys.Count >= 2 && catalog.HasModule(innerCornerModule))
                            assignedModule = innerCornerModule;
                        else if (occupiedCount == 1 && solidKeys.Count >= 2 && catalog.HasModule(outerCornerModule))
                            assignedModule = outerCornerModule;
                        if (assignedModule.Length == 0) continue;
                        string targetKey = FirstReplaceableWall(edgeMap, solidKeys, wallModule);
                        if (targetKey.Length == 0) continue;
                        var target = (GdDict)edgeMap[targetKey];
                        target["module_id"] = assignedModule;
                    }
                }
            }
        }

        static string FirstReplaceableWall(GdDict edgeMap, List<string> solidKeys, string wallModule)
        {
            foreach (string edgeKeyValue in solidKeys)
            {
                var edge = (GdDict)edgeMap[edgeKeyValue];
                if (V.Bool(edge.Get("portal", false))) continue;
                string moduleId = V.Str(edge.Get("module_id", ""));
                if (moduleId == wallModule || moduleId == WALL_MODULE) return edgeKeyValue;
            }
            foreach (string edgeKeyValue in solidKeys)
            {
                var edge = (GdDict)edgeMap[edgeKeyValue];
                if (!V.Bool(edge.Get("portal", false))) return edgeKeyValue;
            }
            return "";
        }

        GdArray EmitSocketBindings(
            ModularSocketCatalog catalog,
            GdArray floorPlacements,
            GdArray ceilingPlacements,
            GdArray edgePlacements)
        {
            var bindings = new GdArray();
            var allRecords = new GdArray();
            allRecords.AppendArray(floorPlacements);
            allRecords.AppendArray(ceilingPlacements);
            allRecords.AppendArray(edgePlacements);
            for (int i = 0; i < allRecords.Count; i++)
            {
                if (!(allRecords[i] is GdDict recordA)) continue;
                string moduleA = V.Str(recordA.Get("module_id", ""));
                Vec3 posA = AsVector3(recordA.Get("position", Vec3.Zero));
                double yawA = V.F64(recordA.Get("yaw_degrees", 0.0));
                for (int j = i + 1; j < allRecords.Count; j++)
                {
                    if (!(allRecords[j] is GdDict recordB)) continue;
                    string moduleB = V.Str(recordB.Get("module_id", ""));
                    Vec3 posB = AsVector3(recordB.Get("position", Vec3.Zero));
                    double yawB = V.F64(recordB.Get("yaw_degrees", 0.0));
                    if ((double)posA.DistanceTo(posB) > CELL_SIZE * 1.5) continue;
                    foreach (var socketAVariant in catalog.SocketsOf(moduleA))
                    {
                        if (!(socketAVariant is GdDict socketA)) continue;
                        foreach (var socketBVariant in catalog.SocketsOf(moduleB))
                        {
                            if (!(socketBVariant is GdDict socketB)) continue;
                            if (!SocketsMatch(catalog, socketA, socketB)) continue;
                            Vec3 worldA = catalog.WorldSocketPosition(posA, yawA, catalog.SocketLocalPosition(socketA));
                            Vec3 worldB = catalog.WorldSocketPosition(posB, yawB, catalog.SocketLocalPosition(socketB));
                            if (!catalog.PositionsAgree(worldA, worldB)) continue;
                            bindings.Append(BindingRecord(recordA, socketA, recordB, socketB));
                            bindings.Append(BindingRecord(recordB, socketB, recordA, socketA));
                        }
                    }
                }
            }
            return bindings;
        }

        /// <summary>A pair matches when each socket's kind is accepted by the other's compatible_kinds.</summary>
        static bool SocketsMatch(ModularSocketCatalog catalog, GdDict socketA, GdDict socketB) =>
            catalog.SocketsCompatible(socketA, socketB);

        static GdDict BindingRecord(GdDict localRecord, GdDict localSocket, GdDict neighborRecord, GdDict neighborSocket)
        {
            return new GdDict
            {
                { "placement_id", V.Str(localRecord.Get("placement_id", localRecord.Get("id", ""))) },
                { "socket_id", V.Str(localSocket.Get("id", "")) },
                { "neighbor_placement_id", V.Str(neighborRecord.Get("placement_id", neighborRecord.Get("id", ""))) },
                { "neighbor_socket_id", V.Str(neighborSocket.Get("id", "")) },
                { "kind", V.Str(localSocket.Get("kind", "")) },
            };
        }

        static void AttachBindingsToRecords(GdArray records, GdArray bindings)
        {
            var byId = new Dictionary<string, GdArray>(StringComparer.Ordinal);
            foreach (var bindingVariant in bindings)
            {
                if (!(bindingVariant is GdDict binding)) continue;
                string placementId = V.Str(binding.Get("placement_id", ""));
                if (placementId.Length == 0) continue;
                if (!byId.TryGetValue(placementId, out GdArray list))
                {
                    list = new GdArray();
                    byId[placementId] = list;
                }
                list.Append(binding);
            }
            foreach (var recordVariant in records)
            {
                if (!(recordVariant is GdDict record)) continue;
                string placementId = V.Str(record.Get("placement_id", record.Get("id", "")));
                record["socket_bindings"] = byId.TryGetValue(placementId, out GdArray found) ? found.DeepCopy() : new GdArray();
            }
        }

        string PortalModuleFromCatalog(ModularSocketCatalog catalog, GdDict portal, string state)
        {
            string declared = V.Str(portal.Get("module_id", ""));
            if (declared.Length != 0 && catalog.HasModule(declared) && catalog.HasKind(declared, "portal_edge")) return declared;
            string preferred = DEFAULT_PORTAL_MODULE;
            if (state == "LOCKED") preferred = LOCKED_MODULE;
            else if (state == "HATCH") preferred = HATCH_MODULE;
            else if (state == "BREACH") return declared;
            string chosen = catalog.ChooseModule(new[] { "portal_edge", "wall_base" }, preferred);
            if (chosen.Length == 0) return PortalModule(portal, state);
            return chosen;
        }

        static Vec3 AsVector3(object value)
        {
            if (value is Vec3 v) return v;
            if (value is GdArray values && values.Count >= 3)
                return new Vec3(V.F64(values[0]), V.F64(values[1]), V.F64(values[2]));
            return Vec3.Zero;
        }

        /// <summary>
        /// Post-pass: replace wall_straight_1x1 with corner / end-cap / t-junction modules based on the neighboring
        /// SOLID edge topology at each endpoint. Updates both the placement and the edge_map record.
        /// </summary>
        void RefineWallModules(GdDict edgeMap, GdArray edgePlacements)
        {
            foreach (var placementVariant in edgePlacements)
            {
                var placement = (GdDict)placementVariant;
                string kind = V.Str(placement.Get("kind", ""));
                if (kind != "SOLID") continue;
                string moduleId = V.Str(placement.Get("module_id", ""));
                if (moduleId != WALL_MODULE) continue; // already a portal or special module
                string ek = V.Str(placement.Get("edge_key", ""));
                if (ek.Length == 0) continue;
                ParsedEdge parsed = ParseEdgeKey(ek);
                if (!parsed.Ok) continue;
                long connections = CountPerpendicularConnections(edgeMap, parsed);
                string newModule = WallModuleForConnections(connections, parsed, edgeMap, placement);
                if (newModule != WALL_MODULE)
                {
                    placement["module_id"] = newModule;
                    // Also update the edge_map record.
                    if (edgeMap.Has(ek)) ((GdDict)edgeMap[ek])["module_id"] = newModule;
                }
            }
        }

        /// <summary>GDScript <c>_parse_edge_key()</c> result (<c>{"ok", "axis", "deck", "x", "y"}</c>).</summary>
        struct ParsedEdge
        {
            public bool Ok;
            public string Axis;
            public long Deck;
            public long X;
            public long Y;
        }

        /// <summary>
        /// Horizontal <c>"{deck}|h|{y}|{x}"</c>: edge between rows y and y+1 at column x.
        /// Vertical <c>"{deck}|v|{y}|{x}"</c>: edge between columns x and x+1 at row y.
        /// </summary>
        static ParsedEdge ParseEdgeKey(string ek)
        {
            List<string> parts = GdString.Split(ek, "|");
            if (parts.Count < 4) return new ParsedEdge { Ok = false };
            long deck = V.StringToInt(parts[0]);
            string axis = parts[1];
            long a = V.StringToInt(parts[2]);
            long b = V.StringToInt(parts[3]);
            if (axis == "h") return new ParsedEdge { Ok = true, Axis = "h", Deck = deck, X = b, Y = a };
            if (axis == "v") return new ParsedEdge { Ok = true, Axis = "v", Deck = deck, X = b, Y = a };
            return new ParsedEdge { Ok = false };
        }

        static string Key(long deck, string axis, long a, long b) =>
            GdString.FormatInt(deck) + "|" + axis + "|" + GdString.FormatInt(a) + "|" + GdString.FormatInt(b);

        static bool IsSolid(GdDict edgeMap, string key) =>
            edgeMap.Has(key) && V.Str(((GdDict)edgeMap[key]).Get("kind", "")) == "SOLID";

        /// <summary>
        /// Bitmask of SOLID perpendicular edges at the endpoints of the given edge: bits 0-1 at endpoint A
        /// (west / north), bits 2-3 at endpoint B (east / south).
        /// </summary>
        static long CountPerpendicularConnections(GdDict edgeMap, ParsedEdge parsed)
        {
            string axis = parsed.Axis;
            long deck = parsed.Deck;
            long x = parsed.X;
            long y = parsed.Y;
            long mask = 0;

            if (axis == "h")
            {
                // West endpoint (x, y+1): vertical edges at x-1.
                if (IsSolid(edgeMap, Key(deck, "v", y, x - 1))) mask |= 1;
                if (IsSolid(edgeMap, Key(deck, "v", y + 1, x - 1))) mask |= 2;
                // East endpoint (x+1, y+1): vertical edges at x.
                if (IsSolid(edgeMap, Key(deck, "v", y, x))) mask |= 4;
                if (IsSolid(edgeMap, Key(deck, "v", y + 1, x))) mask |= 8;
            }
            else
            {
                // North endpoint (x+1, y): horizontal edges at y-1.
                if (IsSolid(edgeMap, Key(deck, "h", y - 1, x))) mask |= 1;
                if (IsSolid(edgeMap, Key(deck, "h", y - 1, x + 1))) mask |= 2;
                // South endpoint (x+1, y+1): horizontal edges at y.
                if (IsSolid(edgeMap, Key(deck, "h", y, x))) mask |= 4;
                if (IsSolid(edgeMap, Key(deck, "h", y, x + 1))) mask |= 8;
            }

            return mask;
        }

        static string WallModuleForConnections(long mask, ParsedEdge parsed, GdDict edgeMap, GdDict placement)
        {
            long count = 0;
            for (int i = 0; i < 4; i++)
                if ((mask & (1L << i)) != 0) count += 1;

            // Isolated wall — end cap.
            if (count == 0) return WALL_END_CAP_MODULE;

            // One perpendicular connection at an endpoint — a corner.
            if (count == 1) return PickCornerType(mask, parsed, edgeMap, placement);

            long aConnections = mask & 3; // bits 0-1 (endpoint A)
            long bConnections = mask & 12; // bits 2-3 (endpoint B)
            long aCount = 0;
            long bCount = 0;
            if ((aConnections & 1) != 0) aCount += 1;
            if ((aConnections & 2) != 0) aCount += 1;
            if ((bConnections & 4) != 0) bCount += 1;
            if ((bConnections & 8) != 0) bCount += 1;

            if (count == 2)
            {
                // Both connections at the same endpoint — T-junction.
                if (aCount == 2 || bCount == 2) return WALL_T_JUNCTION_MODULE;
                if (aCount == 1 && bCount == 1)
                {
                    // Different endpoints: same side = straight, different sides = corner.
                    bool aFirst = (mask & 1) != 0;
                    bool bFirst = (mask & 4) != 0;
                    if (aFirst == bFirst) return WALL_MODULE;
                    return PickCornerType(mask, parsed, edgeMap, placement);
                }
            }

            // Three connections — T-junction. Four — cross (no cross module; T-junction is the best fit).
            return WALL_T_JUNCTION_MODULE;
        }

        /// <summary>Exterior edge (no other room) = outer corner; between two rooms = inner corner.</summary>
        static string PickCornerType(long mask, ParsedEdge parsed, GdDict edgeMap, GdDict placement)
        {
            string otherRoom = V.Str(placement.Get("other_room", ""));
            if (otherRoom.Length == 0) return WALL_OUTER_CORNER_MODULE;
            return WALL_INNER_CORNER_MODULE;
        }

        GdDict IndexPortals(GdDict layout, GdDict roomById, GdDict roomByCell, List<string> errors)
        {
            var indexed = new GdDict();
            object portalsVariant = layout.Get("portals", null);
            if (!(portalsVariant is GdArray portals))
            {
                errors.Add("layout missing canonical portals array");
                return indexed;
            }
            foreach (var portalVariant in portals)
            {
                if (!(portalVariant is GdDict portal))
                {
                    errors.Add("portal record must be an object");
                    continue;
                }
                string portalId = V.Str(portal.Get("id", ""));
                string fromRoom = V.Str(portal.Get("from_room", ""));
                string toRoom = V.Str(portal.Get("to_room", ""));
                if (!roomById.Has(fromRoom) || !roomById.Has(toRoom) || fromRoom == toRoom)
                {
                    errors.Add("portal room endpoints are invalid: " + portalId);
                    continue;
                }
                long fromDeck = V.I64(((GdDict)roomById[fromRoom]).Get("deck", -1L));
                long toDeck = V.I64(((GdDict)roomById[toRoom]).Get("deck", -1L));
                CellInfo fromInfo = ReadCell(portal.Get("from_cell", null), fromDeck);
                CellInfo toInfo = ReadCell(portal.Get("to_cell", null), toDeck);
                if (!fromInfo.Ok || !toInfo.Ok)
                {
                    errors.Add("portal endpoints are malformed: " + portalId);
                    continue;
                }
                if (fromInfo.Deck != fromDeck || toInfo.Deck != toDeck)
                {
                    errors.Add("portal endpoint deck mismatch: " + portalId);
                    continue;
                }
                if (fromDeck != toDeck)
                {
                    errors.Add("cross-deck portal must remain a vertical connection: " + portalId);
                    continue;
                }
                Vec2i fromCell = fromInfo.Cell;
                Vec2i toCell = toInfo.Cell;
                string fromKey = CellKey(fromDeck, fromCell);
                string toKey = CellKey(toDeck, toCell);
                if (V.Str(roomByCell.Get(fromKey, "")) != fromRoom || V.Str(roomByCell.Get(toKey, "")) != toRoom)
                {
                    errors.Add("portal endpoints are not owned by declared rooms: " + portalId);
                    continue;
                }
                Vec2i edgeCell = fromCell;
                string direction = DirectionBetween(fromCell, toCell);
                bool logicalBoundary = false;
                if (direction.Length == 0 && portal.Get("edge_cell", null) != null)
                {
                    CellInfo edgeInfo = ReadCell(portal.Get("edge_cell", null), fromDeck);
                    string declaredDirection = V.Str(portal.Get("edge_direction", ""));
                    if (edgeInfo.Ok && DIRECTIONS.Has(declaredDirection) &&
                        V.Str(roomByCell.Get(CellKey(fromDeck, edgeInfo.Cell), "")) == fromRoom)
                    {
                        edgeCell = edgeInfo.Cell;
                        direction = declaredDirection;
                        logicalBoundary = true;
                    }
                }
                if (direction.Length == 0)
                {
                    errors.Add("portal endpoints are not adjacent: " + portalId);
                    continue;
                }
                string key = EdgeKey(fromDeck, edgeCell, direction);
                if (indexed.Has(key))
                {
                    errors.Add("duplicate portal edge: " + key);
                    continue;
                }
                GdDict indexedPortal = portal.DeepCopy();
                indexedPortal["edge_key"] = key;
                indexedPortal["direction"] = direction;
                indexedPortal["edge_cell"] = edgeCell;
                indexedPortal["edge_other_room"] = toRoom;
                indexedPortal["logical_from_cell"] = fromCell;
                indexedPortal["logical_to_cell"] = toCell;
                indexedPortal["logical_boundary"] = logicalBoundary;
                indexedPortal["from_cell_key"] = fromKey;
                indexedPortal["to_cell_key"] = toKey;
                indexed[key] = indexedPortal;
            }
            return indexed;
        }

        static string DirectionBetween(Vec2i fromCell, Vec2i toCell)
        {
            Vec2i delta = toCell - fromCell;
            foreach (var direction in DIRECTIONS.Keys)
                if ((Vec2i)DIRECTIONS[direction] == delta) return V.Str(direction);
            return "";
        }

        string PortalKind(GdDict portal, GdDict layout)
        {
            if (BlockedLinkMatches(portal, layout)) return "LOCKED";
            string raw = V.Str(portal.Get("state", portal.Get("portal_type", portal.Get("kind", "DOOR")))).ToUpperInvariant();
            if (raw == "OPEN") return "DOOR";
            if (raw == "DOOR" || raw == "LOCKED" || raw == "HATCH" || raw == "BREACH") return raw;
            return "DOOR";
        }

        bool BlockedLinkMatches(GdDict portal, GdDict layout)
        {
            object blockedVariant = layout.Get("blocked_links", new GdArray());
            if (!(blockedVariant is GdArray blocked)) return false;
            string portalFrom = V.Str(portal.Get("from_room", ""));
            string portalTo = V.Str(portal.Get("to_room", ""));
            Vec2i portalFromCell = CellXz(portal.Get("from_cell", portal.Get("logical_from_cell", null)));
            Vec2i portalToCell = CellXz(portal.Get("to_cell", portal.Get("logical_to_cell", null)));
            foreach (var linkVariant in blocked)
            {
                if (!(linkVariant is GdDict link)) continue;
                string fromRoom = V.Str(link.Get("from_room", ""));
                string toRoom = V.Str(link.Get("to_room", ""));
                bool roomsMatch = (fromRoom == portalFrom && toRoom == portalTo) || (fromRoom == portalTo && toRoom == portalFrom);
                if (!roomsMatch) continue;
                Vec2i fromCell = CellXz(link.Get("from_cell", null));
                Vec2i toCell = CellXz(link.Get("to_cell", null));
                if (fromCell == CellSentinel || toCell == CellSentinel) continue;
                if ((fromCell == portalFromCell && toCell == portalToCell) || (fromCell == portalToCell && toCell == portalFromCell))
                    return true;
            }
            return false;
        }

        static Vec2i CellXz(object value)
        {
            if (value is Vec2i v) return v;
            if (value is GdArray arr && arr.Count >= 2) return new Vec2i(V.I32(arr[0]), V.I32(arr[1]));
            return CellSentinel;
        }

        static string PortalModule(GdDict portal, string state)
        {
            string declared = V.Str(portal.Get("module_id", ""));
            if (declared.Length != 0) return declared;
            if (state == "LOCKED") return LOCKED_MODULE;
            if (state == "HATCH") return HATCH_MODULE;
            if (state == "BREACH") return "";
            return DEFAULT_PORTAL_MODULE;
        }

        internal static CellInfo ReadCell(object value, long defaultDeck)
        {
            if (value is Vec2i v) return new CellInfo { Ok = defaultDeck >= 0, Cell = v, Deck = defaultDeck };
            if (!(value is GdArray values))
            {
                if (value is string s)
                {
                    List<double> parsed = ParseVectorString(s, 2);
                    if (parsed.Count == 2 && defaultDeck >= 0)
                        return new CellInfo
                        {
                            Ok = true,
                            Cell = new Vec2i((int)GdMath.Trunc(parsed[0]), (int)GdMath.Trunc(parsed[1])),
                            Deck = defaultDeck,
                        };
                }
                return CellInfo.Fail;
            }
            if (values.Count < 2 || !IsInteger(values[0]) || !IsInteger(values[1])) return CellInfo.Fail;
            long deck = defaultDeck;
            if (values.Count >= 3)
            {
                if (!IsInteger(values[2])) return CellInfo.Fail;
                deck = V.I64(values[2]);
            }
            if (deck < 0) return CellInfo.Fail;
            return new CellInfo { Ok = true, Cell = new Vec2i(V.I32(values[0]), V.I32(values[1])), Deck = deck };
        }

        internal static List<double> ParseVectorString(string value, int expected)
        {
            string text = GdString.StripEdges(value);
            if (GdString.BeginsWith(text, "(") && GdString.EndsWith(text, ")"))
                text = SubstrSafe(text, 1, text.Length - 2);
            List<string> pieces = GdString.Split(text, ",");
            if (pieces.Count != expected) return new List<double>();
            var result = new List<double>();
            foreach (string piece in pieces)
            {
                string token = GdString.StripEdges(piece);
                if (!IsValidFloat(token)) return new List<double>();
                result.Add(V.StringToFloat(token));
            }
            return result;
        }

        /// <summary>GDScript <c>substr(from, len)</c> for a single-character string "(" / ")" edge case.</summary>
        static string SubstrSafe(string s, int from, int len)
        {
            if (from >= s.Length || len <= 0) return "";
            if (from + len > s.Length) len = s.Length - from;
            return s.Substring(from, len);
        }

        /// <summary>
        /// Godot 4.7 <c>String.is_valid_float()</c>: optional leading sign, digits with at most one '.', an optional
        /// 'e' exponent (after digits) with an optional sign; at least one mantissa digit.
        /// </summary>
        internal static bool IsValidFloat(string s)
        {
            int len = s.Length;
            if (len == 0) return false;
            int from = 0;
            if (s[0] == '+' || s[0] == '-') from++;
            bool exponentFound = false;
            bool periodFound = false;
            bool signFound = false;
            bool exponentValuesFound = false;
            bool numbersFound = false;
            for (int i = from; i < len; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9')
                {
                    if (exponentFound) exponentValuesFound = true;
                    else numbersFound = true;
                }
                else if (numbersFound && !exponentFound && c == 'e')
                {
                    exponentFound = true;
                }
                else if (!periodFound && !exponentFound && c == '.')
                {
                    periodFound = true;
                }
                else if ((c == '-' || c == '+') && exponentFound && !exponentValuesFound && !signFound)
                {
                    signFound = true;
                }
                else
                {
                    return false;
                }
            }
            return numbersFound;
        }

        internal static bool IsInteger(object value)
        {
            if (value is long) return true;
            if (value is double d) return GdMath.IsEqualApprox(d, GdMath.Round(d));
            if (value is string s) return GdString.IsValidInt(s);
            return false;
        }

        static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return true;
            return false;
        }
    }
}
