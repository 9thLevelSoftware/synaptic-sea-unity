// Ported from scripts/systems/sea_graph.gd @ 96ecb2b0
using System;
using System.Collections;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-D6.2: pure strategic route graph over the Synaptic Sea marker field.
    /// Nodes are ship markers / hubs; edges carry travel cost (fuel + food) and biome band.
    /// Never touches the scene tree. Chart UI / travel controller consume summaries.
    /// </summary>
    public class SeaGraph
    {
        public const double DEFAULT_FUEL_PER_UNIT = 0.08;
        public const double DEFAULT_FOOD_PER_UNIT = 0.03;
        public const string EXTRACTION_NODE_ID = "extraction";
        public const string HUB_NODE_ID = "hub";

        /// <summary>node_id -> { id, kind, position:[x,y,z], biome_band, marker_id?, ship_type? }</summary>
        public GdDict Nodes = new GdDict();

        /// <summary>undirected edge key "a|b" -> { from, to, distance, fuel_cost, food_cost, biome_band }</summary>
        public GdDict Edges = new GdDict();

        public long WorldSeed = 0;
        public double FuelPerUnit = DEFAULT_FUEL_PER_UNIT;
        public double FoodPerUnit = DEFAULT_FOOD_PER_UNIT;

        static readonly Vec3 DefaultExtractionPosition = new Vec3(200f, 0f, 200f);

        public void Clear()
        {
            Nodes.Clear();
            Edges.Clear();
        }

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            Clear();
            WorldSeed = V.I64(config.Get("world_seed", 0L));
            FuelPerUnit = Math.Max(0.0, V.F64(config.Get("fuel_per_unit", DEFAULT_FUEL_PER_UNIT)));
            FoodPerUnit = Math.Max(0.0, V.F64(config.Get("food_per_unit", DEFAULT_FOOD_PER_UNIT)));
        }

        /// <summary>
        /// Build a strategic graph from hub + extraction + optional marker list.
        /// markers: dicts (<see cref="GdDict"/>) or <see cref="ShipMarker"/> objects with marker_id, position
        /// (Vector3 or Array), ship_type. <paramref name="extractionPosition"/> defaults to (200, 0, 200).
        /// </summary>
        public long BuildFromMarkers(IEnumerable markers, Vec3 hubPosition = default, Vec3? extractionPosition = null)
        {
            Vec3 extraction = extractionPosition ?? DefaultExtractionPosition;
            Clear();
            AddNode(HUB_NODE_ID, new GdDict
            {
                { "id", HUB_NODE_ID },
                { "kind", "hub" },
                { "position", PosArray(hubPosition) },
                { "biome_band", 0L },
            });
            AddNode(EXTRACTION_NODE_ID, new GdDict
            {
                { "id", EXTRACTION_NODE_ID },
                { "kind", "extraction" },
                { "position", PosArray(extraction) },
                { "biome_band", BiomeBandForDistance(hubPosition.DistanceTo(extraction)) },
            });
            if (markers != null)
            {
                foreach (object m in markers)
                {
                    string mid = "";
                    Vec3 pos = Vec3.Zero;
                    string shipType = "";
                    if (m is GdDict d)
                    {
                        mid = V.Str(d.Get("marker_id", d.Get("id", "")));
                        pos = ReadPos(d.Get("position", Vec3.Zero));
                        shipType = V.Str(d.Get("ship_type", ""));
                    }
                    else if (m is ShipMarker sm)
                    {
                        // GDScript: m.get("marker_id") / m.get("position") / m.get("ship_type") on the marker object.
                        if (sm.MarkerId != null)
                            mid = sm.MarkerId;
                        pos = sm.Position;
                        if (sm.ShipType != null)
                            shipType = sm.ShipType;
                    }
                    // Any other object: Object.get() of an unknown property is null, so mid stays "" and it is skipped.
                    if (mid.Length == 0)
                        continue;
                    double distHub = hubPosition.DistanceTo(pos);
                    AddNode(mid, new GdDict
                    {
                        { "id", mid },
                        { "kind", "marker" },
                        { "position", PosArray(pos) },
                        { "biome_band", BiomeBandForDistance(distHub) },
                        { "marker_id", mid },
                        { "ship_type", shipType },
                    });
                }
            }
            ConnectKNearest(3);
            // Always ensure a path toward extraction: connect extraction to its nearest 2 nodes
            ConnectNodeToNearest(EXTRACTION_NODE_ID, 2);
            ConnectNodeToNearest(HUB_NODE_ID, 2);
            return Nodes.Count;
        }

        /// <summary>Sample MarkerGenerator cells around the hub into a graph (deterministic).</summary>
        public long BuildFromWorldSeed(long pWorldSeed, long cellRadius = 1, Vec3 hubPosition = default)
        {
            WorldSeed = pWorldSeed;
            var gen = new MarkerGenerator();
            var markers = new List<ShipMarker>();
            for (long cx = -cellRadius; cx < cellRadius + 1; cx++)
            {
                for (long cy = -cellRadius; cy < cellRadius + 1; cy++)
                {
                    List<ShipMarker> cellMarkers = gen.MarkersForCell(pWorldSeed, new Vec2i(unchecked((int)cx), unchecked((int)cy)));
                    foreach (ShipMarker m in cellMarkers)
                        markers.Add(m);
                }
            }
            var extract = new Vec3((double)(cellRadius + 1) * MarkerGenerator.CELL_SIZE, 0.0, (double)(cellRadius + 1) * MarkerGenerator.CELL_SIZE);
            return BuildFromMarkers(markers, hubPosition, extract);
        }

        void AddNode(string id, GdDict data)
        {
            Nodes[id] = data.DeepCopy();
        }

        static GdArray PosArray(Vec3 pos) => GdArray.Of((double)pos.X, (double)pos.Y, (double)pos.Z);

        static Vec3 ReadPos(object v)
        {
            if (v is Vec3 vec)
                return vec;
            if (v is GdArray a && a.Count >= 3)
                return new Vec3(V.F64(a[0]), V.F64(a[1]), V.F64(a[2]));
            return Vec3.Zero;
        }

        Vec3 NodePos(string id)
        {
            if (!Nodes.Has(id))
                return Vec3.Inf;
            return ReadPos(((GdDict)Nodes[id]).Get("position", new GdArray()));
        }

        /// <summary>Biome progression bands by distance from hub (0 near → 3 deep).</summary>
        static long BiomeBandForDistance(double distance)
        {
            if (distance < 40.0)
                return 0;
            if (distance < 100.0)
                return 1;
            if (distance < 200.0)
                return 2;
            return 3;
        }

        public static string BiomeBandName(long band)
        {
            switch (band)
            {
                case 0:
                    return "near_field";
                case 1:
                    return "dead_fleet";
                case 2:
                    return "breach_field";
                default:
                    return "abyssal";
            }
        }

        static string EdgeKey(string a, string b)
        {
            if (GdString.Less(a, b))
                return a + "|" + b;
            return b + "|" + a;
        }

        void AddEdge(string a, string b)
        {
            if (a == b || !Nodes.Has(a) || !Nodes.Has(b))
                return;
            string key = EdgeKey(a, b);
            if (Edges.Has(key))
                return;
            Vec3 pa = NodePos(a);
            Vec3 pb = NodePos(b);
            double dist = pa.DistanceTo(pb);
            long bandA = V.I64(((GdDict)Nodes[a]).Get("biome_band", 0L));
            long bandB = V.I64(((GdDict)Nodes[b]).Get("biome_band", 0L));
            long band = Math.Max(bandA, bandB);
            // Deeper bands cost more per unit
            double bandMult = 1.0 + 0.25 * (double)band;
            Edges[key] = new GdDict
            {
                { "from", a },
                { "to", b },
                { "distance", dist },
                { "fuel_cost", dist * FuelPerUnit * bandMult },
                { "food_cost", dist * FoodPerUnit * bandMult },
                { "biome_band", band },
                { "biome_name", BiomeBandName(band) },
            };
        }

        void ConnectKNearest(long k)
        {
            var ids = new List<object>(Nodes.Keys);
            foreach (object id in ids)
                ConnectNodeToNearest(V.Str(id), k);
        }

        struct Scored
        {
            public string Id;
            public double D;
        }

        void ConnectNodeToNearest(string id, long k)
        {
            if (!Nodes.Has(id))
                return;
            Vec3 origin = NodePos(id);
            var scored = new List<Scored>();
            foreach (object other in new List<object>(Nodes.Keys))
            {
                string oid = V.Str(other);
                if (oid == id)
                    continue;
                double d = origin.DistanceTo(NodePos(oid));
                scored.Add(new Scored { Id = oid, D = d });
            }
            GdSort.SortCustom(scored, (a, b) => a.D < b.D);
            long n = Math.Min(k, (long)scored.Count);
            for (int i = 0; i < n; i++)
                AddEdge(id, scored[i].Id);
        }

        public long NodeCount() => Nodes.Count;

        public long EdgeCount() => Edges.Count;

        public bool HasNode(string id) => Nodes.Has(id);

        public GdDict GetNode(string id)
        {
            if (!Nodes.Has(id))
                return new GdDict();
            return ((GdDict)Nodes[id]).DeepCopy();
        }

        public GdDict GetEdge(string a, string b)
        {
            string key = EdgeKey(a, b);
            if (!Edges.Has(key))
                return new GdDict();
            return ((GdDict)Edges[key]).DeepCopy();
        }

        public GdArray Neighbors(string id)
        {
            var output = new GdArray();
            foreach (object key in Edges.Keys)
            {
                var e = (GdDict)Edges[key];
                string a = V.Str(e.Get("from", ""));
                string b = V.Str(e.Get("to", ""));
                if (a == id)
                    output.Append(b);
                else if (b == id)
                    output.Append(a);
            }
            GdSort.Sort(output);
            return output;
        }

        /// <summary>Dijkstra by fuel_cost. Returns { ok, path: Array[node_id], fuel, food, distance, reason }.</summary>
        public GdDict FindRoute(string fromId, string toId)
        {
            var output = new GdDict
            {
                { "ok", false },
                { "path", new GdArray() },
                { "fuel", 0.0 },
                { "food", 0.0 },
                { "distance", 0.0 },
                { "reason", "" },
            };
            if (!Nodes.Has(fromId) || !Nodes.Has(toId))
            {
                output["reason"] = "unknown_node";
                return output;
            }
            if (fromId == toId)
            {
                output["ok"] = true;
                output["path"] = GdArray.Of(fromId);
                return output;
            }
            var dist = new Dictionary<string, double>(StringComparer.Ordinal);
            var prev = new Dictionary<string, string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (object id in Nodes.Keys)
                dist[V.Str(id)] = double.PositiveInfinity;
            dist[fromId] = 0.0;
            while (true)
            {
                string u = "";
                double best = double.PositiveInfinity;
                foreach (object id in Nodes.Keys)
                {
                    string sid = V.Str(id);
                    if (visited.Contains(sid))
                        continue;
                    double d = dist.TryGetValue(sid, out double dv) ? dv : double.PositiveInfinity;
                    if (d < best)
                    {
                        best = d;
                        u = sid;
                    }
                }
                if (u.Length == 0 || best == double.PositiveInfinity)
                    break;
                if (u == toId)
                    break;
                visited.Add(u);
                foreach (object v in Neighbors(u))
                {
                    string sv = V.Str(v);
                    GdDict e = GetEdge(u, sv);
                    if (e.IsEmpty)
                        continue;
                    double alt = best + V.F64(e.Get("fuel_cost", 0.0));
                    double cur = dist.TryGetValue(sv, out double cv) ? cv : double.PositiveInfinity;
                    if (alt < cur)
                    {
                        dist[sv] = alt;
                        prev[sv] = u;
                    }
                }
            }
            double toDist = dist.TryGetValue(toId, out double td) ? td : double.PositiveInfinity;
            if (toDist == double.PositiveInfinity)
            {
                output["reason"] = "no_path";
                return output;
            }
            var path = new GdArray();
            string curId = toId;
            while (curId != "")
            {
                path.Insert(0, curId);
                if (curId == fromId)
                    break;
                curId = prev.TryGetValue(curId, out string p) ? p : "";
                if (path.Count > Nodes.Count + 2)
                {
                    output["reason"] = "cycle";
                    return output;
                }
            }
            double fuel = 0.0;
            double food = 0.0;
            double distance = 0.0;
            for (int i = 0; i < path.Count - 1; i++)
            {
                GdDict e2 = GetEdge(V.Str(path[i]), V.Str(path[i + 1]));
                fuel += V.F64(e2.Get("fuel_cost", 0.0));
                food += V.F64(e2.Get("food_cost", 0.0));
                distance += V.F64(e2.Get("distance", 0.0));
            }
            output["ok"] = true;
            output["path"] = path;
            output["fuel"] = fuel;
            output["food"] = food;
            output["distance"] = distance;
            return output;
        }

        /// <summary>
        /// Apply travel costs to a simple inventory/resources dict { fuel, food } (mutated in place).
        /// Returns { ok, fuel_left, food_left, reason }.
        /// </summary>
        public GdDict ApplyTravelCost(GdDict resources, GdDict route)
        {
            var result = new GdDict { { "ok", false }, { "fuel_left", 0.0 }, { "food_left", 0.0 }, { "reason", "" } };
            if (!V.Bool(route.Get("ok", false)))
            {
                result["reason"] = "bad_route";
                return result;
            }
            double needFuel = V.F64(route.Get("fuel", 0.0));
            double needFood = V.F64(route.Get("food", 0.0));
            double fuel = V.F64(resources.Get("fuel", 0.0));
            double food = V.F64(resources.Get("food", 0.0));
            if (fuel < needFuel)
            {
                result["reason"] = "insufficient_fuel";
                result["fuel_left"] = fuel;
                result["food_left"] = food;
                return result;
            }
            if (food < needFood)
            {
                result["reason"] = "insufficient_food";
                result["fuel_left"] = fuel;
                result["food_left"] = food;
                return result;
            }
            resources["fuel"] = fuel - needFuel;
            resources["food"] = food - needFood;
            result["ok"] = true;
            result["fuel_left"] = V.F64(resources["fuel"]);
            result["food_left"] = V.F64(resources["food"]);
            return result;
        }

        /// <summary>Route toward extraction from hub (strategic goal).</summary>
        public GdDict RouteToExtraction(string fromId = HUB_NODE_ID) => FindRoute(fromId, EXTRACTION_NODE_ID);

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "schema", "sea_graph_v1" },
                { "world_seed", WorldSeed },
                { "fuel_per_unit", FuelPerUnit },
                { "food_per_unit", FoodPerUnit },
                { "node_count", (long)Nodes.Count },
                { "edge_count", (long)Edges.Count },
                { "nodes", Nodes.DeepCopy() },
                { "edges", Edges.DeepCopy() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            WorldSeed = V.I64(summary.Get("world_seed", WorldSeed));
            FuelPerUnit = Math.Max(0.0, V.F64(summary.Get("fuel_per_unit", FuelPerUnit)));
            FoodPerUnit = Math.Max(0.0, V.F64(summary.Get("food_per_unit", FoodPerUnit)));
            object n = summary.Get("nodes", new GdDict());
            object e = summary.Get("edges", new GdDict());
            if (!(n is GdDict nd) || !(e is GdDict ed))
                return false;
            Nodes = nd.DeepCopy();
            Edges = ed.DeepCopy();
            return true;
        }
    }
}
