// Ported from scripts/procgen/life_boat.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Builds the fixed life boat: three single-cell rooms in a line, <c>[engine_bay] — [airlock] — [cockpit]</c>. The
    /// airlock is the connection point to the derelict's dock. Not procgen: the occupancy is hand-authored; geometry
    /// comes from <see cref="StructuralEdgeCompiler"/>, same contract as derelicts.
    /// <para>
    /// RUNTIME: GDScript <c>build()</c> instanced a "LifeBoat" Node3D with a "ShipStructure" child, one Node3D per room
    /// and one wrapper scene per compiled plan record. <see cref="Build"/> returns the same tree as pure records
    /// (<see cref="BuildResult"/>) for the Unity scene builder.
    /// </para>
    /// </summary>
    public static class LifeBoatBuilder
    {
        public const string SCHEMA_VERSION = "1.2.0";
        public const double CELL_SIZE = 4.0;
        public const double DECK_HEIGHT = 4.0;
        public const string DEFAULT_KIT_ID = "ship_structural_v0";
        public const string DEFAULT_KIT_PATH = "res://data/kits/ship_structural_v0.json";

        /// <summary>Room definitions; index 0 is the airlock (dock connection point).</summary>
        public static readonly IReadOnlyList<GdDict> ROOMS = new[]
        {
            new GdDict { { "id", "airlock_01" }, { "role", "airlock" }, { "deck", 0L } },
            new GdDict { { "id", "cockpit_01" }, { "role", "bridge" }, { "deck", 0L } },
            new GdDict { { "id", "engine_bay_01" }, { "role", "engineering" }, { "deck", 0L } },
        };

        /// <summary>Fixed grid positions (cell x, cell z) per room; bow = +X.</summary>
        public static readonly GdDict ROOM_CELL_X = new GdDict { { "airlock_01", 0L }, { "cockpit_01", 1L }, { "engine_bay_01", -1L } };
        public static readonly GdDict ROOM_CELL_Z = new GdDict { { "airlock_01", 0L }, { "cockpit_01", 0L }, { "engine_bay_01", 0L } };

        /// <summary>RUNTIME: one room Node3D under "ShipStructure".</summary>
        public sealed class RoomNode
        {
            public string RoomId;
            /// <summary><c>(cx * CELL_SIZE, deck * DECK_HEIGHT, cz * CELL_SIZE)</c>, Godot frame.</summary>
            public Vec3 Position;
        }

        /// <summary>RUNTIME: one instanced wrapper scene.</summary>
        public sealed class WrapperRecord
        {
            /// <summary>placement_id (or id / module_id) with ':' and '|' replaced by '_'.</summary>
            public string NodeName;
            public string ModuleId;
            /// <summary>The kit's <c>godot_wrapper_scene</c> for the module (Runtime maps module_id to a prefab).</summary>
            public string ScenePath;
            /// <summary>Parent room id, or "" for records parented to ShipStructure.</summary>
            public string ParentRoomId;
            /// <summary>World position minus the parent room position.</summary>
            public Vec3 LocalPosition;
            /// <summary>Plan position (ship-local), Godot frame.</summary>
            public Vec3 Position;
            /// <summary>Applied as <c>rotation_degrees.y</c>.</summary>
            public double YawDegrees;
            /// <summary>"floor_placements", "placements" or "ceiling_placements".</summary>
            public string Layer;
        }

        /// <summary>RUNTIME: stands in for the returned "LifeBoat" Node3D.</summary>
        public sealed class BuildResult
        {
            public const string ROOT_NAME = "LifeBoat";
            public const string STRUCTURE_NAME = "ShipStructure";
            public GdDict Layout;
            public List<RoomNode> Rooms = new List<RoomNode>();
            public List<WrapperRecord> Wrappers = new List<WrapperRecord>();

            /// <summary>
            /// Unity-port addition (A4): the res:// kit document whose wrapper map was actually used (<see cref="ModuleSceneMap"/>
            /// falls back to v0 when the layout's kit has no wrapper scenes). The Runtime resolves its prefab catalog from it.
            /// </summary>
            public string KitPath = DEFAULT_KIT_PATH;

            /// <summary>GDScript <c>get_airlock_node()</c>: the airlock room record.</summary>
            public RoomNode AirlockRoom()
            {
                foreach (var room in Rooms)
                    if (room.RoomId == "airlock_01") return room;
                return null;
            }
        }

        /// <summary>
        /// GDScript <c>build()</c>. Returns null when the layout has no structural plan, the kit has no wrapper map or
        /// a plan record's module has no wrapper scene (GDScript also failed on a scene path missing on disk; that
        /// check belongs to the Runtime builder).
        /// </summary>
        public static BuildResult Build(string biome = "")
        {
            GdDict layout = BuildLayout(biome);
            if (!(layout.Get("structural_plan", null) is GdDict plan) || plan.IsEmpty)
            {
                CoreServices.Log.Error("LifeBoatBuilder: missing validated structural_plan");
                return null;
            }
            GdDict moduleToScene = ModuleSceneMap(V.Str(layout.Get("kit_id", DEFAULT_KIT_ID)));
            if (moduleToScene.IsEmpty)
            {
                CoreServices.Log.Error("LifeBoatBuilder: kit has no wrapper scenes");
                return null;
            }

            var result = new BuildResult { Layout = layout, KitPath = ResolveKitPath(V.Str(layout.Get("kit_id", DEFAULT_KIT_ID))) };
            var roomNodes = new Dictionary<string, RoomNode>();
            foreach (GdDict roomDef in ROOMS)
            {
                string rid = V.Str(roomDef["id"]);
                long cx = V.I64(ROOM_CELL_X[rid]);
                long cz = V.I64(ROOM_CELL_Z[rid]);
                var node = new RoomNode
                {
                    RoomId = rid,
                    Position = new Vec3((double)cx * CELL_SIZE, (double)V.I64(roomDef["deck"]) * DECK_HEIGHT, (double)cz * CELL_SIZE),
                };
                result.Rooms.Add(node);
                roomNodes[rid] = node;
            }

            foreach (string layer in new[] { "floor_placements", "placements", "ceiling_placements" })
            {
                if (!InstancePlanRecords(plan.Get(layer, null), layer, moduleToScene, roomNodes, result)) return null;
            }
            return result;
        }

        /// <summary>Layout dictionary in LayoutSerializer schema 1.2.0 format with a compiled, validated plan.</summary>
        public static GdDict BuildLayout(string biome = "")
        {
            string kitId = KitIdForBiome(biome);
            var roomsArray = new GdArray();
            foreach (GdDict roomDef in ROOMS)
            {
                string rid = V.Str(roomDef["id"]);
                string role = V.Str(roomDef["role"]);
                long deck = V.I64(roomDef["deck"]);
                long cx = V.I64(ROOM_CELL_X[rid]);
                long cz = V.I64(ROOM_CELL_Z[rid]);
                roomsArray.Append(new GdDict
                {
                    { "id", rid },
                    { "room_role", role },
                    { "role", role },
                    { "deck", deck },
                    { "cells", GdArray.Of(GdArray.Of(cx, cz)) },
                    { "footprint", GdArray.Of(1L, 1L) },
                    { "structural_placements", new GdArray() },
                    { "portals", new GdArray() },
                    { "interior_zones", new GdDict() },
                    { "motif_requests", new GdArray() },
                    { "world_origin", GdArray.Of((double)cx * CELL_SIZE, (double)deck * DECK_HEIGHT, (double)cz * CELL_SIZE) },
                });
            }

            var portals = GdArray.Of(
                new GdDict
                {
                    { "id", "airlock_01_to_cockpit_01" },
                    { "from_room", "airlock_01" },
                    { "to_room", "cockpit_01" },
                    { "from_cell", GdArray.Of(ROOM_CELL_X["airlock_01"], ROOM_CELL_Z["airlock_01"], 0L) },
                    { "to_cell", GdArray.Of(ROOM_CELL_X["cockpit_01"], ROOM_CELL_Z["cockpit_01"], 0L) },
                    { "module_id", "doorway_frame_open_1x1" },
                    { "state", "DOOR" },
                },
                new GdDict
                {
                    { "id", "airlock_01_to_engine_bay_01" },
                    { "from_room", "airlock_01" },
                    { "to_room", "engine_bay_01" },
                    { "from_cell", GdArray.Of(ROOM_CELL_X["airlock_01"], ROOM_CELL_Z["airlock_01"], 0L) },
                    { "to_cell", GdArray.Of(ROOM_CELL_X["engine_bay_01"], ROOM_CELL_Z["engine_bay_01"], 0L) },
                    { "module_id", "doorway_frame_open_1x1" },
                    { "state", "DOOR" },
                });
            var roomLinks = new GdArray();
            foreach (GdDict portal in portals)
            {
                roomLinks.Append(new GdDict
                {
                    { "id", V.Str(portal["id"]) },
                    { "from_room", V.Str(portal["from_room"]) },
                    { "to_room", V.Str(portal["to_room"]) },
                    { "from_cell", portal["from_cell"] },
                    { "to_cell", portal["to_cell"] },
                    { "module_id", V.Str(portal["module_id"]) },
                    { "link_type", "door" },
                });
            }

            var layout = new GdDict
            {
                { "schema_version", SCHEMA_VERSION },
                { "document_kind", "ship_layout" },
                { "program_id", "life_boat_fixed" },
                { "kit_id", kitId },
                { "design_intent", "fixed hand-authored life boat layout" },
                { "cell_size", CELL_SIZE },
                { "rooms", roomsArray },
                { "room_links", roomLinks },
                { "portals", portals },
                { "blocked_links", new GdArray() },
                { "vertical_connections", new GdArray() },
                { "landmarks", new GdArray() },
                { "critical_path", GdArray.Of("airlock_01", "cockpit_01") },
                { "fire_zones", new GdArray() },
                { "arc_zones", new GdArray() },
                { "breach_zones", new GdArray() },
                { "prototype", new GdDict { { "start_room", "airlock_01" }, { "goal_room", "cockpit_01" } } },
            };

            GdDict structuralPlan = new StructuralEdgeCompiler().Compile(layout);
            GdDict verdict = new StructuralPlanValidator().Validate(structuralPlan, layout);
            if (!V.Bool(verdict.Get("ok", false)))
                CoreServices.Log.Error("LifeBoatBuilder: structural plan validation failed: " + V.Str(verdict.Get("errors", new GdArray())));
            layout["structural_plan"] = structuralPlan;
            ApplyPlanToRooms(layout, structuralPlan, portals);
            return layout;
        }

        /// <summary>The life boat RoomGraph (room roles without the plan).</summary>
        public static RoomGraph BuildGraph()
        {
            var graph = new RoomGraph();
            foreach (GdDict roomDef in ROOMS) graph.AddRoom(V.Str(roomDef["id"]), V.Str(roomDef["role"]), V.I64(roomDef["deck"]));
            graph.AddLink("airlock_01", "cockpit_01", "door");
            graph.AddLink("airlock_01", "engine_bay_01", "door");
            return graph;
        }

        public static string KitIdForBiome(string biome)
        {
            switch (biome)
            {
                case "breach_field": return "ship_structural_hazard";
                case "dead_fleet": return "ship_structural_industrial";
                default: return DEFAULT_KIT_ID;
            }
        }

        static GdArray PlanRecords(GdDict plan)
        {
            var all = new GdArray();
            all.AppendArray(plan.GetArrayOrEmpty("floor_placements"));
            all.AppendArray(plan.GetArrayOrEmpty("placements"));
            all.AppendArray(plan.GetArrayOrEmpty("ceiling_placements"));
            return all;
        }

        static string RecordRoomId(GdDict record)
        {
            string roomId = V.Str(record.Get("room_id", record.Get("owner_room", "")));
            if (roomId.Length == 0 && record.Get("room_ids", new GdArray()) is GdArray roomIds && !roomIds.IsEmpty)
                roomId = V.Str(roomIds[0]);
            return roomId;
        }

        static void ApplyPlanToRooms(GdDict layout, GdDict plan, GdArray portals)
        {
            var byRoom = new GdDict();
            foreach (var roomVariant in layout.GetArrayOrEmpty("rooms"))
            {
                if (!(roomVariant is GdDict room)) continue;
                room["structural_placements"] = new GdArray();
                room["portals"] = new GdArray();
                byRoom[V.Str(room.Get("id", ""))] = room;
            }
            foreach (var recordVariant in PlanRecords(plan))
            {
                if (!(recordVariant is GdDict record)) continue;
                string roomId = RecordRoomId(record);
                if (!byRoom.Has(roomId)) continue;
                GdArray pos = Vec3ToArray(record.Get("position", new GdArray()));
                object cellVariant = record.Get("cell", GdArray.Of(0L, 0L, 0L));
                GdArray cellArr = cellVariant as GdArray ?? CellToArray(cellVariant, V.I64(record.Get("deck", 0L)));
                ((GdDict)byRoom[roomId]).GetArray("structural_placements").Append(new GdDict
                {
                    { "name", V.Str(record.Get("placement_id", record.Get("id", ""))) },
                    { "module", V.Str(record.Get("module_id", "")) },
                    { "module_id", V.Str(record.Get("module_id", "")) },
                    { "position", pos.Count >= 3 ? pos : cellArr },
                    { "world_position", pos },
                    { "yaw_degrees", V.F64(record.Get("yaw_degrees", 0.0)) },
                });
            }
            foreach (var portalVariant in portals)
            {
                if (!(portalVariant is GdDict portal)) continue;
                string fromRoom = V.Str(portal.Get("from_room", ""));
                if (byRoom.Has(fromRoom)) ((GdDict)byRoom[fromRoom]).GetArray("portals").Append(portal.DeepCopy());
            }
        }

        static bool InstancePlanRecords(object recordsVariant, string layer, GdDict moduleToScene,
            Dictionary<string, RoomNode> roomNodes, BuildResult result)
        {
            if (!(recordsVariant is GdArray records)) return true;
            foreach (var recordVariant in records)
            {
                if (!(recordVariant is GdDict record)) continue;
                string moduleId = V.Str(record.Get("module_id", ""));
                if (moduleId.Length == 0) continue;
                string scenePath = V.Str(moduleToScene.Get(moduleId, ""));
                // RUNTIME: GDScript also required ResourceLoader.exists(scene_path) and a Node3D PackedScene root.
                if (scenePath.Length == 0)
                {
                    CoreServices.Log.Error("LifeBoatBuilder: missing wrapper for module " + moduleId);
                    return false;
                }
                Vec3 worldPos = AsVector3(record.Get("position", Vec3.Zero));
                string roomId = RecordRoomId(record);
                roomNodes.TryGetValue(roomId, out RoomNode parent);
                Vec3 parentPos = parent?.Position ?? Vec3.Zero;
                result.Wrappers.Add(new WrapperRecord
                {
                    NodeName = V.Str(record.Get("placement_id", record.Get("id", moduleId))).Replace(":", "_").Replace("|", "_"),
                    ModuleId = moduleId,
                    ScenePath = scenePath,
                    ParentRoomId = parent != null ? roomId : "",
                    LocalPosition = worldPos - parentPos,
                    Position = worldPos,
                    YawDegrees = V.F64(record.Get("yaw_degrees", 0.0)),
                    Layer = layer,
                });
            }
            return true;
        }

        /// <summary>
        /// Unity-port addition (A4): the kit path <see cref="ModuleSceneMap"/> reads for <paramref name="kitId"/> (a missing
        /// kit, or one with no wrapper scenes, resolves to <see cref="DEFAULT_KIT_PATH"/>).
        /// </summary>
        public static string ResolveKitPath(string kitId)
        {
            string kitPath = "res://data/kits/" + kitId + ".json";
            if (!CatalogRegistry.Exists(kitPath)) return DEFAULT_KIT_PATH;
            return ModuleSceneMapAt(kitPath).IsEmpty ? DEFAULT_KIT_PATH : kitPath;
        }

        /// <summary>module_id -&gt; godot_wrapper_scene from the kit JSON (falls back to the default kit).</summary>
        public static GdDict ModuleSceneMap(string kitId)
        {
            string kitPath = "res://data/kits/" + kitId + ".json";
            if (!CatalogRegistry.Exists(kitPath)) kitPath = DEFAULT_KIT_PATH;
            GdDict moduleToScene = ModuleSceneMapAt(kitPath);
            if (moduleToScene.IsEmpty && kitPath != DEFAULT_KIT_PATH) return ModuleSceneMap(DEFAULT_KIT_ID);
            return moduleToScene;
        }

        static GdDict ModuleSceneMapAt(string kitPath)
        {
            GdDict parsed = CatalogRegistry.LoadDict(kitPath);
            var moduleToScene = new GdDict();
            if (parsed != null && parsed.Get("modules", new GdArray()) is GdArray modules)
            {
                foreach (var moduleVariant in modules)
                {
                    if (!(moduleVariant is GdDict module)) continue;
                    string moduleId = V.Str(module.Get("module_id", ""));
                    string scenePath = V.Str(module.Get("godot_wrapper_scene", ""));
                    if (moduleId.Length != 0 && scenePath.Length != 0) moduleToScene[moduleId] = scenePath;
                }
            }
            return moduleToScene;
        }

        static GdArray Vec3ToArray(object value)
        {
            if (value is Vec3 v) return GdArray.Of((double)v.X, (double)v.Y, (double)v.Z);
            if (value is GdArray arr && arr.Count >= 3) return GdArray.Of(V.F64(arr[0]), V.F64(arr[1]), V.F64(arr[2]));
            return new GdArray();
        }

        static GdArray CellToArray(object value, long deck)
        {
            if (value is Vec2i cell) return GdArray.Of((long)cell.X, deck, (long)cell.Y);
            if (value is GdArray arr) return arr;
            return GdArray.Of(0L, deck, 0L);
        }

        static Vec3 AsVector3(object value)
        {
            if (value is Vec3 v) return v;
            if (value is GdArray arr && arr.Count >= 3) return new Vec3(V.F64(arr[0]), V.F64(arr[1]), V.F64(arr[2]));
            return Vec3.Zero;
        }
    }
}
