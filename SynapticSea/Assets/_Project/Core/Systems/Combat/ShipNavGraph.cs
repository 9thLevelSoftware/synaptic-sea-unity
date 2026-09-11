// Ported from scripts/systems/ship_nav_graph.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure walkable-cell graph built from a ship layout (ADR-0049, ADR-0054). The production graph is
    /// standing-play: compiler OPEN/DOOR/HATCH plus layout.vertical_connections, with LOCKED/BREACH present at
    /// <see cref="BLOCKED_COST"/>. Floor-module 4-connect is the fallback when structural_plan is absent.
    /// Node positions are Godot-frame <see cref="Vec3"/> (float32); component math is done in double like GDScript.
    /// </summary>
    public sealed class ShipNavGraph
    {
        public const double DEFAULT_CELL_SIZE = 4.0;
        public const double DEFAULT_DECK_HEIGHT = 4.0;
        public const double BLOCKED_COST = 1.0e9;
        public const double FIRE_COST_MULT = 6.0;

        public static readonly IReadOnlyList<string> FLOOR_MODULE_PREFIXES = new[] { "floor_", "corridor_floor", "ramp_" };

        static readonly Vec2i InvalidCell = new Vec2i(-99999, -99999);

        public double CellSize = DEFAULT_CELL_SIZE;
        public double DeckHeight = DEFAULT_DECK_HEIGHT;
        /// <summary>node_id -> { "pos": Vec3, "room_id": String, "key": String[, "cell_key": String] }.</summary>
        public GdDict Nodes = new GdDict();
        /// <summary>Undirected edge key "a|b" -> cost (float).</summary>
        public GdDict Edges = new GdDict();
        /// <summary>Base edges frozen after build (for re-applying dynamic costs).</summary>
        GdDict _baseEdges = new GdDict();
        public bool Dirty = true;

        public void Clear()
        {
            Nodes.Clear();
            Edges.Clear();
            _baseEdges.Clear();
            Dirty = true;
        }

        /// <summary>Builds the graph from a layout.json-shaped dictionary. Returns the node count.</summary>
        public long BuildFromLayout(GdDict layout)
        {
            Clear();
            CellSize = Math.Max(0.1, V.F64(layout.Get("cell_size", DEFAULT_CELL_SIZE)));
            DeckHeight = Math.Max(0.1, V.F64(layout.Get("deck_height", DEFAULT_DECK_HEIGHT)));
            object planVariant = layout.Get("structural_plan", new GdDict());
            if (planVariant is GdDict plan)
            {
                if (!plan.IsEmpty && plan.Has("occupancy") && plan.Has("edges"))
                    return BuildFromStructuralPlan(layout);
            }
            return BuildFromFloorPlacements(layout);
        }

        public long BuildFromStructuralPlan(GdDict layout)
        {
            object planVariant = layout.Get("structural_plan", new GdDict());
            if (!(planVariant is GdDict plan)) return 0;
            object occupancyVariant = plan.Get("occupancy", new GdDict());
            object edgesVariant = plan.Get("edges", new GdDict());
            if (!(occupancyVariant is GdDict occupancy) || !(edgesVariant is GdDict planEdges)) return 0;
            foreach (object occupancyKeyVariant in new List<object>(occupancy.Keys))
            {
                object recordVariant = occupancy[occupancyKeyVariant];
                if (!(recordVariant is GdDict record)) continue;
                Vec3 pos = OccupancyWorldPosition(record);
                string key = KeyForPos(pos);
                if (Nodes.Has(key)) continue;
                Nodes[key] = new GdDict
                {
                    { "pos", SnapPos(pos) },
                    { "room_id", V.Str(record.Get("room_id", "")) },
                    { "key", key },
                    { "cell_key", V.Str(record.Get("cell_key", occupancyKeyVariant)) },
                };
            }
            foreach (object edgeVariant in new List<object>(planEdges.Values))
            {
                if (!(edgeVariant is GdDict edge)) continue;
                string kind = V.Str(edge.Get("kind", edge.Get("state", "SOLID"))).ToUpperInvariant();
                if (kind == "SOLID") continue;
                List<string> pair = EdgeNodeKeys(edge, occupancy, layout);
                if (pair.Count != 2) continue;
                double cost = StandingCostForKind(kind);
                SetBaseEdge(pair[0], pair[1], cost);
            }
            // Vertical hops first; blocked_links overlay last so a blocked cross-deck room_link cannot be reopened.
            AddVerticalConnectionEdges(layout, occupancy);
            OverlayBlockedLinks(layout, occupancy);
            _baseEdges = Edges.DeepCopy();
            Dirty = false;
            return Nodes.Count;
        }

        public static double StandingCostForKind(string kind)
        {
            string k = kind.ToUpperInvariant();
            if (k == "OPEN" || k == "DOOR") return 1.0;
            if (k == "HATCH") return 1.15;
            return BLOCKED_COST;
        }

        public static double CrouchCostForKind(string kind)
        {
            string k = kind.ToUpperInvariant();
            if (k == "BREACH") return 1.75;
            if (k == "LOCKED" || k == "SOLID") return BLOCKED_COST;
            return StandingCostForKind(k);
        }

        long BuildFromFloorPlacements(GdDict layout)
        {
            object roomsV = layout.Get("rooms", new GdArray());
            if (!(roomsV is GdArray rooms)) return 0;
            foreach (object roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room)) continue;
                string roomId = V.Str(room.Get("id", ""));
                object placementsV = room.Get("structural_placements", new GdArray());
                if (!(placementsV is GdArray placements)) continue;
                foreach (object placementVariant in placements)
                {
                    if (!(placementVariant is GdDict placement)) continue;
                    string moduleId = V.Str(placement.Get("module_id", placement.Get("module", "")));
                    if (!IsFloorModule(moduleId)) continue;
                    Vec3 pos = ReadWorldPosition(placement);
                    if (pos == Vec3.Inf) continue;
                    string key = KeyForPos(pos);
                    if (Nodes.Has(key)) continue;
                    Nodes[key] = new GdDict
                    {
                        { "pos", SnapPos(pos) },
                        { "room_id", roomId },
                        { "key", key },
                    };
                }
            }
            object structuralPlanVariant = layout.Get("structural_plan", null);
            if (structuralPlanVariant is GdDict structuralPlan && !structuralPlan.IsEmpty)
            {
                ConnectStructuralEdges(structuralPlan);
                ConnectVerticalNeighbors();
            }
            else
            {
                ConnectOrthogonalNeighbors();
            }
            _baseEdges = Edges.DeepCopy();
            Dirty = false;
            return Nodes.Count;
        }

        public long NodeCount() => Nodes.Count;

        public long EdgeCount() => Edges.Count;

        public bool HasNode(string nodeId) => Nodes.Has(nodeId);

        public Vec3 GetNodePos(string nodeId)
        {
            if (!Nodes.Has(nodeId)) return Vec3.Inf;
            object pos = ((GdDict)Nodes[nodeId]).Get("pos", Vec3.Inf);
            return pos is Vec3 v ? v : Vec3.Inf;
        }

        public string GetNodeRoom(string nodeId)
        {
            if (!Nodes.Has(nodeId)) return "";
            return V.Str(((GdDict)Nodes[nodeId]).Get("room_id", ""));
        }

        /// <summary>Nearest graph node to a world position ("" when the graph is empty).</summary>
        public string NearestNode(Vec3 worldPos)
        {
            string best = "";
            double bestD = double.PositiveInfinity;
            foreach (object key in Nodes.Keys)
            {
                Vec3 p = GetNodePos(V.Str(key));
                double d = p.DistanceSquaredTo(worldPos);
                if (d < bestD)
                {
                    bestD = d;
                    best = V.Str(key);
                }
            }
            return best;
        }

        /// <summary>Passable neighbours as [{ "to": node_id, "cost": float }], skipping BLOCKED_COST edges.</summary>
        public GdArray Neighbors(string nodeId)
        {
            var output = new GdArray();
            if (!Nodes.Has(nodeId)) return output;
            foreach (var kv in Edges)
            {
                double cost = V.F64(kv.Value);
                if (cost >= BLOCKED_COST) continue;
                string[] parts = V.Str(kv.Key).Split('|');
                if (parts.Length != 2) continue;
                if (parts[0] == nodeId)
                    output.Append(new GdDict { { "to", parts[1] }, { "cost", cost } });
                else if (parts[1] == nodeId)
                    output.Append(new GdDict { { "to", parts[0] }, { "cost", cost } });
            }
            return output;
        }

        public double EdgeCost(string a, string b)
        {
            string k = EdgeKey(a, b);
            if (!Edges.Has(k)) return BLOCKED_COST;
            return V.F64(Edges[k]);
        }

        /// <summary>Public cell -> standing-node lookup. Occupancy records win; else the snapped cell if that node exists.</summary>
        public string NodeKeyForCell(object value, long fallbackDeck, GdDict occupancy) =>
            NodeKeyFromCell(value, fallbackDeck, occupancy);

        public List<string> NodeKeysForEdge(GdDict edge, GdDict occupancy, GdDict layout = null) =>
            EdgeNodeKeys(edge, occupancy, layout ?? new GdDict());

        public static string OccupancyKeyForCell(object value, long fallbackDeck)
        {
            Vec2i cell;
            long deck = fallbackDeck;
            if (value is Vec2i v)
            {
                cell = v;
            }
            else if (value is GdArray values && values.Count >= 2)
            {
                cell = new Vec2i((int)V.I64(values[0]), (int)V.I64(values[1]));
                if (values.Count >= 3) deck = V.I64(values[2]);
            }
            else
            {
                return "";
            }
            return SurvivalCompat.FormatD(deck) + "|" + SurvivalCompat.FormatD(cell.X) + "|" + SurvivalCompat.FormatD(cell.Y);
        }

        public bool OccupancyHasCell(GdDict occupancy, object value, long fallbackDeck)
        {
            string key = OccupancyKeyForCell(value, fallbackDeck);
            return key.Length != 0 && occupancy.Has(key);
        }

        public bool HasBaseEdge(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0 || a == b) return false;
            return _baseEdges.Has(EdgeKey(a, b));
        }

        public double BaseEdgeCost(string a, string b)
        {
            string k = EdgeKey(a, b);
            if (!_baseEdges.Has(k)) return BLOCKED_COST;
            return V.F64(_baseEdges[k]);
        }

        /// <summary>Undirected base hops as [a, b] pairs (GDScript PackedStringArray pairs). Includes BLOCKED_COST edges.</summary>
        public GdArray BaseEdgePairs()
        {
            var output = new GdArray();
            foreach (object edgeKey in _baseEdges.Keys)
            {
                string[] parts = V.Str(edgeKey).Split('|');
                if (parts.Length != 2) continue;
                output.Append(GdArray.Of(parts[0], parts[1]));
            }
            return output;
        }

        public void SetEdgeBlocked(string a, string b, bool blocked = true)
        {
            string k = EdgeKey(a, b);
            if (!_baseEdges.Has(k) && !Edges.Has(k)) return;
            if (blocked)
                Edges[k] = BLOCKED_COST;
            else
                Edges[k] = V.F64(_baseEdges.Get(k, 1.0));
            Dirty = true;
        }

        public void SetEdgeCostMultiplier(string a, string b, double mult)
        {
            string k = EdgeKey(a, b);
            if (!_baseEdges.Has(k)) return;
            double baseCost = V.F64(_baseEdges[k]);
            Edges[k] = baseCost * Math.Max(0.0, mult);
            Dirty = true;
        }

        /// <summary>Resets dynamic costs to the static base graph.</summary>
        public void ResetDynamicCosts()
        {
            Edges = _baseEdges.DeepCopy();
            Dirty = true;
        }

        /// <summary>Fire cost: any edge touching a room on fire gets FIRE_COST_MULT * max(1, intensity). fire_rooms: room_id -> intensity.</summary>
        public void ApplyFireCosts(GdDict fireRooms)
        {
            if (fireRooms.IsEmpty) return;
            foreach (object edgeKey in _baseEdges.Keys)
            {
                string[] parts = V.Str(edgeKey).Split('|');
                if (parts.Length != 2) continue;
                string ra = GetNodeRoom(parts[0]);
                string rb = GetNodeRoom(parts[1]);
                double intensity = Math.Max(V.F64(fireRooms.Get(ra, 0.0)), V.F64(fireRooms.Get(rb, 0.0)));
                if (intensity <= 0.0) continue;
                double baseCost = V.F64(_baseEdges[edgeKey]);
                Edges[edgeKey] = baseCost * FIRE_COST_MULT * Math.Max(1.0, intensity);
            }
            Dirty = true;
        }

        /// <summary>Blocks edges whose endpoints straddle a bulkhead pair (room_id substring match, case-insensitive).</summary>
        public void BlockBulkhead(string compartmentA, string compartmentB)
        {
            if (compartmentA.Length == 0 || compartmentB.Length == 0) return;
            foreach (object edgeKey in _baseEdges.Keys)
            {
                string[] parts = V.Str(edgeKey).Split('|');
                if (parts.Length != 2) continue;
                string ra = GetNodeRoom(parts[0]).ToLowerInvariant();
                string rb = GetNodeRoom(parts[1]).ToLowerInvariant();
                string ca = compartmentA.ToLowerInvariant();
                string cb = compartmentB.ToLowerInvariant();
                // Cross edge: one endpoint matches the A family, the other the B family.
                bool aOn0 = SurvivalCompat.Find(ra, ca) >= 0;
                bool aOn1 = SurvivalCompat.Find(rb, ca) >= 0;
                bool bOn0 = SurvivalCompat.Find(ra, cb) >= 0;
                bool bOn1 = SurvivalCompat.Find(rb, cb) >= 0;
                if ((aOn0 && bOn1) || (bOn0 && aOn1)) Edges[edgeKey] = BLOCKED_COST;
            }
            Dirty = true;
        }

        public void MarkDirty()
        {
            Dirty = true;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "node_count", (long)Nodes.Count },
                { "edge_count", (long)Edges.Count },
                { "cell_size", CellSize },
                { "dirty", Dirty },
            };
        }

        static bool IsFloorModule(string moduleId)
        {
            if (moduleId.Length == 0) return false;
            foreach (string prefix in FLOOR_MODULE_PREFIXES)
            {
                if (SurvivalCompat.BeginsWith(moduleId, prefix) || SurvivalCompat.Find(moduleId, prefix) >= 0) return true;
            }
            return false;
        }

        static Vec3 ReadWorldPosition(GdDict placement) =>
            Vec3FromVariant(placement.Get("world_position", placement.Get("position", null)));

        Vec3 SnapPos(Vec3 pos)
        {
            // GDScript reads each float32 component as a 64-bit float; the Vector3 constructor narrows back.
            double gx = GdMath.Round(pos.X / CellSize) * CellSize;
            double gy = GdMath.Round(pos.Y / DeckHeight) * DeckHeight;
            double gz = GdMath.Round(pos.Z / CellSize) * CellSize;
            return new Vec3(gx, gy, gz);
        }

        string KeyForPos(Vec3 pos)
        {
            Vec3 s = SnapPos(pos);
            return SurvivalCompat.FormatD(GdMath.RoundI(s.X / CellSize)) + ":" +
                   SurvivalCompat.FormatD(GdMath.RoundI(s.Y / DeckHeight)) + ":" +
                   SurvivalCompat.FormatD(GdMath.RoundI(s.Z / CellSize));
        }

        static string EdgeKey(string a, string b) => SurvivalCompat.Less(a, b) ? a + "|" + b : b + "|" + a;

        void SetBaseEdge(string a, string b, double cost)
        {
            if (a.Length == 0 || b.Length == 0 || a == b) return;
            if (!Nodes.Has(a) || !Nodes.Has(b)) return;
            Edges[EdgeKey(a, b)] = cost;
        }

        void OverlayBlockedLinks(GdDict layout, GdDict occupancy)
        {
            object blockedVariant = layout.Get("blocked_links", new GdArray());
            if (!(blockedVariant is GdArray blocked)) return;
            GdDict roomDecks = RoomDecks(layout);
            foreach (object linkVariant in blocked)
            {
                if (!(linkVariant is GdDict link)) continue;
                string fromRoom = V.Str(link.Get("from_room", ""));
                string toRoom = V.Str(link.Get("to_room", ""));
                string fromKey = NodeKeyFromCell(link.Get("from_cell", null), V.I64(roomDecks.Get(fromRoom, 0L)), occupancy);
                string toKey = NodeKeyFromCell(link.Get("to_cell", null), V.I64(roomDecks.Get(toRoom, 0L)), occupancy);
                if (fromKey.Length == 0 || toKey.Length == 0) continue;
                SetBaseEdge(fromKey, toKey, BLOCKED_COST);
            }
        }

        void AddVerticalConnectionEdges(GdDict layout, GdDict occupancy)
        {
            object verticalVariant = layout.Get("vertical_connections", new GdArray());
            if (!(verticalVariant is GdArray vertical)) return;
            GdDict roomDecks = RoomDecks(layout);
            foreach (object linkVariant in vertical)
            {
                if (!(linkVariant is GdDict link)) continue;
                string fromRoom = V.Str(link.Get("from_room", ""));
                string toRoom = V.Str(link.Get("to_room", ""));
                string fromKey = NodeKeyFromCell(link.Get("from_cell", null), V.I64(roomDecks.Get(fromRoom, 0L)), occupancy);
                string toKey = NodeKeyFromCell(link.Get("to_cell", null), V.I64(roomDecks.Get(toRoom, 0L)), occupancy);
                if (fromKey.Length == 0 || toKey.Length == 0) continue;
                Vec3 pa = GetNodePos(fromKey);
                Vec3 pb = GetNodePos(toKey);
                double dx = Math.Abs((double)pa.X - pb.X);
                double dz = Math.Abs((double)pa.Z - pb.Z);
                bool sameXz = dx < 0.01 && dz < 0.01;
                double cost = sameXz ? 1.25 : 1.5;
                SetBaseEdge(fromKey, toKey, cost);
            }
        }

        List<string> EdgeNodeKeys(GdDict edge, GdDict occupancy, GdDict layout)
        {
            long deck = V.I64(edge.Get("deck", 0L));
            GdArray cells = StandingCellsForEdge(edge, layout);
            if (cells.Count < 2) return new List<string>();
            string a = NodeKeyFromCell(cells[0], deck, occupancy);
            string b = NodeKeyFromCell(cells[1], deck, occupancy);
            if (a.Length == 0 || b.Length == 0 || a == b) return new List<string>();
            return new List<string> { a, b };
        }

        static GdArray StandingCellsForEdge(GdDict edge, GdDict layout)
        {
            GdArray logical = LogicalEndpointCells(edge, layout);
            if (logical.Count >= 2) return logical;
            object sourceCells = edge.Get("source_cells", new GdArray());
            if (sourceCells is GdArray arr) return arr;
            return new GdArray();
        }

        static GdArray LogicalEndpointCells(GdDict edge, GdDict layout)
        {
            object lf = edge.Get("logical_from_cell", null);
            object lt = edge.Get("logical_to_cell", null);
            if (lf != null && lt != null) return GdArray.Of(lf, lt);
            if (!V.Bool(edge.Get("portal", false)) && !V.Bool(edge.Get("logical_boundary", false))) return new GdArray();
            object portalsV = layout.Get("portals", new GdArray());
            if (!(portalsV is GdArray portals)) return new GdArray();
            string edgeKeyValue = V.Str(edge.Get("key", edge.Get("edge_key", "")));
            object edgeCell = edge.Get("cell", null);
            string direction = V.Str(edge.Get("direction", ""));
            string owner = V.Str(edge.Get("owner_room", ""));
            string other = V.Str(edge.Get("other_room", ""));
            foreach (object portalV in portals)
            {
                if (!(portalV is GdDict portal)) continue;
                if (!V.Bool(portal.Get("logical_boundary", false))) continue;
                string portalKey = V.Str(portal.Get("edge_key", ""));
                if (edgeKeyValue.Length != 0 && portalKey == edgeKeyValue)
                    return GdArray.Of(portal.Get("from_cell", null), portal.Get("to_cell", null));
                string portalDir = V.Str(portal.Get("edge_direction", portal.Get("direction", "")));
                if (portalDir == direction && CellXzEqual(portal.Get("edge_cell", null), edgeCell))
                    return GdArray.Of(portal.Get("from_cell", null), portal.Get("to_cell", null));
                string fromRoom = V.Str(portal.Get("from_room", ""));
                string toRoom = V.Str(portal.Get("to_room", ""));
                if (other.Length == 0) continue;
                if ((fromRoom == owner && toRoom == other) || (fromRoom == other && toRoom == owner))
                    return GdArray.Of(portal.Get("from_cell", null), portal.Get("to_cell", null));
            }
            return new GdArray();
        }

        static bool CellXzEqual(object a, object b)
        {
            Vec2i ac = CellXz(a);
            Vec2i bc = CellXz(b);
            if (ac == InvalidCell || bc == InvalidCell) return false;
            return ac == bc;
        }

        static Vec2i CellXz(object value)
        {
            if (value is Vec2i v) return v;
            if (value is GdArray arr && arr.Count >= 2) return new Vec2i((int)V.I64(arr[0]), (int)V.I64(arr[1]));
            return InvalidCell;
        }

        string NodeKeyFromCell(object value, long fallbackDeck, GdDict occupancy)
        {
            Vec2i cell;
            long deck = fallbackDeck;
            if (value is Vec2i v)
            {
                cell = v;
            }
            else if (value is GdArray values && values.Count >= 2)
            {
                cell = new Vec2i((int)V.I64(values[0]), (int)V.I64(values[1]));
                if (values.Count >= 3) deck = V.I64(values[2]);
            }
            else
            {
                return "";
            }
            string occupancyKey = OccupancyKeyForCell(value, fallbackDeck);
            if (occupancyKey.Length == 0)
                occupancyKey = SurvivalCompat.FormatD(deck) + "|" + SurvivalCompat.FormatD(cell.X) + "|" + SurvivalCompat.FormatD(cell.Y);
            if (occupancy.Has(occupancyKey))
            {
                object recordVariant = occupancy[occupancyKey];
                if (recordVariant is GdDict record) return KeyForPos(OccupancyWorldPosition(record));
            }
            var pos = new Vec3((double)cell.X * CellSize, (double)deck * DeckHeight, (double)cell.Y * CellSize);
            string key = KeyForPos(pos);
            return Nodes.Has(key) ? key : "";
        }

        Vec3 OccupancyWorldPosition(GdDict record)
        {
            Vec3 fromPos = Vec3FromVariant(record.Get("position", record.Get("world_position", null)));
            if (fromPos != Vec3.Inf) return fromPos;
            long deck = V.I64(record.Get("deck", 0L));
            Vec2i cell = CellXzFromVariant(record.Get("cell", null));
            if (cell == InvalidCell)
            {
                string[] keyParts = V.Str(record.Get("cell_key", "")).Split('|');
                if (keyParts.Length >= 3)
                {
                    deck = V.StringToInt(keyParts[0]);
                    cell = new Vec2i((int)V.StringToInt(keyParts[1]), (int)V.StringToInt(keyParts[2]));
                }
            }
            if (cell == InvalidCell) cell = Vec2i.Zero;
            return new Vec3((double)cell.X * CellSize, (double)deck * DeckHeight, (double)cell.Y * CellSize);
        }

        static Vec3 Vec3FromVariant(object raw)
        {
            if (raw is Vec3 v) return v;
            if (raw is GdArray arr && arr.Count >= 3) return new Vec3(V.F64(arr[0]), V.F64(arr[1]), V.F64(arr[2]));
            if (raw is GdDict d)
            {
                if (d.Has("x") && d.Has("y") && d.Has("z"))
                    return new Vec3(V.F64(d.Get("x", 0.0)), V.F64(d.Get("y", 0.0)), V.F64(d.Get("z", 0.0)));
            }
            return Vec3.Inf;
        }

        static Vec2i CellXzFromVariant(object raw)
        {
            if (raw is Vec2i v) return v;
            if (raw is GdArray arr && arr.Count >= 2) return new Vec2i((int)V.I64(arr[0]), (int)V.I64(arr[1]));
            if (raw is GdDict d)
            {
                if (d.Has("x") && d.Has("y")) return new Vec2i((int)V.I64(d.Get("x", 0L)), (int)V.I64(d.Get("y", 0L)));
            }
            return InvalidCell;
        }

        static GdDict RoomDecks(GdDict layout)
        {
            var output = new GdDict();
            object roomsVariant = layout.Get("rooms", new GdArray());
            if (!(roomsVariant is GdArray rooms)) return output;
            foreach (object roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room)) continue;
                string roomId = V.Str(room.Get("id", ""));
                if (roomId.Length != 0) output[roomId] = V.I64(room.Get("deck", 0L));
            }
            return output;
        }

        void ConnectOrthogonalNeighbors()
        {
            var keys = new List<object>(Nodes.Keys);
            GdSort.Sort(keys);
            for (int i = 0; i < keys.Count; i++)
            {
                string ka = V.Str(keys[i]);
                Vec3 pa = GetNodePos(ka);
                for (int j = i + 1; j < keys.Count; j++)
                {
                    string kb = V.Str(keys[j]);
                    Vec3 pb = GetNodePos(kb);
                    double dx = Math.Abs((double)pa.X - pb.X);
                    double dy = Math.Abs((double)pa.Y - pb.Y);
                    double dz = Math.Abs((double)pa.Z - pb.Z);
                    // Same deck 4-connected.
                    if (dy < 0.01)
                    {
                        bool ortho = (Math.Abs(dx - CellSize) < 0.01 && dz < 0.01) ||
                                     (Math.Abs(dz - CellSize) < 0.01 && dx < 0.01);
                        if (ortho) Edges[EdgeKey(ka, kb)] = 1.0;
                    }
                    // Vertical stack (elevators / multi-deck shafts).
                    else if (Math.Abs(dy - DeckHeight) < 0.01 && dx < 0.01 && dz < 0.01)
                    {
                        Edges[EdgeKey(ka, kb)] = 1.25;
                    }
                    // Ramp-like diagonal: one cell step in XZ and one deck step.
                    else if (Math.Abs(dy - DeckHeight) < 0.01)
                    {
                        bool stepXz = (Math.Abs(dx - CellSize) < 0.01 && dz < 0.01) ||
                                      (Math.Abs(dz - CellSize) < 0.01 && dx < 0.01) ||
                                      (Math.Abs(dx - CellSize) < 0.01 && Math.Abs(dz - CellSize) < 0.01);
                        if (stepXz) Edges[EdgeKey(ka, kb)] = 1.5;
                    }
                }
            }
        }

        void ConnectVerticalNeighbors()
        {
            var keys = new List<object>(Nodes.Keys);
            GdSort.Sort(keys);
            for (int i = 0; i < keys.Count; i++)
            {
                string ka = V.Str(keys[i]);
                Vec3 pa = GetNodePos(ka);
                for (int j = i + 1; j < keys.Count; j++)
                {
                    string kb = V.Str(keys[j]);
                    Vec3 pb = GetNodePos(kb);
                    double dx = Math.Abs((double)pa.X - pb.X);
                    double dy = Math.Abs((double)pa.Y - pb.Y);
                    double dz = Math.Abs((double)pa.Z - pb.Z);
                    if (Math.Abs(dy - DeckHeight) >= 0.01) continue;
                    if (dx < 0.01 && dz < 0.01)
                    {
                        Edges[EdgeKey(ka, kb)] = 1.25;
                    }
                    else if ((Math.Abs(dx - CellSize) < 0.01 && dz < 0.01) ||
                             (Math.Abs(dz - CellSize) < 0.01 && dx < 0.01) ||
                             (Math.Abs(dx - CellSize) < 0.01 && Math.Abs(dz - CellSize) < 0.01))
                    {
                        Edges[EdgeKey(ka, kb)] = 1.5;
                    }
                }
            }
        }

        void ConnectStructuralEdges(GdDict structuralPlan)
        {
            object edgesVariant = structuralPlan.Get("edges", null);
            if (edgesVariant is GdDict edgeDict)
            {
                foreach (object edgeKeyVariant in new List<object>(edgeDict.Keys))
                {
                    if (edgeDict[edgeKeyVariant] is GdDict edge) ConnectStructuralEdge(edge);
                }
            }
            else if (edgesVariant is GdArray edgeArray)
            {
                foreach (object edgeVariant in edgeArray)
                {
                    if (edgeVariant is GdDict edge) ConnectStructuralEdge(edge);
                }
            }
        }

        void ConnectStructuralEdge(GdDict edge)
        {
            string kind = V.Str(edge.Get("kind", edge.Get("state", "SOLID"))).ToUpperInvariant();
            if (kind == "SOLID") return;
            object sourceCellsVariant = edge.Get("source_cells", new GdArray());
            if (!(sourceCellsVariant is GdArray sourceCells) || sourceCells.Count < 2) return;
            GdDict first = ReadStructuralCell(sourceCells[0], V.I64(edge.Get("deck", -1L)));
            GdDict second = ReadStructuralCell(sourceCells[1], V.I64(edge.Get("deck", -1L)));
            if (!V.Bool(first.Get("ok", false)) || !V.Bool(second.Get("ok", false))) return;
            if (V.I64(first.Get("deck", -1L)) != V.I64(second.Get("deck", -1L))) return;
            string firstKey = KeyForCell(V.I64(first["deck"]), (Vec2i)first["cell"]);
            string secondKey = KeyForCell(V.I64(second["deck"]), (Vec2i)second["cell"]);
            if (!Nodes.Has(firstKey) || !Nodes.Has(secondKey) || firstKey == secondKey) return;
            if (kind == "OPEN" || kind == "DOOR" || kind == "HATCH" || kind == "BREACH")
                Edges[EdgeKey(firstKey, secondKey)] = 1.0;
            else if (kind == "LOCKED")
                Edges[EdgeKey(firstKey, secondKey)] = BLOCKED_COST;
        }

        static GdDict ReadStructuralCell(object value, long defaultDeck)
        {
            if (value is GdArray values)
            {
                if (values.Count < 2) return new GdDict { { "ok", false } };
                object x = values[0];
                object z = values[1];
                if (!IsIntegerValue(x) || !IsIntegerValue(z)) return new GdDict { { "ok", false } };
                long deck = defaultDeck;
                if (values.Count >= 3 && IsIntegerValue(values[2])) deck = V.I64(values[2]);
                if (deck < 0) return new GdDict { { "ok", false } };
                return new GdDict { { "ok", true }, { "cell", new Vec2i((int)V.I64(x), (int)V.I64(z)) }, { "deck", deck } };
            }
            if (value is string str)
            {
                string text = SurvivalCompat.StripEdges(str);
                if (SurvivalCompat.BeginsWith(text, "(") && SurvivalCompat.EndsWith(text, ")"))
                    text = text.Substring(1, Math.Max(0, text.Length - 2));
                string[] parts = text.Split(',');
                if (parts.Length < 2) parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return new GdDict { { "ok", false } };
                string xText = SurvivalCompat.StripEdges(parts[0]);
                string zText = SurvivalCompat.StripEdges(parts[1]);
                if (!SurvivalCompat.IsValidInt(xText) || !SurvivalCompat.IsValidInt(zText)) return new GdDict { { "ok", false } };
                long deck = defaultDeck;
                if (parts.Length >= 3 && SurvivalCompat.IsValidInt(SurvivalCompat.StripEdges(parts[2])))
                    deck = V.StringToInt(SurvivalCompat.StripEdges(parts[2]));
                if (deck < 0) return new GdDict { { "ok", false } };
                return new GdDict
                {
                    { "ok", true },
                    { "cell", new Vec2i((int)V.StringToInt(xText), (int)V.StringToInt(zText)) },
                    { "deck", deck },
                };
            }
            return new GdDict { { "ok", false } };
        }

        static string KeyForCell(long deck, Vec2i cell) =>
            SurvivalCompat.FormatD(cell.X) + ":" + SurvivalCompat.FormatD(deck) + ":" + SurvivalCompat.FormatD(cell.Y);

        static bool IsIntegerValue(object value)
        {
            return value is long ||
                   (value is double d && GdMath.IsEqualApprox(d, GdMath.Round(d))) ||
                   (value is string s && SurvivalCompat.IsValidInt(s));
        }
    }
}
