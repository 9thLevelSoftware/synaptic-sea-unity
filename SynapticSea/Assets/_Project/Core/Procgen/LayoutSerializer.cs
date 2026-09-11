// Ported from scripts/procgen/layout_serializer.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Assembles a complete layout.json dictionary (schema 1.2.0) from the pipeline stage outputs
    /// (CellLayoutEngine grid, WallDoorResolver geometry, RoomAssigner plan).
    /// </summary>
    public sealed class LayoutSerializer
    {
        public const double CELL_SIZE = 4.0;
        public const double DECK_HEIGHT = 4.0;

        /// <summary>Default motif requests per role.</summary>
        public static readonly GdDict ROLE_MOTIFS = new GdDict
        {
            { "airlock", GdArray.Of("mot-airlock-entry-locker") },
            { "dock", GdArray.Of("mot-airlock-entry-locker") },
            { "corridor", GdArray.Of("mot-maintenance-workbench-corner") },
            { "engineering", GdArray.Of("mot-engineering-console") },
        };

        /// <summary>Floor module per role.</summary>
        public static readonly GdDict FLOOR_MODULES = new GdDict
        {
            { "corridor", "corridor_floor_1x1" },
            { "main_spine", "corridor_floor_1x1" },
        };

        public const string DEFAULT_FLOOR = "floor_1x1";

        public GdDict Serialize(GdDict cellGrid, GdDict geometry, IList<GdDict> roomPlan, string templateId,
            long seedValue, string archetypeName)
        {
            GdDict roomsData = cellGrid.GetDictOrEmpty("rooms");
            GdArray adjacencies = cellGrid.GetArrayOrEmpty("adjacencies");

            // Build room role / variant lookup.
            var roomRoles = new GdDict();
            var roomVariants = new GdDict();
            var roomOrder = new List<string>();
            foreach (var room in roomPlan)
            {
                string rid = V.Str(room["id"]);
                roomRoles[rid] = V.Str(room.Get("role", ""));
                roomVariants[rid] = V.Str(room.Get("variant", "standard"));
                roomOrder.Add(rid);
            }

            // Assemble rooms array.
            var roomsArray = new GdArray();
            var allPortals = new GdArray();
            foreach (string rid in roomOrder)
            {
                if (!roomsData.Has(rid)) continue;
                var roomData = (GdDict)roomsData[rid];
                string role = V.Str(roomRoles.Get(rid, ""));
                long deck = V.I64(roomData.Get("deck", 0L));
                GdArray cells = roomData.GetArrayOrEmpty("cells");

                GdArray placements = BuildStructuralPlacements(cells, deck, role);
                GdDict geo = geometry.GetDictOrEmpty(rid);

                // Serialize cells to plain arrays for JSON round-trip.
                var serializedCells = new GdArray();
                foreach (var cell in cells)
                {
                    if (cell is Vec2i v) serializedCells.Append(GdArray.Of((long)v.X, (long)v.Y));
                    else if (cell is GdArray) serializedCells.Append(cell);
                    else serializedCells.Append(GdArray.Of(CellX(cell), CellY(cell)));
                }

                var roomDict = new GdDict
                {
                    { "id", rid },
                    { "room_role", role },
                    { "variant", V.Str(roomVariants.Get(rid, "standard")) },
                    { "deck", deck },
                    { "cells", serializedCells },
                    { "footprint", FootprintFromCells(cells) },
                    { "structural_placements", placements },
                };

                // Add wall/portal/interior data if available.
                if (!geo.IsEmpty)
                {
                    GdArray wallPlacements = BuildWallPlacements(geo.GetArrayOrEmpty("wall_segments"));
                    foreach (var wp in wallPlacements) placements.Append(wp);

                    GdArray roomPortals = SerializePortals(geo.GetArrayOrEmpty("portals"));
                    roomDict["portals"] = roomPortals;
                    foreach (GdDict p in roomPortals)
                    {
                        GdDict portalCopy = p.ShallowCopy();
                        portalCopy["from_room"] = rid;
                        allPortals.Append(portalCopy);
                    }
                    roomDict["interior_zones"] = SerializeInteriorZones(geo.GetDictOrEmpty("interior_zones"));
                }
                else
                {
                    roomDict["portals"] = new GdArray();
                    roomDict["interior_zones"] = new GdDict();
                }

                roomDict["motif_requests"] = (ROLE_MOTIFS.Get(role) as GdArray)?.ShallowCopy() ?? new GdArray();
                roomsArray.Append(roomDict);
            }

            // Build room_links from adjacencies.
            GdArray roomLinks = BuildRoomLinks(adjacencies, roomsData);

            // Canonical portals from room_links (one per adjacency); cross-deck links belong in vertical_connections.
            var canonicalPortals = new GdArray();
            foreach (GdDict link in roomLinks)
            {
                string fromRoom = V.Str(link.Get("from_room", ""));
                string toRoom = V.Str(link.Get("to_room", ""));
                var fromCell = link.Get("from_cell") as GdArray ?? GdArray.Of(0L, 0L, 0L);
                var toCell = link.Get("to_cell") as GdArray ?? GdArray.Of(0L, 0L, 0L);
                if (fromCell.Count >= 3 && toCell.Count >= 3 && V.I64(fromCell[2]) != V.I64(toCell[2])) continue;
                canonicalPortals.Append(new GdDict
                {
                    { "id", "portal_" + fromRoom + "_" + toRoom },
                    { "from_room", fromRoom },
                    { "to_room", toRoom },
                    { "from_cell", fromCell },
                    { "to_cell", toCell },
                    { "state", "DOOR" },
                });
            }

            GdArray verticalConnections = BuildVerticalConnections(roomsData, adjacencies, roomRoles);
            var structuralLayout = new GdDict
            {
                { "cell_size", CELL_SIZE },
                { "rooms", roomsArray },
                { "portals", canonicalPortals },
                { "vertical_connections", verticalConnections },
            };
            GdDict structuralPlan = new StructuralEdgeCompiler().Compile(structuralLayout);

            GdArray landmarks = BuildLandmarks(roomsData, roomRoles, roomOrder);

            // Critical path (BFS from first to last room).
            string entryId = roomOrder.Count > 0 ? roomOrder[0] : "";
            string destId = roomOrder.Count > 0 ? roomOrder[roomOrder.Count - 1] : "";
            GdArray criticalPath = BuildCriticalPath(entryId, destId, adjacencies);

            return new GdDict
            {
                { "schema_version", "1.2.0" },
                { "document_kind", "ship_layout" },
                { "program_id", "procgen-" + archetypeName + "-seed-" + GdString.FormatInt(seedValue) },
                { "kit_id", "ship_structural_v0" },
                { "design_intent", "procedurally generated " + templateId + " ship" },
                { "cell_size", CELL_SIZE },
                { "rooms", roomsArray },
                { "room_links", roomLinks },
                { "portals", canonicalPortals },
                { "blocked_links", new GdArray() },
                { "vertical_connections", verticalConnections },
                { "landmarks", landmarks },
                { "critical_path", criticalPath },
                { "fire_zones", new GdArray() },
                { "arc_zones", new GdArray() },
                { "breach_zones", new GdArray() },
                { "encounters", new GdArray() }, // populated by EncounterInjector
                { "structural_plan", structuralPlan },
                { "prototype", new GdDict { { "start_room", entryId }, { "goal_room", destId } } },
            };
        }

        /// <summary><c>cell.x</c> of a Vector2i or an <c>[x, y]</c> array (GDScript property access).</summary>
        static long CellX(object cell) => cell is Vec2i v ? v.X : cell is GdArray a && a.Count >= 1 ? V.I64(a[0]) : 0;

        static long CellY(object cell) => cell is Vec2i v ? v.Y : cell is GdArray a && a.Count >= 2 ? V.I64(a[1]) : 0;

        static GdArray FootprintFromCells(GdArray cells)
        {
            if (cells.IsEmpty) return GdArray.Of(0L, 0L);
            long minX = 999999, maxX = -999999, minZ = 999999, maxZ = -999999;
            foreach (var cell in cells)
            {
                long x = CellX(cell);
                long z = CellY(cell);
                minX = System.Math.Min(minX, x);
                maxX = System.Math.Max(maxX, x);
                minZ = System.Math.Min(minZ, z);
                maxZ = System.Math.Max(maxZ, z);
            }
            return GdArray.Of(maxX - minX + 1, maxZ - minZ + 1);
        }

        static GdArray BuildStructuralPlacements(GdArray cells, long deck, string role)
        {
            var placements = new GdArray();
            string floorModule = V.Str(FLOOR_MODULES.Get(role, DEFAULT_FLOOR));

            foreach (var cell in cells)
            {
                long x = CellX(cell);
                long z = CellY(cell);
                double worldX = (double)x * CELL_SIZE;
                double worldY = (double)deck * DECK_HEIGHT;
                double worldZ = (double)z * CELL_SIZE;

                string cellName = deck == 0
                    ? "floor_cell_x" + GdString.FormatInt(x) + "_z" + GdString.FormatInt(z)
                    : "floor_cell_d" + GdString.FormatInt(deck) + "_x" + GdString.FormatInt(x) + "_z" + GdString.FormatInt(z);

                placements.Append(new GdDict
                {
                    { "name", cellName },
                    { "module", floorModule },
                    { "world_position", GdArray.Of(worldX, worldY, worldZ) },
                });
            }

            // Add ramp module for ramp rooms.
            if (role == "ramp" && !cells.IsEmpty)
            {
                object firstCell = cells[0];
                long x = CellX(firstCell);
                long z = CellY(firstCell);
                placements.Append(new GdDict
                {
                    { "name", "ramp_up_1x2" },
                    { "module", "ramp_up_1x2" },
                    { "world_position", GdArray.Of((double)x * CELL_SIZE, (double)deck * DECK_HEIGHT, (double)z * CELL_SIZE) },
                });
            }

            return placements;
        }

        static GdArray BuildWallPlacements(GdArray wallSegments)
        {
            var placements = new GdArray();
            foreach (GdDict wall in wallSegments)
            {
                object pos = wall.Get("position", Vec3.Zero);
                GdArray worldPos = pos is Vec3 p ? GdArray.Of((double)p.X, (double)p.Y, (double)p.Z) : GdArray.Of(0.0, 0.0, 0.0);
                placements.Append(new GdDict
                {
                    { "name", V.Str(wall.Get("name", "")) },
                    { "module", V.Str(wall.Get("module_id", "wall_straight_1x1")) },
                    { "world_position", worldPos },
                    { "yaw_degrees", V.F64(wall.Get("yaw_degrees", 0.0)) },
                });
            }
            return placements;
        }

        static GdArray SerializePortals(GdArray portals)
        {
            var result = new GdArray();
            foreach (var portalVariant in portals)
            {
                if (!(portalVariant is GdDict source)) continue;
                GdDict portal = source.ShallowCopy();
                if (portal.Get("position") is Vec3 pos) portal["position"] = GdArray.Of((double)pos.X, (double)pos.Y, (double)pos.Z);
                foreach (string cellKey in new[] { "from_cell", "to_cell" })
                {
                    if (portal.Get(cellKey) is Vec2i cell) portal[cellKey] = GdArray.Of((long)cell.X, (long)cell.Y);
                }
                result.Append(portal);
            }
            return result;
        }

        static GdDict SerializeInteriorZones(GdDict zones)
        {
            var result = new GdDict();
            result["reserved_cells"] = SerializeCellList(zones.GetArrayOrEmpty("reserved_cells"));
            result["center_slots"] = SerializeCellList(zones.GetArrayOrEmpty("center_slots"));
            result["wall_slots"] = SerializeWallSlots(zones.GetArrayOrEmpty("wall_slots"));
            return result;
        }

        /// <summary>
        /// Slot cells are always JSON <c>[x, z]</c> (deck lives on the room). Parses Vector2i, arrays, dictionaries
        /// with a <c>cell</c>, <c>floor_cell_*</c> names, and leftover "(x, y)" strings. Returns [] when unparseable.
        /// </summary>
        public static GdArray ParseSlotCell(object value)
        {
            if (value is Vec2i v) return GdArray.Of((long)v.X, (long)v.Y);
            // GDScript also accepts Vector2 (rounded); the port has no Vector2 Variant.
            if (value is GdArray arr)
            {
                if (arr.Count >= 2) return GdArray.Of(V.I64(arr[0]), V.I64(arr[1]));
                return new GdArray();
            }
            if (value is GdDict d) return ParseSlotCell(d.Get("cell", null));
            if (!(value is string raw)) return new GdArray();
            string s = GdString.StripEdges(raw);
            if (GdString.BeginsWith(s, "floor_cell")) return ParseFloorCellName(s);
            if (GdString.BeginsWith(s, "(") && GdString.EndsWith(s, ")") && s.Length >= 5) s = s.Substring(1, s.Length - 2);
            List<string> parts = GdString.Split(s, ",");
            if (parts.Count < 2) return new GdArray();
            string xs = GdString.StripEdges(parts[0]);
            string zs = GdString.StripEdges(parts[1]);
            if (StructuralEdgeCompiler.IsValidFloat(xs) && StructuralEdgeCompiler.IsValidFloat(zs))
                return GdArray.Of(GdMath.Trunc(GdMath.Round(V.StringToFloat(xs))), GdMath.Trunc(GdMath.Round(V.StringToFloat(zs))));
            return new GdArray();
        }

        static GdArray ParseFloorCellName(string placementName)
        {
            List<string> parts = GdString.Split(placementName, "_");
            for (int i = 0; i < parts.Count; i++)
            {
                if (GdString.BeginsWith(parts[i], "x") && i + 1 < parts.Count && GdString.BeginsWith(parts[i + 1], "z"))
                {
                    string xStr = parts[i].Substring(1);
                    string zStr = parts[i + 1].Substring(1);
                    if (GdString.IsValidInt(xStr) && GdString.IsValidInt(zStr))
                        return GdArray.Of(V.StringToInt(xStr), V.StringToInt(zStr));
                }
            }
            return new GdArray();
        }

        static GdArray SerializeCellList(GdArray cells)
        {
            var serialized = new GdArray();
            foreach (var cell in cells)
            {
                GdArray parsed = ParseSlotCell(cell);
                if (parsed.Count >= 2) serialized.Append(parsed);
            }
            return serialized;
        }

        static GdArray SerializeWallSlots(GdArray slots)
        {
            var serialized = new GdArray();
            foreach (var slotVariant in slots)
            {
                object cellValue = slotVariant;
                bool againstWall = true;
                var extra = new GdDict();
                if (slotVariant is GdDict slot)
                {
                    cellValue = slot.Get("cell", null);
                    againstWall = V.Bool(slot.Get("against_wall", true));
                    foreach (var kv in slot)
                    {
                        string key = V.Str(kv.Key);
                        if (key == "cell" || key == "against_wall") continue;
                        extra[kv.Key] = kv.Value;
                    }
                }
                GdArray parsed = ParseSlotCell(cellValue);
                if (parsed.Count < 2) continue;
                var entry = new GdDict { { "cell", parsed }, { "against_wall", againstWall } };
                foreach (var kv in extra) entry[kv.Key] = kv.Value;
                serialized.Append(entry);
            }
            return serialized;
        }

        static GdArray CellWithDeck(object cell, long deck) =>
            cell is Vec2i v ? GdArray.Of((long)v.X, (long)v.Y, deck) : GdArray.Of(0L, 0L, deck);

        static GdArray BuildRoomLinks(GdArray adjacencies, GdDict roomsData)
        {
            var links = new GdArray();
            foreach (GdDict adj in adjacencies)
            {
                string fromRoom = V.Str(adj.Get("from_room", ""));
                string toRoom = V.Str(adj.Get("to_room", ""));
                object fromCell = adj.Get("from_cell", Vec2i.Zero);
                object toCell = adj.Get("to_cell", Vec2i.Zero);

                long fromDeck = V.I64(roomsData.GetDictOrEmpty(fromRoom).Get("deck", 0L));
                long toDeck = V.I64(roomsData.GetDictOrEmpty(toRoom).Get("deck", 0L));

                links.Append(new GdDict
                {
                    { "id", fromRoom + "_to_" + toRoom },
                    { "from_room", fromRoom },
                    { "to_room", toRoom },
                    { "from_cell", CellWithDeck(fromCell, fromDeck) },
                    { "to_cell", CellWithDeck(toCell, toDeck) },
                    { "module_id", "doorway_frame_open_1x1" },
                });
            }
            return links;
        }

        static GdArray BuildVerticalConnections(GdDict roomsData, GdArray adjacencies, GdDict roomRoles)
        {
            var connections = new GdArray();
            foreach (GdDict adj in adjacencies)
            {
                string fromRoom = V.Str(adj.Get("from_room", ""));
                string toRoom = V.Str(adj.Get("to_room", ""));
                if (!roomsData.Has(fromRoom) || !roomsData.Has(toRoom)) continue;
                long fromDeck = V.I64(((GdDict)roomsData[fromRoom]).Get("deck", 0L));
                long toDeck = V.I64(((GdDict)roomsData[toRoom]).Get("deck", 0L));
                if (fromDeck == toDeck) continue;

                string fromRole = V.Str(roomRoles.Get(fromRoom, ""));
                string module = fromRole == "ramp" ? "ramp_up_1x2" : "floor_1x1";

                connections.Append(new GdDict
                {
                    { "id", fromRoom + "_to_" + toRoom },
                    { "type", fromRole == "ramp" ? "ramp" : "elevator" },
                    { "module_id", module },
                    { "from_room", fromRoom },
                    { "from_cell", CellWithDeck(adj.Get("from_cell", Vec2i.Zero), fromDeck) },
                    { "to_room", toRoom },
                    { "to_cell", CellWithDeck(adj.Get("to_cell", Vec2i.Zero), toDeck) },
                });
            }
            return connections;
        }

        static GdArray BuildLandmarks(GdDict roomsData, GdDict roomRoles, List<string> roomOrder)
        {
            var landmarks = new GdArray();

            // First hub/spine room — blue beacon.
            foreach (string rid in roomOrder)
            {
                string role = V.Str(roomRoles.Get(rid, ""));
                if (role == "hub" || role == "main_spine")
                {
                    landmarks.Append(new GdDict
                    {
                        { "id", rid + "_blue_beacon" },
                        { "room_id", rid },
                        { "kind", "orientation_beacon" },
                        { "position", RoomCenterPosition(roomsData.GetDictOrEmpty(rid)) },
                        { "color", "blue" },
                    });
                    break;
                }
            }

            // Destination room — green beacon.
            if (roomOrder.Count > 0)
            {
                string destId = roomOrder[roomOrder.Count - 1];
                landmarks.Append(new GdDict
                {
                    { "id", destId + "_green_core" },
                    { "room_id", destId },
                    { "kind", "destination_core" },
                    { "position", RoomCenterPosition(roomsData.GetDictOrEmpty(destId)) },
                    { "color", "green" },
                });
            }

            return landmarks;
        }

        static GdArray RoomCenterPosition(GdDict roomData)
        {
            GdArray cells = roomData.GetArrayOrEmpty("cells");
            long deck = V.I64(roomData.Get("deck", 0L));
            if (cells.IsEmpty) return GdArray.Of(0.0, 0.0, 0.0);

            double sumX = 0.0;
            double sumZ = 0.0;
            foreach (var cell in cells)
            {
                sumX += (double)CellX(cell) * CELL_SIZE;
                sumZ += (double)CellY(cell) * CELL_SIZE;
            }
            double count = cells.Count;
            return GdArray.Of(sumX / count, (double)deck * DECK_HEIGHT + 0.15, sumZ / count);
        }

        static GdArray BuildCriticalPath(string startId, string endId, GdArray adjacencies)
        {
            if (startId.Length == 0 || endId.Length == 0) return new GdArray();

            var adjMap = new GdDict();
            foreach (GdDict adj in adjacencies)
            {
                string fr = V.Str(adj["from_room"]);
                string tr = V.Str(adj["to_room"]);
                if (!adjMap.Has(fr)) adjMap[fr] = new GdArray();
                ((GdArray)adjMap[fr]).Append(tr);
                if (!adjMap.Has(tr)) adjMap[tr] = new GdArray();
                ((GdArray)adjMap[tr]).Append(fr);
            }

            var visited = new GdDict { { startId, "" } };
            var queue = new List<string> { startId };
            while (queue.Count > 0)
            {
                string current = queue[0];
                queue.RemoveAt(0);
                if (current == endId) break;
                foreach (var neighbor in adjMap.GetArrayOrEmpty(current))
                {
                    if (!visited.Has(neighbor))
                    {
                        visited[neighbor] = current;
                        queue.Add(V.Str(neighbor));
                    }
                }
            }

            if (!visited.Has(endId)) return GdArray.Of(startId);

            var path = new GdArray();
            string currentPath = endId;
            while (currentPath.Length != 0)
            {
                path.Insert(0, currentPath);
                currentPath = V.Str(visited.Get(currentPath, ""));
            }
            return path;
        }
    }
}
