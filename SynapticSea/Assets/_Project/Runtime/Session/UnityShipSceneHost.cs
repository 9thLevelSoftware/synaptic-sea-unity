// Scene half of the ship scene-root lifecycle in scripts/procgen/playable_generated_ship.gd @ 96ecb2b0
// (loader.load_from_paths, the ShipGenerator loader, LifeBoatBuilder.build's node tree, add_child/queue_free).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// <see cref="IShipSceneHost"/> over <see cref="ShipSceneBuilder"/>. Ship roots live under <see cref="SessionRoot"/>
    /// (at the world origin, like the Godot coordinator node). Detached roots are inactive until attached, so a freshly
    /// generated derelict never shows at the origin. Raises <see cref="RootAttached"/> / <see cref="RootFreed"/> for the
    /// scene views (ceiling fade, atmosphere, sensors).
    /// </summary>
    public sealed class UnityShipSceneHost : IShipSceneHost
    {
        public readonly Transform SessionRoot;

        /// <summary>The current home loader (replaced on every reload).</summary>
        public ShipLoaderNode HomeLoader { get; private set; }

        public readonly List<SceneShipRoot> Roots = new List<SceneShipRoot>();

        public event Action<SceneShipRoot> RootAttached;
        public event Action<SceneShipRoot> RootFreed;

        /// <summary>Last loader summary (<c>ship_loaded</c>) and failure reason.</summary>
        public GdDict LastLoadSummary { get; private set; }
        public string LastFailure { get; private set; } = "";

        public UnityShipSceneHost(Transform sessionRoot)
        {
            SessionRoot = sessionRoot != null ? sessionRoot : throw new ArgumentNullException(nameof(sessionRoot));
        }

        public IShipLoaderView LoadHomeShip(string layoutPath, string kitPath, string gameplaySlicePath, out string failureReason)
        {
            failureReason = "";
            if (HomeLoader != null)
            {
                FreeShipRoot(HomeLoader);
                HomeLoader = null;
            }
            var builder = ShipSceneBuilder.Create(SessionRoot, "GeneratedShipLoader");
            string reason = "";
            builder.LoadFailed += r => reason = r;
            builder.ShipLoaded += s => LastLoadSummary = s;
            if (!builder.LoadFromPaths(layoutPath, kitPath, gameplaySlicePath))
            {
                failureReason = reason.Length > 0 ? reason : "load_failed";
                LastFailure = failureReason;
                DestroyGameObject(builder.View.gameObject);
                return null;
            }
            var node = new ShipLoaderNode(builder.View);
            HomeLoader = node;
            Roots.Add(node);
            node.Attach(SessionRoot);
            Physics.SyncTransforms();
            RootAttached?.Invoke(node);
            return node;
        }

        public IShipLoaderView BuildShipScene(ShipDocuments documents)
        {
            if (documents == null || documents.Layout == null) return null;
            var go = new GameObject(string.IsNullOrEmpty(documents.Name) ? "GeneratedDerelict" : GodotNodeName.Validate(documents.Name));
            go.SetActive(false);
            var view = go.AddComponent<ShipView>();
            var builder = new ShipSceneBuilder(view) { KitPath = documents.KitPath ?? "" };
            string reason = "";
            builder.LoadFailed += r => reason = r;
            if (!builder.LoadFromDocuments(documents.Layout, documents.Kit, documents.GameplaySlice ?? new GdDict(), documents.IsAway))
            {
                LastFailure = reason;
                Debug.LogWarning("UnityShipSceneHost: derelict build failed: " + reason);
                DestroyGameObject(go);
                return null;
            }
            var node = new ShipLoaderNode(view);
            Roots.Add(node);
            return node;
        }

        public IShipSceneRoot BuildLifeboatScene(LifeBoatBuilder.BuildResult lifeboat)
        {
            if (lifeboat == null) return null;
            var go = new GameObject(LifeBoatBuilder.BuildResult.ROOT_NAME);
            go.SetActive(false);
            var structure = new GameObject(LifeBoatBuilder.BuildResult.STRUCTURE_NAME).transform;
            structure.SetParent(go.transform, false);
            var rooms = new List<Vec3>();
            foreach (LifeBoatBuilder.RoomNode room in lifeboat.Rooms)
            {
                var roomGo = new GameObject(room.RoomId);
                roomGo.transform.SetParent(structure, false);
                roomGo.transform.localPosition = Frame.ToUnity(room.Position);
                rooms.Add(room.Position);
            }
            GdDict layout = lifeboat.Layout ?? new GdDict();
            KitPrefabCatalog kit = string.IsNullOrEmpty(lifeboat.KitPath) ? KitCatalogResolver.ForLayout(layout) : KitCatalogResolver.ForKitPath(lifeboat.KitPath);
            if (kit != null)
            {
                var built = new StructuralLayoutBuilder().Build(layout, kit, structure);
                if (built == null) Debug.LogWarning("UnityShipSceneHost: lifeboat structure failed to build");
            }
            var node = new LifeboatSceneRoot(go, rooms);
            Roots.Add(node);
            return node;
        }

        public void AttachShipRoot(IShipSceneRoot root)
        {
            if (!(root is SceneShipRoot r) || !r.IsValid) return;
            r.Attach(SessionRoot);
            Physics.SyncTransforms();
            RootAttached?.Invoke(r);
        }

        public void FreeShipRoot(IShipSceneRoot root)
        {
            if (!(root is SceneShipRoot r)) return;
            if (r == HomeLoader) HomeLoader = null;
            Roots.Remove(r);
            bool wasValid = r.IsValid;
            r.Free();
            if (wasValid) RootFreed?.Invoke(r);
        }

        public void SetShipRootPosition(IShipSceneRoot root, Vec3 position)
        {
            if (root == null) return;
            root.Transform = new Xform3(root.Transform.Basis, position);
            Physics.SyncTransforms();
        }

        public void SetShipRootGlobalTransform(IShipSceneRoot root, Xform3 xform)
        {
            if (root == null) return;
            root.Transform = xform;
            Physics.SyncTransforms();
        }

        public bool IsParentedToSession(IShipSceneRoot root) => root is SceneShipRoot r && r.IsInsideTree;

        /// <summary>Tears every root down (scene unload).</summary>
        public void FreeAll()
        {
            foreach (SceneShipRoot r in new List<SceneShipRoot>(Roots)) FreeShipRoot(r);
            HomeLoader = null;
        }

        static void DestroyGameObject(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
