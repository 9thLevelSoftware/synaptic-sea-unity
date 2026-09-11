using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>
    /// Builds one prefab per structural kit module, replacing the Godot wrapper scenes
    /// (scenes/wrappers/structural/&lt;kit&gt;/*.tscn). Inputs:
    ///  - kit JSON (StreamingAssets/data/kits/&lt;kit&gt;.json): module identity, family, footprint, nav blocker, pivot policy
    ///  - placement contract JSON: socket positions and bounds (the wrapper Marker3D sockets all sit at the origin)
    ///  - Godot wrapper .tscn (fixtures/godot_wrappers/&lt;kit&gt;/): the BoxShape3D collision truth and visual variants
    ///    (the kit's collision_proxy_records are Z-up Blender boxes and are NOT used)
    ///  - glTFast-imported GLBs under Assets/Content/Structural/&lt;kit&gt;/
    /// Output: Assets/Content/Prefabs/Structural/&lt;kit&gt;/&lt;module&gt;.prefab, a KitCatalog asset, and a JSON report.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.StructuralPrefabBuilder.BuildAll -quit [-kit ship_structural_v0] [-strict]
    /// </summary>
    public static class StructuralPrefabBuilder
    {
        const string DefaultKit = "ship_structural_v0";
        const int StructureLayer = 7;
        const int CeilingLayer = 13;

        static readonly Regex ExtResourceRx = new Regex(@"\[ext_resource[^\]]*path=""(?<path>[^""]+)""[^\]]*id=""(?<id>[^""]+)""");
        static readonly Regex BoxShapeRx = new Regex(@"\[sub_resource type=""BoxShape3D"" id=""(?<id>[^""]+)""\]\s*size = Vector3\((?<size>[^)]*)\)");
        static readonly Regex NodeHeaderRx = new Regex(@"^\[node name=""(?<name>[^""]+)""(?<rest>[^\]]*)\]", RegexOptions.Multiline);

        sealed class BoxSpec
        {
            public string Name;
            public Vector3 GodotCenter;
            public Vector3 Size;
        }

        sealed class Report
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly GdArray Modules = new GdArray();
        }

        [MenuItem("Synaptic Sea/Content/Build Structural Prefabs")]
        public static void BuildAllMenu() => Build(DefaultKit, strict: false, exitWhenDone: false);

        public static void BuildAll()
        {
            string[] args = Environment.GetCommandLineArgs();
            string kit = ArgValue(args, "-kit") ?? DefaultKit;
            bool strict = args.Contains("-strict");
            Build(kit, strict, exitWhenDone: Application.isBatchMode);
        }

        static string ArgValue(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        public static void Build(string kitId, bool strict, bool exitWhenDone)
        {
            var report = new Report();
            string kitPath = Path.Combine(Application.streamingAssetsPath, "data", "kits", kitId + ".json");
            var kit = File.Exists(kitPath) ? GdJson.ParseDict(File.ReadAllText(kitPath)) : null;
            if (kit == null)
            {
                report.Errors.Add($"kit JSON missing or invalid: {kitPath}");
                Finish(kitId, report, strict, exitWhenDone, null);
                return;
            }

            string prefabDir = $"Assets/Content/Prefabs/Structural/{kitId}";
            Directory.CreateDirectory(prefabDir);

            var catalogEntries = new List<KitCatalog.Entry>();
            foreach (object m in kit.GetArrayOrEmpty("modules"))
            {
                if (!(m is GdDict module)) continue;
                string moduleId = module.GetString("module_id");
                try
                {
                    var prefab = BuildModule(kitId, module, prefabDir, report);
                    if (prefab != null) catalogEntries.Add(new KitCatalog.Entry { moduleId = moduleId, prefab = prefab });
                }
                catch (Exception e)
                {
                    report.Errors.Add($"{moduleId}: {e.GetType().Name}: {e.Message}");
                }
            }

            var catalog = WriteCatalog(kitId, kit.GetFloat("grid_step_m", 4.0), catalogEntries);
            Finish(kitId, report, strict, exitWhenDone, catalog);
        }

        static StructuralModule BuildModule(string kitId, GdDict module, string prefabDir, Report report)
        {
            string moduleId = module.GetString("module_id");
            string family = module.GetString("module_family");
            var entry = new GdDict { { "module_id", moduleId } };
            report.Modules.Add(entry);

            // ---- contract (sockets + bounds)
            string contractPath = Path.Combine(Application.streamingAssetsPath, "data", "placement", "contracts", "structural", kitId, moduleId + "_contract.json");
            var contract = File.Exists(contractPath) ? GdJson.ParseDict(File.ReadAllText(contractPath)) : null;
            if (contract == null) report.Errors.Add($"{moduleId}: contract missing ({contractPath})");

            // ---- Godot wrapper scene (collision + visual variants)
            string tscnPath = Path.Combine(RepoRoot, "fixtures", "godot_wrappers", kitId, moduleId + ".tscn");
            if (!File.Exists(tscnPath))
            {
                report.Errors.Add($"{moduleId}: wrapper scene missing ({tscnPath})");
                return null;
            }
            string tscn = File.ReadAllText(tscnPath);
            var ext = ExtResourceRx.Matches(tscn).Cast<Match>().ToDictionary(x => x.Groups["id"].Value, x => x.Groups["path"].Value);
            var boxes = ParseCollisionBoxes(tscn, moduleId, report);
            var variants = ParseVisualVariants(tscn, ext);

            // ---- assemble
            var root = new GameObject(moduleId);
            try
            {
                bool isCeiling = family == "ceiling" || moduleId.StartsWith("ceiling", StringComparison.Ordinal);
                int layer = isCeiling ? CeilingLayer : StructureLayer;
                root.layer = layer;

                var sm = root.AddComponent<StructuralModule>();
                sm.kitId = kitId;
                sm.moduleId = moduleId;
                sm.moduleFamily = family;
                var fp = module.GetArrayOrEmpty("footprint_cells");
                sm.footprintCells = new Vector2Int(fp.Count > 0 ? V.I32(fp[0]) : 1, fp.Count > 1 ? V.I32(fp[1]) : 1);
                sm.navBlocker = module.GetBool("nav_blocker");
                sm.pivotPolicy = module.GetString("pivot_policy");

                // Sockets from the contract, named like the Godot anchors.
                var sockets = new GameObject("Sockets");
                sockets.transform.SetParent(root.transform, false);
                sockets.layer = layer;
                var contractSocketIds = new HashSet<string>(StringComparer.Ordinal);
                if (contract != null)
                {
                    var bounds = contract.GetDictOrEmpty("bounds");
                    sm.godotBoundsMin = ToVector3(bounds.GetArrayOrEmpty("local_min_m"));
                    sm.godotBoundsMax = ToVector3(bounds.GetArrayOrEmpty("local_max_m"));
                    foreach (object s in contract.GetArrayOrEmpty("sockets"))
                    {
                        if (!(s is GdDict socket)) continue;
                        string id = socket.GetString("id");
                        contractSocketIds.Add(id);
                        var godotPos = Vec3.FromArray(socket.Get("position_m"));
                        var go = new GameObject("Anchor_SOCK_" + id);
                        go.layer = layer;
                        go.transform.SetParent(sockets.transform, false);
                        go.transform.localPosition = Frame.ToUnity(godotPos);
                        var marker = go.AddComponent<SocketMarker>();
                        marker.socketId = id;
                        marker.kind = socket.GetString("kind");
                        marker.compatibleKinds = socket.GetArrayOrEmpty("compatible_kinds").Select(V.Str).ToArray();
                        marker.godotLocalPosition = new Vector3(godotPos.X, godotPos.Y, godotPos.Z);
                    }
                }
                foreach (object n in module.GetArrayOrEmpty("socket_names"))
                {
                    string name = V.Str(n);
                    string id = name.StartsWith("SOCK_", StringComparison.Ordinal) ? name.Substring(5) : name;
                    if (!contractSocketIds.Contains(id)) report.Warnings.Add($"{moduleId}: kit socket '{name}' has no contract position");
                }

                // Collision from the Godot wrapper boxes.
                var collisionRoot = new GameObject("CollisionRoot");
                collisionRoot.layer = layer;
                collisionRoot.transform.SetParent(root.transform, false);
                foreach (var box in boxes)
                {
                    var bc = collisionRoot.AddComponent<BoxCollider>();
                    bc.center = new Vector3(-box.GodotCenter.x, box.GodotCenter.y, box.GodotCenter.z);
                    bc.size = box.Size;
                    if (box.Size == Vector3.one && box.GodotCenter == Vector3.zero)
                        report.Warnings.Add($"{moduleId}: collision '{box.Name}' is the Godot 1 m placeholder cube (kept for parity)");
                }

                // Visual variants.
                var visual = new GameObject("Visual");
                visual.layer = layer;
                visual.transform.SetParent(root.transform, false);
                sm.intactVisual = InstantiateVariant(kitId, moduleId, "Intact", variants, "1_visual", visual.transform, layer, report);
                sm.damagedVisual = InstantiateVariant(kitId, moduleId, "Damaged", variants, "2_visual_damaged", visual.transform, layer, report);
                sm.breachedVisual = InstantiateVariant(kitId, moduleId, "Breached", variants, "3_visual_breached", visual.transform, layer, report);
                if (sm.damagedVisual == null) sm.damagedVisual = sm.intactVisual;
                if (sm.breachedVisual == null) sm.breachedVisual = sm.intactVisual;
                sm.SetIntegrity(StructuralModule.IntegrityIntact);

                GameObjectUtility.SetStaticEditorFlags(root, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic);
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, GameObjectUtility.GetStaticEditorFlags(root));

                // Bounds drift check: contract bounds vs the intact renderers (converted back to the Godot frame).
                if (contract != null && sm.intactVisual != null)
                {
                    var renderers = sm.intactVisual.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToArray();
                    if (renderers.Length > 0)
                    {
                        var b = renderers[0].bounds;
                        foreach (var r in renderers.Skip(1)) b.Encapsulate(r.bounds);
                        entry["render_bounds_unity_min"] = GdArray.Of((double)b.min.x, (double)b.min.y, (double)b.min.z);
                        entry["render_bounds_unity_max"] = GdArray.Of((double)b.max.x, (double)b.max.y, (double)b.max.z);
                    }
                }

                entry["sockets"] = (long)contractSocketIds.Count;
                entry["colliders"] = (long)boxes.Count;
                entry["layer"] = (long)layer;

                string prefabPath = $"{prefabDir}/{moduleId}.prefab";
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool ok);
                if (!ok || saved == null)
                {
                    report.Errors.Add($"{moduleId}: failed to save prefab {prefabPath}");
                    return null;
                }
                entry["prefab"] = prefabPath;
                return saved.GetComponent<StructuralModule>();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static List<BoxSpec> ParseCollisionBoxes(string tscn, string moduleId, Report report)
        {
            var sizes = BoxShapeRx.Matches(tscn).Cast<Match>().ToDictionary(x => x.Groups["id"].Value, x => ParseVec(x.Groups["size"].Value));
            var result = new List<BoxSpec>();
            foreach (var (name, attrs, body) in Nodes(tscn))
            {
                if (!attrs.Contains("type=\"CollisionShape3D\"") || !attrs.Contains("parent=\"CollisionRoot\"")) continue;
                var shape = Regex.Match(body, @"shape = SubResource\(""(?<id>[^""]+)""\)");
                if (!shape.Success || !sizes.TryGetValue(shape.Groups["id"].Value, out Vector3 size))
                {
                    report.Errors.Add($"{moduleId}: collision node '{name}' has no BoxShape3D");
                    continue;
                }
                Vector3 center = Vector3.zero;
                var pos = Regex.Match(body, @"position = Vector3\((?<v>[^)]*)\)");
                if (pos.Success) center = ParseVec(pos.Groups["v"].Value);
                var xform = Regex.Match(body, @"transform = Transform3D\((?<v>[^)]*)\)");
                if (xform.Success)
                {
                    var parts = xform.Groups["v"].Value.Split(',').Select(p => float.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
                    center = new Vector3(parts[9], parts[10], parts[11]);
                    // Rotated box → its axis-aligned extents (|R| * size). Exact for the 90° turns the kit uses.
                    var r = new float[3, 3] { { parts[0], parts[3], parts[6] }, { parts[1], parts[4], parts[7] }, { parts[2], parts[5], parts[8] } };
                    bool axisAligned = true;
                    var s = new[] { size.x, size.y, size.z };
                    var e = new float[3];
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            float a = Math.Abs(r[i, j]);
                            if (a > 1e-4f && Math.Abs(a - 1f) > 1e-4f) axisAligned = false;
                            e[i] += a * s[j];
                        }
                    }
                    size = new Vector3(e[0], e[1], e[2]);
                    if (!axisAligned) report.Warnings.Add($"{moduleId}: collision '{name}' has a non-90° rotation; using its bounding box");
                }
                result.Add(new BoxSpec { Name = name, GodotCenter = center, Size = size });
            }
            if (result.Count == 0) report.Warnings.Add($"{moduleId}: wrapper has no collision boxes");
            return result;
        }

        static Dictionary<string, string> ParseVisualVariants(string tscn, Dictionary<string, string> ext)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, attrs, _) in Nodes(tscn))
            {
                if (!name.StartsWith("VisualInstance", StringComparison.Ordinal)) continue;
                var inst = Regex.Match(attrs, @"instance=ExtResource\(""(?<id>[^""]+)""\)");
                if (inst.Success && ext.TryGetValue(inst.Groups["id"].Value, out string path)) map[inst.Groups["id"].Value] = path;
            }
            return map;
        }

        static IEnumerable<(string name, string attrs, string body)> Nodes(string tscn)
        {
            var headers = NodeHeaderRx.Matches(tscn).Cast<Match>().ToList();
            for (int i = 0; i < headers.Count; i++)
            {
                int start = headers[i].Index + headers[i].Length;
                int end = i + 1 < headers.Count ? headers[i + 1].Index : tscn.Length;
                yield return (headers[i].Groups["name"].Value, headers[i].Groups["rest"].Value, tscn.Substring(start, end - start));
            }
        }

        static GameObject InstantiateVariant(string kitId, string moduleId, string label, Dictionary<string, string> variants,
            string extId, Transform parent, int layer, Report report)
        {
            if (!variants.TryGetValue(extId, out string resPath)) return null;
            const string prefix = "res://assets/imported/structural/";
            if (!resPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                report.Errors.Add($"{moduleId}: unexpected visual path {resPath}");
                return null;
            }
            string assetPath = "Assets/Content/Structural/" + resPath.Substring(prefix.Length);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null)
            {
                report.Errors.Add($"{moduleId}: missing GLB {assetPath}");
                return null;
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, parent);
            instance.name = label;
            int hidden = 0;
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer;
                if (IsCollisionProxyName(t.name))
                {
                    var r = t.GetComponent<Renderer>();
                    if (r != null && r.enabled)
                    {
                        r.enabled = false;
                        hidden++;
                    }
                }
            }
            if (hidden > 0) report.Warnings.Add($"{moduleId}/{label}: hid {hidden} collision-proxy mesh(es) that Godot rendered untextured");
            return instance;
        }

        static bool IsCollisionProxyName(string name) =>
            name.StartsWith("Collision_", StringComparison.Ordinal) || name.EndsWith("-col", StringComparison.Ordinal) ||
            name.EndsWith("-colonly", StringComparison.Ordinal) || name.EndsWith("-convcol", StringComparison.Ordinal);

        static KitCatalog WriteCatalog(string kitId, double gridStep, List<KitCatalog.Entry> entries)
        {
            const string dir = "Assets/Resources/Catalogs";
            Directory.CreateDirectory(dir);
            string path = $"{dir}/KitCatalog_{kitId}.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<KitCatalog>(path);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<KitCatalog>();
                AssetDatabase.CreateAsset(catalog, path);
            }
            catalog.kitId = kitId;
            catalog.gridStepMetres = (float)gridStep;
            catalog.modules = entries;
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            return catalog;
        }

        static void Finish(string kitId, Report report, bool strict, bool exitWhenDone, KitCatalog catalog)
        {
            var doc = new GdDict
            {
                { "kit_id", kitId },
                { "prefab_count", (long)(catalog?.modules.Count ?? 0) },
                { "errors", new GdArray(report.Errors) },
                { "warnings", new GdArray(report.Warnings) },
                { "modules", report.Modules },
            };
            string logDir = Path.Combine(RepoRoot, "builds", "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, $"structural-prefab-report-{kitId}.json"), GdJson.Stringify(doc, "  "), new UTF8Encoding(false));
            AssetDatabase.Refresh();

            string summary = $"STRUCTURAL PREFABS {(report.Errors.Count == 0 ? "PASS" : "FAIL")} kit={kitId} prefabs={catalog?.modules.Count ?? 0} errors={report.Errors.Count} warnings={report.Warnings.Count}";
            foreach (var e in report.Errors) Debug.LogError("[StructuralPrefabBuilder] " + e);
            foreach (var w in report.Warnings) Debug.Log("[StructuralPrefabBuilder] warning: " + w);
            Debug.Log("[StructuralPrefabBuilder] " + summary);
            if (exitWhenDone) EditorApplication.Exit(report.Errors.Count > 0 || (strict && report.Warnings.Count > 0) ? 1 : 0);
        }

        static Vector3 ParseVec(string csv)
        {
            var p = csv.Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
            return new Vector3(p[0], p[1], p[2]);
        }

        static Vector3 ToVector3(GdArray a) =>
            a != null && a.Count >= 3 ? new Vector3((float)V.F64(a[0]), (float)V.F64(a[1]), (float)V.F64(a[2])) : Vector3.zero;
    }
}
