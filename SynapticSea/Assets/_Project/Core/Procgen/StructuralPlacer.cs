// Ported from scripts/procgen/structural_placer.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Legacy debug-only room visualizer. It is not a valid structural compiler and must never be used by
    /// ShipGenerator, GeneratedShipLoader, or staged captures (use <see cref="StructuralEdgeCompiler"/>).
    /// <para>
    /// Lays a <see cref="RoomGraph"/> out on a 2D grid (BFS with per-role directional preferences, airlock/bridge
    /// separation) and lists the structural modules of every room.
    /// </para>
    /// RUNTIME: the GDScript built a <c>Node3D</c> tree ("ShipStructure" root, one child <c>Node3D</c> per room
    /// named after the room id, and per room one instantiated wrapper scene per module named
    /// <c>"{module_id}_{i}"</c>). This port returns the same information as a pure <see cref="Placement"/> record
    /// list; the Unity scene builder instantiates <c>KitCatalog</c> prefabs keyed by <see cref="ModulePlacement.ModuleId"/>
    /// and converts the Godot-frame positions with the Runtime <c>Frame</c> converter.
    /// </summary>
    public sealed class StructuralPlacer
    {
        public static readonly IReadOnlyList<string> LEGACY_DEBUG_SMOKE_NAMES = new[]
        {
            "structural_placer_smoke.gd",
            "main_playable_lifeboat_biome_skin_smoke.gd",
        };

        public const string DEPRECATION_DIAGNOSTIC =
            "STRUCTURAL PLACER DEPRECATED: legacy/debug-only path; use StructuralEdgeCompiler";

        public const double CELL_SIZE = 4.0;
        public const double ROOM_GAP = 2.0;
        public const string MODULE_BASE_PATH = "res://scenes/wrappers/structural/ship_structural_v0/";

        /// <summary>Room footprints in grid cells (default 1x1).</summary>
        public static readonly GdDict ROOM_FOOTPRINTS = new GdDict
        {
            { "engineering", new Vec2i(2, 1) },
            { "bridge", new Vec2i(2, 1) },
            { "cargo", new Vec2i(2, 1) },
            { "life_support", new Vec2i(2, 1) },
            { "bay", new Vec2i(2, 1) },
        };

        /// <summary>Module lists per role.</summary>
        public static readonly GdDict ROOM_MODULES = new GdDict
        {
            // --- Ship roles ---
            { "airlock", GdArray.Of("floor_1x1", "floor_1x1", "doorway_frame_open_1x1") },
            { "corridor", GdArray.Of("corridor_floor_1x1", "corridor_floor_1x1") },
            { "engineering", GdArray.Of("floor_1x1", "floor_2x1", "wall_straight_1x1") },
            { "life_support", GdArray.Of("floor_1x1", "floor_1x1", "wall_straight_1x1") },
            { "bridge", GdArray.Of("floor_2x1", "floor_2x1", "wall_straight_1x1") },
            { "cargo", GdArray.Of("floor_2x1", "floor_2x1") },
            { "crew_quarters", GdArray.Of("floor_1x1", "floor_1x1") },
            { "medical", GdArray.Of("floor_1x1", "floor_1x1") },
            { "maintenance", GdArray.Of("floor_1x1", "corridor_floor_1x1") },
            // --- Life boat roles ---
            { "cockpit", GdArray.Of("floor_1x1", "floor_1x1", "wall_straight_1x1") },
            { "engine_bay", GdArray.Of("floor_1x1", "floor_2x1", "wall_straight_1x1") },
            // --- Derelict roles ---
            { "compartment", GdArray.Of("floor_1x1", "floor_1x1", "floor_1x1") },
            { "bay", GdArray.Of("floor_2x1", "floor_2x1", "floor_1x1") },
            { "quarters", GdArray.Of("floor_1x1", "floor_1x1") },
            { "dock", GdArray.Of("floor_1x1", "floor_1x1", "doorway_frame_open_1x1") },
        };

        public static readonly IReadOnlyList<string> FALLBACK_MODULES = new[] { "floor_1x1" };

        /// <summary>Direction vectors: 0=north(-Z), 1=east(+X), 2=south(+Z), 3=west(-X).</summary>
        public static readonly IReadOnlyList<Vec2i> DIRECTIONS = new[]
        {
            new Vec2i(0, -1), new Vec2i(1, 0), new Vec2i(0, 1), new Vec2i(-1, 0),
        };

        /// <summary>Preferred direction order per role, relative to the parent room.</summary>
        public static readonly GdDict DIRECTION_PREFERENCES = new GdDict
        {
            { "engineering", GdArray.Of(2L, 1L, 3L, 0L) }, // aft
            { "engine_bay", GdArray.Of(2L, 1L, 3L, 0L) }, // aft
            { "bridge", GdArray.Of(0L, 1L, 3L, 2L) }, // forward
            { "cockpit", GdArray.Of(0L, 1L, 3L, 2L) }, // forward
            { "cargo", GdArray.Of(1L, 2L, 3L, 0L) }, // starboard
            { "life_support", GdArray.Of(3L, 0L, 1L, 2L) }, // port
            { "airlock", GdArray.Of(1L, 3L, 2L, 0L) }, // sides (east/west), NOT forward
            { "dock", GdArray.Of(1L, 3L, 2L, 0L) }, // sides
            { "corridor", GdArray.Of(0L, 1L, 2L, 3L) }, // no preference
            { "maintenance", GdArray.Of(2L, 3L, 1L, 0L) }, // aft-port
            { "medical", GdArray.Of(3L, 2L, 0L, 1L) }, // port-aft
            { "crew_quarters", GdArray.Of(3L, 0L, 1L, 2L) }, // port
            { "quarters", GdArray.Of(3L, 0L, 1L, 2L) }, // port
            { "compartment", GdArray.Of(0L, 1L, 2L, 3L) }, // no preference
        };

        /// <summary>Roles that are "forward" — used for the airlock separation check.</summary>
        public static readonly IReadOnlyList<string> FORWARD_ROLES = new[] { "bridge", "cockpit" };

        /// <summary>Minimum grid distance between airlock and bridge on non-life-boat ships.</summary>
        public const long AIRLOCK_BRIDGE_MIN_DIST = 3;

        static readonly Vec2i NoPosition = new Vec2i(-99999, -99999);

        public GodotRandom Rng = new GodotRandom();

        // Role -> module-list source, configured once from res://data/kits/ and shared by all placers (read-only
        // after Configure()). Roles without an explicit kit mapping fall back to ROOM_MODULES.
        static KitCatalog _sharedKitCatalog;
        public KitCatalog KitCatalogRef;

        /// <summary>Biome id used to pick a biome-biased kit. Empty selects the default kit.</summary>
        public string Biome = "";

        // ------------------------------------------------------------------ placement records

        /// <summary>
        /// RUNTIME: one structural module instance. GDScript instantiated <see cref="ScenePath"/> as a child of the
        /// room node, named <see cref="NodeName"/>, at <see cref="LocalPosition"/> (no rotation). Modules whose
        /// wrapper scene is missing were skipped with an error; the Runtime builder skips module ids without a
        /// KitCatalog prefab the same way.
        /// </summary>
        public sealed class ModulePlacement
        {
            public string RoomId;
            public string ModuleId;

            /// <summary><c>"{module_id}_{index}"</c>.</summary>
            public string NodeName;

            /// <summary>Index in the room's module list (drives the name and the Z offset).</summary>
            public long Index;

            /// <summary>Godot wrapper scene: <c>MODULE_BASE_PATH + module_id + ".tscn"</c>.</summary>
            public string ScenePath;

            /// <summary>Offset from the room node: <c>(0, 0, index * CELL_SIZE)</c>, Godot frame, float32.</summary>
            public Vec3 LocalPosition;

            /// <summary>Ship-local position: room position + <see cref="LocalPosition"/>, Godot frame.</summary>
            public Vec3 Position;

            /// <summary>Always 0: the placer never rotated modules.</summary>
            public double YawDegrees;

            public GdDict ToDict() => new GdDict
            {
                { "room_id", RoomId },
                { "module_id", ModuleId },
                { "node_name", NodeName },
                { "index", Index },
                { "scene_path", ScenePath },
                { "local_position", LocalPosition },
                { "position", Position },
                { "yaw_degrees", YawDegrees },
            };
        }

        /// <summary>RUNTIME: one room node (named <see cref="RoomId"/>) under the ShipStructure root.</summary>
        public sealed class RoomPlacement
        {
            public string RoomId;
            public string Role;

            /// <summary>Grid slot from the BFS layout (<c>(0, 0)</c> for a room that could not be placed).</summary>
            public Vec2i GridPosition;

            /// <summary><c>(grid.x * (CELL_SIZE + ROOM_GAP), 0, grid.y * (CELL_SIZE + ROOM_GAP))</c>, Godot frame.</summary>
            public Vec3 Position;

            public List<ModulePlacement> Modules = new List<ModulePlacement>();

            public GdDict ToDict()
            {
                var modules = new GdArray();
                foreach (var m in Modules) modules.Append(m.ToDict());
                return new GdDict
                {
                    { "room_id", RoomId },
                    { "role", Role },
                    { "grid_position", GridPosition },
                    { "position", Position },
                    { "yaw_degrees", 0.0 },
                    { "modules", modules },
                };
            }
        }

        /// <summary>RUNTIME: stands in for the returned "ShipStructure" <c>Node3D</c> root (identity transform).</summary>
        public sealed class Placement
        {
            public const string ROOT_NAME = "ShipStructure";

            /// <summary>Rooms in <c>graph.rooms</c> order (the GDScript child order).</summary>
            public List<RoomPlacement> Rooms = new List<RoomPlacement>();

            /// <summary>Every module record, flattened in room then module order.</summary>
            public List<ModulePlacement> AllModules()
            {
                var all = new List<ModulePlacement>();
                foreach (var room in Rooms) all.AddRange(room.Modules);
                return all;
            }

            public GdDict ToDict()
            {
                var rooms = new GdArray();
                foreach (var r in Rooms) rooms.Append(r.ToDict());
                return new GdDict { { "name", ROOT_NAME }, { "rooms", rooms } };
            }
        }

        // ------------------------------------------------------------------ entry point

        void EmitDeprecationDiagnostic()
        {
            // GDScript scans OS.get_cmdline_args() for the legacy smoke names.
            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch (NotSupportedException) { args = new string[0]; }
            foreach (string argument in args)
            {
                foreach (string smokeName in LEGACY_DEBUG_SMOKE_NAMES)
                    if (GdString.EndsWith(argument, smokeName)) return;
            }
            CoreServices.Log.Warning(DEPRECATION_DIAGNOSTIC);
        }

        /// <summary>
        /// GDScript <c>place_structure()</c>. Returns null when the graph is empty or the grid layout fails.
        /// RUNTIME: returns placement records instead of a <c>Node3D</c> tree.
        /// </summary>
        public Placement PlaceStructure(RoomGraph graph, long seedValue = 0, string pBiome = "")
        {
            EmitDeprecationDiagnostic();
            if (graph.Rooms.Count == 0) return null;

            Rng.Seed = seedValue;
            Biome = pBiome ?? "";
            EnsureKitCatalog();

            // Phase 1: compute grid positions via BFS with strong preferences.
            Dictionary<string, Vec2i> gridPositions = LayoutRooms(graph);
            if (gridPositions.Count == 0)
            {
                CoreServices.Log.Error("STRUCTURAL PLACER FAIL grid layout returned empty");
                return null;
            }

            // Phase 2: airlock separation enforcement.
            EnforceAirlockSeparation(graph, gridPositions);

            // Phase 3: RUNTIME: the GDScript built the Node3D tree here ("ShipStructure" root + room children).
            var root = new Placement();
            foreach (var room in graph.Rooms)
            {
                string rid = V.Str(room["id"]);
                string role = V.Str(room["role"]);
                Vec2i gridPos = gridPositions.TryGetValue(rid, out Vec2i p) ? p : Vec2i.Zero;

                double worldX = (double)gridPos.X * (CELL_SIZE + ROOM_GAP);
                double worldZ = (double)gridPos.Y * (CELL_SIZE + ROOM_GAP);

                RoomPlacement roomNode = CreateRoomNode(room, new Vec3(worldX, 0.0, worldZ));
                roomNode.GridPosition = gridPos;
                root.Rooms.Add(roomNode);
            }

            return root;
        }

        // --- BFS Layout ---

        Dictionary<string, Vec2i> LayoutRooms(RoomGraph graph)
        {
            var positions = new Dictionary<string, Vec2i>();
            var occupied = new Dictionary<Vec2i, string>();

            if (graph.Rooms.Count == 0) return positions;

            // Place first room at origin.
            string firstId = V.Str(graph.Rooms[0]["id"]);
            string firstRole = V.Str(graph.Rooms[0]["role"]);
            Vec2i firstFp = FootprintForRole(firstRole);
            positions[firstId] = new Vec2i(0, 0);
            OccupyCells(occupied, new Vec2i(0, 0), firstFp, firstId);

            var visited = new HashSet<string> { firstId };
            var queue = new List<string> { firstId };

            while (queue.Count > 0)
            {
                string currentId = queue[0];
                queue.RemoveAt(0);
                Vec2i currentPos = positions[currentId];
                Vec2i currentFp = FootprintForRole(RoleForRoom(graph, currentId));

                var connected = new List<string>();
                foreach (string cid in graph.GetConnectedRooms(currentId)) connected.Add(cid);

                // Shuffle connected rooms for more varied branching.
                ShuffleArray(connected);

                foreach (string connectedId in connected)
                {
                    if (visited.Contains(connectedId)) continue;
                    visited.Add(connectedId);

                    string connectedRole = RoleForRoom(graph, connectedId);
                    Vec2i connectedFp = FootprintForRole(connectedRole);

                    Vec2i bestPos = FindAdjacentPosition(currentPos, currentFp, connectedFp, connectedRole, occupied);

                    if (bestPos == NoPosition) bestPos = FindAnyFreePosition(currentPos, connectedFp, occupied);

                    if (bestPos == NoPosition)
                    {
                        CoreServices.Log.Error("STRUCTURAL PLACER WARN could not place room " + connectedId);
                        continue;
                    }

                    positions[connectedId] = bestPos;
                    OccupyCells(occupied, bestPos, connectedFp, connectedId);
                    queue.Add(connectedId);
                }
            }

            return positions;
        }

        /// <summary>Fisher-Yates shuffle drawing <c>rng.randi_range(0, i)</c> from the end (not Array.shuffle).</summary>
        void ShuffleArray(List<string> arr)
        {
            for (int i = arr.Count - 1; i > 0; i--)
            {
                int j = (int)Rng.RandiRange(0, i);
                string tmp = arr[i];
                arr[i] = arr[j];
                arr[j] = tmp;
            }
        }

        Vec2i FindAdjacentPosition(
            Vec2i parentPos,
            Vec2i parentFp,
            Vec2i newFp,
            string newRole,
            Dictionary<Vec2i, string> occupied)
        {
            var prefs = DIRECTION_PREFERENCES.Get(newRole) as GdArray ?? GdArray.Of(0L, 1L, 2L, 3L);

            // Try preferred directions, then random ones (each random pick consumes an rng draw).
            var tried = new HashSet<long>();
            for (int attempt = 0; attempt < prefs.Count * 2; attempt++)
            {
                long dirIdx;
                if (attempt < prefs.Count) dirIdx = V.I64(prefs[attempt]);
                else dirIdx = Rng.RandiRange(0, 3);
                if (tried.Contains(dirIdx)) continue;
                tried.Add(dirIdx);

                Vec2i dir = DIRECTIONS[(int)dirIdx];
                Vec2i candidate = AdjacentCell(parentPos, parentFp, dir);
                if (CanPlace(candidate, newFp, occupied)) return candidate;

                // Try rotated footprint.
                var rotatedFp = new Vec2i(newFp.Y, newFp.X);
                if (rotatedFp != newFp)
                {
                    if (CanPlace(candidate, rotatedFp, occupied)) return candidate;
                }
            }

            // Try ALL directions with ALL rotations as fallback.
            for (int dirIdx = 0; dirIdx < 4; dirIdx++)
            {
                Vec2i dir = DIRECTIONS[dirIdx];
                foreach (Vec2i fp in new[] { newFp, new Vec2i(newFp.Y, newFp.X) })
                {
                    Vec2i candidate = AdjacentCell(parentPos, parentFp, dir);
                    if (CanPlace(candidate, fp, occupied)) return candidate;
                }
            }

            return NoPosition;
        }

        static Vec2i AdjacentCell(Vec2i parentPos, Vec2i parentFp, Vec2i dir)
        {
            if (dir == new Vec2i(0, -1)) return new Vec2i(parentPos.X, parentPos.Y - 1);
            if (dir == new Vec2i(1, 0)) return new Vec2i(parentPos.X + parentFp.X, parentPos.Y);
            if (dir == new Vec2i(0, 1)) return new Vec2i(parentPos.X, parentPos.Y + parentFp.Y);
            return new Vec2i(parentPos.X - 1, parentPos.Y);
        }

        static bool CanPlace(Vec2i pos, Vec2i footprint, Dictionary<Vec2i, string> occupied)
        {
            for (int dx = 0; dx < footprint.X; dx++)
            {
                for (int dz = 0; dz < footprint.Y; dz++)
                {
                    if (occupied.ContainsKey(new Vec2i(pos.X + dx, pos.Y + dz))) return false;
                }
            }
            return true;
        }

        static Vec2i FindAnyFreePosition(Vec2i parentPos, Vec2i footprint, Dictionary<Vec2i, string> occupied)
        {
            for (int radius = 1; radius < 20; radius++)
            {
                for (int dx = -radius; dx < radius + 1; dx++)
                {
                    for (int dz = -radius; dz < radius + 1; dz++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue;
                        var candidate = new Vec2i(parentPos.X + dx, parentPos.Y + dz);
                        if (CanPlace(candidate, footprint, occupied)) return candidate;
                    }
                }
            }
            return NoPosition;
        }

        static void OccupyCells(Dictionary<Vec2i, string> occupied, Vec2i pos, Vec2i footprint, string roomId)
        {
            for (int dx = 0; dx < footprint.X; dx++)
            {
                for (int dz = 0; dz < footprint.Y; dz++)
                    occupied[new Vec2i(pos.X + dx, pos.Y + dz)] = roomId;
            }
        }

        // --- Airlock Separation ---
        // On non-life-boat ships, if the airlock is within AIRLOCK_BRIDGE_MIN_DIST (Manhattan) of the bridge,
        // swap the airlock with the room farthest from the bridge.

        void EnforceAirlockSeparation(RoomGraph graph, Dictionary<string, Vec2i> positions)
        {
            string airlockId = "";
            string bridgeId = "";
            foreach (var room in graph.Rooms)
            {
                string role = V.Str(room["role"]);
                if (role == "airlock") airlockId = V.Str(room["id"]);
                else if (Contains(FORWARD_ROLES, role)) bridgeId = V.Str(room["id"]);
            }

            if (airlockId.Length == 0 || bridgeId.Length == 0) return; // derelict or life boat

            Vec2i airlockPos = positions.TryGetValue(airlockId, out Vec2i ap) ? ap : Vec2i.Zero;
            Vec2i bridgePos = positions.TryGetValue(bridgeId, out Vec2i bp) ? bp : Vec2i.Zero;
            long dist = Math.Abs(airlockPos.X - bridgePos.X) + Math.Abs(airlockPos.Y - bridgePos.Y);

            if (dist >= AIRLOCK_BRIDGE_MIN_DIST) return; // already far enough

            // Find the room farthest from the bridge that is not itself forward.
            string bestSwap = "";
            long bestDist = dist;
            foreach (var room in graph.Rooms)
            {
                string rid = V.Str(room["id"]);
                if (rid == airlockId || rid == bridgeId) continue;
                string role = V.Str(room["role"]);
                if (Contains(FORWARD_ROLES, role)) continue;
                Vec2i rpos = positions.TryGetValue(rid, out Vec2i rp) ? rp : Vec2i.Zero;
                long rdist = Math.Abs(rpos.X - bridgePos.X) + Math.Abs(rpos.Y - bridgePos.Y);
                if (rdist > bestDist)
                {
                    bestDist = rdist;
                    bestSwap = rid;
                }
            }

            if (bestSwap.Length == 0) return; // no suitable swap found

            // Swap positions. GDScript `positions[best_swap]` errors (aborting the swap) for an unplaced room.
            if (!positions.TryGetValue(bestSwap, out Vec2i swapPos))
            {
                CoreServices.Log.Error("STRUCTURAL PLACER: invalid access to unplaced room " + bestSwap);
                return;
            }
            positions[bestSwap] = airlockPos;
            positions[airlockId] = swapPos;
        }

        // --- Helpers ---

        static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return true;
            return false;
        }

        static Vec2i FootprintForRole(string role)
        {
            if (ROOM_FOOTPRINTS.Has(role)) return (Vec2i)ROOM_FOOTPRINTS[role];
            return new Vec2i(1, 1);
        }

        static string RoleForRoom(RoomGraph graph, string roomId)
        {
            GdDict room = graph.GetRoom(roomId);
            if (room.IsEmpty) return "";
            return V.Str(room["role"]);
        }

        /// <summary>RUNTIME: GDScript <c>_create_room_node</c> built the room Node3D and instantiated its modules.</summary>
        RoomPlacement CreateRoomNode(GdDict room, Vec3 worldPos)
        {
            string roomId = V.Str(room.Get("id", "room"));
            string role = V.Str(room.Get("role", ""));

            var roomNode = new RoomPlacement { RoomId = roomId, Role = role, Position = worldPos };

            List<string> modules = ModulesForRole(role);
            for (int i = 0; i < modules.Count; i++)
            {
                string stem = modules[i];
                // RUNTIME: _instantiate_module(stem) loaded MODULE_BASE_PATH + stem + ".tscn" and skipped the module
                // (push_error) when the scene was missing; existence is the Runtime builder's concern.
                var local = new Vec3(0.0, 0.0, (double)i * CELL_SIZE);
                roomNode.Modules.Add(new ModulePlacement
                {
                    RoomId = roomId,
                    ModuleId = stem,
                    NodeName = stem + "_" + GdString.FormatInt(i),
                    Index = i,
                    ScenePath = MODULE_BASE_PATH + stem + ".tscn",
                    LocalPosition = local,
                    Position = worldPos + local,
                    YawDegrees = 0.0,
                });
            }

            return roomNode;
        }

        void EnsureKitCatalog()
        {
            if (_sharedKitCatalog == null)
            {
                _sharedKitCatalog = new KitCatalog();
                // Configure() never throws; a missing kits dir leaves the catalog empty (ROOM_MODULES fallback).
                _sharedKitCatalog.Configure("res://data/kits/");
            }
            KitCatalogRef = _sharedKitCatalog;
        }

        /// <summary>Drops the process-wide kit catalog cache (tests that swap the resource reader).</summary>
        public static void ResetSharedKitCatalog() => _sharedKitCatalog = null;

        /// <summary>
        /// Module list for <paramref name="role"/>: the selected kit's explicit mapping when it has one, else the
        /// built-in <see cref="ROOM_MODULES"/> list, else <see cref="FALLBACK_MODULES"/>.
        /// </summary>
        public List<string> ModulesForRole(string role)
        {
            if (KitCatalogRef != null && KitCatalogRef.HasRoleFor(role, Biome))
            {
                List<string> fromKit = KitCatalogRef.KitsForRole(role, Biome);
                if (fromKit.Count > 0) return fromKit;
            }
            if (!ROOM_MODULES.Has(role)) return new List<string>(FALLBACK_MODULES);
            if (ROOM_MODULES[role] is GdArray raw)
            {
                var output = new List<string>();
                foreach (var entry in raw) output.Add(V.Str(entry));
                return output;
            }
            return new List<string>(FALLBACK_MODULES);
        }
    }
}
