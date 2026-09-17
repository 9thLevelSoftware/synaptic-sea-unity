// Scene boundary for scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 (ship scene-root lifecycle).
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// RUNTIME: every place the coordinator created, parented, moved or freed a ship scene root: the home
    /// <c>loader.load_from_paths</c>, the <c>GeneratedShipLoader</c> the ShipGenerator built from generated documents,
    /// <c>LifeBoatBuilder.build</c>'s node tree, <c>add_child</c>, <c>remove_child + queue_free</c>, and the
    /// <c>position = DERELICT_DOCK_OFFSET</c> anchor. The Runtime implements it over <c>ShipSceneBuilder</c> /
    /// <c>ShipView</c> (which should implement <see cref="IShipLoaderView"/>).
    /// </summary>
    public interface IShipSceneHost
    {
        /// <summary>
        /// <c>loader.load_from_paths(layout, kit, slice)</c> for the home ship (ShipSceneBuilder.LoadFromPaths). Synchronous,
        /// like Godot: returns the loaded view (attached under the session, at origin), or null with
        /// <paramref name="failureReason"/> (the session raises <c>PlayableFailed</c>).
        /// </summary>
        IShipLoaderView LoadHomeShip(string layoutPath, string kitPath, string gameplaySlicePath, out string failureReason);

        /// <summary>
        /// Builds a DETACHED derelict scene root from <see cref="ShipGenerator"/> output (ShipSceneBuilder.LoadFromDocuments
        /// with <c>IsAway</c>); null on failure. The session attaches it with <see cref="AttachShipRoot"/>.
        /// </summary>
        IShipLoaderView BuildShipScene(ShipDocuments documents);

        /// <summary>
        /// Instantiates the lifeboat node tree <c>LifeBoatBuilder.build(biome)</c> described (a plain Node3D with a
        /// <c>ShipStructure</c> child; it should implement <see cref="IShipInteriorView"/>). Detached; null on failure.
        /// </summary>
        IShipSceneRoot BuildLifeboatScene(LifeBoatBuilder.BuildResult lifeboat);

        /// <summary><c>add_child(root)</c> under the coordinator (makes <see cref="IShipSceneRoot.IsInsideTree"/> true).</summary>
        void AttachShipRoot(IShipSceneRoot root);

        /// <summary><c>remove_child(root)</c> when parented to the coordinator, then <c>queue_free()</c>.</summary>
        void FreeShipRoot(IShipSceneRoot root);

        /// <summary><c>(root as Node3D).position = p</c> (a root parented to the coordinator at origin).</summary>
        void SetShipRootPosition(IShipSceneRoot root, Vec3 position);

        /// <summary><c>(root as Node3D).global_transform = xform</c>.</summary>
        void SetShipRootGlobalTransform(IShipSceneRoot root, Xform3 xform);

        /// <summary><c>root.get_parent() == self</c>.</summary>
        bool IsParentedToSession(IShipSceneRoot root);
    }
}
