// Ported from scripts/procgen/generated_ship_loader.gd (the pure spec/query half) @ 96ecb2b0
// Also carries small private mirrors of LayoutSerializer.parse_slot_cell, GameplaySliceBuilder.boarding_cell_xz and
// ComponentPlacementState.MAX_*_FILLS until those Phase 4 ports land (swap the calls over then).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Everything <c>GeneratedShipLoader</c> computes from the layout / gameplay-slice documents without touching the
    /// scene: objective and loot specs, room centers, hazard-zone markers and specs, authored atmosphere specs, portal
    /// specs, placed-prop plans, the dressing plan (lights, fog markers, dressing props — same RNG and iteration order
    /// as Godot), and the marker / trigger-volume descriptors the Runtime <c>ShipSceneBuilder</c> instantiates.
    /// Every position is in Godot's frame, local to the ship root (the Godot loader node). The Runtime converts
    /// through <c>Frame</c>.
    /// </summary>
    public sealed class GeneratedShipLayout
    {
        public const double CELL_SIZE = 4.0;
        public const double FLOOR_Y_OFFSET = 0.12;
        public const double ATMOSPHERE_VOLUME_HALF_WIDTH = CELL_SIZE * 0.5;
        public const double ATMOSPHERE_VOLUME_HEIGHT = 2.5;
        public const double OBJECTIVE_TRIGGER_RADIUS = 1.5;
        public const double RADIATION_VOLUME_HALF_WIDTH = 1.25;
        public const double RADIATION_VOLUME_AXIAL_END_PADDING = 1.0;
        public static readonly IReadOnlyList<string> FLOOR_MODULES = new[] { "floor_1x1", "corridor_floor_1x1" };
        public static readonly IReadOnlyList<string> DRESSING_PROP_KINDS = new[] { "crate", "pipe", "growth" };

        // ComponentPlacementState.MAX_WALL_FILLS / MAX_CENTER_FILLS (scripts/systems/component_placement_state.gd).
        const long MAX_WALL_FILLS = 3;
        const long MAX_CENTER_FILLS = 1;

        // Marker colors/sizes (_LANDMARK_COLOR ... _VERTICAL_TRANSITION_SIZE).
        public static readonly float[] LANDMARK_COLOR = { 0.15f, 0.65f, 1.0f, 1.0f };
        public static readonly float[] BLOCKED_ROUTE_COLOR = { 0.85f, 0.2f, 0.18f, 1.0f };
        public static readonly float[] VERTICAL_TRANSITION_COLOR = { 0.9f, 0.68f, 0.25f, 1.0f };
        public static readonly Vec3 LANDMARK_SIZE = new Vec3(0.8f, 2.4f, 0.8f);
        public static readonly Vec3 BLOCKED_ROUTE_SIZE = new Vec3(3.8f, 2.0f, 0.45f);
        public static readonly Vec3 VERTICAL_TRANSITION_SIZE = new Vec3(4.0f, 0.45f, 5.5f);
        public static readonly float[] RADIATION_VOLUME_COLOR = { 0.65f, 0.2f, 0.9f, 0.3f };
        public static readonly float[] ATMOSPHERE_VOLUME_COLOR = { 0.15f, 0.7f, 0.9f, 0.08f };

        // ------------------------------------------------------------------ descriptors

        /// <summary>A collidable marker box (<c>_make_marker_node</c>). Basis columns are Godot-frame axes.</summary>
        public sealed class MarkerSpec
        {
            public string Name;
            public Vec3 Position;
            public float[] Color;
            public Vec3 Size;
            public Vec3 BasisX = Vec3.Right, BasisY = Vec3.Up, BasisZ = Vec3.Back;
        }

        /// <summary>A trigger box (<c>_make_trigger_volume</c> / <c>_make_oriented_trigger_volume</c>).</summary>
        public sealed class TriggerVolumeSpec
        {
            public string Name;
            /// <summary>"radiation" or "atmosphere".</summary>
            public string Kind;
            public Vec3 Position;
            public float[] Color;
            public Vec3 Size;
            public Vec3 BasisX = Vec3.Right, BasisY = Vec3.Up, BasisZ = Vec3.Back;
            /// <summary>The zone spec (radiation) or the atmosphere spec (Godot meta "atmosphere").</summary>
            public GdDict Spec;
        }

        /// <summary>A resolved vertical connection (Godot NavigationLink3D; kept as data for the nav graph).</summary>
        public sealed class VerticalLinkSpec
        {
            public string Name;
            public Vec3 Start, End;
        }

        public enum DressingKind { Light, Fog, Prop }

        /// <summary>One child of <c>DressingVisuals</c>, in Godot child order.</summary>
        public sealed class DressingItem
        {
            public DressingKind Kind;
            public string Name;
            public string RoomId;
            public string Dressing;
            public Vec3 Position;
            // Light
            public double LightEnergy;
            public double OmniRange;
            public float[] LightColor;
            public double PropDensity;
            public double FogDensity;
            public GdArray Tint;
            // Fog
            public float SphereRadius, SphereHeight;
            public float[] Albedo;
            // Prop
            public string PropKind;
            public long SlotIndex;
            public GdArray SlotCell;
        }

        /// <summary>A portal to instantiate: the spec (with runtime/from/to positions) and its midpoint.</summary>
        public sealed class PortalPlan
        {
            public int Index;
            public GdDict Spec;
            public Vec3 Position;
        }

        /// <summary>
        /// One authored placed prop in document order: either an error (unknown id) or a candidate the Runtime
        /// materializes (a dressing binding → prop prefab, else a GameplayPropFactory primitive).
        /// </summary>
        public sealed class PlacedPropEntry
        {
            public string Error;
            public string PropId;
            public GdDict Spec;
            public GdDict DressingBinding;
            public long QuarterTurns;
            public Vec3 Position;
            public object AuthoredId;
        }

        // ------------------------------------------------------------------ state (mirrors the loader vars)

        public GdDict LayoutDoc = new GdDict();
        public GdDict GameplayDoc = new GdDict();
        public GdArray ObjectiveSpecs = new GdArray();
        public GdArray LootContainerSpecs = new GdArray();
        public Vec3 StartPosition = Vec3.Inf;
        public Vec3 GoalPosition = Vec3.Inf;
        public GdDict RoomVariantDescriptors = new GdDict();
        public readonly List<Vec3> BreachZoneMarkers = new List<Vec3>();
        public GdArray BreachZoneSpecs = new GdArray();
        public readonly List<Vec3> FireZoneMarkers = new List<Vec3>();
        public GdArray FireZoneSpecs = new GdArray();
        public readonly List<Vec3> ArcZoneMarkers = new List<Vec3>();
        public GdArray ArcZoneSpecs = new GdArray();
        public readonly List<Vec3> RadiationZoneMarkers = new List<Vec3>();
        public GdArray RadiationZoneSpecs = new GdArray();
        public GdArray RadiationZoneSegments = new GdArray();
        public GdArray AuthoredAtmosphereSpecs = new GdArray();
        public GdArray AuthoredPortalSpecs = new GdArray();

        public readonly List<MarkerSpec> Landmarks = new List<MarkerSpec>();
        public readonly List<MarkerSpec> BlockedRoutes = new List<MarkerSpec>();
        public readonly List<MarkerSpec> VerticalTransitions = new List<MarkerSpec>();
        public readonly List<TriggerVolumeSpec> RadiationVolumes = new List<TriggerVolumeSpec>();
        public readonly List<TriggerVolumeSpec> AtmosphereVolumes = new List<TriggerVolumeSpec>();
        public readonly List<VerticalLinkSpec> VerticalLinks = new List<VerticalLinkSpec>();

        public GeneratedShipLayout() { }

        public GeneratedShipLayout(GdDict layout, GdDict gameplay)
        {
            LayoutDoc = layout ?? new GdDict();
            GameplayDoc = gameplay ?? new GdDict();
            BuildRoomVariantDescriptors();
        }

        GdArray Rooms => LayoutDoc.Get("rooms") as GdArray ?? new GdArray();

        static ILog Log => CoreServices.Log;

        // ------------------------------------------------------------------ objectives / loot

        /// <summary>Port of <c>_build_objective_specs</c>. Returns [] (with an error logged) on any invalid objective.</summary>
        public GdArray BuildObjectiveSpecs(string gameplaySlicePath)
        {
            if (!(LayoutDoc.Get("rooms", new GdArray()) is GdArray rooms))
            {
                Log.Error("layout missing rooms array: " + gameplaySlicePath);
                return new GdArray();
            }
            if (!(GameplayDoc.Get("objectives", new GdArray()) is GdArray objectives))
            {
                Log.Error("gameplay slice missing objectives array: " + gameplaySlicePath);
                return new GdArray();
            }
            if (objectives.IsEmpty)
            {
                Log.Error("gameplay slice contains no objectives: " + gameplaySlicePath);
                return new GdArray();
            }

            long expectedSequence = 1;
            var specs = new GdArray();
            foreach (object objectiveVariant in objectives)
            {
                if (!(objectiveVariant is GdDict objective))
                {
                    Log.Error("gameplay slice objective is not an object: " + gameplaySlicePath);
                    return new GdArray();
                }
                string objectiveId = V.Str(objective.Get("id", ""));
                if (objectiveId.Length == 0)
                {
                    Log.Error("gameplay slice objective missing id: " + gameplaySlicePath);
                    return new GdArray();
                }
                long sequence = V.I64(objective.Get("sequence", 0L));
                if (sequence != expectedSequence)
                {
                    Log.Error($"gameplay slice objective sequence mismatch: expected={expectedSequence} got={sequence} objective={objectiveId}");
                    return new GdArray();
                }
                expectedSequence += 1;

                string roomId = V.Str(objective.Get("room_id", ""));
                if (roomId.Length == 0)
                {
                    Log.Error("gameplay slice objective missing room_id: " + objectiveId);
                    return new GdArray();
                }
                GdDict room = FindRoom(rooms, roomId);
                if (room.IsEmpty)
                {
                    Log.Error("objective room not found in layout: " + roomId);
                    return new GdArray();
                }
                if (!(objective.Get("approach_cell", new GdArray()) is GdArray approachCell))
                {
                    Log.Error("objective missing approach_cell: " + objectiveId);
                    return new GdArray();
                }
                if (approachCell.Count < 3)
                {
                    Log.Error("objective approach_cell is incomplete: " + objectiveId);
                    return new GdArray();
                }

                Vec3 targetPosition = RoomCellWorld(LayoutDoc, room, approachCell);
                if (targetPosition == Vec3.Inf)
                {
                    Log.Error($"no floor position for approach cell objective={objectiveId} room={roomId} cell={V.Str(approachCell)}");
                    return new GdArray();
                }

                string kind = V.Str(objective.Get("kind", "single"));
                var stepSpecs = new GdArray();
                if (kind == "repair_junction")
                {
                    object stepsVariant = objective.Get("steps", new GdArray());
                    if (!(stepsVariant is GdArray steps) || steps.Count < 2)
                    {
                        Log.Error("repair_junction objective requires at least 2 steps: " + objectiveId);
                        return new GdArray();
                    }
                    var seenStepIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (object stepVariant in steps)
                    {
                        if (!(stepVariant is GdDict step))
                        {
                            Log.Error("repair_junction step is not an object: " + objectiveId);
                            return new GdArray();
                        }
                        string stepId = V.Str(step.Get("step_id", ""));
                        if (stepId.Length == 0)
                        {
                            Log.Error("repair_junction step missing step_id: " + objectiveId);
                            return new GdArray();
                        }
                        if (seenStepIds.Contains(stepId))
                        {
                            Log.Error($"repair_junction duplicate step_id '{stepId}' in objective {objectiveId}");
                            return new GdArray();
                        }
                        seenStepIds.Add(stepId);
                        GdArray stepApproach = approachCell.ShallowCopy();
                        if (step.Get("approach_cell", new GdArray()) is GdArray sa && sa.Count >= 3) stepApproach = sa;
                        Vec3 stepPosition = RoomCellWorld(LayoutDoc, room, stepApproach);
                        if (stepPosition == Vec3.Inf)
                        {
                            Log.Error($"no floor position for step approach cell objective={objectiveId} step={stepId} cell={V.Str(stepApproach)}");
                            return new GdArray();
                        }
                        stepSpecs.Add(new GdDict
                        {
                            { "step_id", stepId },
                            { "approach_cell", stepApproach },
                            { "position", stepPosition },
                        });
                    }
                }

                var spec = new GdDict
                {
                    { "id", objectiveId },
                    { "sequence", sequence },
                    { "type", V.Str(objective.Get("type", "unknown")) },
                    { "kind", kind },
                    { "room_id", roomId },
                    { "approach_cell", approachCell.ShallowCopy() },
                    { "position", targetPosition },
                    { "radius", OBJECTIVE_TRIGGER_RADIUS },
                    { "steps", stepSpecs },
                };
                if (objective.Has("loot_table")) spec["loot_table"] = V.Str(objective.Get("loot_table", ""));
                specs.Add(spec);
            }
            return specs;
        }

        /// <summary>Port of <c>_build_loot_container_specs</c>.</summary>
        public GdArray BuildLootContainerSpecs()
        {
            if (!(LayoutDoc.Get("rooms", new GdArray()) is GdArray rooms)) return new GdArray();
            if (!(GameplayDoc.Get("loot_containers", new GdArray()) is GdArray containers)) return new GdArray();
            var output = new GdArray();
            foreach (object cVariant in containers)
            {
                if (!(cVariant is GdDict c)) continue;
                string cid = V.Str(c.Get("id", ""));
                string roomId = V.Str(c.Get("room_id", ""));
                if (cid.Length == 0 || roomId.Length == 0) continue;
                GdDict room = FindRoom(rooms, roomId);
                if (room.IsEmpty) continue;
                if (!(c.Get("approach_cell", new GdArray()) is GdArray approach) || approach.Count < 3) continue;
                Vec3 pos = RoomCellWorld(LayoutDoc, room, approach);
                if (pos == Vec3.Inf) continue;
                var lootSpec = new GdDict
                {
                    { "id", cid },
                    { "kind", V.Str(c.Get("kind", "generic_crate")) },
                    { "room_id", roomId },
                    { "loot_table", V.Str(c.Get("loot_table", "generic_crate")) },
                    { "position", pos },
                    { "approach_cell", approach.ShallowCopy() },
                };
                if (c.Has("slot_kind"))
                {
                    lootSpec["slot_kind"] = V.Str(c.Get("slot_kind", ""));
                    lootSpec["slot_index"] = V.I64(c.Get("slot_index", 0L));
                }
                // Explicit authored stacks must survive into the coordinator.
                if (c.Has("contents") && c.Get("contents") is GdArray contents) lootSpec["contents"] = contents.DeepCopy();
                output.Add(lootSpec);
            }
            return output;
        }

        // ------------------------------------------------------------------ rooms / cells

        public static GdDict FindRoom(GdArray rooms, string roomId)
        {
            if (rooms == null) return new GdDict();
            foreach (object roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room)) continue;
                if (V.Str(room.Get("id", "")) == roomId) return room;
            }
            return new GdDict();
        }

        public GdDict FindRoomInLayout(string roomId) => FindRoomIn(LayoutDoc, roomId);

        static GdDict FindRoomIn(GdDict layout, string roomId)
        {
            if (!(layout.Get("rooms", new GdArray()) is GdArray rooms)) return new GdDict();
            return FindRoom(rooms, roomId);
        }

        static GdArray FloorPlacements(GdDict layout)
        {
            var plan = layout.Get("structural_plan", new GdDict()) as GdDict ?? new GdDict();
            return plan.Get("floor_placements", new GdArray()) as GdArray;
        }

        /// <summary>Port of <c>_room_cell_world</c> against <paramref name="layout"/>'s floor placements.</summary>
        public static Vec3 RoomCellWorld(GdDict layout, GdDict room, GdArray cell)
        {
            if (cell.Count < 2) return Vec3.Inf;
            string roomId = V.Str(room.Get("id", ""));
            long deck = V.I64(room.Get("deck", -1L));
            if (cell.Count >= 3) deck = V.I64(cell[2]);
            string cellKey = deck.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                             V.I64(cell[0]).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                             V.I64(cell[1]).ToString(System.Globalization.CultureInfo.InvariantCulture);
            GdArray floors = FloorPlacements(layout);
            if (floors == null) return Vec3.Inf;
            foreach (object floorVariant in floors)
            {
                if (!(floorVariant is GdDict floor)) continue;
                if (V.Str(floor.Get("cell_key", "")) != cellKey || V.Str(floor.Get("room_id", "")) != roomId) continue;
                GdArray pos = ReadPlacementPosition(floor);
                if (pos.Count < 3) return Vec3.Inf;
                return new Vec3(V.F64(pos[0]), V.F64(pos[1]) + FLOOR_Y_OFFSET, V.F64(pos[2]));
            }
            return Vec3.Inf;
        }

        /// <summary>Port of <c>_room_center</c>: float32 mean of the room's floor placements (+ FLOOR_Y_OFFSET).</summary>
        public static Vec3 RoomCenter(GdDict layout, string roomId)
        {
            GdArray floors = FloorPlacements(layout);
            if (floors == null) return Vec3.Inf;
            Vec3 total = Vec3.Zero;
            long count = 0;
            foreach (object floorVariant in floors)
            {
                if (!(floorVariant is GdDict floor)) continue;
                if (V.Str(floor.Get("room_id", "")) != roomId) continue;
                GdArray pos = ReadPlacementPosition(floor);
                if (pos.Count < 3) continue;
                total += new Vec3(V.F64(pos[0]), V.F64(pos[1]) + FLOOR_Y_OFFSET, V.F64(pos[2]));
                count += 1;
            }
            if (count == 0) return Vec3.Inf;
            return total / (float)count;
        }

        public Vec3 RoomCenter(string roomId) => RoomCenter(LayoutDoc, roomId);

        /// <summary>Port of <c>_room_center_from_cells</c> (dressing fallback when a room has no floor placements).</summary>
        public static Vec3 RoomCenterFromCells(GdDict room)
        {
            if (!(room.Get("cells", new GdArray()) is GdArray cells) || cells.IsEmpty) return Vec3.Inf;
            Vec3 total = Vec3.Zero;
            long n = 0;
            long deck = V.I64(room.Get("deck", 0L));
            foreach (object cellVariant in cells)
            {
                GdArray parsed = ParseSlotCell(cellVariant);
                if (parsed.Count < 2) continue;
                total += new Vec3(V.F64(parsed[0]) * CELL_SIZE, (double)deck * 4.0 + FLOOR_Y_OFFSET, V.F64(parsed[1]) * CELL_SIZE);
                n += 1;
            }
            if (n <= 0) return Vec3.Inf;
            return total / (float)n;
        }

        /// <summary>Port of <c>_read_placement_position</c>: the raw position array (elements may be numeric strings), or [].</summary>
        public static GdArray ReadPlacementPosition(GdDict placement)
        {
            if (placement == null) return new GdArray();
            object raw = placement.Get("position", null);
            if (raw == null) raw = placement.Get("world_position", null);
            if (raw is Vec3 vector) return GdArray.Of((double)vector.X, (double)vector.Y, (double)vector.Z);
            if (raw is string s)
            {
                GdArray parsed = ParseVectorString(s, 3);
                return parsed.Count == 3 ? parsed : new GdArray();
            }
            if (!(raw is GdArray arr)) return new GdArray();
            if (arr.Count < 3) return new GdArray();
            for (int i = 0; i < 3; i++)
            {
                object v = arr[i];
                if (!(v is long) && !(v is double) && !(v is string)) return new GdArray();
                if (v is string vs && !IsValidFloat(vs)) return new GdArray();
            }
            return arr;
        }

        static GdArray ParseVectorString(string value, int expected)
        {
            string text = GdString.StripEdges(value);
            if (GdString.BeginsWith(text, "(") && GdString.EndsWith(text, ")")) text = text.Substring(1, text.Length - 2);
            List<string> parts = GdString.Split(text, ",");
            if (parts.Count != expected) return new GdArray();
            var result = new GdArray();
            foreach (string part in parts)
            {
                string token = GdString.StripEdges(part);
                if (!IsValidFloat(token)) return new GdArray();
                result.Add(V.StringToFloat(token));
            }
            return result;
        }

        /// <summary>Godot <c>String.is_valid_float()</c>.</summary>
        public static bool IsValidFloat(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int from = s[0] == '+' || s[0] == '-' ? 1 : 0;
            bool exponentFound = false, periodFound = false, signFound = false, exponentValuesFound = false, numbersFound = false;
            for (int i = from; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9')
                {
                    if (exponentFound) exponentValuesFound = true;
                    else numbersFound = true;
                }
                else if (numbersFound && !exponentFound && c == 'e') exponentFound = true;
                else if (!periodFound && !exponentFound && c == '.') periodFound = true;
                else if ((c == '-' || c == '+') && exponentFound && !exponentValuesFound && !signFound) signFound = true;
                else return false;
            }
            return numbersFound;
        }

        /// <summary>Mirror of <c>LayoutSerializer.parse_slot_cell</c>: [x, z] ints, or [] when unparseable.</summary>
        public static GdArray ParseSlotCell(object value)
        {
            switch (value)
            {
                case Vec2i v2:
                    return GdArray.Of((long)v2.X, (long)v2.Y);
                case GdArray arr:
                    return arr.Count >= 2 ? GdArray.Of(V.I64(arr[0]), V.I64(arr[1])) : new GdArray();
                case GdDict d:
                    return ParseSlotCell(d.Get("cell", null));
                case string raw:
                    {
                        string s = GdString.StripEdges(raw);
                        if (GdString.BeginsWith(s, "floor_cell")) return ParseFloorCellName(s);
                        if (GdString.BeginsWith(s, "(") && GdString.EndsWith(s, ")") && s.Length >= 5) s = s.Substring(1, s.Length - 2);
                        List<string> parts = GdString.Split(s, ",");
                        if (parts.Count < 2) return new GdArray();
                        string xs = GdString.StripEdges(parts[0]);
                        string zs = GdString.StripEdges(parts[1]);
                        if (IsValidFloat(xs) && IsValidFloat(zs))
                            return GdArray.Of(GdMath.RoundI(V.StringToFloat(xs)), GdMath.RoundI(V.StringToFloat(zs)));
                        return new GdArray();
                    }
                default:
                    return new GdArray();
            }
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

        /// <summary>Mirror of <c>GameplaySliceBuilder.boarding_cell_xz</c>.</summary>
        public static GdArray BoardingCellXz(GdDict room)
        {
            if (room.Get("interior_zones", new GdDict()) is GdDict interior &&
                interior.Get("reserved_cells", new GdArray()) is GdArray reserved && !reserved.IsEmpty)
            {
                GdArray parsed = ParseSlotCell(reserved[0]);
                if (parsed.Count >= 2) return parsed;
            }
            if (room.Get("structural_placements", new GdArray()) is GdArray placements)
            {
                foreach (object placement in placements)
                {
                    if (!(placement is GdDict pd)) continue;
                    string placementName = V.Str(pd.Get("name", ""));
                    if (!GdString.BeginsWith(placementName, "floor_cell")) continue;
                    GdArray parsedFloor = ParseSlotCell(placementName);
                    if (parsedFloor.Count >= 2) return parsedFloor;
                }
            }
            return new GdArray();
        }

        /// <summary>Port of <c>_cell_world_from_link_endpoint</c> (resolves against <paramref name="sourceLayout"/>).</summary>
        public Vec3 CellWorldFromLinkEndpoint(GdDict linkDoc, string cellKey, string roomKey, GdDict sourceLayout)
        {
            if (!(linkDoc.Get(cellKey, new GdArray()) is GdArray endpoint)) return Vec3.Inf;
            if (endpoint.Count < 2) return Vec3.Inf;
            string roomId = V.Str(linkDoc.Get(roomKey, ""));
            if (roomId.Length == 0) return Vec3.Inf;
            GdDict layout = sourceLayout != null && !sourceLayout.IsEmpty ? sourceLayout : LayoutDoc;
            GdDict room = FindRoomIn(layout, roomId);
            if (room.IsEmpty) return Vec3.Inf;
            return RoomCellWorld(layout, room, endpoint);
        }

        /// <summary>Port of <c>_room_center_for_blocked_link</c>.</summary>
        public static Vec3 RoomCenterForBlockedLink(GdDict link, string roomKey, GdDict layout)
        {
            string roomId = V.Str(link.Get(roomKey, ""));
            if (roomId.Length == 0) return Vec3.Inf;
            if (!(layout.Get("rooms", new GdArray()) is GdArray)) return Vec3.Inf;
            return RoomCenter(layout, roomId);
        }

        /// <summary>
        /// The <c>_build_navigation_region</c> precondition: at least one floor/corridor-floor placement with a readable
        /// position. (The NavigationRegion3D bake itself is dropped: it was debug-only; runtime AI uses ShipNavGraph.)
        /// </summary>
        public bool HasNavigableFloor()
        {
            GdArray floors = FloorPlacements(LayoutDoc);
            if (floors == null) return false;
            foreach (object floorVariant in floors)
            {
                if (!(floorVariant is GdDict floor)) continue;
                if (!Contains(FLOOR_MODULES, V.Str(floor.Get("module_id", "")))) continue;
                if (ReadPlacementPosition(floor).Count < 3) continue;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ public metadata accessors

        public string GetRoomRole(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return "";
            GdDict room = FindRoomInLayout(roomId);
            return room.IsEmpty ? "" : V.Str(room.Get("room_role", ""));
        }

        public long GetRoomDeck(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return -1;
            GdDict room = FindRoomInLayout(roomId);
            return room.IsEmpty ? -1 : V.I64(room.Get("deck", -1L));
        }

        public Vec3 GetRoomCenter(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return Vec3.Inf;
            if (!(LayoutDoc.Get("rooms", new GdArray()) is GdArray)) return Vec3.Inf;
            return RoomCenter(LayoutDoc, roomId);
        }

        public List<string> GetCriticalPath()
        {
            var output = new List<string>();
            object raw = LayoutDoc.Get("critical_path", new GdArray());
            if (!(raw is GdArray)) raw = GameplayDoc.Get("critical_path", new GdArray());
            if (!(raw is GdArray entries)) return output;
            foreach (object entry in entries) output.Add(V.Str(entry));
            return output;
        }

        GdArray DictEntriesCopy(string key)
        {
            var output = new GdArray();
            if (!(LayoutDoc.Get(key, new GdArray()) is GdArray raw)) return output;
            foreach (object entry in raw)
                if (entry is GdDict d) output.Add(d.DeepCopy());
            return output;
        }

        public GdArray GetRoomLinks() => DictEntriesCopy("room_links");
        public GdArray GetEncounterMarkers() => DictEntriesCopy("encounters");
        public GdArray GetBlockedLinks() => DictEntriesCopy("blocked_links");
        public GdArray GetLandmarkSpecs() => DictEntriesCopy("landmarks");

        // ------------------------------------------------------------------ room variant descriptors

        /// <summary>Port of <c>_build_room_variant_descriptors</c> (PKG-B5.1 presets per room variant).</summary>
        public void BuildRoomVariantDescriptors()
        {
            RoomVariantDescriptors.Clear();
            if (!(LayoutDoc.Get("rooms", new GdArray()) is GdArray rooms)) return;
            var selector = new RoomVariantSelector();
            foreach (object roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room)) continue;
                string variant = V.Str(room.Get("variant", "standard"));
                GdDict effects = selector.EffectsFor(variant);
                if (effects.IsEmpty) continue;
                string rid = V.Str(room.Get("id", ""));
                if (rid.Length == 0) continue;
                string dressing = V.Str(effects.Get("dressing", ""));
                GdDict preset = selector.DressingPreset(dressing);
                var entry = new GdDict
                {
                    { "variant", variant },
                    { "dressing", dressing },
                    { "prop_density", V.F64(preset.Get("prop_density", 1.0)) },
                    { "fog_density", V.F64(preset.Get("fog_density", 0.0)) },
                    { "light_energy", V.F64(preset.Get("light_energy", 0.5)) },
                };
                if (preset.Get("tint", GdArray.Of(1.0, 1.0, 1.0, 1.0)) is GdArray tint && tint.Count >= 3) entry["tint"] = tint.ShallowCopy();
                if (preset.Get("light_color", GdArray.Of(1.0, 1.0, 1.0, 1.0)) is GdArray lightColor && lightColor.Count >= 3)
                    entry["light_color"] = lightColor.ShallowCopy();
                RoomVariantDescriptors[rid] = entry;
            }
        }

        // ------------------------------------------------------------------ dressing (PKG-B5.1 / REQ-FILL-001)

        /// <summary>
        /// Port of <c>_apply_dressing_visuals</c> + <c>_place_dressing_props</c>: the DressingVisuals children in
        /// Godot order. Requires <see cref="LootContainerSpecs"/> (occupied cells).
        /// </summary>
        public List<DressingItem> BuildDressingPlan()
        {
            var items = new List<DressingItem>();
            if (RoomVariantDescriptors.IsEmpty) return items;
            if (!(LayoutDoc.Get("rooms", new GdArray()) is GdArray rooms)) return items;
            GdDict occupied = DressingOccupiedCells(rooms);
            long seedValue = SeedFromLayoutDoc(LayoutDoc);
            long roomIndex = 0;
            foreach (object roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room))
                {
                    roomIndex += 1;
                    continue;
                }
                string rid = V.Str(room.Get("id", ""));
                if (rid.Length == 0 || !RoomVariantDescriptors.Has(rid))
                {
                    roomIndex += 1;
                    continue;
                }
                var desc = (GdDict)RoomVariantDescriptors[rid];
                string dressing = V.Str(desc.Get("dressing", ""));
                if (dressing.Length == 0)
                {
                    roomIndex += 1;
                    continue;
                }
                Vec3 center = RoomCenter(LayoutDoc, rid);
                if (center == Vec3.Inf) center = RoomCenterFromCells(room);
                if (center == Vec3.Inf)
                {
                    roomIndex += 1;
                    continue;
                }

                var light = new DressingItem
                {
                    Kind = DressingKind.Light,
                    Name = "DressingLight_" + rid,
                    RoomId = rid,
                    Dressing = dressing,
                    Position = center + new Vec3(0f, 2f, 0f),
                    LightEnergy = (float)V.F64(desc.Get("light_energy", 0.5)),
                    OmniRange = 6.0,
                    LightColor = new[] { 1f, 1f, 1f, 1f },
                    PropDensity = V.F64(desc.Get("prop_density", 1.0)),
                    FogDensity = V.F64(desc.Get("fog_density", 0.0)),
                };
                if (desc.Get("light_color", GdArray.Of(1.0, 1.0, 1.0, 1.0)) is GdArray lc && lc.Count >= 3)
                    light.LightColor = new[] { (float)V.F64(lc[0]), (float)V.F64(lc[1]), (float)V.F64(lc[2]), 1f };
                object tint = desc.Get("tint", new GdArray());
                if (tint is GdArray tintArray) light.Tint = tintArray.ShallowCopy();
                items.Add(light);

                double fogDensity = V.F64(desc.Get("fog_density", 0.0));
                if (fogDensity > 0.001)
                {
                    float radius = (float)(1.5 + fogDensity * 20.0);
                    var fog = new DressingItem
                    {
                        Kind = DressingKind.Fog,
                        Name = "DressingFog_" + rid,
                        RoomId = rid,
                        Dressing = dressing,
                        FogDensity = fogDensity,
                        SphereRadius = radius,
                        SphereHeight = (float)(radius * 2.0),
                        Position = center + new Vec3(0f, 1.5f, 0f),
                    };
                    if (tint is GdArray t && t.Count >= 3)
                        fog.Albedo = new[] { (float)V.F64(t[0]), (float)V.F64(t[1]), (float)V.F64(t[2]), (float)GdMath.Clampf(fogDensity * 4.0, 0.05, 0.35) };
                    else
                        fog.Albedo = new[] { 0.5f, 0.5f, 0.5f, 0.12f };
                    items.Add(fog);
                }
                PlaceDressingProps(items, room, rid, dressing, V.F64(desc.Get("prop_density", 1.0)), occupied, seedValue, roomIndex);
                roomIndex += 1;
            }
            return items;
        }

        /// <summary>Port of <c>_seed_from_layout_doc</c>.</summary>
        public static long SeedFromLayoutDoc(GdDict layout)
        {
            if (layout.Has("seed_value")) return V.I64(layout.Get("seed_value", 0L));
            string pid = V.Str(layout.Get("program_id", ""));
            int idx = GdString.RFind(pid, "seed-");
            if (idx < 0) return 0;
            string tail = pid.Substring(Math.Min(pid.Length, idx + 5));
            string digits = "";
            for (int i = 0; i < tail.Length; i++)
            {
                string ch = tail.Substring(i, 1);
                if (GdString.IsValidInt(ch)) digits += ch;
                else break;
            }
            return GdString.IsValidInt(digits) ? V.StringToInt(digits) : 0;
        }

        static string CellKey(string rid, object x, object z) =>
            rid + "|" + V.I64(x).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
            V.I64(z).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Port of <c>_dressing_occupied_cells</c>.</summary>
        public GdDict DressingOccupiedCells(GdArray rooms)
        {
            var occupied = new GdDict();
            object prototype = LayoutDoc.Get("prototype", new GdDict());
            string startRoom = prototype is GdDict pd ? V.Str(pd.Get("start_room", "")) : "";
            if (startRoom.Length == 0) startRoom = V.Str(GameplayDoc.Get("start_room", ""));
            foreach (object roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room)) continue;
                string rid = V.Str(room.Get("id", ""));
                if (room.Get("interior_zones", new GdDict()) is GdDict interior &&
                    interior.Get("reserved_cells", new GdArray()) is GdArray reserved)
                {
                    foreach (object cellVariant in reserved)
                    {
                        GdArray parsed = ParseSlotCell(cellVariant);
                        if (parsed.Count >= 2) occupied[CellKey(rid, parsed[0], parsed[1])] = true;
                    }
                }
                if (rid == startRoom)
                {
                    GdArray boarding = BoardingCellXz(room);
                    if (boarding.Count >= 2) occupied[CellKey(rid, boarding[0], boarding[1])] = true;
                }
            }
            foreach (object lootVariant in LootContainerSpecs)
            {
                if (!(lootVariant is GdDict loot)) continue;
                GdArray parsed = ParseSlotCell(loot.Get("approach_cell", new GdArray()));
                if (parsed.Count >= 2) occupied[CellKey(V.Str(loot.Get("room_id", "")), parsed[0], parsed[1])] = true;
            }
            if (GameplayDoc.Get("objectives", new GdArray()) is GdArray objectives)
            {
                foreach (object objVariant in objectives)
                {
                    if (!(objVariant is GdDict obj)) continue;
                    OccupyApproach(occupied, V.Str(obj.Get("room_id", "")), obj.Get("approach_cell", new GdArray()));
                    if (obj.Get("steps", new GdArray()) is GdArray steps)
                    {
                        foreach (object stepVariant in steps)
                        {
                            if (!(stepVariant is GdDict step)) continue;
                            OccupyApproach(occupied, V.Str(step.Get("room_id", obj.Get("room_id", ""))), step.Get("approach_cell", new GdArray()));
                        }
                    }
                }
            }
            ReserveComponentSlots(rooms, occupied);
            return occupied;
        }

        static void OccupyApproach(GdDict occupied, string roomId, object cellVariant)
        {
            GdArray parsed = ParseSlotCell(cellVariant);
            if (parsed.Count < 2 || roomId.Length == 0) return;
            occupied[CellKey(roomId, parsed[0], parsed[1])] = true;
        }

        static void ReserveComponentSlots(GdArray rooms, GdDict occupied)
        {
            // Components populate after dressing; hold the same 3 wall + 1 center cap those fills will take so clutter
            // cannot steal machine slots. Small rooms (<= MAX_WALL_FILLS free walls) keep one wall for dressing.
            foreach (object roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room)) continue;
                string rid = V.Str(room.Get("id", ""));
                if (rid.Length == 0) continue;
                if (!(room.Get("interior_zones", new GdDict()) is GdDict walls)) continue;
                long wallFree = UnoccupiedSlotCount(rid, walls, "wall_slots", occupied);
                long wallCap = MAX_WALL_FILLS;
                if (wallFree > 0 && wallFree <= MAX_WALL_FILLS) wallCap = Math.Max(0, wallFree - 1);
                ReserveSlotKind(rid, walls, "wall_slots", wallCap, occupied);
                ReserveSlotKind(rid, walls, "center_slots", MAX_CENTER_FILLS, occupied);
            }
        }

        static long UnoccupiedSlotCount(string rid, GdDict interior, string slotKey, GdDict occupied)
        {
            if (!(interior.Get(slotKey, new GdArray()) is GdArray slots)) return 0;
            long n = 0;
            foreach (object item in slots)
            {
                GdArray parsed = ParseSlotCell(item);
                if (parsed.Count < 2) continue;
                if (occupied.Has(CellKey(rid, parsed[0], parsed[1]))) continue;
                n += 1;
            }
            return n;
        }

        static void ReserveSlotKind(string rid, GdDict interior, string slotKey, long maxCount, GdDict occupied)
        {
            if (!(interior.Get(slotKey, new GdArray()) is GdArray slots)) return;
            long kept = 0;
            foreach (object item in slots)
            {
                if (kept >= maxCount) break;
                GdArray parsed = ParseSlotCell(item);
                if (parsed.Count < 2) continue;
                string key = CellKey(rid, parsed[0], parsed[1]);
                if (occupied.Has(key)) continue;
                occupied[key] = true;
                kept += 1;
            }
        }

        void PlaceDressingProps(List<DressingItem> items, GdDict room, string rid, string dressing, double propDensity,
            GdDict occupied, long seedValue, long roomIndex)
        {
            if (!(room.Get("interior_zones", new GdDict()) is GdDict interior)) return;
            if (!(interior.Get("wall_slots", new GdArray()) is GdArray wallSlots) || wallSlots.IsEmpty) return;
            var available = new List<(GdArray cell, long index)>();
            for (int i = 0; i < wallSlots.Count; i++)
            {
                GdArray parsed = ParseSlotCell(wallSlots[i]);
                if (parsed.Count < 2) continue;
                if (occupied.Has(CellKey(rid, parsed[0], parsed[1]))) continue;
                available.Add((parsed, i));
            }
            if (available.Count == 0) return;
            long count = GdMath.Clampi((long)GdMath.Round(available.Count * propDensity), 0, available.Count);
            if (propDensity > 0.001 && available.Count > 0) count = Math.Max(1, count);
            if (count <= 0) return;
            var rng = GodotRandom.FromSeed(unchecked(seedValue ^ roomIndex) & 0x7FFFFFFF);
            if (rng.Seed == 0) rng.Seed = 1;
            long deck = V.I64(room.Get("deck", 0L));
            for (int p = 0; p < count; p++)
            {
                var (cell, index) = available[p];
                Vec3 world = RoomCellWorld(LayoutDoc, room, GdArray.Of(V.I64(cell[0]), V.I64(cell[1]), deck));
                if (world == Vec3.Inf)
                    world = new Vec3(V.F64(cell[0]) * CELL_SIZE, (double)deck * 4.0 + FLOOR_Y_OFFSET, V.F64(cell[1]) * CELL_SIZE);
                string kind = DRESSING_PROP_KINDS[(int)rng.RandiRange(0, DRESSING_PROP_KINDS.Count - 1)];
                items.Add(new DressingItem
                {
                    Kind = DressingKind.Prop,
                    Name = "DressingProp_" + rid + "_" + p.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    RoomId = rid,
                    Dressing = dressing,
                    Position = world,
                    PropKind = kind,
                    SlotIndex = index,
                    SlotCell = cell.ShallowCopy(),
                });
                occupied[CellKey(rid, cell[0], cell[1])] = true;
            }
        }

        // ------------------------------------------------------------------ coherence markers + hazard zones

        /// <summary>Port of <c>_add_coherence_runtime_nodes</c>' data half, in the same order.</summary>
        public void BuildCoherenceMarkers()
        {
            BuildLandmarks();
            BuildBlockedRoutes();
            BuildVisibleVerticalTransitions();
            BuildBreachZones();
            BuildFireZones();
            BuildArcZones();
            BuildRadiationZones();
            BuildAuthoredAtmosphereVolumes();
        }

        void BuildLandmarks()
        {
            if (!(LayoutDoc.Get("landmarks", new GdArray()) is GdArray landmarks)) return;
            foreach (object landmarkVariant in landmarks)
            {
                if (!(landmarkVariant is GdDict landmark)) continue;
                Vec3 pos = Vec3FromArray(landmark.Get("position", new GdArray()), Vec3.Inf);
                if (pos == Vec3.Inf) continue;
                Landmarks.Add(new MarkerSpec
                {
                    Name = "Landmark_" + V.Str(landmark.Get("id", (long)Landmarks.Count)),
                    Position = pos,
                    Color = LANDMARK_COLOR,
                    Size = LANDMARK_SIZE,
                });
            }
        }

        (Vec3 from, Vec3 to) ResolveEndpointsWithRoomFallback(GdDict zone)
        {
            Vec3 fromPos = CellWorldFromLinkEndpoint(zone, "from_cell", "from_room", LayoutDoc);
            Vec3 toPos = CellWorldFromLinkEndpoint(zone, "to_cell", "to_room", LayoutDoc);
            if (fromPos == Vec3.Inf) fromPos = RoomCenterForBlockedLink(zone, "from_room", LayoutDoc);
            if (toPos == Vec3.Inf) toPos = RoomCenterForBlockedLink(zone, "to_room", LayoutDoc);
            return (fromPos, toPos);
        }

        void BuildBlockedRoutes()
        {
            if (!(LayoutDoc.Get("blocked_links", new GdArray()) is GdArray links)) return;
            foreach (object linkVariant in links)
            {
                if (!(linkVariant is GdDict link)) continue;
                var (fromPos, toPos) = ResolveEndpointsWithRoomFallback(link);
                if (fromPos == Vec3.Inf || toPos == Vec3.Inf) continue;
                Vec3 mid = (fromPos + toPos) * 0.5f;
                var marker = new MarkerSpec
                {
                    Name = "BlockedRoute_" + V.Str(link.Get("id", (long)BlockedRoutes.Count)),
                    Position = mid,
                    Color = BLOCKED_ROUTE_COLOR,
                    Size = BLOCKED_ROUTE_SIZE,
                };
                if ((toPos - mid).LengthSquared() > 0.0001) LookingAt(toPos - mid, Vec3.Up, out marker.BasisX, out marker.BasisY, out marker.BasisZ);
                BlockedRoutes.Add(marker);
            }
        }

        void BuildVisibleVerticalTransitions()
        {
            if (!(LayoutDoc.Get("vertical_connections", new GdArray()) is GdArray links)) return;
            foreach (object linkVariant in links)
            {
                if (!(linkVariant is GdDict link)) continue;
                Vec3 fromPos = CellWorldFromLinkEndpoint(link, "from_cell", "from_room", LayoutDoc);
                Vec3 toPos = CellWorldFromLinkEndpoint(link, "to_cell", "to_room", LayoutDoc);
                if (fromPos == Vec3.Inf || toPos == Vec3.Inf) continue;
                Vec3 mid = (fromPos + toPos) * 0.5f;
                var marker = new MarkerSpec
                {
                    Name = "VisibleVerticalTransition_" + V.Str(link.Get("id", (long)VerticalTransitions.Count)),
                    Position = mid,
                    Color = VERTICAL_TRANSITION_COLOR,
                    Size = VERTICAL_TRANSITION_SIZE,
                };
                if ((toPos - mid).LengthSquared() > 0.0001)
                {
                    // Transitions often run purely along world Y; a horizontal up reference keeps the look-at valid.
                    Vec3 up = Math.Abs((toPos - mid).Normalized().Dot(Vec3.Up)) > 0.999 ? Vec3.Forward : Vec3.Up;
                    LookingAt(toPos - mid, up, out marker.BasisX, out marker.BasisY, out marker.BasisZ);
                }
                VerticalTransitions.Add(marker);
            }
        }

        /// <summary>Port of <c>_add_vertical_links</c>: resolved links (Godot built NavigationLink3D nodes); returns the count.</summary>
        public int BuildVerticalLinks()
        {
            VerticalLinks.Clear();
            if (!(LayoutDoc.Get("vertical_connections", new GdArray()) is GdArray links)) return 0;
            int count = 0;
            foreach (object linkVariant in links)
            {
                if (!(linkVariant is GdDict link)) continue;
                Vec3 fromPos = CellWorldFromLinkEndpoint(link, "from_cell", "from_room", LayoutDoc);
                Vec3 toPos = CellWorldFromLinkEndpoint(link, "to_cell", "to_room", LayoutDoc);
                if (fromPos == Vec3.Inf || toPos == Vec3.Inf)
                {
                    Log.Warning("Skipping unresolved vertical link " + V.Str(link.Get("id", (long)count)));
                    continue;
                }
                VerticalLinks.Add(new VerticalLinkSpec { Name = "VerticalLink_" + V.Str(link.Get("id", (long)count)), Start = fromPos, End = toPos });
                count += 1;
            }
            return count;
        }

        void BuildBreachZones()
        {
            if (!(LayoutDoc.Get("breach_zones", new GdArray()) is GdArray zones)) return;
            foreach (object zoneVariant in zones)
            {
                if (!(zoneVariant is GdDict zone)) continue;
                var (fromPos, toPos) = ResolveEndpointsWithRoomFallback(zone);
                if (fromPos == Vec3.Inf || toPos == Vec3.Inf) continue;
                Vec3 midpoint = (fromPos + toPos) * 0.5f;
                BreachZoneMarkers.Add(midpoint);
                GdDict spec = NormalizeZoneSpec(zone);
                spec["position"] = midpoint;
                BreachZoneSpecs.Add(spec);
            }
        }

        void BuildFireZones() => BuildMidpointZones("fire_zones", FireZoneMarkers, FireZoneSpecs);

        // Arc placement is template-specific (hazard_type_3.md): no fallback zones are injected.
        void BuildArcZones() => BuildMidpointZones("arc_zones", ArcZoneMarkers, ArcZoneSpecs);

        void BuildMidpointZones(string key, List<Vec3> markers, GdArray specs)
        {
            if (!(LayoutDoc.Get(key, new GdArray()) is GdArray zones)) return;
            foreach (object zoneVariant in zones)
            {
                if (!(zoneVariant is GdDict zone)) continue;
                var (fromPos, toPos) = ResolveEndpointsWithRoomFallback(zone);
                if (fromPos == Vec3.Inf || toPos == Vec3.Inf) continue;
                markers.Add((fromPos + toPos) * 0.5f);
                specs.Add(NormalizeZoneSpec(zone));
            }
        }

        void BuildRadiationZones()
        {
            if (!(LayoutDoc.Get("radiation_zones", new GdArray()) is GdArray zones)) return;
            foreach (object zoneVariant in zones)
            {
                if (!(zoneVariant is GdDict zone)) continue;
                var (fromPos, toPos) = ResolveEndpointsWithRoomFallback(zone);
                if (fromPos == Vec3.Inf || toPos == Vec3.Inf) continue;
                Vec3 midpoint = (fromPos + toPos) * 0.5f;
                RadiationZoneMarkers.Add(midpoint);
                GdDict spec = NormalizeZoneSpec(zone);
                RadiationZoneSpecs.Add(spec);
                RadiationZoneSegments.Add(new GdDict { { "from", fromPos }, { "to", toPos } });
                double length = Math.Max(RADIATION_VOLUME_AXIAL_END_PADDING * 2.0,
                    fromPos.DistanceTo(toPos) + RADIATION_VOLUME_AXIAL_END_PADDING * 2.0);
                var volume = new TriggerVolumeSpec
                {
                    Name = "RadiationZone_" + V.Str(zone.Get("id", (long)(RadiationZoneMarkers.Count - 1))),
                    Kind = "radiation",
                    Position = midpoint,
                    Color = RADIATION_VOLUME_COLOR,
                    Size = new Vec3(length, 2.5, 2.5),
                    Spec = spec,
                };
                Vec3 segment = toPos - fromPos;
                if (segment.LengthSquared() > 0.000001)
                    ShortestArc(Vec3.Right, segment.Normalized(), out volume.BasisX, out volume.BasisY, out volume.BasisZ);
                RadiationVolumes.Add(volume);
            }
        }

        static bool RoomHasAtmosphere(GdDict room) =>
            room.Has("atmosphere_bp") || room.Has("oxygen_bp") || room.Has("depressurized") || room.Has("vented") ||
            room.Has("radiation_bp") || room.Has("temperature_c");

        void BuildAuthoredAtmosphereVolumes()
        {
            if (!(LayoutDoc.Get("rooms", new GdArray()) is GdArray rooms) || !(LayoutDoc.Get("structural_plan", new GdDict()) is GdDict plan)) return;
            if (!(plan.Get("floor_placements", new GdArray()) is GdArray floors)) return;
            foreach (object floorVariant in floors)
            {
                if (!(floorVariant is GdDict floor)) continue;
                string roomId = V.Str(floor.Get("room_id", ""));
                GdDict room = FindRoom(rooms, roomId);
                if (room.IsEmpty || !RoomHasAtmosphere(room)) continue;
                GdArray positionArray = ReadPlacementPosition(floor);
                if (positionArray.Count < 3) continue;
                var position = new Vec3(V.F64(positionArray[0]), V.F64(positionArray[1]) + FLOOR_Y_OFFSET, V.F64(positionArray[2]));
                var spec = new GdDict
                {
                    { "room_id", roomId },
                    { "position", position },
                    { "depressurized", V.Bool(room.Get("depressurized", false)) },
                    { "vented", V.Bool(room.Get("vented", false)) },
                    { "radiation_bp", V.I64(room.Get("radiation_bp", 0L)) },
                };
                if (room.Has("atmosphere_bp") || room.Has("oxygen_bp"))
                    spec["oxygen_bp"] = V.I64(room.Get("atmosphere_bp", room.Get("oxygen_bp", 10000L)));
                if (room.Has("temperature_c")) spec["temperature_c"] = V.F64(room["temperature_c"]);
                AuthoredAtmosphereSpecs.Add(spec);
                AtmosphereVolumes.Add(new TriggerVolumeSpec
                {
                    Name = "AuthoredAtmosphere_" + roomId + "_" + (AuthoredAtmosphereSpecs.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Kind = "atmosphere",
                    Position = position,
                    Color = ATMOSPHERE_VOLUME_COLOR,
                    Size = new Vec3(CELL_SIZE, 2.5, CELL_SIZE),
                    Spec = spec.DeepCopy(),
                });
            }
        }

        /// <summary>
        /// Port of <c>_normalize_zone_spec</c> (REQ-013 / ADR-0005): deep copy, and copy a non-empty string <c>id</c>
        /// into <c>zone_id</c> when the spec has none.
        /// </summary>
        public static GdDict NormalizeZoneSpec(GdDict zone)
        {
            GdDict output = zone.DeepCopy();
            if (!output.Has("zone_id") && output.Has("id") && output["id"] is string zoneId && zoneId.Length > 0)
                output["zone_id"] = zoneId;
            return output;
        }

        // ------------------------------------------------------------------ portals

        /// <summary>Port of <c>_runtime_portal_sources</c>: every structural-plan portal edge, merged with its authored portal.</summary>
        public static GdArray RuntimePortalSources(GdDict sourceLayout)
        {
            var authoredByEdge = new GdDict();
            object authoredVariant = sourceLayout.Get("portals", sourceLayout.Get("connections", new GdArray()));
            if (authoredVariant is GdArray authoredList)
            {
                foreach (object raw in authoredList)
                {
                    if (!(raw is GdDict authored)) continue;
                    string authoredKey = V.Str(authored.Get("edge_key", ""));
                    if (authoredKey.Length > 0) authoredByEdge[authoredKey] = authored.DeepCopy();
                }
            }
            if (!(sourceLayout.Get("structural_plan", new GdDict()) is GdDict plan) || !(plan.Get("edges", new GdDict()) is GdDict edges))
                return authoredVariant is GdArray fallback ? fallback.DeepCopy() : new GdArray();
            var sources = new GdArray();
            foreach (object edgeRaw in edges.Values)
            {
                if (!(edgeRaw is GdDict edge)) continue;
                if (!V.Bool(edge.Get("portal", false))) continue;
                string edgeKey = V.Str(edge.Get("edge_key", edge.Get("key", "")));
                GdDict source = (authoredByEdge.Get(edgeKey, new GdDict()) as GdDict ?? new GdDict()).DeepCopy();
                source["id"] = V.Str(source.Get("id", "edge:" + edgeKey));
                source["edge_key"] = edgeKey;
                source["state"] = V.Str(edge.Get("kind", source.Get("state", "DOOR")));
                source["kind"] = source["state"];
                source["exterior"] = V.Bool(edge.Get("exterior", source.Get("exterior", false)));
                source["yaw_degrees"] = V.F64(edge.Get("yaw_degrees", source.Get("yaw_degrees", 0.0)));
                if (edge.Get("source_cells", new GdArray()) is GdArray sourceCells && sourceCells.Count >= 2)
                {
                    source["from_cell"] = sourceCells[0];
                    source["to_cell"] = sourceCells[1];
                }
                if (edge.Get("room_ids", new GdArray()) is GdArray roomIds && roomIds.Count >= 2)
                {
                    source["from_room"] = V.Str(roomIds[0]);
                    source["to_room"] = V.Str(roomIds[1]);
                }
                sources.Add(source);
            }
            return sources;
        }

        /// <summary>
        /// Port of the data half of <c>_add_authored_portal_runtime_nodes</c>: one plan per resolvable portal, and
        /// <see cref="AuthoredPortalSpecs"/> filled in the same order.
        /// </summary>
        public List<PortalPlan> BuildPortalPlans()
        {
            var plans = new List<PortalPlan>();
            GdArray portalSources = RuntimePortalSources(LayoutDoc);
            for (int index = 0; index < portalSources.Count; index++)
            {
                var source = portalSources[index] as GdDict ?? new GdDict();
                var (fromPos, toPos) = ResolveEndpointsWithRoomFallback(source);
                if (fromPos == Vec3.Inf && toPos == Vec3.Inf) continue;
                Vec3 midpoint = fromPos == Vec3.Inf ? toPos : toPos == Vec3.Inf ? fromPos : (fromPos + toPos) * 0.5f;
                GdDict spec = source.DeepCopy();
                spec["runtime_position"] = midpoint;
                spec["from_position"] = fromPos;
                spec["to_position"] = toPos;
                plans.Add(new PortalPlan { Index = index, Spec = spec, Position = midpoint });
                AuthoredPortalSpecs.Add(spec);
            }
            return plans;
        }

        // ------------------------------------------------------------------ placed props

        /// <summary>
        /// Port of the data half of <c>_add_authored_placed_props</c>. <paramref name="propCatalog"/> is the
        /// <c>props</c> dictionary of data/kits/gameplay_prop_v0.json; <paramref name="visualCatalog"/> is a loaded
        /// PropVisualBindingCatalog (null when it failed to load).
        /// </summary>
        public List<PlacedPropEntry> BuildPlacedPropPlan(GdDict propCatalog, PropVisualBindingCatalog visualCatalog)
        {
            var entries = new List<PlacedPropEntry>();
            if (!(GameplayDoc.Get("placed_props", new GdArray()) is GdArray raw)) return entries;
            propCatalog = propCatalog ?? new GdDict();
            foreach (object rawProp in raw)
            {
                if (!(rawProp is GdDict authored)) continue;
                string propId = GdString.StripEdges(V.Str(authored.Get("visual_id", "")));
                if (propId.Length == 0)
                    propId = GdString.StripEdges(V.Str(authored.Get("prop_id", authored.Get("asset_id", authored.Get("proto", "")))));
                if (propId.Length == 0) continue;
                string roomId = GdString.StripEdges(V.Str(authored.Get("room_id", "")));
                object cellVariant = authored.Get("cell", authored.Get("approach_cell", new GdArray()));
                if (roomId.Length == 0 || !(cellVariant is GdArray cell)) continue;
                if (cell.Count < 2) continue;
                GdDict room = FindRoom(LayoutDoc.Get("rooms", new GdArray()) as GdArray, roomId);
                if (room.IsEmpty) continue;
                Vec3 position = RoomCellWorld(LayoutDoc, room, cell);
                if (position == Vec3.Inf) continue;
                GdDict dressingBinding = visualCatalog != null ? visualCatalog.GetDressingBinding(propId) : new GdDict();
                if (!propCatalog.Has(propId) && dressingBinding.IsEmpty)
                {
                    entries.Add(new PlacedPropEntry { Error = $"unknown authored placed prop visual_id '{propId}'" });
                    continue;
                }
                long quarterTurns = 0;
                if (authored.Has("quarter_turn")) quarterTurns = V.I64(authored.Get("quarter_turn", 0L));
                else if (authored.Has("rotation")) quarterTurns = V.I64(authored.Get("rotation", 0L));
                else if (authored.Has("yaw_degrees")) quarterTurns = GdMath.RoundI(V.F64(authored.Get("yaw_degrees", 0.0)) / 90.0);
                quarterTurns = GdMath.Posmod(quarterTurns, 4);
                GdDict spec = authored.DeepCopy();
                spec["visual_id"] = propId;
                spec["room_id"] = roomId;
                spec["cell"] = cell.DeepCopy();
                spec["position"] = position;
                spec["quarter_turn"] = quarterTurns;
                entries.Add(new PlacedPropEntry
                {
                    PropId = propId,
                    Spec = spec,
                    DressingBinding = dressingBinding,
                    QuarterTurns = quarterTurns,
                    Position = position,
                    AuthoredId = authored.Has("id") ? authored["id"] : null,
                });
            }
            return entries;
        }

        // ------------------------------------------------------------------ runtime queries

        public GdDict GetRadiationZoneAt(Vec3 localPosition, double radius = RADIATION_VOLUME_HALF_WIDTH)
        {
            var best = new GdDict();
            double bestDistance = double.PositiveInfinity;
            double halfWidth = Math.Max(0.0, radius);
            for (int index = 0; index < RadiationZoneMarkers.Count; index++)
            {
                bool contains = false;
                if (index < RadiationZoneSegments.Count && RadiationZoneSegments[index] is GdDict segment)
                {
                    object fromPos = segment.Get("from", Vec3.Inf);
                    object toPos = segment.Get("to", Vec3.Inf);
                    if (fromPos is Vec3 f && toPos is Vec3 t) contains = PointInRadiationBox(localPosition, f, t, halfWidth);
                }
                else
                {
                    contains = localPosition.DistanceTo(RadiationZoneMarkers[index]) <= halfWidth;
                }
                double distance = localPosition.DistanceTo(RadiationZoneMarkers[index]);
                if (contains && distance <= bestDistance)
                {
                    bestDistance = distance;
                    if (index < RadiationZoneSpecs.Count && RadiationZoneSpecs[index] is GdDict spec) best = spec.DeepCopy();
                }
            }
            return best;
        }

        static bool PointInRadiationBox(Vec3 point, Vec3 fromPos, Vec3 toPos, double halfWidth)
        {
            Vec3 segment = toPos - fromPos;
            Vec3 midpoint = (fromPos + toPos) * 0.5f;
            Vec3 bx = Vec3.Right, by = Vec3.Up, bz = Vec3.Back;
            if (segment.LengthSquared() > 0.000001) ShortestArc(Vec3.Right, segment.Normalized(), out bx, out by, out bz);
            // basis.inverse() * v for an orthonormal basis is the transpose: the dot with each column.
            Vec3 d = point - midpoint;
            float lx = d.Dot(bx), ly = d.Dot(by), lz = d.Dot(bz);
            double halfLength = Math.Max(RADIATION_VOLUME_AXIAL_END_PADDING, segment.Length() * 0.5 + RADIATION_VOLUME_AXIAL_END_PADDING);
            return Math.Abs(lx) <= halfLength && Math.Abs(ly) <= halfWidth && Math.Abs(lz) <= halfWidth;
        }

        public GdDict GetAuthoredAtmosphereAt(Vec3 localPosition)
        {
            var best = new GdDict();
            double bestDistance = double.PositiveInfinity;
            foreach (object specVariant in AuthoredAtmosphereSpecs)
            {
                if (!(specVariant is GdDict spec)) continue;
                if (!(spec.Get("position", Vec3.Inf) is Vec3 placement)) continue;
                Vec3 delta = localPosition - placement;
                bool withinBox = Math.Abs(delta.X) <= ATMOSPHERE_VOLUME_HALF_WIDTH
                                 && delta.Y >= 0.0 && delta.Y <= ATMOSPHERE_VOLUME_HEIGHT
                                 && Math.Abs(delta.Z) <= ATMOSPHERE_VOLUME_HALF_WIDTH;
                double distance = delta.LengthSquared();
                if (withinBox && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = spec.DeepCopy();
                }
            }
            return best;
        }

        public double GetAuthoredAtmosphereDrainMultiplierAt(Vec3 localPosition)
        {
            GdDict atmosphere = GetAuthoredAtmosphereAt(localPosition);
            if (atmosphere.IsEmpty) return 1.0;
            // A vented compartment is depressurized even when older documents omit oxygen_bp and depressurized.
            if (V.Bool(atmosphere.Get("depressurized", false)) || V.Bool(atmosphere.Get("vented", false))) return 1.0;
            if (!atmosphere.Has("oxygen_bp")) return 1.0;
            double oxygenBp = GdMath.Clampf(V.F64(atmosphere.Get("oxygen_bp", 10000L)), 0.0, 10000.0);
            return GdMath.Clampf(1.0 - oxygenBp / 10000.0, 0.0, 1.0);
        }

        // ------------------------------------------------------------------ math helpers (Godot float32)

        /// <summary>Port of <c>_vec3_from_array</c>: only int/float components.</summary>
        public static Vec3 Vec3FromArray(object value, Vec3 fallback)
        {
            if (!(value is GdArray array) || array.Count < 3) return fallback;
            for (int i = 0; i < 3; i++)
                if (!(array[i] is long) && !(array[i] is double)) return fallback;
            return new Vec3(V.F64(array[0]), V.F64(array[1]), V.F64(array[2]));
        }

        /// <summary>Godot <c>Basis.looking_at(target, up)</c> (model front −Z); columns are the basis axes.</summary>
        public static void LookingAt(Vec3 target, Vec3 up, out Vec3 x, out Vec3 y, out Vec3 z)
        {
            Vec3 vz = -target.Normalized();
            Vec3 vx = up.Cross(vz);
            if (vx.IsZeroApprox())
            {
                // Godot errors out and returns an identity basis.
                x = Vec3.Right;
                y = Vec3.Up;
                z = Vec3.Back;
                return;
            }
            vx = vx.Normalized();
            Vec3 vy = vz.Cross(vx);
            x = vx;
            y = vy;
            z = vz;
        }

        /// <summary>Godot <c>Basis(Quaternion(arc_from, arc_to))</c>: the shortest-arc rotation, as basis columns.</summary>
        public static void ShortestArc(Vec3 arcFrom, Vec3 arcTo, out Vec3 x, out Vec3 y, out Vec3 z)
        {
            Vec3 n0 = arcFrom.Normalized();
            Vec3 n1 = arcTo.Normalized();
            float d = n0.Dot(n1);
            float qx, qy, qz, qw;
            if (Math.Abs(d) > 1.0f - (float)GdMath.CmpEpsilon)
            {
                if (d >= 0f)
                {
                    x = Vec3.Right;
                    y = Vec3.Up;
                    z = Vec3.Back;
                    return;
                }
                // Antiparallel: 180° about any perpendicular axis (Vector3::get_any_perpendicular).
                Vec3 axis = Math.Abs(n0.X) <= Math.Abs(n0.Y) && Math.Abs(n0.X) <= Math.Abs(n0.Z) ? new Vec3(0f, -n0.Z, n0.Y)
                    : Math.Abs(n0.Y) <= Math.Abs(n0.Z) ? new Vec3(n0.Z, 0f, -n0.X) : new Vec3(-n0.Y, n0.X, 0f);
                axis = axis.Normalized();
                qx = axis.X;
                qy = axis.Y;
                qz = axis.Z;
                qw = 0f;
            }
            else
            {
                Vec3 c = n0.Cross(n1);
                float s = (float)Math.Sqrt((1.0f + d) * 2.0f);
                float rs = 1.0f / s;
                qx = c.X * rs;
                qy = c.Y * rs;
                qz = c.Z * rs;
                qw = s * 0.5f;
            }
            float lengthSq = qx * qx + qy * qy + qz * qz + qw * qw;
            float sc = 2.0f / lengthSq;
            float xs = qx * sc, ys = qy * sc, zs = qz * sc;
            float wx = qw * xs, wy = qw * ys, wz = qw * zs;
            float xx = qx * xs, xy = qx * ys, xz = qx * zs;
            float yy = qy * ys, yz = qy * zs, zz = qz * zs;
            x = new Vec3(1.0f - (yy + zz), xy + wz, xz - wy);
            y = new Vec3(xy - wz, 1.0f - (xx + zz), yz + wx);
            z = new Vec3(xz + wy, yz - wx, 1.0f - (xx + yy));
        }

        static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return true;
            return false;
        }
    }
}
