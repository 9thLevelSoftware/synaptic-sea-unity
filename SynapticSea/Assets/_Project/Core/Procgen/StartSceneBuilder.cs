// Ported from scripts/procgen/start_scene_builder.gd @ 96ecb2b0
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Builds the combined start scene: a randomized derelict with the fixed life boat attached at the dock.
    /// Pipeline: derelict layout (ShipLayoutGenerator.generate), life boat layout (LifeBoatBuilder.build_layout),
    /// gameplay slices for both, then the life boat positioned <see cref="DOCK_GAP"/> past the derelict's dock.
    /// RUNTIME: GDScript wrote both document pairs to user://start_scenario/ and loaded them with two
    /// GeneratedShipLoaders ("Derelict", "LifeBoat") under a "StartScene" Node3D; <see cref="Build"/> returns the
    /// documents and the life boat offset instead.
    /// </summary>
    public static class StartSceneBuilder
    {
        public const string DERELICT_ARCHETYPE_PATH = "res://data/procgen/archetypes/derelict.json";
        /// <summary>Structural kit used by GeneratedShipLoader for both ships.</summary>
        public const string KIT_PATH = "res://data/kits/ship_structural_v0.json";
        /// <summary>Temp directory Godot wrote the documents to (informational).</summary>
        public const string TEMP_DIR = "user://start_scenario";
        /// <summary>Gap between the derelict dock and the life boat airlock (world units).</summary>
        public const double DOCK_GAP = 6.0;

        /// <summary>RUNTIME: stands in for the returned "StartScene" Node3D.</summary>
        public sealed class StartSceneDocuments
        {
            public const string ROOT_NAME = "StartScene";

            /// <summary>Child "Derelict": <c>load_from_paths(derelict_layout, KIT_PATH, derelict_gameplay)</c> (is_away false).</summary>
            public ShipDocuments Derelict;

            /// <summary>Child "LifeBoat": <c>load_from_paths(lifeboat_layout, KIT_PATH, lifeboat_gameplay)</c>.</summary>
            public ShipDocuments LifeBoat;

            /// <summary>Life boat node position: dock floor-cell centroid + (0, 0, DOCK_GAP), Godot frame.</summary>
            public Vec3 LifeBoatPosition;
        }

        /// <summary>Builds the start scene documents for <paramref name="seedValue"/>; null on failure.</summary>
        public static StartSceneDocuments Build(long seedValue)
        {
            GdDict archetype = LoadArchetype(DERELICT_ARCHETYPE_PATH);
            if (archetype.IsEmpty)
            {
                CoreServices.Log.Error("StartSceneBuilder: could not load derelict archetype");
                return null;
            }

            // Step 1: derelict layout.
            ShipBlueprint blueprint = BuildBlueprint(archetype, seedValue);
            GdDict derelictLayout = new ShipLayoutGenerator().Generate(blueprint, archetype);
            if (derelictLayout.IsEmpty)
            {
                CoreServices.Log.Error("StartSceneBuilder: derelict layout generation failed");
                return null;
            }

            // Step 2: life boat layout.
            GdDict lbLayout = LifeBoatBuilder.BuildLayout();
            if (lbLayout.IsEmpty)
            {
                CoreServices.Log.Error("StartSceneBuilder: life boat layout generation failed");
                return null;
            }

            // Step 3: gameplay slices.
            var sliceBuilder = new GameplaySliceBuilder();
            GdDict derelictGameplay = sliceBuilder.Build(derelictLayout);
            if (derelictGameplay.IsEmpty)
            {
                CoreServices.Log.Error("StartSceneBuilder: derelict gameplay slice build failed");
                return null;
            }
            GdDict lbGameplay = sliceBuilder.Build(lbLayout);
            if (lbGameplay.IsEmpty)
            {
                CoreServices.Log.Error("StartSceneBuilder: life boat gameplay slice build failed");
                return null;
            }

            // Steps 4-5 (RUNTIME): JSON files under TEMP_DIR, loaded through GeneratedShipLoader.load_from_paths.
            GdDict kit = CatalogRegistry.LoadDict(KIT_PATH) ?? new GdDict();
            var derelict = new ShipDocuments
            {
                Layout = RoundTrip(derelictLayout),
                Kit = kit,
                GameplaySlice = RoundTrip(derelictGameplay),
                IsAway = false,
                KitPath = KIT_PATH,
                Name = "Derelict",
            };
            var lifeBoat = new ShipDocuments
            {
                Layout = RoundTrip(lbLayout),
                Kit = kit.DeepCopy(),
                GameplaySlice = RoundTrip(lbGameplay),
                IsAway = false,
                KitPath = KIT_PATH,
                Name = "LifeBoat",
            };

            // Step 6: position the life boat adjacent to the derelict's dock room.
            Vec3 dockPos = FindDockPosition(derelictLayout);
            if (dockPos == Vec3.Inf)
            {
                CoreServices.Log.Error("StartSceneBuilder: no dock room found in derelict layout; cannot position life boat");
                return null;
            }

            return new StartSceneDocuments
            {
                Derelict = derelict,
                LifeBoat = lifeBoat,
                LifeBoatPosition = dockPos + new Vec3(0.0, 0.0, DOCK_GAP),
            };
        }

        /// <summary><c>JSON.stringify(data, "  ")</c> written to disk, then parsed back by the loader.</summary>
        static GdDict RoundTrip(GdDict data) => GdJson.ParseDict(GdJson.Stringify(data, "  "));

        /// <summary>Blueprint from the archetype's blueprint dict, overriding the seed.</summary>
        public static ShipBlueprint BuildBlueprint(GdDict archetype, long seedValue)
        {
            GdDict bpData = archetype.GetDictOrEmpty("blueprint");
            long size = V.I64(bpData.Get("size", (long)ShipBlueprint.Size.Medium));
            long condition = V.I64(bpData.Get("condition", (long)ShipBlueprint.Condition.Wrecked));
            return new ShipBlueprint(size, condition, seedValue);
        }

        /// <summary>
        /// Average world position of the dock room's floor placements (floor_1x1 / corridor_floor_1x1), or
        /// <see cref="Vec3.Inf"/> when no dock room has one.
        /// </summary>
        public static Vec3 FindDockPosition(GdDict layout)
        {
            foreach (var roomVariant in layout.GetArrayOrEmpty("rooms"))
            {
                if (!(roomVariant is GdDict room)) continue;
                string role = V.Str(room.Get("room_role", ""));
                string rid = V.Str(room.Get("id", ""));
                if (role != "dock" && !GdString.BeginsWith(rid, "dock")) continue;
                Vec3 sum = Vec3.Zero;
                long count = 0;
                foreach (var placementVariant in room.GetArrayOrEmpty("structural_placements"))
                {
                    if (!(placementVariant is GdDict placement)) continue;
                    string moduleId = V.Str(placement.Get("module_id", placement.Get("module", "")));
                    if (moduleId != "floor_1x1" && moduleId != "corridor_floor_1x1") continue;
                    object pos = placement.Get("world_position", placement.Get("position", null));
                    if (!(pos is GdArray posArr) || posArr.Count < 3) continue;
                    sum = sum + new Vec3(V.F64(posArr[0]), V.F64(posArr[1]), V.F64(posArr[2]));
                    count += 1;
                }
                if (count > 0) return sum / (float)count;
            }
            return Vec3.Inf;
        }

        static GdDict LoadArchetype(string path)
        {
            if (!CatalogRegistry.Exists(path)) return new GdDict();
            return CatalogRegistry.LoadDict(path) ?? new GdDict();
        }
    }
}
