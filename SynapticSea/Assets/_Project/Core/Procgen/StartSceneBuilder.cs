// Ported from scripts/procgen/start_scene_builder.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Builds the start scene documents for <paramref name="seedValue"/>; null on failure.
        /// <paramref name="boardingCellFallback"/> (Unity port, C3): when the derelict has no dock room, place the life boat
        /// at the boarding/airlock cell (<see cref="FindBoardingPosition"/>) instead of failing. Off = Godot, which fails
        /// for every legacy template seed.
        /// </summary>
        public static StartSceneDocuments Build(long seedValue, bool boardingCellFallback = false)
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
            if (dockPos == Vec3.Inf && boardingCellFallback)
            {
                dockPos = FindBoardingPosition(derelictLayout, V.Str(derelictGameplay.Get("start_room", "")));
                if (dockPos != Vec3.Inf)
                    CoreServices.Log.Info("StartSceneBuilder: no dock room; life boat placed at the boarding cell " + dockPos);
            }
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

        /// <summary>
        /// Unity port (C3): the boarding cell's world centre used when a layout has no dock room. Prefers the
        /// <c>airlock</c> room, then <paramref name="startRoomId"/>, then the first room with a boarding cell
        /// (<see cref="GeneratedShipLayout.BoardingCellXz"/>: reserved_cells[0], else the first floor_cell_*).
        /// <see cref="Vec3.Inf"/> when no room has one.
        /// </summary>
        public static Vec3 FindBoardingPosition(GdDict layout, string startRoomId = "")
        {
            GdArray rooms = layout?.GetArrayOrEmpty("rooms") ?? new GdArray();
            var candidates = new List<GdDict>();
            foreach (var roomVariant in rooms)
            {
                if (roomVariant is GdDict room && (V.Str(room.Get("room_role", "")) == "airlock" || GdString.BeginsWith(V.Str(room.Get("id", "")), "airlock")))
                    candidates.Add(room);
            }
            if (!string.IsNullOrEmpty(startRoomId))
            {
                foreach (var roomVariant in rooms)
                {
                    if (roomVariant is GdDict room && V.Str(room.Get("id", "")) == startRoomId && !candidates.Contains(room))
                        candidates.Add(room);
                }
            }
            foreach (var roomVariant in rooms)
            {
                if (roomVariant is GdDict room && !candidates.Contains(room))
                    candidates.Add(room);
            }
            foreach (GdDict room in candidates)
            {
                GdArray cell = GeneratedShipLayout.BoardingCellXz(room);
                if (cell.Count < 2) continue;
                return StructuralEdgeCompiler.CellWorldPosition(V.I64(room.Get("deck", 0L)), new Vec2i((int)V.I64(cell[0]), (int)V.I64(cell[1])));
            }
            return Vec3.Inf;
        }

        // ------------------------------------------------------------------ Unity port (C2/C3): the generated home start

        /// <summary>Tries per New Run start: seed, seed+1, ... seed+MAX_START_ATTEMPTS-1.</summary>
        public const int MAX_START_ATTEMPTS = 8;
        /// <summary>Home blueprint for a New Run: a medium, damaged ship (the golden sidecar's size / condition class).</summary>
        public const long HOME_SIZE = (long)ShipBlueprint.Size.Medium;
        public const long HOME_CONDITION = (long)ShipBlueprint.Condition.Damaged;

        /// <summary>A viable generated home start: the ship documents plus where the life boat attaches.</summary>
        public sealed class HomeStart
        {
            public ShipDocuments Documents;
            public ShipBlueprint Blueprint;
            /// <summary>The seed the player asked for.</summary>
            public long RequestedSeed;
            /// <summary>The seed actually generated (RequestedSeed + Attempts - 1).</summary>
            public long Seed;
            public int Attempts;
            /// <summary>"dock" (dock room floor centroid) or "boarding" (boarding/airlock cell fallback).</summary>
            public string AnchorSource = "";
            /// <summary>Ship-local life boat anchor + (0, 0, DOCK_GAP), Godot frame.</summary>
            public Vec3 LifeBoatPosition;
            /// <summary>One "seed N: reason" line per rejected attempt.</summary>
            public readonly List<string> Rejections = new List<string>();
        }

        /// <summary>
        /// Generates the New Run home ship for <paramref name="seedValue"/> through <see cref="ShipGenerator"/> with the run
        /// context, gated by <see cref="ValidateHomeStart"/>. A rejected seed is retried deterministically with seed+1, up to
        /// <paramref name="maxAttempts"/> tries; each rejection is logged. Null (logged as an error) when no try is viable.
        /// </summary>
        /// <param name="extraGate">Optional further check after <see cref="ValidateHomeStart"/> ("" = accept); tests use it to
        /// force rejections.</param>
        public static HomeStart BuildHomeStart(long seedValue, string biomeId, string difficultyId, long size = HOME_SIZE, long condition = HOME_CONDITION,
            int maxAttempts = MAX_START_ATTEMPTS, Func<long, ShipDocuments, string> extraGate = null)
        {
            var result = new HomeStart { RequestedSeed = seedValue };
            for (int attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
            {
                long seed = unchecked(seedValue + attempt);
                result.Attempts = attempt + 1;
                var generator = new ShipGenerator();
                generator.ConfigureRunContext(biomeId ?? "", difficultyId ?? "");
                ShipDocuments docs = generator.GenerateFromSeed(seed, size, condition);
                Vec3 anchor = Vec3.Inf;
                string source = "";
                string reason = docs == null ? "generation failed" : ValidateHomeStart(docs, out anchor, out source);
                if (reason.Length == 0 && extraGate != null) reason = extraGate(seed, docs) ?? "";
                if (reason.Length == 0)
                {
                    docs.IsAway = false;
                    result.Documents = docs;
                    result.Blueprint = new ShipBlueprint(size, condition, seed);
                    result.Seed = seed;
                    result.AnchorSource = source;
                    result.LifeBoatPosition = anchor + new Vec3(0.0, 0.0, DOCK_GAP);
                    if (attempt > 0)
                        CoreServices.Log.Warning("StartSceneBuilder: reseeded the home start from " + GdString.FormatInt(seedValue) + " to " + GdString.FormatInt(seed));
                    return result;
                }
                string line = "seed " + GdString.FormatInt(seed) + ": " + reason;
                result.Rejections.Add(line);
                CoreServices.Log.Warning("StartSceneBuilder: home start rejected, " + line);
            }
            CoreServices.Log.Error("StartSceneBuilder: no viable home start in " + result.Attempts + " attempts from seed " + GdString.FormatInt(seedValue));
            return null;
        }

        /// <summary>
        /// The start gate: "" when <paramref name="docs"/> can host a run, else the first failed check, in order:
        /// <list type="number">
        /// <item>the stamped structural plan passes <see cref="StructuralPlanValidator"/>;</item>
        /// <item>the gameplay slice has objectives and a start room with occupied cells;</item>
        /// <item>walkability: every objective room and the goal room are reachable from the start room through non-SOLID
        /// edges (<see cref="WalkabilityContract.RoomsReachable"/> on enclosure adjacency, since route gates and breaches open
        /// during play);</item>
        /// <item>a life boat anchor: the dock room, else the boarding/airlock cell (<see cref="FindBoardingPosition"/>), and a
        /// dock port the session can dock the life boat to (<c>DockPorts.ForDerelict</c>).</item>
        /// </list>
        /// </summary>
        public static string ValidateHomeStart(ShipDocuments docs, out Vec3 anchor, out string anchorSource)
        {
            anchor = Vec3.Inf;
            anchorSource = "";
            if (docs == null || docs.Layout == null || docs.Layout.IsEmpty) return "no layout";
            GdDict layout = docs.Layout;
            GdDict slice = docs.GameplaySlice ?? new GdDict();
            GdDict plan = layout.GetDictOrEmpty("structural_plan");
            GdDict verdict = new StructuralPlanValidator().Validate(plan, layout);
            if (!V.Bool(verdict.Get("ok", false)))
                return "structural plan invalid: " + GdJson.Stringify(verdict.Get("errors", new GdArray()));
            GdArray objectives = slice.GetArrayOrEmpty("objectives");
            if (objectives.IsEmpty) return "gameplay slice has no objectives";
            string startRoom = V.Str(slice.Get("start_room", ""));
            if (startRoom.Length == 0) return "gameplay slice has no start room";
            GdDict occupancy = plan.GetDictOrEmpty("occupancy");
            if (WalkabilityContract.RoomCellKeys(occupancy, startRoom).Count == 0) return "start room " + startRoom + " has no occupied cells";
            GdDict adjacency = WalkabilityContract.BuildAdjacency(occupancy, plan.GetDictOrEmpty("edges"), layout, false);
            var goals = new List<string>();
            foreach (var objectiveVariant in objectives)
            {
                if (!(objectiveVariant is GdDict objective)) continue;
                string room = V.Str(objective.Get("room_id", ""));
                if (room.Length != 0 && !goals.Contains(room)) goals.Add(room);
            }
            string goalRoom = V.Str(slice.Get("goal_room", ""));
            if (goalRoom.Length != 0 && !goals.Contains(goalRoom)) goals.Add(goalRoom);
            foreach (string room in goals)
            {
                if (room != startRoom && !WalkabilityContract.RoomsReachable(adjacency, occupancy, startRoom, room))
                    return "room " + room + " is not walkable from start room " + startRoom;
            }
            anchor = FindDockPosition(layout);
            anchorSource = "dock";
            if (anchor == Vec3.Inf)
            {
                anchor = FindBoardingPosition(layout, startRoom);
                anchorSource = "boarding";
            }
            if (anchor == Vec3.Inf)
            {
                anchorSource = "";
                return "no dock room and no boarding cell for the life boat";
            }
            // The session docks the life boat through DockPorts (dock room, else airlock room).
            if (Systems.DockPorts.ForDerelict(layout).IsEmpty)
                return "no dock or airlock port for the life boat to dock to";
            return "";
        }

        static GdDict LoadArchetype(string path)
        {
            if (!CatalogRegistry.Exists(path)) return new GdDict();
            return CatalogRegistry.LoadDict(path) ?? new GdDict();
        }
    }
}
