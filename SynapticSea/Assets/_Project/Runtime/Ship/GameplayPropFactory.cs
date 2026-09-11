// Ported from scripts/placement/gameplay_prop_factory.gd @ 96ecb2b0
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Catalog-backed builder for slice interactable visuals (data/kits/gameplay_prop_v0.json). Every prop keeps a
    /// direct child renderer named <c>Mesh</c> so callers can replace a legacy marker without changing interaction or
    /// collision ownership. Visual only (Prop layer, no collider).
    /// </summary>
    public static class GameplayPropFactory
    {
        public const string DEFAULT_KIT_PATH = "res://data/kits/gameplay_prop_v0.json";

        static GdDict _catalog = new GdDict();
        static string _catalogPath = "";

        public static GdDict LoadCatalog(string path = DEFAULT_KIT_PATH)
        {
            if (path == _catalogPath && !_catalog.IsEmpty) return _catalog;
            _catalogPath = path;
            _catalog = new GdDict();
            if (string.IsNullOrEmpty(path) || !CatalogRegistry.Exists(path)) return _catalog;
            GdDict parsed = CatalogRegistry.LoadDict(path);
            if (parsed != null && parsed.Get("props", new GdDict()) is GdDict) _catalog = parsed;
            return _catalog;
        }

        /// <summary>Drops the cached catalog (tests, data hot reload).</summary>
        public static void ClearCache()
        {
            _catalog = new GdDict();
            _catalogPath = "";
        }

        /// <summary>
        /// Builds <c>GameplayProp_&lt;id&gt;</c> at <paramref name="godotWorldPosition"/> (Godot frame, local to
        /// <paramref name="parent"/>) raised by the catalog <c>y_offset</c>.
        /// </summary>
        public static GameObject Build(string propId, Vec3 godotWorldPosition, Transform parent = null)
        {
            GdDict catalog = LoadCatalog();
            var props = catalog.Get("props", new GdDict()) as GdDict ?? new GdDict();
            var prop = props.Get(propId, new GdDict()) as GdDict ?? new GdDict();
            var node = new GameObject(GodotNodeName.Validate("GameplayProp_" + propId)) { layer = PhysicsLayers.Prop };
            if (parent != null) node.transform.SetParent(parent, false);
            Vec3 position = godotWorldPosition + Vec3.Up * (float)V.F64(prop.Get("y_offset", 0.0));
            node.transform.localPosition = Frame.ToUnity(position);

            // RUNTIME: Godot loaded an optional `mesh_path` Mesh/PackedScene; every catalog entry ships with an empty
            // mesh_path, so the port only builds the primitive. A future mesh would be a prefab in the PropCatalog.
            string meshPath = V.Str(prop.Get("mesh_path", ""));
            if (meshPath.Length > 0) Debug.LogWarning($"GameplayPropFactory: mesh_path '{meshPath}' is not supported in the port; using the primitive");

            float scale = (float)V.F64(prop.Get("scale", 1.0));
            float height = Mathf.Max(0.1f, (float)V.F64(prop.Get("height_hint", 1.0)));
            Mesh mesh;
            Vector3 meshScale;
            switch (V.Str(prop.Get("primitive", "box")))
            {
                case "sphere":
                    mesh = RuntimeVisualCatalog.Sphere;
                    meshScale = new Vector3(height, height, height);
                    break;
                case "capsule":
                    // Godot CapsuleMesh(height h, radius 0.28h); Unity's capsule is height 2 / radius 0.5, so the
                    // hemispheres stretch slightly (h/r 3.57 vs 4).
                    mesh = RuntimeVisualCatalog.Capsule;
                    meshScale = new Vector3(height * 0.56f, height * 0.5f, height * 0.56f);
                    break;
                case "cylinder":
                    mesh = RuntimeVisualCatalog.Cylinder(height * 0.32f, height * 0.38f, height);
                    meshScale = Vector3.one;
                    break;
                default:
                    mesh = RuntimeVisualCatalog.Cube;
                    meshScale = new Vector3(height * 0.9f, height, height * 0.75f);
                    break;
            }
            Color albedo = CatalogColor(prop);
            RuntimeVisualCatalog.AddMesh(node.transform, "Mesh", mesh, RuntimeVisualCatalog.Material(albedo),
                Vector3.zero, Quaternion.identity, meshScale * scale, PhysicsLayers.Prop, castShadows: true);
            return node;
        }

        public static GameObject BuildFromCatalog(string propId, Vec3 godotWorldPosition, Transform parent = null) =>
            Build(propId, godotWorldPosition, parent);

        static Color CatalogColor(GdDict prop)
        {
            var fallback = new Color(0.7f, 0.7f, 0.7f, 1f);
            string albedo = V.Str(prop.Get("albedo", ""));
            return albedo.Length == 0 ? fallback : AtmosphereApplier.ColorValue(albedo, fallback);
        }
    }
}
