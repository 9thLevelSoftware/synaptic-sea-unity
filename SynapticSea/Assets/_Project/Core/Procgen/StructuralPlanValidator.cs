// Ported from scripts/procgen/structural_plan_validator.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Fail-closed validator for compiler-produced structural plans. Edge placements and floor placements are
    /// separate contracts: floors identify occupied cells; edge records identify canonical boundaries.
    /// Returns <c>{ok, errors, stats}</c>.
    /// </summary>
    public sealed class StructuralPlanValidator
    {
        public static readonly IReadOnlyList<string> FLOOR_MODULES = new[] { "floor_1x1", "corridor_floor_1x1" };
        public static readonly IReadOnlyList<string> CEILING_MODULES = new[] { "ceiling_cap_1x1" };
        public static readonly IReadOnlyList<string> EDGE_KINDS = new[] { "SOLID", "OPEN", "DOOR", "LOCKED", "HATCH", "BREACH" };

        public GdDict Validate(GdDict plan, GdDict topology)
        {
            var errors = new List<string>();
            var stats = new GdDict
            {
                { "occupied_cells", 0L },
                { "floor_placements", 0L },
                { "ceiling_placements", 0L },
                { "socket_bindings", 0L },
                { "edges", 0L },
                { "edge_placements", 0L },
            };
            if (plan == null || plan.IsEmpty)
            {
                errors.Add("structural plan must be a non-empty object");
                return Verdict(errors, stats);
            }

            object compilerErrorsVariant = plan.Get("errors", null);
            if (!(compilerErrorsVariant is GdArray compilerErrors)) errors.Add("structural plan errors must be an array");
            else if (!compilerErrors.IsEmpty)
            {
                foreach (var compilerError in compilerErrors) errors.Add("compiler error: " + V.Str(compilerError));
            }

            if (!(plan.Get("occupancy", null) is GdDict occupancy))
            {
                errors.Add("occupancy must be a dictionary");
                return Verdict(errors, stats);
            }
            stats["occupied_cells"] = (long)occupancy.Count;
            if (occupancy.IsEmpty) errors.Add("occupancy must be non-empty");

            object edgesVariant = plan.Get("edges", null);
            if (!(edgesVariant is GdDict)) errors.Add("edges must be a dictionary");
            GdDict edges = edgesVariant as GdDict ?? new GdDict();
            stats["edges"] = (long)edges.Count;

            object placementsVariant = plan.Get("placements", null);
            if (!(placementsVariant is GdArray)) errors.Add("placements must be an array");
            GdArray placements = placementsVariant as GdArray ?? new GdArray();
            stats["edge_placements"] = (long)placements.Count;

            ValidateOccupancyRecords(occupancy, errors);
            ValidateFloorPlacements(plan, occupancy, topology, errors, stats);
            ValidateCeilingPlacements(plan, occupancy, topology, errors, stats);
            ValidateSocketBindings(plan, errors, stats);
            ValidateNotFloorOnly(plan, occupancy, errors);
            ValidateEdgePlacements(edges, placements, errors);
            ValidatePortalEndpoints(topology, occupancy, edges, errors);
            ValidateWalkableFloodFill(topology, occupancy, edges, errors);

            return Verdict(errors, stats);
        }

        static GdDict Verdict(List<string> errors, GdDict stats) => new GdDict
        {
            { "ok", errors.Count == 0 },
            { "errors", GdString.ToGdArray(errors) },
            { "stats", stats },
        };

        static StructuralEdgeCompiler.CellInfo ReadCell(object value, long defaultDeck) =>
            StructuralEdgeCompiler.ReadCell(value, defaultDeck);

        static bool IsInteger(object value) => StructuralEdgeCompiler.IsInteger(value);

        static void ValidateOccupancyRecords(GdDict occupancy, List<string> errors)
        {
            foreach (var kv in occupancy)
            {
                string occupancyKey = V.Str(kv.Key);
                if (!(kv.Value is GdDict record))
                {
                    errors.Add("occupancy record must be an object: " + occupancyKey);
                    continue;
                }
                var parsedCell = ReadCell(record.Get("cell", null), V.I64(record.Get("deck", -1L)));
                if (!parsedCell.Ok)
                {
                    errors.Add("occupancy cell is malformed: " + occupancyKey);
                    continue;
                }
                long deck = parsedCell.Deck;
                Vec2i cell = parsedCell.Cell;
                if (!IsInteger(record.Get("deck", null)) || V.I64(record.Get("deck")) != deck)
                    errors.Add("occupancy deck mismatch: " + occupancyKey);
                if (V.Str(record.Get("cell_key", occupancyKey)) != occupancyKey)
                    errors.Add("occupancy cell_key mismatch: " + occupancyKey);
                if (StructuralEdgeCompiler.CellKey(deck, cell) != occupancyKey)
                    errors.Add("occupancy canonical key mismatch: " + occupancyKey);
                if (V.Str(record.Get("room_id", "")).Length == 0)
                    errors.Add("occupancy room_id missing: " + occupancyKey);
            }
        }

        void ValidateFloorPlacements(GdDict plan, GdDict occupancy, GdDict topology, List<string> errors, GdDict stats)
        {
            if (!(plan.Get("floor_placements", null) is GdArray floors))
            {
                errors.Add("floor_placements must be a non-empty array");
                return;
            }
            stats["floor_placements"] = (long)floors.Count;
            if (floors.IsEmpty)
            {
                errors.Add("floor_placements must be non-empty");
                return;
            }

            var seenCellKeys = new GdDict();
            // The floor contract is exactly one record for each occupied cell.
            GdDict roomDecks = RoomDecks(topology);
            foreach (var floorVariant in floors)
            {
                if (!(floorVariant is GdDict floor))
                {
                    errors.Add("floor placement must be an object");
                    continue;
                }
                string cellKeyValue = V.Str(floor.Get("cell_key", ""));
                if (cellKeyValue.Length == 0)
                {
                    errors.Add("floor placement cell_key missing");
                    continue;
                }
                if (seenCellKeys.Has(cellKeyValue))
                {
                    errors.Add("duplicate floor placement cell_key: " + cellKeyValue);
                    continue;
                }
                seenCellKeys[cellKeyValue] = true;
                if (floor.Has("edge_key") && V.Str(floor.Get("edge_key", "")).Length != 0)
                    errors.Add("floor placement must not declare edge_key: " + cellKeyValue);
                string roomId = V.Str(floor.Get("room_id", ""));
                if (roomId.Length == 0) errors.Add("floor placement room_id missing: " + cellKeyValue);
                else if (!roomDecks.Has(roomId)) errors.Add("floor placement room unknown: " + roomId);
                object deckValue = floor.Get("deck", null);
                if (!IsInteger(deckValue))
                {
                    errors.Add("floor placement deck malformed: " + cellKeyValue);
                    continue;
                }
                long deck = V.I64(deckValue);
                var parsedCell = ReadCell(floor.Get("cell", null), deck);
                if (!parsedCell.Ok)
                {
                    errors.Add("floor placement cell malformed: " + cellKeyValue);
                    continue;
                }
                Vec2i cell = parsedCell.Cell;
                if (parsedCell.Deck != deck) errors.Add("floor placement deck/cell mismatch: " + cellKeyValue);
                string expectedKey = StructuralEdgeCompiler.CellKey(deck, cell);
                if (expectedKey != cellKeyValue)
                    errors.Add("floor placement cell mismatch: expected=" + expectedKey + " got=" + cellKeyValue);
                if (!occupancy.Has(cellKeyValue))
                {
                    errors.Add("floor placement has no occupancy cell: " + cellKeyValue);
                }
                else if (occupancy[cellKeyValue] is GdDict occupancyRecord)
                {
                    if (V.Str(occupancyRecord.Get("room_id", "")) != roomId)
                        errors.Add("floor placement room mismatch: " + cellKeyValue);
                    string occupancyModule = V.Str(occupancyRecord.Get("module_id", ""));
                    if (occupancyModule.Length != 0 && V.Str(floor.Get("module_id", "")) != occupancyModule)
                        errors.Add("floor placement module mismatch: " + cellKeyValue);
                }
                if (roomDecks.Has(roomId) && V.I64(roomDecks[roomId]) != deck)
                    errors.Add("floor placement room deck mismatch: " + cellKeyValue);
                string moduleId = V.Str(floor.Get("module_id", ""));
                if (!Contains(FLOOR_MODULES, moduleId)) errors.Add("unsupported floor placement module: " + moduleId);
                if (!ReadPosition(floor.Get("position", null), out Vec3 position))
                {
                    errors.Add("floor placement position malformed: " + cellKeyValue);
                }
                else
                {
                    Vec3 expectedPosition = StructuralEdgeCompiler.CellWorldPosition(deck, cell);
                    if (!IsEqualApproxV(position, expectedPosition)) errors.Add("floor placement position mismatch: " + cellKeyValue);
                }
                if (!IsZero(floor.Get("yaw_degrees", null))) errors.Add("floor placement yaw must be zero: " + cellKeyValue);
            }

            if (seenCellKeys.Count != occupancy.Count)
                errors.Add("floor placements are not an exact occupancy bijection: floors=" + GdString.FormatInt(seenCellKeys.Count) +
                           " occupancy=" + GdString.FormatInt(occupancy.Count));
            foreach (var occupancyKey in occupancy.Keys)
            {
                if (!seenCellKeys.Has(V.Str(occupancyKey))) errors.Add("occupancy cell has no floor placement: " + V.Str(occupancyKey));
            }
        }

        void ValidateCeilingPlacements(GdDict plan, GdDict occupancy, GdDict topology, List<string> errors, GdDict stats)
        {
            if (!(plan.Get("ceiling_placements", null) is GdArray ceilings))
            {
                errors.Add("ceiling_placements must be an array");
                return;
            }
            stats["ceiling_placements"] = (long)ceilings.Count;
            GdDict openingKeys = VerticalOpeningKeys(topology);
            long requiredCount = 0;
            foreach (var occupancyKey in occupancy.Keys)
                if (!openingKeys.Has(V.Str(occupancyKey))) requiredCount += 1;
            if (occupancy.Count > 0 && requiredCount > 0 && ceilings.IsEmpty)
            {
                errors.Add("ceiling_placements missing for occupied cells");
                return;
            }

            var seenCellKeys = new GdDict();
            foreach (var ceilingVariant in ceilings)
            {
                if (!(ceilingVariant is GdDict ceiling))
                {
                    errors.Add("ceiling placement must be an object");
                    continue;
                }
                string cellKeyValue = V.Str(ceiling.Get("cell_key", ""));
                if (cellKeyValue.Length == 0)
                {
                    errors.Add("ceiling placement cell_key missing");
                    continue;
                }
                if (seenCellKeys.Has(cellKeyValue))
                {
                    errors.Add("duplicate ceiling placement cell_key: " + cellKeyValue);
                    continue;
                }
                seenCellKeys[cellKeyValue] = true;
                if (openingKeys.Has(cellKeyValue)) errors.Add("ceiling placement on authored vertical opening: " + cellKeyValue);
                if (!occupancy.Has(cellKeyValue))
                {
                    errors.Add("ceiling placement has no occupancy cell: " + cellKeyValue);
                    continue;
                }
                if (occupancy[cellKeyValue] is GdDict occupancyRecord)
                {
                    if (V.Str(ceiling.Get("room_id", "")) != V.Str(occupancyRecord.Get("room_id", "")))
                        errors.Add("ceiling placement room mismatch: " + cellKeyValue);
                }
                object deckValue = ceiling.Get("deck", null);
                if (!IsInteger(deckValue))
                {
                    errors.Add("ceiling placement deck malformed: " + cellKeyValue);
                    continue;
                }
                long deck = V.I64(deckValue);
                var parsedCell = ReadCell(ceiling.Get("cell", null), deck);
                if (!parsedCell.Ok)
                {
                    errors.Add("ceiling placement cell malformed: " + cellKeyValue);
                    continue;
                }
                Vec2i cell = parsedCell.Cell;
                if (StructuralEdgeCompiler.CellKey(deck, cell) != cellKeyValue) errors.Add("ceiling placement cell mismatch: " + cellKeyValue);
                string moduleId = V.Str(ceiling.Get("module_id", ""));
                if (!Contains(CEILING_MODULES, moduleId) && GdString.Find(moduleId, "ceiling") < 0)
                    errors.Add("unsupported ceiling placement module: " + moduleId);
                if (!ReadPosition(ceiling.Get("position", null), out Vec3 position))
                {
                    errors.Add("ceiling placement position malformed: " + cellKeyValue);
                }
                else
                {
                    Vec3 expectedPosition = StructuralEdgeCompiler.CellWorldPosition(deck, cell);
                    if (!IsEqualApproxV(position, expectedPosition)) errors.Add("ceiling placement position mismatch: " + cellKeyValue);
                }
            }

            foreach (var occupancyKeyVariant in occupancy.Keys)
            {
                string occupancyKey = V.Str(occupancyKeyVariant);
                if (openingKeys.Has(occupancyKey)) continue;
                if (!seenCellKeys.Has(occupancyKey)) errors.Add("occupancy cell has no ceiling placement: " + occupancyKey);
            }
        }

        static void ValidateSocketBindings(GdDict plan, List<string> errors, GdDict stats)
        {
            object bindingsVariant = plan.Get("socket_bindings", null);
            long boundCount;
            if (bindingsVariant is GdArray bindings)
            {
                boundCount = bindings.Count;
                foreach (var bindingVariant in bindings)
                {
                    if (!(bindingVariant is GdDict binding))
                    {
                        errors.Add("socket_binding must be an object");
                        continue;
                    }
                    if (V.Str(binding.Get("placement_id", "")).Length == 0 || V.Str(binding.Get("socket_id", "")).Length == 0)
                        errors.Add("socket_binding missing placement_id or socket_id");
                    if (V.Str(binding.Get("neighbor_placement_id", "")).Length == 0 || V.Str(binding.Get("neighbor_socket_id", "")).Length == 0)
                        errors.Add("socket_binding missing neighbor ids");
                }
            }
            else if (bindingsVariant is GdDict bindingDict)
            {
                boundCount = bindingDict.Count;
            }
            else
            {
                errors.Add("socket_bindings must be an array");
                return;
            }
            stats["socket_bindings"] = boundCount;
            if (boundCount <= 0)
            {
                long placementBound = 0;
                var all = new GdArray();
                all.AppendArray(plan.GetArrayOrEmpty("placements"));
                all.AppendArray(plan.GetArrayOrEmpty("floor_placements"));
                all.AppendArray(plan.GetArrayOrEmpty("ceiling_placements"));
                foreach (var recordVariant in all)
                {
                    if (!(recordVariant is GdDict record)) continue;
                    if (record.Has("socket_bindings") && record.Get("socket_bindings", new GdArray()) is GdArray nested)
                        placementBound += nested.Count;
                }
                if (placementBound <= 0) errors.Add("socket_bindings missing");
            }
        }

        static void ValidateNotFloorOnly(GdDict plan, GdDict occupancy, List<string> errors)
        {
            if (occupancy.IsEmpty) return;
            if (!(plan.Get("placements", new GdArray()) is GdArray placements))
            {
                errors.Add("floor-only structural plan: missing edge placements");
                return;
            }
            long enclosureCount = 0;
            foreach (var placementVariant in placements)
            {
                if (!(placementVariant is GdDict placement)) continue;
                string moduleId = V.Str(placement.Get("module_id", ""));
                if (GdString.Find(moduleId, "wall") >= 0 || GdString.Find(moduleId, "door") >= 0 || GdString.Find(moduleId, "portal") >= 0)
                    enclosureCount += 1;
            }
            if (enclosureCount <= 0) errors.Add("floor-only structural plan: no wall or portal placements");
        }

        static GdDict VerticalOpeningKeys(GdDict topology)
        {
            var keys = new GdDict();
            if (!(topology.Get("vertical_connections", new GdArray()) is GdArray vertical)) return keys;
            GdDict roomDecks = RoomDecks(topology);
            foreach (var linkVariant in vertical)
            {
                if (!(linkVariant is GdDict link)) continue;
                string fromRoom = V.Str(link.Get("from_room", ""));
                string toRoom = V.Str(link.Get("to_room", ""));
                long fromDeck = V.I64(roomDecks.Get(fromRoom, -1L));
                long toDeck = V.I64(roomDecks.Get(toRoom, -1L));
                var fromInfo = ReadCell(link.Get("from_cell", null), fromDeck);
                var toInfo = ReadCell(link.Get("to_cell", null), toDeck);
                if (fromInfo.Ok) keys[StructuralEdgeCompiler.CellKey(fromInfo.Deck, fromInfo.Cell)] = true;
                if (toInfo.Ok) keys[StructuralEdgeCompiler.CellKey(toInfo.Deck, toInfo.Cell)] = true;
            }
            return keys;
        }

        static void ValidateEdgePlacements(GdDict edges, GdArray placements, List<string> errors)
        {
            var seenEdgeKeys = new GdDict();
            foreach (var placementVariant in placements)
            {
                if (!(placementVariant is GdDict placement))
                {
                    errors.Add("edge placement must be an object");
                    continue;
                }
                string edgeKeyValue = V.Str(placement.Get("edge_key", ""));
                if (edgeKeyValue.Length == 0)
                {
                    errors.Add("edge placement missing edge_key");
                    continue;
                }
                if (seenEdgeKeys.Has(edgeKeyValue))
                {
                    errors.Add("duplicate edge placement: " + edgeKeyValue);
                    continue;
                }
                seenEdgeKeys[edgeKeyValue] = true;
                if (!edges.Has(edgeKeyValue))
                {
                    errors.Add("edge placement references missing edge: " + edgeKeyValue);
                    continue;
                }
                if (!(edges[edgeKeyValue] is GdDict edge))
                {
                    errors.Add("edge record must be an object: " + edgeKeyValue);
                    continue;
                }
                string kind = V.Str(placement.Get("kind", ""));
                if (!Contains(EDGE_KINDS, kind)) errors.Add("unsupported edge kind: " + kind);
                if (kind == "OPEN") errors.Add("OPEN edge must not have a placement: " + edgeKeyValue);
                if (Contains(FLOOR_MODULES, V.Str(placement.Get("module_id", "")))) errors.Add("floor module cannot be an edge placement: " + edgeKeyValue);
                if (V.Str(edge.Get("kind", edge.Get("state", ""))) != kind) errors.Add("edge placement kind mismatch: " + edgeKeyValue);
                if (V.Str(edge.Get("module_id", "")) != V.Str(placement.Get("module_id", ""))) errors.Add("edge placement module mismatch: " + edgeKeyValue);
                ValidateEdgePose(placement, edgeKeyValue, errors);
            }

            foreach (var kv in edges)
            {
                string edgeKeyValue = V.Str(kv.Key);
                if (!(kv.Value is GdDict edge))
                {
                    errors.Add("edge record must be an object: " + edgeKeyValue);
                    continue;
                }
                string kind = V.Str(edge.Get("kind", edge.Get("state", "")));
                if (!Contains(EDGE_KINDS, kind)) errors.Add("unsupported edge kind: " + kind);
                if (kind != "OPEN" && V.Bool(edge.Get("wrapper_required", edge.Get("placement_required", true))) && !seenEdgeKeys.Has(edgeKeyValue))
                    errors.Add("required edge has no placement: " + edgeKeyValue);
            }
        }

        static void ValidateEdgePose(GdDict placement, string edgeKeyValue, List<string> errors)
        {
            object deckVariant = placement.Get("deck", null);
            string direction = V.Str(placement.Get("direction", ""));
            if (!IsInteger(deckVariant) || !StructuralEdgeCompiler.DIRECTIONS.Has(direction))
            {
                errors.Add("edge placement grid pose malformed: " + edgeKeyValue);
                return;
            }
            long deck = V.I64(deckVariant);
            var parsedCell = ReadCell(placement.Get("cell", null), deck);
            if (!parsedCell.Ok)
            {
                errors.Add("edge placement cell malformed: " + edgeKeyValue);
                return;
            }
            Vec2i cell = parsedCell.Cell;
            string expectedKey = StructuralEdgeCompiler.EdgeKey(deck, cell, direction);
            if (expectedKey != edgeKeyValue) errors.Add("edge placement edge_key mismatch: " + edgeKeyValue);
            Vec3 expectedPosition = StructuralEdgeCompiler.EdgeWorldPosition(deck, cell, direction);
            if (!ReadPosition(placement.Get("position", null), out Vec3 position) || !IsEqualApproxV(position, expectedPosition))
                errors.Add("edge placement position mismatch: " + edgeKeyValue);
            double expectedYaw = V.F64(StructuralEdgeCompiler.YAW_DEGREES[direction]);
            if (!IsNumber(placement.Get("yaw_degrees", null)) || !GdMath.IsEqualApprox(V.F64(placement.Get("yaw_degrees")), expectedYaw))
                errors.Add("edge placement yaw mismatch: " + edgeKeyValue);
        }

        static void ValidatePortalEndpoints(GdDict topology, GdDict occupancy, GdDict edges, List<string> errors)
        {
            if (!(topology.Get("portals", null) is GdArray portals))
            {
                errors.Add("topology portals must be an array");
                return;
            }
            GdDict roomDecks = RoomDecks(topology);
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
                if (!roomDecks.Has(fromRoom) || !roomDecks.Has(toRoom))
                {
                    errors.Add("portal room endpoints are not reciprocal: " + portalId);
                    continue;
                }
                var fromInfo = ReadCell(portal.Get("from_cell", null), V.I64(roomDecks[fromRoom]));
                var toInfo = ReadCell(portal.Get("to_cell", null), V.I64(roomDecks[toRoom]));
                if (!fromInfo.Ok || !toInfo.Ok)
                {
                    errors.Add("portal endpoints are malformed: " + portalId);
                    continue;
                }
                if (fromInfo.Deck != toInfo.Deck)
                {
                    errors.Add("portal endpoints must be same-deck: " + portalId);
                    continue;
                }
                Vec2i fromCell = fromInfo.Cell;
                Vec2i toCell = toInfo.Cell;
                string fromKey = StructuralEdgeCompiler.CellKey(fromInfo.Deck, fromCell);
                string toKey = StructuralEdgeCompiler.CellKey(toInfo.Deck, toCell);
                if (OccupancyRoom(occupancy, fromKey) != fromRoom || OccupancyRoom(occupancy, toKey) != toRoom)
                {
                    errors.Add("portal endpoints are not reciprocal: " + portalId);
                    continue;
                }
                string declaredFromDirection = V.Str(portal.Get("from_direction", ""));
                string declaredToDirection = V.Str(portal.Get("to_direction", ""));
                if (declaredFromDirection.Length != 0 || declaredToDirection.Length != 0)
                {
                    if (!StructuralEdgeCompiler.OPPOSITE.Has(declaredFromDirection) ||
                        declaredToDirection != V.Str(StructuralEdgeCompiler.OPPOSITE[declaredFromDirection]))
                        errors.Add("opposed portal normals mismatch: " + portalId);
                }
                Vec2i delta = toCell - fromCell;
                Vec2i edgeCell = fromCell;
                string direction = "";
                foreach (var candidate in StructuralEdgeCompiler.DIRECTIONS.Keys)
                {
                    if ((Vec2i)StructuralEdgeCompiler.DIRECTIONS[candidate] == delta)
                    {
                        direction = V.Str(candidate);
                        break;
                    }
                }
                bool logicalBoundary = false;
                if (direction.Length == 0 && portal.Get("edge_cell", null) != null)
                {
                    var edgeInfo = ReadCell(portal.Get("edge_cell", null), fromInfo.Deck);
                    string declaredDirection = V.Str(portal.Get("edge_direction", ""));
                    if (edgeInfo.Ok && StructuralEdgeCompiler.DIRECTIONS.Has(declaredDirection))
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
                string edgeKeyValue = StructuralEdgeCompiler.EdgeKey(fromInfo.Deck, edgeCell, direction);
                if (!edges.Has(edgeKeyValue))
                {
                    errors.Add("portal has no canonical edge: " + edgeKeyValue);
                    continue;
                }
                var edge = (GdDict)edges[edgeKeyValue];
                if (!V.Bool(edge.Get("portal", false))) errors.Add("portal edge was compiled as non-portal: " + edgeKeyValue);
                if (V.Str(edge.Get("kind", "SOLID")) == "SOLID") errors.Add("topology-connected rooms blocked by SOLID edge: " + edgeKeyValue);
                if (logicalBoundary && V.Str(edge.Get("other_room", "")) != toRoom) errors.Add("logical portal room endpoint mismatch: " + edgeKeyValue);
            }
        }

        static string OccupancyRoom(GdDict occupancy, string cellKeyValue)
        {
            if (!occupancy.Has(cellKeyValue)) return "";
            if (!(occupancy[cellKeyValue] is GdDict record)) return "";
            return V.Str(record.Get("room_id", ""));
        }

        static void ValidateWalkableFloodFill(GdDict topology, GdDict occupancy, GdDict edges, List<string> errors)
        {
            var adjacency = new GdDict();
            foreach (var occupancyKey in occupancy.Keys) adjacency[V.Str(occupancyKey)] = new GdArray();
            foreach (var edgeVariant in edges.Values)
            {
                if (!(edgeVariant is GdDict edge)) continue;
                string kind = V.Str(edge.Get("kind", edge.Get("state", "SOLID")));
                if (!WalkabilityContract.EnclosurePassable(kind)) continue;
                GdArray sourceCells = edge.Get("source_cells", new GdArray()) as GdArray ?? new GdArray();
                if (sourceCells.Count < 2) continue;
                if (!CellKeyFromValue(sourceCells[0], V.I64(edge.Get("deck", -1L)), out string firstKey)) continue;
                if (!CellKeyFromValue(sourceCells[1], V.I64(edge.Get("deck", -1L)), out string secondKey)) continue;
                if (!adjacency.Has(firstKey) || !adjacency.Has(secondKey)) continue;
                ((GdArray)adjacency[firstKey]).Append(secondKey);
                ((GdArray)adjacency[secondKey]).Append(firstKey);
            }

            if (topology.Get("vertical_connections", new GdArray()) is GdArray vertical)
            {
                GdDict roomDecks = RoomDecks(topology);
                foreach (var linkVariant in vertical)
                {
                    if (!(linkVariant is GdDict link)) continue;
                    string fromRoom = V.Str(link.Get("from_room", ""));
                    string toRoom = V.Str(link.Get("to_room", ""));
                    if (!roomDecks.Has(fromRoom) || !roomDecks.Has(toRoom)) continue;
                    var fromInfo = ReadCell(link.Get("from_cell", null), V.I64(roomDecks[fromRoom]));
                    var toInfo = ReadCell(link.Get("to_cell", null), V.I64(roomDecks[toRoom]));
                    if (!fromInfo.Ok || !toInfo.Ok) continue;
                    string fromKey = StructuralEdgeCompiler.CellKey(fromInfo.Deck, fromInfo.Cell);
                    string toKey = StructuralEdgeCompiler.CellKey(toInfo.Deck, toInfo.Cell);
                    if (adjacency.Has(fromKey) && adjacency.Has(toKey))
                    {
                        ((GdArray)adjacency[fromKey]).Append(toKey);
                        ((GdArray)adjacency[toKey]).Append(fromKey);
                    }
                }
            }

            if (topology.Get("portals", new GdArray()) is GdArray portals)
            {
                foreach (var portalVariant in portals)
                {
                    if (!(portalVariant is GdDict portal)) continue;
                    GdDict roomDecks = RoomDecks(topology);
                    string fromRoom = V.Str(portal.Get("from_room", ""));
                    string toRoom = V.Str(portal.Get("to_room", ""));
                    if (!roomDecks.Has(fromRoom) || !roomDecks.Has(toRoom)) continue;
                    var fromInfo = ReadCell(portal.Get("from_cell", null), V.I64(roomDecks[fromRoom]));
                    var toInfo = ReadCell(portal.Get("to_cell", null), V.I64(roomDecks[toRoom]));
                    if (!fromInfo.Ok || !toInfo.Ok) continue;
                    string fromKey = StructuralEdgeCompiler.CellKey(fromInfo.Deck, fromInfo.Cell);
                    string toKey = StructuralEdgeCompiler.CellKey(toInfo.Deck, toInfo.Cell);
                    if (!adjacency.Has(fromKey) || !adjacency.Has(toKey)) continue;
                    if (!Reachable(adjacency, fromKey, toKey))
                    {
                        // A diagonal legacy link has an explicit rendered boundary, but must still participate in
                        // logical flood fill exactly once.
                        if (V.Bool(portal.Get("logical_boundary", false)))
                        {
                            ((GdArray)adjacency[fromKey]).Append(toKey);
                            ((GdArray)adjacency[toKey]).Append(fromKey);
                        }
                        if (!Reachable(adjacency, fromKey, toKey))
                            errors.Add("flood-fill/topology reachability disagreement: " + V.Str(portal.Get("id", "")));
                    }
                }
            }

            ValidateCriticalPathReachability(topology, occupancy, adjacency, errors);
        }

        static void ValidateCriticalPathReachability(GdDict topology, GdDict occupancy, GdDict adjacency, List<string> errors)
        {
            if (!(topology.Get("critical_path", new GdArray()) is GdArray critical)) return;
            for (int index = 0; index < critical.Count - 1; index++)
            {
                string fromRoom = V.Str(critical[index]);
                string toRoom = V.Str(critical[index + 1]);
                List<string> fromCells = RoomCells(occupancy, fromRoom);
                List<string> toCells = RoomCells(occupancy, toRoom);
                if (fromCells.Count == 0 || toCells.Count == 0)
                {
                    errors.Add("topology reachability room missing: " + fromRoom + " -> " + toRoom);
                    continue;
                }
                bool connected = false;
                foreach (string fromKey in fromCells)
                {
                    foreach (string toKey in toCells)
                    {
                        if (Reachable(adjacency, fromKey, toKey))
                        {
                            connected = true;
                            break;
                        }
                    }
                    if (connected) break;
                }
                if (!connected) errors.Add("flood-fill/topology reachability disagreement: " + fromRoom + " -> " + toRoom);
            }
        }

        static List<string> RoomCells(GdDict occupancy, string roomId)
        {
            var cells = new List<string>();
            foreach (var keyVariant in occupancy.Keys)
            {
                string key = V.Str(keyVariant);
                if (OccupancyRoom(occupancy, key) == roomId) cells.Add(key);
            }
            return cells;
        }

        static bool Reachable(GdDict adjacency, string startKey, string goalKey)
        {
            var queue = new List<string> { startKey };
            var visited = new HashSet<string> { startKey };
            int head = 0;
            while (head < queue.Count)
            {
                string current = queue[head++];
                if (current == goalKey) return true;
                foreach (var neighborVariant in adjacency.GetArrayOrEmpty(current))
                {
                    string neighbor = V.Str(neighborVariant);
                    if (visited.Add(neighbor)) queue.Add(neighbor);
                }
            }
            return false;
        }

        static GdDict RoomDecks(GdDict topology)
        {
            var output = new GdDict();
            if (!(topology.Get("rooms", new GdArray()) is GdArray rooms)) return output;
            foreach (var roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room)) continue;
                string roomId = V.Str(room.Get("id", ""));
                if (roomId.Length != 0 && IsInteger(room.Get("deck", null))) output[roomId] = V.I64(room.Get("deck"));
            }
            return output;
        }

        static bool CellKeyFromValue(object value, long defaultDeck, out string key)
        {
            var info = ReadCell(value, defaultDeck);
            key = info.Ok ? StructuralEdgeCompiler.CellKey(info.Deck, info.Cell) : "";
            return info.Ok;
        }

        static bool ReadPosition(object value, out Vec3 position)
        {
            position = Vec3.Zero;
            if (value is Vec3 v)
            {
                position = v;
                return true;
            }
            if (value is GdArray values)
            {
                if (values.Count < 3 || !IsNumber(values[0]) || !IsNumber(values[1]) || !IsNumber(values[2])) return false;
                position = new Vec3(V.F64(values[0]), V.F64(values[1]), V.F64(values[2]));
                return true;
            }
            if (value is string s)
            {
                List<double> parsed = StructuralEdgeCompiler.ParseVectorString(s, 3);
                if (parsed.Count == 3)
                {
                    position = new Vec3(parsed[0], parsed[1], parsed[2]);
                    return true;
                }
            }
            return false;
        }

        static bool IsNumber(object value) =>
            value is long || value is double || (value is string s && StructuralEdgeCompiler.IsValidFloat(s));

        static bool IsZero(object value) => IsNumber(value) && GdMath.IsEqualApprox(V.F64(value), 0.0);

        static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return true;
            return false;
        }

        /// <summary><c>Vector3.is_equal_approx</c>: per-component <c>Math::is_equal_approx(float, float)</c>.</summary>
        internal static bool IsEqualApproxV(Vec3 a, Vec3 b) => IsEqualApproxF(a.X, b.X) && IsEqualApproxF(a.Y, b.Y) && IsEqualApproxF(a.Z, b.Z);

        internal static bool IsEqualApproxF(float a, float b)
        {
            if (a == b) return true;
            float tolerance = (float)((float)GdMath.CmpEpsilon * System.Math.Abs(a));
            if (tolerance < (float)GdMath.CmpEpsilon) tolerance = (float)GdMath.CmpEpsilon;
            return System.Math.Abs((float)(a - b)) < tolerance;
        }
    }
}
