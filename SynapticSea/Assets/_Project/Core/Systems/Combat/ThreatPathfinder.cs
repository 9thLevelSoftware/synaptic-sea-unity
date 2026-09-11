// Ported from scripts/systems/threat_pathfinder.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Pure A* over <see cref="ShipNavGraph"/> (ADR-0049). Returns world-space waypoints. Never touches the scene tree.</summary>
    public static class ThreatPathfinder
    {
        public const long MAX_EXPANSIONS = 4096;

        /// <summary>Find a path from world start to world goal: an array of <see cref="Vec3"/> waypoints, empty if unreachable.</summary>
        public static GdArray FindPath(ShipNavGraph graph, Vec3 startWorld, Vec3 goalWorld)
        {
            if (graph == null || graph.NodeCount() == 0)
                return new GdArray();
            string startId = graph.NearestNode(startWorld);
            string goalId = graph.NearestNode(goalWorld);
            if (startId.Length == 0 || goalId.Length == 0)
                return new GdArray();
            if (startId == goalId)
                return GdArray.Of(graph.GetNodePos(goalId));
            var cameFrom = new Dictionary<string, string>();
            var gScore = new Dictionary<string, double> { { startId, 0.0 } };
            // open set as a list of (id, f); linear scan is fine for ship-scale graphs
            var open = new List<KeyValuePair<string, double>> { new KeyValuePair<string, double>(startId, Heuristic(graph, startId, goalId)) };
            var closed = new HashSet<string>();
            long expansions = 0;
            while (open.Count != 0 && expansions < MAX_EXPANSIONS)
            {
                expansions += 1;
                int bestI = 0;
                double bestF = open[0].Value;
                for (int i = 1; i < open.Count; i++)
                {
                    double f = open[i].Value;
                    if (f < bestF)
                    {
                        bestF = f;
                        bestI = i;
                    }
                }
                string current = open[bestI].Key;
                open.RemoveAt(bestI);
                if (current == goalId)
                    return Reconstruct(graph, cameFrom, current);
                if (closed.Contains(current))
                    continue;
                closed.Add(current);
                foreach (object neigh in graph.Neighbors(current))
                {
                    if (!(neigh is GdDict n))
                        continue;
                    string nid = V.Str(n.Get("to", ""));
                    double step = V.F64(n.Get("cost", 1.0));
                    if (nid.Length == 0 || closed.Contains(nid))
                        continue;
                    double tent = GetOrInf(gScore, current) + step;
                    if (tent < GetOrInf(gScore, nid))
                    {
                        cameFrom[nid] = current;
                        gScore[nid] = tent;
                        double f2 = tent + Heuristic(graph, nid, goalId);
                        open.Add(new KeyValuePair<string, double>(nid, f2));
                    }
                }
            }
            return new GdArray();
        }

        /// <summary>Farthest reachable node position from <paramref name="fromWorld"/> (for FLEE).</summary>
        public static Vec3 FarthestPoint(ShipNavGraph graph, Vec3 fromWorld, Vec3 avoidWorld)
        {
            if (graph == null || graph.NodeCount() == 0)
                return fromWorld;
            string startId = graph.NearestNode(fromWorld);
            if (startId.Length == 0)
                return fromWorld;
            // Dijkstra distances from start; pick max distance, break ties by distance from avoid.
            var dist = new GdDict { { startId, 0.0 } }; // insertion-ordered, like the GDScript Dictionary
            var open = new List<string> { startId };
            var visited = new HashSet<string>();
            while (open.Count != 0)
            {
                string cur = open[0];
                open.RemoveAt(0);
                if (visited.Contains(cur))
                    continue;
                visited.Add(cur);
                foreach (object neigh in graph.Neighbors(cur))
                {
                    if (!(neigh is GdDict n))
                        continue;
                    string nid = V.Str(n.Get("to", ""));
                    double step = V.F64(n.Get("cost", 1.0));
                    if (nid.Length == 0)
                        continue;
                    double tent = V.F64(dist.Get(cur, double.PositiveInfinity)) + step;
                    if (tent < V.F64(dist.Get(nid, double.PositiveInfinity)))
                    {
                        dist[nid] = tent;
                        open.Add(nid);
                    }
                }
            }
            string bestId = startId;
            double bestScore = -1.0;
            foreach (var kv in dist)
            {
                double dPath = V.F64(kv.Value);
                Vec3 p = graph.GetNodePos(V.Str(kv.Key));
                double dAvoid = p.DistanceTo(avoidWorld);
                double score = dPath * 0.35 + dAvoid;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = V.Str(kv.Key);
                }
            }
            return graph.GetNodePos(bestId);
        }

        /// <summary>
        /// Advance along waypoints by <c>speed * delta</c>. Returns { position: Vec3, path_index: int, arrived: bool }.
        /// </summary>
        public static GdDict StepAlongPath(GdArray path, long pathIndex, Vec3 current, double speed, double delta)
        {
            long idx = pathIndex;
            Vec3 pos = current;
            double remaining = Math.Max(0.0, speed) * Math.Max(0.0, delta);
            if (path.Count == 0 || remaining <= 0.0)
                return new GdDict { { "position", pos }, { "path_index", idx }, { "arrived", path.Count == 0 } };
            while (remaining > 0.0 && idx < path.Count)
            {
                Vec3 wp = AsVec3(path[(int)idx], pos);
                double dist = pos.DistanceTo(wp);
                if (dist <= 0.05)
                {
                    pos = wp;
                    idx += 1;
                    continue;
                }
                if (remaining >= dist)
                {
                    pos = wp;
                    remaining -= dist;
                    idx += 1;
                }
                else
                {
                    pos = pos.MoveToward(wp, (float)remaining);
                    remaining = 0.0;
                }
            }
            return new GdDict
            {
                { "position", pos },
                { "path_index", idx },
                { "arrived", idx >= path.Count },
            };
        }

        static Vec3 AsVec3(object v, Vec3 fallback)
        {
            if (v is Vec3 vec)
                return vec;
            if (v is GdArray arr && arr.Count >= 3)
                return new Vec3(V.F64(arr[0]), V.F64(arr[1]), V.F64(arr[2]));
            return fallback;
        }

        static double Heuristic(ShipNavGraph graph, string a, string b)
        {
            Vec3 pa = graph.GetNodePos(a);
            Vec3 pb = graph.GetNodePos(b);
            if (pa == Vec3.Inf || pb == Vec3.Inf)
                return 0.0;
            return pa.DistanceTo(pb) / Math.Max(0.1, graph.CellSize);
        }

        static GdArray Reconstruct(ShipNavGraph graph, Dictionary<string, string> cameFrom, string current)
        {
            var chain = new List<string> { current };
            while (cameFrom.TryGetValue(current, out string prev))
            {
                current = prev;
                chain.Insert(0, current);
            }
            var waypoints = new GdArray();
            foreach (string id in chain)
            {
                Vec3 p = graph.GetNodePos(id);
                if (p != Vec3.Inf)
                    waypoints.Append(p);
            }
            return waypoints;
        }

        static double GetOrInf(Dictionary<string, double> d, string key) =>
            d.TryGetValue(key, out double v) ? v : double.PositiveInfinity;
    }
}
