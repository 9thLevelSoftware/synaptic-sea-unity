// Ported from scripts/procgen/room_graph_generator.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// DEPRECATED 2026-07-01 (Domain 7): orphaned from the live generation pipeline (TemplateSelector -&gt;
    /// RoomAssigner -&gt; CellLayoutEngine -&gt; ...). Retained for reference / unit-test use only.
    /// <para>
    /// Generates a procedural <see cref="RoomGraph"/> from a <see cref="ShipBlueprint"/>. Ship mode (default):
    /// airlock + system rooms + weighted fill. Derelict mode (<c>archetype.type == "derelict"</c>): dock + generic
    /// compartments, no system rooms.
    /// </para>
    /// </summary>
    public sealed class RoomGraphGenerator
    {
        public static readonly IReadOnlyList<string> REQUIRED_ROLES = new[] { "airlock" };

        public static readonly GdDict SYSTEM_ROLES = new GdDict
        {
            { "power", "engineering" },
            { "life_support", "life_support" },
            { "propulsion", "engineering" },
            { "navigation", "bridge" },
            { "scanners", "bridge" },
        };

        /// <summary>Ship-mode optional roles.</summary>
        public static readonly IReadOnlyList<string> OPTIONAL_ROLES = new[]
        {
            "corridor",
            "cargo",
            "crew_quarters",
            "medical",
            "maintenance",
        };

        /// <summary>Derelict-mode roles. No systems — just structural space.</summary>
        public static readonly IReadOnlyList<string> DERELICT_OPTIONAL_ROLES = new[]
        {
            "compartment",
            "corridor",
            "bay",
            "quarters",
            "hangar",
        };

        public static readonly GdDict DEFAULT_WEIGHTS = new GdDict
        {
            { "corridor", 3L },
            { "cargo", 2L },
            { "crew_quarters", 2L },
            { "maintenance", 2L },
            { "medical", 1L },
        };

        public static readonly GdDict DEFAULT_DERELICT_WEIGHTS = new GdDict
        {
            { "compartment", 4L },
            { "corridor", 3L },
            { "bay", 2L },
            { "quarters", 2L },
        };

        public const long DEFAULT_MAX_DUPLICATES = 2;

        public GodotRandom Rng = new GodotRandom();

        public RoomGraph Generate(ShipBlueprint blueprint, GdDict archetype = null)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint), "RoomGraphGenerator: blueprint must not be null");
            archetype = archetype ?? new GdDict();

            Rng.Seed = blueprint.SeedValue;

            var graph = new RoomGraph();
            long targetCount = PickRoomCount(blueprint);
            bool isDerelict = V.Str(archetype.Get("type", "")) == "derelict";

            if (isDerelict) GenerateDerelict(graph, blueprint, archetype, targetCount);
            else GenerateShip(graph, blueprint, archetype, targetCount);

            ConnectRooms(graph);
            return graph;
        }

        // --- Ship mode (functional ships) ---

        void GenerateShip(RoomGraph graph, ShipBlueprint blueprint, GdDict archetype, long targetCount)
        {
            // Step 1: airlock.
            graph.AddRoom(MakeRoomId("airlock", 1, graph), "airlock", 0);
            // Step 2: system rooms.
            AddRequiredRooms(graph, blueprint);
            // Step 3: guaranteed roles.
            AddGuaranteedRoles(graph, archetype, targetCount);
            // Step 4: weighted fill.
            FillOptionalRoomsWeighted(graph, targetCount, archetype);
        }

        // --- Derelict mode (dead shells) ---

        void GenerateDerelict(RoomGraph graph, ShipBlueprint blueprint, GdDict archetype, long targetCount)
        {
            // The anchor room is the dock — the one fixed point where the life boat attaches.
            graph.AddRoom(MakeRoomId("dock", 1, graph), "dock", 0);
            AddGuaranteedRoles(graph, archetype, targetCount);
            FillDerelictRooms(graph, targetCount, archetype);
        }

        void FillDerelictRooms(RoomGraph graph, long targetCount, GdDict archetype)
        {
            GdDict weights = BuildDerelictWeights(archetype);
            long maxDup = V.I64(archetype.Get("max_duplicates", 3L));
            if (maxDup < 1) maxDup = 3;

            while (graph.Rooms.Count < targetCount)
            {
                string role = PickWeightedRoleFromPool(graph, weights, maxDup, DERELICT_OPTIONAL_ROLES);
                if (role.Length == 0) role = "compartment"; // fallback
                long idx = NextIndexForRole(graph, role);
                graph.AddRoom(MakeRoomId(role, idx, graph), role, 0);
                if (weights.Has(role)) weights[role] = Math.Max(1L, V.I64(weights[role]) / 2);
            }
        }

        GdDict BuildDerelictWeights(GdDict archetype)
        {
            var weights = new GdDict();
            GdDict source = archetype.GetDict("role_weights", DEFAULT_DERELICT_WEIGHTS);
            foreach (string role in DERELICT_OPTIONAL_ROLES)
            {
                if (source.Has(role)) weights[role] = V.I64(source[role]);
                else if (DEFAULT_DERELICT_WEIGHTS.Has(role)) weights[role] = V.I64(DEFAULT_DERELICT_WEIGHTS[role]);
                else weights[role] = 1L;
            }
            return weights;
        }

        // --- Shared helpers ---

        long PickRoomCount(ShipBlueprint blueprint)
        {
            long lo = blueprint.RoomCountRange.X;
            long hi = blueprint.RoomCountRange.Y;
            if (hi < lo) hi = lo;
            return Rng.RandiRange(lo, hi);
        }

        void AddRequiredRooms(RoomGraph graph, ShipBlueprint blueprint)
        {
            AddUniqueRole(graph, "engineering", 1);
            if (blueprint.ShipSize == (long)ShipBlueprint.Size.LifeBoat) return;
            AddUniqueRole(graph, "life_support", 1);
            AddUniqueRole(graph, "bridge", 1);
        }

        void AddUniqueRole(RoomGraph graph, string role, long instanceIndex)
        {
            foreach (var room in graph.Rooms)
                if (V.Str(room["role"]) == role) return;
            long idx = instanceIndex;
            while (graph.GetRoom(MakeRoomId(role, idx, graph)).IsEmpty == false) idx += 1;
            graph.AddRoom(MakeRoomId(role, idx, graph), role, 0);
        }

        void AddGuaranteedRoles(RoomGraph graph, GdDict archetype, long targetCount)
        {
            if (archetype.IsEmpty) return;
            GdArray guaranteed = archetype.GetArrayOrEmpty("guaranteed_roles");
            foreach (var roleEntry in guaranteed)
            {
                string role = V.Str(roleEntry);
                if (graph.Rooms.Count >= targetCount) break;
                bool already = false;
                foreach (var room in graph.Rooms)
                {
                    if (V.Str(room["role"]) == role)
                    {
                        already = true;
                        break;
                    }
                }
                if (already) continue;
                long idx = NextIndexForRole(graph, role);
                graph.AddRoom(MakeRoomId(role, idx, graph), role, 0);
            }
        }

        void FillOptionalRoomsWeighted(RoomGraph graph, long targetCount, GdDict archetype)
        {
            GdDict weights = BuildWeights(archetype);
            long maxDup = V.I64(archetype.Get("max_duplicates", DEFAULT_MAX_DUPLICATES));
            if (maxDup < 1) maxDup = DEFAULT_MAX_DUPLICATES;

            while (graph.Rooms.Count < targetCount)
            {
                string role = PickWeightedRoleFromPool(graph, weights, maxDup, OPTIONAL_ROLES);
                if (role.Length == 0) role = "corridor";
                long idx = NextIndexForRole(graph, role);
                graph.AddRoom(MakeRoomId(role, idx, graph), role, 0);
                if (weights.Has(role)) weights[role] = Math.Max(1L, V.I64(weights[role]) / 2);
            }
        }

        GdDict BuildWeights(GdDict archetype)
        {
            var weights = new GdDict();
            GdDict source = archetype.GetDict("role_weights", DEFAULT_WEIGHTS);
            foreach (string role in OPTIONAL_ROLES)
            {
                if (source.Has(role)) weights[role] = V.I64(source[role]);
                else if (DEFAULT_WEIGHTS.Has(role)) weights[role] = V.I64(DEFAULT_WEIGHTS[role]);
                else weights[role] = 1L;
            }
            return weights;
        }

        string PickWeightedRoleFromPool(RoomGraph graph, GdDict weights, long maxDup, IReadOnlyList<string> pool)
        {
            var candidates = new List<string>();
            var candidateWeights = new List<long>();
            long totalWeight = 0;

            foreach (string role in pool)
            {
                if (CountRole(graph, role) >= maxDup) continue;
                long w = V.I64(weights.Get(role, 1L));
                if (w <= 0) continue;
                candidates.Add(role);
                candidateWeights.Add(w);
                totalWeight += w;
            }

            if (candidates.Count == 0) return "";

            long roll = Rng.RandiRange(1, totalWeight);
            long cumulative = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                cumulative += candidateWeights[i];
                if (roll <= cumulative) return candidates[i];
            }

            return candidates[0];
        }

        static long CountRole(RoomGraph graph, string role)
        {
            long count = 0;
            foreach (var room in graph.Rooms)
                if (V.Str(room["role"]) == role) count += 1;
            return count;
        }

        static long NextIndexForRole(RoomGraph graph, string role)
        {
            long maxSeen = 0;
            foreach (var room in graph.Rooms)
            {
                if (V.Str(room["role"]) != role) continue;
                string rid = V.Str(room["id"]);
                int sep = GdString.RFind(rid, "_");
                if (sep < 0 || sep == rid.Length - 1) continue;
                string tail = rid.Substring(sep + 1);
                if (!GdString.IsValidInt(tail)) continue;
                long n = V.StringToInt(tail);
                if (n > maxSeen) maxSeen = n;
            }
            return maxSeen + 1;
        }

        static string MakeRoomId(string role, long idx, RoomGraph graph) => role + "_" + GdString.FormatIntPadded(idx, 2);

        // --- Connectivity ---

        void ConnectRooms(RoomGraph graph)
        {
            if (graph.Rooms.Count < 2) return;

            // Linear chain ensures connectivity.
            for (int i = 0; i < graph.Rooms.Count - 1; i++)
            {
                string fromId = V.Str(graph.Rooms[i]["id"]);
                string toId = V.Str(graph.Rooms[i + 1]["id"]);
                graph.AddLink(fromId, toId, "door");
            }

            // Random branches for variety, scaled with ship size.
            int roomCount = graph.Rooms.Count;
            long extraTarget = (long)GdMath.Round(Math.Sqrt((double)roomCount));
            if (extraTarget > 6) extraTarget = 6;
            if (extraTarget < 0) extraTarget = 0;

            HashSet<string> existing = IndexExistingLinks(graph);
            long attempts = 0;
            long maxAttempts = extraTarget * 8 + 15;
            long added = 0;
            while (added < extraTarget && attempts < maxAttempts)
            {
                attempts += 1;
                int a = (int)Rng.RandiRange(0, roomCount - 1);
                int b = (int)Rng.RandiRange(0, roomCount - 1);
                if (a == b) continue;
                if (Degree(graph, a) >= 3 || Degree(graph, b) >= 3) continue;
                if (Math.Abs(a - b) == 1) continue;
                string key = LinkKey(a, b);
                if (existing.Contains(key)) continue;
                existing.Add(key);
                string aid = V.Str(graph.Rooms[a]["id"]);
                string bid = V.Str(graph.Rooms[b]["id"]);
                graph.AddLink(aid, bid, "door");
                added += 1;
            }
        }

        static HashSet<string> IndexExistingLinks(RoomGraph graph)
        {
            var index = new HashSet<string>();
            foreach (var link in graph.Links)
            {
                string fromId = V.Str(link["from_room"]);
                string toId = V.Str(link["to_room"]);
                int fi = IndexOfRoom(graph, fromId);
                int ti = IndexOfRoom(graph, toId);
                if (fi < 0 || ti < 0) continue;
                index.Add(LinkKey(fi, ti));
            }
            return index;
        }

        static string LinkKey(long a, long b)
        {
            long lo = a;
            long hi = b;
            if (lo > hi)
            {
                long tmp = lo;
                lo = hi;
                hi = tmp;
            }
            return GdString.FormatInt(lo) + "-" + GdString.FormatInt(hi);
        }

        static int IndexOfRoom(RoomGraph graph, string roomId)
        {
            for (int i = 0; i < graph.Rooms.Count; i++)
                if (V.Str(graph.Rooms[i]["id"]) == roomId) return i;
            return -1;
        }

        static long Degree(RoomGraph graph, int idx)
        {
            if (idx < 0 || idx >= graph.Rooms.Count) return 0;
            string rid = V.Str(graph.Rooms[idx]["id"]);
            long deg = 0;
            foreach (var link in graph.Links)
                if (V.Str(link["from_room"]) == rid || V.Str(link["to_room"]) == rid) deg += 1;
            return deg;
        }
    }
}
