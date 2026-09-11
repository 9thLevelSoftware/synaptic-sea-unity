using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>
    /// Builds a visual-only prefab per prop sidecar (<c>Assets/Content/Props/**/&lt;id&gt;.sidecar.json</c>), replacing
    /// Godot's RuntimePropVisualBinder GLB mounting. Verifies the GLB sha256 against the sidecar (port of
    /// tools/validate_prop_visual_bindings.py) and writes Assets/Resources/Catalogs/PropCatalog.asset plus a report.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.PropPrefabBuilder.BuildAll -quit
    /// </summary>
    public static class PropPrefabBuilder
    {
        const int PropLayer = 11;
        const string PrefabDir = "Assets/Content/Prefabs/Props";

        [MenuItem("Synaptic Sea/Content/Build Prop Prefabs")]
        public static void BuildAllMenu() => Build(exitWhenDone: false);

        public static void BuildAll() => Build(exitWhenDone: Application.isBatchMode);

        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        static void Build(bool exitWhenDone)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var built = new List<PropVisual>();
            Directory.CreateDirectory(PrefabDir);

            foreach (string sidecarPath in Directory.GetFiles("Assets/Content/Props", "*.sidecar.json", SearchOption.AllDirectories)
                         .Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal))
            {
                var sidecar = GdJson.ParseDict(File.ReadAllText(sidecarPath));
                if (sidecar == null)
                {
                    errors.Add($"{sidecarPath}: invalid JSON");
                    continue;
                }
                string assetId = sidecar.GetString("asset_id");
                string glbPath = sidecarPath.Replace(".sidecar.json", ".glb");
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
                if (model == null)
                {
                    errors.Add($"{assetId}: GLB not found at {glbPath}");
                    continue;
                }

                var source = sidecar.GetDictOrEmpty("source");
                string expectedSha = source.GetString("sha256");
                string actualSha = Sha256(glbPath);
                if (!string.IsNullOrEmpty(expectedSha) && !string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{assetId}: sha256 mismatch (sidecar {expectedSha}, file {actualSha})");

                var root = new GameObject(assetId) { layer = PropLayer };
                try
                {
                    var pv = root.AddComponent<PropVisual>();
                    var binding = sidecar.GetDictOrEmpty("binding");
                    var placement = sidecar.GetDictOrEmpty("placement");
                    var bounds = sidecar.GetDictOrEmpty("bounds");
                    pv.assetId = assetId;
                    pv.propKind = sidecar.GetString("prop_kind");
                    pv.bindingNamespace = binding.GetString("namespace");
                    pv.bindingIds = binding.GetArrayOrEmpty("ids").Select(V.Str).ToArray();
                    pv.allowedYawDegrees = placement.GetArrayOrEmpty("allowed_yaw_deg").Select(v => (float)V.F64(v)).ToArray();
                    pv.surface = placement.GetString("surface");
                    pv.collisionPolicy = sidecar.GetString("collision_policy");
                    pv.godotBoundsMin = ToVector3(bounds.GetArrayOrEmpty("local_min_m"));
                    pv.godotBoundsMax = ToVector3(bounds.GetArrayOrEmpty("local_max_m"));
                    pv.sourceSha256 = expectedSha;

                    if (placement.GetString("origin") != "scene_origin")
                        warnings.Add($"{assetId}: placement origin '{placement.GetString("origin")}' is applied by the runtime binder");

                    var visual = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
                    visual.name = "Visual";
                    visual.transform.localPosition = Frame.ToUnity(placement.Get("offset_m"));
                    var rot = Vec3.FromArray(placement.Get("rotation_degrees"));
                    // Mirroring X keeps pitch about X and negates yaw (Y) and roll (Z).
                    visual.transform.localRotation = Quaternion.Euler(rot.X, -rot.Y, -rot.Z);
                    visual.transform.localScale = Vector3.one * (float)placement.GetFloat("scale", 1.0);
                    foreach (var t in visual.GetComponentsInChildren<Transform>(true))
                    {
                        t.gameObject.layer = PropLayer;
                        foreach (var c in t.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(c);
                    }

                    // Bounds drift: sidecar Godot bounds vs imported renderers (converted back to the Godot frame).
                    var renderers = visual.GetComponentsInChildren<Renderer>(true);
                    if (renderers.Length > 0)
                    {
                        var b = renderers[0].bounds;
                        foreach (var r in renderers.Skip(1)) b.Encapsulate(r.bounds);
                        var godotMin = new Vector3(-b.max.x, b.min.y, b.min.z);
                        var godotMax = new Vector3(-b.min.x, b.max.y, b.max.z);
                        if (Vector3.Distance(godotMin, pv.godotBoundsMin) > 0.05f || Vector3.Distance(godotMax, pv.godotBoundsMax) > 0.05f)
                            warnings.Add($"{assetId}: bounds drift sidecar[{pv.godotBoundsMin}..{pv.godotBoundsMax}] imported[{godotMin}..{godotMax}]");
                    }

                    var saved = PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabDir}/{assetId}.prefab", out bool ok);
                    if (!ok || saved == null) errors.Add($"{assetId}: failed to save prefab");
                    else built.Add(saved.GetComponent<PropVisual>());
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }

            const string catalogPath = "Assets/Resources/Catalogs/PropCatalog.asset";
            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath));
            var catalog = AssetDatabase.LoadAssetAtPath<PropCatalog>(catalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<PropCatalog>();
                AssetDatabase.CreateAsset(catalog, catalogPath);
            }
            catalog.props = built;
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            var doc = new GdDict
            {
                { "prop_count", (long)built.Count },
                { "errors", new GdArray(errors) },
                { "warnings", new GdArray(warnings) },
            };
            string logDir = Path.Combine(RepoRoot, "builds", "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, "prop-prefab-report.json"), GdJson.Stringify(doc, "  "), new UTF8Encoding(false));

            foreach (var e in errors) Debug.LogError("[PropPrefabBuilder] " + e);
            foreach (var w in warnings) Debug.Log("[PropPrefabBuilder] warning: " + w);
            Debug.Log($"[PropPrefabBuilder] PROP PREFABS {(errors.Count == 0 ? "PASS" : "FAIL")} props={built.Count} errors={errors.Count} warnings={warnings.Count}");
            if (exitWhenDone) EditorApplication.Exit(errors.Count == 0 ? 0 : 1);
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return string.Concat(sha.ComputeHash(fs).Select(b => b.ToString("x2")));
        }

        static Vector3 ToVector3(GdArray a) =>
            a != null && a.Count >= 3 ? new Vector3((float)V.F64(a[0]), (float)V.F64(a[1]), (float)V.F64(a[2])) : Vector3.zero;
    }
}
