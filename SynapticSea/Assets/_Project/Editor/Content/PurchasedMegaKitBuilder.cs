using System;
using System.IO;
using System.Linq;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>Local visual adapter preserving the original sockets, collision and navigation.</summary>
    public static class PurchasedMegaKitBuilder
    {
        public const string CatalogPath = "Assets/Resources/Catalogs/KitCatalog_ship_structural_v0_local.asset";
        const string SourceRoot = "Assets/Content/Purchased/MegaKit/";
        const string OutputRoot = "Assets/Content/Purchased/Prefabs/";

        [MenuItem("Synaptic Sea/Content/Enable Purchased MegaKit Floors (local)")]
        public static void Build()
        {
            BuildFromVisuals(SourceRoot + "Platform_Simple.gltf", SourceRoot + "Platform_Squares.gltf");
        }

        /// <summary>Also accepts custom GLB, glTF or prefab tiles already imported into the local project.</summary>
        public static void BuildFromVisuals(string floorAssetPath, string corridorAssetPath)
        {
            AssetDatabase.Refresh();
            var original = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>("Assets/Resources/Catalogs/KitCatalog_ship_structural_v0.asset");
            if (original == null) throw new InvalidOperationException("Default structural catalog must be baked first.");
            var plain = AssetDatabase.LoadAssetAtPath<GameObject>(floorAssetPath);
            var squares = AssetDatabase.LoadAssetAtPath<GameObject>(corridorAssetPath);
            if (plain == null || squares == null) throw new InvalidOperationException("Run tools/import_purchased_megakit.py first.");
            Directory.CreateDirectory(OutputRoot);
            var local = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>(CatalogPath);
            if (local == null)
            {
                local = ScriptableObject.CreateInstance<KitPrefabCatalog>();
                AssetDatabase.CreateAsset(local, CatalogPath);
            }
            local.kitId = original.kitId;
            local.gridStepMetres = original.gridStepMetres;
            local.useAsLocalOverride = false;
            local.modules.Clear();
            foreach (var entry in original.modules)
            {
                bool floor = entry.moduleId == "floor_1x1" || entry.moduleId == "floor_2x1"
                    || entry.moduleId == "corridor_floor_1x1" || entry.moduleId == "corridor_floor_1x2";
                var prefab = floor ? BuildFloor(entry.prefab, entry.moduleId.StartsWith("corridor") ? squares : plain) : entry.prefab;
                local.modules.Add(new KitPrefabCatalog.Entry { moduleId = entry.moduleId, prefab = prefab });
            }
            local.useAsLocalOverride = true;
            EditorUtility.SetDirty(local);
            AssetDatabase.SaveAssets();
            Debug.Log("PURCHASED MEGAKIT PASS: four floor adapters enabled locally; original collision and sockets retained.");
        }

        [MenuItem("Synaptic Sea/Content/Disable Purchased MegaKit Floors (local)")]
        public static void Disable()
        {
            var local = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>(CatalogPath);
            if (local == null) return;
            local.useAsLocalOverride = false;
            EditorUtility.SetDirty(local);
            AssetDatabase.SaveAssets();
        }

        /// <summary>GPU preview of a real deterministic generated ship using the local floor adapter.</summary>
        public static void CaptureGeneratedPreview()
        {
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CatalogRegistry.Clear();
            var documents = new ShipGenerator().GenerateFromSeed(17, 1, 1);
            var root = new GameObject("PurchasedMegaKitPreview");
            var cameraRoot = new GameObject("PreviewCamera");
            var lightRoot = new GameObject("PreviewLight");
            RenderTexture texture = null;
            Texture2D pixels = null;
            try
            {
                var builder = ShipSceneBuilder.Create(root.transform);
                builder.KitCatalog = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>(CatalogPath);
                if (!builder.LoadFromDocuments(documents.Layout, documents.Kit, documents.GameplaySlice, true))
                    throw new InvalidOperationException("Generated preview ship failed to load.");
                foreach (var module in root.GetComponentsInChildren<StructuralModule>(true))
                    if (module.moduleFamily == "ceiling") module.gameObject.SetActive(false);
                var renderers = root.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToArray();
                Bounds bounds = renderers[0].bounds;
                foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                var camera = cameraRoot.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(bounds.size.x, bounds.size.z) * 0.6f;
                camera.transform.position = bounds.center + new Vector3(1, 1, -1).normalized * bounds.size.magnitude * 1.5f;
                camera.transform.LookAt(bounds.center);
                camera.farClipPlane = bounds.size.magnitude * 4f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f, 0.055f, 0.075f);
                var light = lightRoot.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.5f;
                light.transform.rotation = Quaternion.Euler(45, -35, 0);
                texture = new RenderTexture(1600, 1000, 24);
                camera.targetTexture = texture;
                camera.Render();
                RenderTexture.active = texture;
                pixels = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0);
                pixels.Apply();
                string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/screenshots"));
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "purchased-megakit-seed17.png"), pixels.EncodeToPNG());
                Debug.Log("PURCHASED PREVIEW PASS seed=17 renderers=" + renderers.Length);
            }
            finally
            {
                RenderTexture.active = null;
                if (texture != null) Object.DestroyImmediate(texture);
                if (pixels != null) Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(cameraRoot);
                Object.DestroyImmediate(lightRoot);
                CatalogRegistry.Clear();
            }
        }

        static StructuralModule BuildFloor(StructuralModule original, GameObject tile)
        {
            var root = (GameObject)PrefabUtility.InstantiatePrefab(original.gameObject);
            try
            {
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var module = root.GetComponent<StructuralModule>();
                var oldVisual = root.transform.Find("Visual");
                if (oldVisual != null) Object.DestroyImmediate(oldVisual.gameObject);
                var visual = new GameObject("Visual");
                visual.transform.SetParent(root.transform, false);
                int width = module.footprintCells.x, depth = module.footprintCells.y;
                var floor = root.GetComponentsInChildren<BoxCollider>(true).FirstOrDefault();
                if (floor == null || width < 1 || depth < 1) throw new InvalidOperationException("Missing authoritative floor: " + module.moduleId);
                float floorTop = floor.bounds.max.y;
                for (int x = 0; x < width; x++)
                    for (int z = 0; z < depth; z++)
                    {
                        var instance = Object.Instantiate(tile, visual.transform);
                        instance.name = "PurchasedTile_" + x + "_" + z;
                        foreach (var collider in instance.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
                        StructuralLayoutBuilder.HideCollisionOnlyVisuals(instance);
                        var renderers = instance.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToArray();
                        if (renderers.Length == 0) throw new InvalidOperationException("Floor has no renderer.");
                        foreach (var renderer in renderers)
                        {
                            var materials = renderer.sharedMaterials;
                            for (int i = 0; i < materials.Length; i++)
                                if (materials[i] == null) materials[i] = FallbackFloorMaterial();
                            renderer.sharedMaterials = materials;
                        }
                        Bounds bounds = renderers[0].bounds;
                        foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                        if (Mathf.Abs(bounds.size.x - 4f) > 0.2f || Mathf.Abs(bounds.size.z - 4f) > 0.2f)
                            throw new InvalidOperationException("Unsupported tile units: " + bounds.size + "; expected 4 by 4 metres.");
                        instance.transform.position += new Vector3((x - (width - 1) * 0.5f) * 4f - bounds.center.x,
                            floorTop - bounds.max.y, (z - (depth - 1) * 0.5f) * 4f - bounds.center.z);
                        foreach (Transform child in instance.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = root.layer;
                    }
                module.intactVisual = visual;
                module.damagedVisual = visual;
                module.breachedVisual = visual;
                module.SetIntegrity(StructuralModule.IntegrityIntact);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, OutputRoot + module.moduleId + ".prefab");
                return prefab.GetComponent<StructuralModule>();
            }
            finally { Object.DestroyImmediate(root); }
        }

        static Material FallbackFloorMaterial()
        {
            const string path = OutputRoot + "UnassignedFloorSurface.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit shader is unavailable.");
            material = new Material(shader);
            material.SetColor("_BaseColor", new Color(0.24f, 0.29f, 0.32f));
            material.SetFloat("_Metallic", 0.6f);
            material.SetFloat("_Smoothness", 0.3f);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
