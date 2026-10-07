// Scene half of the ship scene-root lifecycle in scripts/procgen/playable_generated_ship.gd @ 96ecb2b0
// (loader.load_from_paths, the ShipGenerator loader, LifeBoatBuilder.build's node tree, add_child/queue_free).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
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
    /// scene views (atmosphere, sensors).
    /// </summary>
    public sealed class UnityShipSceneHost : IShipSceneHost, IPreparedHomeSceneHost
    {
        public readonly Transform SessionRoot;

        /// <summary>The current home loader (replaced on every reload).</summary>
        public ShipLoaderNode HomeLoader { get; private set; }
        ShipLoaderNode _preparedEmptyHome;

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
            bool loaded = IsUserPath(layoutPath) || IsUserPath(gameplaySlicePath)
                ? LoadThroughResources(builder, layoutPath, kitPath, gameplaySlicePath, ref reason)
                : builder.LoadFromPaths(layoutPath, kitPath, gameplaySlicePath);
            if (!loaded)
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

        public IPreparedHome PrepareHome(ShipDocuments documents, IShipLoaderView expectedCurrentHome, out string reason)
        {
            reason = "prepared_home_invalid";
            if (documents == null || documents.IsAway) return null;
            ShipLoaderNode old = expectedCurrentHome as ShipLoaderNode;
            if (expectedCurrentHome == null)
            { if (HomeLoader != null || _preparedEmptyHome != null) return null; }
            else if (old == null || old != HomeLoader || !old.IsValid || !Roots.Contains(old)) return null;
            var selected = BuildShipScene(documents) as ShipLoaderNode;
            if (selected == null || !selected.IsValid) return null;
            if (old == null) _preparedEmptyHome = selected;
            reason = ""; return new PreparedHome(this, old, selected);
        }

        sealed class PreparedHome : IPreparedHome
        {
            readonly UnityShipSceneHost _host;
            readonly ShipLoaderNode _old, _selected;
            readonly Transform _parent;
            readonly bool _visible;
            readonly Xform3 _transform;
            bool _finished;
            public IShipLoaderView PreparedLoader => _selected;
            public bool IsAdopted { get; private set; }
            public PreparedHome(UnityShipSceneHost host, ShipLoaderNode old, ShipLoaderNode selected)
            {
                _host = host; _old = old; _selected = selected;
                if (old != null)
                { _parent = old.GameObject.transform.parent; _visible = old.GameObject.activeSelf; _transform = old.Transform; }
            }
            public bool TryAdopt(out string reason)
            {
                reason = "prepared_home_invalid";
                if (_finished || IsAdopted || _host.HomeLoader != _old || !_selected.IsValid ||
                    (_old == null ? _host._preparedEmptyHome != _selected : !_old.IsValid || !_host.Roots.Contains(_old)) ||
                    !_host.Roots.Contains(_selected) || _selected.IsInsideTree) return false;
                IsAdopted = true; _host.HomeLoader = _selected;
                if (_old != null) _old.GameObject.SetActive(false);
                _host.AttachShipRoot(_selected);
                reason = ""; return true;
            }
            public void RestoreRetainedHome()
            {
                if (_finished || _old != null && !_old.IsValid) throw new InvalidOperationException("retained_home_unavailable");
                _host.HomeLoader = _old;
                if (_old != null)
                {
                    _old.GameObject.transform.SetParent(_parent, false); _old.Transform = _transform;
                    _old.GameObject.SetActive(_visible);
                }
                if (_selected.IsValid) _selected.GameObject.SetActive(false);
                IsAdopted = false; Physics.SyncTransforms();
            }
            public void Commit()
            {
                if (_finished || !IsAdopted || _host.HomeLoader != _selected || !_selected.IsValid)
                    throw new InvalidOperationException("prepared_home_not_adopted");
                _finished = true;
                if (_old == null) _host._preparedEmptyHome = null;
                else _host.FreeShipRoot(_old);
            }
            public void Dispose()
            {
                if (_finished) return;
                if (IsAdopted) RestoreRetainedHome();
                _finished = true;
                if (_old == null && _host._preparedEmptyHome == _selected) _host._preparedEmptyHome = null;
                if (_selected.IsValid) _host.FreeShipRoot(_selected);
            }
        }

        static bool IsUserPath(string path) => FileSystemResourceReader.IsUserPath(path);

        /// <summary>
        /// A generated run's layout and slice live under <c>user://runs/&lt;run_id&gt;/</c> in <see cref="CoreServices.UserStorage"/>,
        /// which need not be a directory on disk (tests use memory storage), so they are read through
        /// <see cref="CoreServices.Resources"/> (which resolves <c>user://</c>) and handed to
        /// <see cref="ShipSceneBuilder.LoadFromDocuments"/>. The kit stays a <c>res://</c> file.
        /// </summary>
        static bool LoadThroughResources(ShipSceneBuilder builder, string layoutPath, string kitPath, string gameplaySlicePath, ref string reason)
        {
            GdDict layout = ReadDict(layoutPath);
            GdDict kit = ReadDict(kitPath);
            GdDict slice = ReadDict(gameplaySlicePath);
            if (layout == null) reason = "layout not found or invalid: " + layoutPath;
            else if (kit == null) reason = "kit not found or invalid: " + kitPath;
            else if (slice == null) reason = "gameplay slice not found or invalid: " + gameplaySlicePath;
            if (layout == null || kit == null || slice == null) return false;
            return builder.LoadFromDocuments(layout, kit, slice, false, new GdDict
            {
                { "layout", layoutPath },
                { "kit", ShipSceneBuilder.ResolvePath(kitPath) },
                { "gameplay_slice", gameplaySlicePath },
            });
        }

        static GdDict ReadDict(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (CoreServices.Resources == null) CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            string text = CoreServices.Resources.ReadText(path);
            return text == null ? null : GdJson.ParseString(text) as GdDict;
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
