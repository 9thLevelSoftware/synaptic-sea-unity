using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// The loaded generated ship (port of the <c>GeneratedShipLoader</c> node and its getters), filled by
    /// <see cref="ShipSceneBuilder"/>. Children: <c>StructuralRoot</c> (structural wrappers, coherence markers, hazard
    /// and atmosphere volumes, placed props, authored portals, <c>DressingVisuals</c>), <c>ObjectiveRoot</c>
    /// (objective volumes) and the slice atmosphere lights.
    ///
    /// <para><b>Coordinate convention.</b> Every <see cref="Vec3"/> returned here (and every <c>position</c> inside the
    /// spec dictionaries) is in <b>Godot's frame, local to this ship root</b> — exactly the values the Godot loader
    /// returned, and what Core models, saves and the nav graph consume. Methods ending in <c>World</c> return Unity
    /// world-space <see cref="Vector3"/>s for scene code (<c>transform.TransformPoint(Frame.ToUnity(v))</c>), so they
    /// follow the ship when it is moved (docking). Unresolved Godot positions are <see cref="Vec3.Inf"/>; their
    /// world variants return <c>null</c>.</para>
    ///
    /// <para>Specs, links and descriptors are returned as deep copies (Godot <c>duplicate(true)</c>); node lists are
    /// shallow copies.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShipView : MonoBehaviour
    {
        /// <summary>The pure half of the loader (docs, specs, dressing plan). Null until a load starts.</summary>
        public GeneratedShipLayout Layout { get; internal set; }

        /// <summary>The last <c>ship_loaded</c> summary (Godot keys), or null.</summary>
        public GdDict Summary { get; internal set; }

        /// <summary><see cref="AtmosphereApplier.Apply"/> result when the layout named a biome, else null.</summary>
        public GdDict AtmosphereSummary { get; internal set; }

        public Transform StructuralRoot { get; internal set; }
        public Transform ObjectiveRoot { get; internal set; }
        public Transform DressingRoot { get; internal set; }

        /// <summary>Structural wrappers by Godot <c>module_key</c> (<c>edge/…</c>, <c>floor/…</c>, <c>ceiling/…</c>).</summary>
        public IReadOnlyDictionary<string, StructuralModule> ModulesByKey => _modulesByKey;
        public IReadOnlyList<StructuralModule> Modules => _modules;

        internal readonly Dictionary<string, StructuralModule> _modulesByKey = new Dictionary<string, StructuralModule>();
        internal readonly List<StructuralModule> _modules = new List<StructuralModule>();
        internal readonly List<RuntimeMarker> _landmarkNodes = new List<RuntimeMarker>();
        internal readonly List<RuntimeMarker> _blockedRouteNodes = new List<RuntimeMarker>();
        internal readonly List<RuntimeMarker> _verticalTransitionNodes = new List<RuntimeMarker>();
        internal readonly List<ZoneVolume> _radiationVolumes = new List<ZoneVolume>();
        internal readonly List<ZoneVolume> _atmosphereVolumes = new List<ZoneVolume>();
        internal readonly List<PlacedProp> _placedPropNodes = new List<PlacedProp>();
        internal readonly List<string> _placedPropErrors = new List<string>();
        internal GdArray _placedPropSpecs = new GdArray();
        internal readonly List<AuthoredPortalRuntime> _authoredPortalNodes = new List<AuthoredPortalRuntime>();
        internal readonly List<GameplayObjectiveVolume> _objectiveVolumes = new List<GameplayObjectiveVolume>();
        internal readonly List<DressingVisual> _dressingNodes = new List<DressingVisual>();

        GeneratedShipLayout L => Layout ?? (Layout = new GeneratedShipLayout());

        // ------------------------------------------------------------------ Godot getters

        public bool HasLoadedShip() =>
            StructuralRoot != null && Layout != null && !Layout.ObjectiveSpecs.IsEmpty && Layout.StartPosition != Vec3.Inf && Layout.GoalPosition != Vec3.Inf;

        public GdDict GetLayoutCopy() => L.LayoutDoc.DeepCopy();

        /// <summary><c>get_start_transform</c>: identity basis at the start room center (origin when unloaded). Godot frame.</summary>
        public Xform3 GetStartTransform() => new Xform3(Basis3.Identity, L.StartPosition == Vec3.Inf ? Vec3.Zero : L.StartPosition);

        /// <summary>The spawn pose in Unity world space (ship rotation, start room center).</summary>
        public Pose GetStartPoseWorld() => new Pose(ToWorld(GetStartTransform().Origin), transform.rotation);

        public Vec3 GetGoalPosition() => L.GoalPosition;
        public Vector3? GetGoalPositionWorld() => ToWorldOrNull(L.GoalPosition);

        public GdArray GetObjectiveSpecsCopy() => L.ObjectiveSpecs.DeepCopy();
        public GdArray GetLootContainerSpecsCopy() => L.LootContainerSpecs.DeepCopy();
        public GdArray GetPlacedPropSpecsCopy() => _placedPropSpecs.DeepCopy();
        public List<PlacedProp> GetPlacedPropNodes() => new List<PlacedProp>(_placedPropNodes);
        public List<string> GetPlacedPropErrors() => new List<string>(_placedPropErrors);
        public GdArray GetAuthoredPortalSpecsCopy() => L.AuthoredPortalSpecs.DeepCopy();
        public List<AuthoredPortalRuntime> GetAuthoredPortalNodes() => new List<AuthoredPortalRuntime>(_authoredPortalNodes);
        public List<GameplayObjectiveVolume> GetObjectiveVolumes() => new List<GameplayObjectiveVolume>(_objectiveVolumes);

        /// <summary>
        /// <c>count_collision_shapes</c>: colliders inside structural wrappers only (portal blockers, hazard volumes and
        /// marker shapes are runtime overlays and are not counted). Disabled colliders count, like Godot.
        /// </summary>
        public int CountCollisionShapes()
        {
            if (StructuralRoot == null) return 0;
            int count = 0;
            foreach (var module in _modules)
                if (module != null) count += module.GetComponentsInChildren<Collider>(true).Length;
            return count;
        }

        public Vec3 GetRoomCenter(string roomId) => L.GetRoomCenter(roomId);
        public Vector3? GetRoomCenterWorld(string roomId) => ToWorldOrNull(L.GetRoomCenter(roomId));
        public string GetRoomRole(string roomId) => L.GetRoomRole(roomId);
        public long GetRoomDeck(string roomId) => L.GetRoomDeck(roomId);
        public List<string> GetCriticalPath() => L.GetCriticalPath();
        public GdArray GetRoomLinks() => L.GetRoomLinks();
        public GdArray GetEncounterMarkers() => L.GetEncounterMarkers();
        public GdArray GetBlockedLinks() => L.GetBlockedLinks();
        public GdArray GetLandmarkSpecs() => L.GetLandmarkSpecs();
        public List<RuntimeMarker> GetLandmarkNodes() => new List<RuntimeMarker>(_landmarkNodes);
        public List<RuntimeMarker> GetBlockedRouteNodes() => new List<RuntimeMarker>(_blockedRouteNodes);
        public List<RuntimeMarker> GetVisibleVerticalTransitionNodes() => new List<RuntimeMarker>(_verticalTransitionNodes);

        public List<Vec3> GetBreachZoneMarkers() => new List<Vec3>(L.BreachZoneMarkers);
        public List<Vector3> GetBreachZoneMarkersWorld() => ToWorld(L.BreachZoneMarkers);
        public GdArray GetBreachZoneSpecs() => L.BreachZoneSpecs.DeepCopy();
        public List<Vec3> GetFireZoneMarkers() => new List<Vec3>(L.FireZoneMarkers);
        public List<Vector3> GetFireZoneMarkersWorld() => ToWorld(L.FireZoneMarkers);
        public GdArray GetFireZoneSpecs() => L.FireZoneSpecs.DeepCopy();
        public List<Vec3> GetArcZoneMarkers() => new List<Vec3>(L.ArcZoneMarkers);
        public List<Vector3> GetArcZoneMarkersWorld() => ToWorld(L.ArcZoneMarkers);
        public GdArray GetArcZoneSpecs() => L.ArcZoneSpecs.DeepCopy();
        public List<Vec3> GetRadiationZoneMarkers() => new List<Vec3>(L.RadiationZoneMarkers);
        public List<Vector3> GetRadiationZoneMarkersWorld() => ToWorld(L.RadiationZoneMarkers);
        public GdArray GetRadiationZoneSpecs() => L.RadiationZoneSpecs.DeepCopy();
        /// <summary>[{from: Vec3, to: Vec3}] segments (Godot frame).</summary>
        public GdArray GetRadiationZoneSegments() => L.RadiationZoneSegments.DeepCopy();
        public List<ZoneVolume> GetRadiationZoneVolumes() => new List<ZoneVolume>(_radiationVolumes);

        /// <summary><c>get_radiation_zone_at(local_position, radius)</c>; the position is Godot-frame, ship-local.</summary>
        public GdDict GetRadiationZoneAt(Vec3 localPosition, double radius = GeneratedShipLayout.RADIATION_VOLUME_HALF_WIDTH) =>
            L.GetRadiationZoneAt(localPosition, radius);

        public GdDict GetRadiationZoneAtWorld(Vector3 worldPosition, double radius = GeneratedShipLayout.RADIATION_VOLUME_HALF_WIDTH) =>
            L.GetRadiationZoneAt(ToLocalGodot(worldPosition), radius);

        public GdArray GetAuthoredAtmosphereSpecs() => L.AuthoredAtmosphereSpecs.DeepCopy();
        public List<ZoneVolume> GetAuthoredAtmosphereVolumes() => new List<ZoneVolume>(_atmosphereVolumes);
        public GdDict GetAuthoredAtmosphereAt(Vec3 localPosition) => L.GetAuthoredAtmosphereAt(localPosition);
        public GdDict GetAuthoredAtmosphereAtWorld(Vector3 worldPosition) => L.GetAuthoredAtmosphereAt(ToLocalGodot(worldPosition));
        public double GetAuthoredAtmosphereDrainMultiplierAt(Vec3 localPosition) => L.GetAuthoredAtmosphereDrainMultiplierAt(localPosition);
        public double GetAuthoredAtmosphereDrainMultiplierAtWorld(Vector3 worldPosition) =>
            L.GetAuthoredAtmosphereDrainMultiplierAt(ToLocalGodot(worldPosition));

        public GdDict GetRoomVariantDescriptors() => L.RoomVariantDescriptors.DeepCopy();
        public List<DressingVisual> GetDressingNodes() => new List<DressingVisual>(_dressingNodes);

        /// <summary>Resolved vertical connections (Godot NavigationLink3D start/end), for the nav graph.</summary>
        public IReadOnlyList<GeneratedShipLayout.VerticalLinkSpec> GetVerticalLinks() => L.VerticalLinks;

        // ------------------------------------------------------------------ frame helpers

        /// <summary>Godot-frame ship-local position → Unity world position.</summary>
        public Vector3 ToWorld(Vec3 godotLocal) => transform.TransformPoint(Frame.ToUnity(godotLocal));

        /// <summary>Unity world position → Godot-frame ship-local position.</summary>
        public Vec3 ToLocalGodot(Vector3 world) => Frame.ToGodot(transform.InverseTransformPoint(world));

        Vector3? ToWorldOrNull(Vec3 godotLocal) => godotLocal == Vec3.Inf ? (Vector3?)null : ToWorld(godotLocal);

        List<Vector3> ToWorld(List<Vec3> points)
        {
            var output = new List<Vector3>(points.Count);
            foreach (var p in points) output.Add(ToWorld(p));
            return output;
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Port of <c>clear_loaded_ship</c>: destroys every child and resets all state.</summary>
        public void Clear()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                child.transform.SetParent(null, false);
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
            Layout = null;
            Summary = null;
            AtmosphereSummary = null;
            StructuralRoot = null;
            ObjectiveRoot = null;
            DressingRoot = null;
            _modulesByKey.Clear();
            _modules.Clear();
            _landmarkNodes.Clear();
            _blockedRouteNodes.Clear();
            _verticalTransitionNodes.Clear();
            _radiationVolumes.Clear();
            _atmosphereVolumes.Clear();
            _placedPropNodes.Clear();
            _placedPropErrors.Clear();
            _placedPropSpecs = new GdArray();
            _authoredPortalNodes.Clear();
            _objectiveVolumes.Clear();
            _dressingNodes.Clear();
        }
    }
}
