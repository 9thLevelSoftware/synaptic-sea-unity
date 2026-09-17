// Scene boundary for scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 (the GeneratedShipLoader queries).
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// A structural wrapper node stamped with <c>module_key</c>/<c>module_kind</c> (or <c>structural_placement_id</c>) meta,
    /// as walked by <c>_scan_work_targets_in_tree</c> / <c>_find_structural_module_node</c>.
    /// </summary>
    public interface IStructuralModuleNode : IModuleSceneView
    {
        /// <summary><c>get_meta("module_kind", "")</c>.</summary>
        string ModuleKind { get; }

        /// <summary><c>get_meta("structural_placement_id", "")</c>.</summary>
        string StructuralPlacementId { get; }

        /// <summary>The node name (legacy goldens resolve <c>room_id/placement</c> keys to a <c>room_id_placement</c> node).</summary>
        string NodeName { get; }

        /// <summary><c>(node as Node3D).global_position</c> (Godot world frame).</summary>
        Vec3 GlobalPosition { get; }

        /// <summary><c>node.has_meta("module_key")</c>.</summary>
        bool HasModuleKeyMeta { get; }
    }

    /// <summary>
    /// RUNTIME: an authored door/hatch/breach portal node (<c>AuthoredPortalRuntime</c>) on the active loader, as the
    /// coordinator's <c>_try_authored_portal_interact</c> / <c>_restore_authored_portal_states</c> used it.
    /// </summary>
    public interface IAuthoredPortal
    {
        /// <summary><c>is_instance_valid(portal)</c>.</summary>
        bool IsValid { get; }

        string PortalId { get; }

        /// <summary><c>portal.portal_kind</c> ("LOCKED", "DOOR", ...).</summary>
        string PortalKind { get; }

        bool IsExterior { get; }

        /// <summary><c>portal.required_flag()</c>.</summary>
        string RequiredFlag();

        Vec3 GlobalPosition { get; }

        /// <summary>
        /// <c>portal.try_interact(flags, player_body)</c>: the result dictionary
        /// <c>{ok, reason, open, unlocked_now, exterior}</c>. The player is passed by world position.
        /// </summary>
        GdDict TryInteract(GdDict flags, Vec3 playerPosition);

        /// <summary><c>portal.restore_persistent_state(unlocked, open)</c>.</summary>
        void RestorePersistentState(bool unlocked, bool open);
    }

    /// <summary>
    /// RUNTIME: the <c>GeneratedShipLoader</c> node (the home loader) or a generated derelict root (also a loader), reduced
    /// to what the coordinator queried. It is the ship's scene root, so it extends <see cref="IShipSceneRoot"/>, and it
    /// exposes its room nodes through <see cref="IShipInteriorView"/> and its structural wrappers through
    /// <see cref="IShipModuleScene"/>.
    /// All positions are LOCAL to the loader node (Godot frame) unless the member says "world"; the loader node's
    /// <see cref="IShipSceneRoot.GlobalTransform"/> lifts them. The home loader sits at the coordinator origin.
    /// The Runtime implements it over <c>GeneratedShipLayout</c> (the pure half of generated_ship_loader.gd) plus the
    /// built <c>ShipView</c>.
    /// </summary>
    public interface IShipLoaderView : IShipSceneRoot, IShipInteriorView, IShipModuleScene
    {
        /// <summary><c>has_loaded_ship()</c>.</summary>
        bool HasLoadedShip { get; }

        /// <summary><c>layout_doc</c> (not a copy).</summary>
        GdDict LayoutDoc { get; }

        /// <summary><c>gameplay_doc</c> (not a copy).</summary>
        GdDict GameplayDoc { get; }

        /// <summary><c>get_layout_copy()</c>.</summary>
        GdDict GetLayoutCopy();

        /// <summary><c>get_start_transform()</c>.</summary>
        Xform3 GetStartTransform();

        /// <summary><c>get_goal_position()</c> (<see cref="Vec3.Inf"/> when none).</summary>
        Vec3 GetGoalPosition();

        /// <summary>
        /// <c>get_objective_specs_copy()</c>: gameplay-slice objectives with a <c>position</c> <see cref="Vec3"/> and, for
        /// repair junctions, <c>steps</c> each carrying a <c>position</c> <see cref="Vec3"/>.
        /// </summary>
        GdArray GetObjectiveSpecsCopy();

        /// <summary><c>get_loot_container_specs_copy()</c> (each with a <c>position</c> <see cref="Vec3"/>).</summary>
        GdArray GetLootContainerSpecsCopy();

        /// <summary><c>get_room_center(room_id)</c>; <see cref="Vec3.Inf"/> when the id is unknown.</summary>
        Vec3 GetRoomCenter(string roomId);

        /// <summary><c>get_blocked_route_nodes()</c> positions.</summary>
        IReadOnlyList<Vec3> GetBlockedRoutePositions();

        IReadOnlyList<Vec3> GetBreachZoneMarkers();
        GdArray GetBreachZoneSpecs();
        IReadOnlyList<Vec3> GetFireZoneMarkers();
        GdArray GetFireZoneSpecs();
        IReadOnlyList<Vec3> GetArcZoneMarkers();
        GdArray GetArcZoneSpecs();
        GdArray GetRadiationZoneSpecs();

        /// <summary><c>get_radiation_zone_at(local_position)</c>.</summary>
        GdDict GetRadiationZoneAt(Vec3 localPosition);

        /// <summary><c>authored_atmosphere_specs</c> (null when the loader has none).</summary>
        GdArray AuthoredAtmosphereSpecs { get; }

        GdDict GetAuthoredAtmosphereAt(Vec3 localPosition);
        double GetAuthoredAtmosphereDrainMultiplierAt(Vec3 localPosition);

        /// <summary><c>get_encounter_markers()</c>.</summary>
        GdArray GetEncounterMarkers();

        /// <summary><c>get_authored_portal_nodes()</c>.</summary>
        IReadOnlyList<IAuthoredPortal> GetAuthoredPortals();

        /// <summary><c>count_collision_shapes()</c>.</summary>
        long CountCollisionShapes();

        /// <summary>
        /// Dressing props (<c>DressingProp_&lt;room&gt;_&lt;n&gt;</c> nodes) as <c>{name, slot_cell}</c>, for
        /// <c>_collect_dressing_occupancy</c>.
        /// </summary>
        GdArray DressingPropSlots();

        /// <summary>Every structural wrapper under the loader (tree order), for the work-target scan and integrity visuals.</summary>
        IReadOnlyList<IStructuralModuleNode> StructuralModuleNodes();
    }
}
