using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SynapticSea.Core.Procgen;
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
    ///  - glTFast-imported GLBs under Assets/Content/Structural/ (ship_structural_v0 or ithappy).
    ///    Wrapper <c>res://assets/imported/structural/...</c> paths map to that Content root.
    /// Output: Assets/Content/Prefabs/Structural/&lt;kit&gt;/&lt;module&gt;.prefab, a KitCatalog asset, and a JSON report.
    ///   F:\Unity\6000.6.0f1\Editor\Unity.exe -batchmode -nographics -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.StructuralPrefabBuilder.BuildAll -quit -kit ithappy_scifi_v0 -logFile builds/logs/ithappy-scifi-bake.log
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.StructuralPrefabBuilder.BuildAll -quit [-kit ship_structural_v0] [-strict]
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.StructuralPrefabBuilder.BuildAll -quit -kit ithappy_scifi_v0
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

        /// <summary>
        /// Derives a module's colliders from its imported visual when every wrapper box is Godot's 1 m placeholder cube.
        /// Returns the number of colliders added under <c>collisionRoot</c> (0 keeps the placeholder).
        /// </summary>
        delegate int CollisionOverride(string moduleId, GameObject collisionRoot, GameObject intactVisual, int layer, Report report);

        /// <summary>
        /// Port-status decision 9 keeps the Godot placeholder cubes for parity. The ramp is the exception: its 1 m cube sat at
        /// the cell centre while the visible treads had no collision, so it gets a slope box derived from the treads. The
        /// pillar and bulkhead keep their cubes; the ceiling's cube is on the Ceiling layer, which collides with nothing.
        /// </summary>
        static readonly Dictionary<string, CollisionOverride> CollisionOverrides = new Dictionary<string, CollisionOverride>(StringComparer.Ordinal)
        {
            ["ramp_up_1x2"] = BuildRampColliders,
        };

        public const string RampSlopeName = "RampSlope";
        public const float RampSlopeThickness = 0.2f;

        static bool IsPlaceholderCube(BoxSpec box) => box.Size == Vector3.one && box.GodotCenter == Vector3.zero;

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

            var catalogEntries = new List<KitPrefabCatalog.Entry>();
            foreach (object m in kit.GetArrayOrEmpty("modules"))
            {
                if (!(m is GdDict module)) continue;
                string moduleId = module.GetString("module_id");
                try
                {
                    var prefab = BuildModule(kitId, module, prefabDir, report);
                    if (prefab != null) catalogEntries.Add(new KitPrefabCatalog.Entry { moduleId = moduleId, prefab = prefab });
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

            // ---- contract (sockets + bounds). Additive kits may reuse v0 JSON contracts.
            string contractPath = ResolveContractPath(kitId, moduleId);
            var contract = File.Exists(contractPath) ? GdJson.ParseDict(File.ReadAllText(contractPath)) : null;
            if (contract == null) report.Errors.Add($"{moduleId}: contract missing ({contractPath})");

            // Companion {module_id}.asset.json beside the GLB is the art-package gate for new modules.
            // Inherited v0 twins may omit it; mesh alone is not shippable for wall_x_junction and later modules.
            string companionPath = KitAuthorityAudit.CompanionAssetJsonPath(ResolveStructuralContentRoot(kitId), moduleId);
            GdDict companion = File.Exists(companionPath) ? GdJson.ParseDict(File.ReadAllText(companionPath)) : null;
            bool hasV0Twin = File.Exists(Path.Combine(Application.streamingAssetsPath, "data", "placement",
                "contracts", "structural", KitCatalog.DEFAULT_KIT_ID, moduleId + "_contract.json"));
            foreach (string issue in KitAuthorityAudit.BakeErrors(module, contract, companion, hasV0Twin))
                report.Errors.Add($"{moduleId}: {issue}");

            // ---- Godot wrapper scene (collision + visual variants)
            string tscnPath = ResolveWrapperPath(kitId, moduleId, module);
            if (string.IsNullOrEmpty(tscnPath) || !File.Exists(tscnPath))
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
                bool placeholderOnly = boxes.Count > 0 && boxes.All(IsPlaceholderCube);
                CollisionOverrides.TryGetValue(moduleId, out CollisionOverride collisionOverride);
                if (!placeholderOnly) collisionOverride = null;
                if (collisionOverride == null)
                {
                    foreach (var box in boxes)
                    {
                        var bc = collisionRoot.AddComponent<BoxCollider>();
                        bc.center = new Vector3(-box.GodotCenter.x, box.GodotCenter.y, box.GodotCenter.z);
                        bc.size = box.Size;
                        if (IsPlaceholderCube(box))
                            report.Warnings.Add($"{moduleId}: collision '{box.Name}' is the Godot 1 m placeholder cube (kept for parity)");
                    }
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

                int colliderCount = boxes.Count;
                if (collisionOverride != null)
                {
                    colliderCount = collisionOverride(moduleId, collisionRoot, sm.intactVisual, layer, report);
                    if (colliderCount > 0)
                    {
                        entry["collision_override"] = "derived";
                    }
                    else
                    {
                        report.Warnings.Add($"{moduleId}: derived collision failed; keeping the Godot placeholder cube");
                        foreach (var box in boxes)
                        {
                            var bc = collisionRoot.AddComponent<BoxCollider>();
                            bc.center = new Vector3(-box.GodotCenter.x, box.GodotCenter.y, box.GodotCenter.z);
                            bc.size = box.Size;
                        }
                        colliderCount = boxes.Count;
                    }
                }
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
                entry["colliders"] = (long)colliderCount;
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

        /// <summary>
        /// One rotated box whose top face runs from the lowest tread top to the highest (extended half a tread past each end),
        /// as wide as the treads. The tread renderers come from the imported GLB, so no dimension is hard-coded; renderer
        /// bounds are already in the Unity frame (the module root sits at the origin while it is assembled).
        /// </summary>
        static int BuildRampColliders(string moduleId, GameObject collisionRoot, GameObject intactVisual, int layer, Report report)
        {
            if (intactVisual == null)
            {
                report.Errors.Add($"{moduleId}: no intact visual to derive the ramp collision from");
                return 0;
            }
            Transform root = collisionRoot.transform.parent;
            var treads = intactVisual.GetComponentsInChildren<Renderer>(true)
                .Where(r => r.enabled && r.name.IndexOf("_tread_", StringComparison.Ordinal) >= 0)
                .Select(r => r.bounds)
                .OrderBy(b => b.max.y)
                .ToList();
            if (treads.Count < 2)
            {
                report.Errors.Add($"{moduleId}: expected tread meshes (_tread_) in the ramp visual, found {treads.Count}");
                return 0;
            }
            Bounds low = treads[0], high = treads[treads.Count - 1];
            float centerX = treads.Average(b => b.center.x);
            var pLow = root.InverseTransformPoint(new Vector3(centerX, low.max.y, low.center.z));
            var pHigh = root.InverseTransformPoint(new Vector3(centerX, high.max.y, high.center.z));
            Vector3 along = pHigh - pLow;
            if (along.sqrMagnitude < 1e-6f || Mathf.Abs(along.z) < 1e-3f)
            {
                report.Errors.Add($"{moduleId}: the treads do not rise along the ramp");
                return 0;
            }
            Vector3 dir = along.normalized;
            Quaternion rotation = Quaternion.LookRotation(dir, Vector3.up);
            float treadDepth = treads.Average(b => b.size.z);
            float width = treads.Max(b => b.size.x);
            var slope = new GameObject(RampSlopeName) { layer = layer };
            slope.transform.SetParent(collisionRoot.transform, false);
            slope.transform.localRotation = rotation;
            slope.transform.localPosition = (pLow + pHigh) * 0.5f - rotation * Vector3.up * (RampSlopeThickness * 0.5f);
            var box = slope.AddComponent<BoxCollider>();
            box.size = new Vector3(width, RampSlopeThickness, along.magnitude + treadDepth);
            float degrees = Vector3.Angle(dir, new Vector3(dir.x, 0f, dir.z));
            report.Warnings.Add($"{moduleId}: collision derived from {treads.Count} treads (rise {along.y:0.###} m over {Mathf.Abs(along.z):0.###} m, {degrees:0.#} deg) instead of the Godot placeholder cube");
            return 1;
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
            string relative = resPath.Substring(prefix.Length);
            string assetPath = "Assets/Content/Structural/" + relative;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            // KEEP ithappy wrappers may still name the v0 GLB path; only that kit may substitute.
            if (model == null
                && string.Equals(kitId, KitCatalog.ITHAPPY_KIT_ID, StringComparison.Ordinal)
                && relative.StartsWith("ship_structural_v0/", StringComparison.Ordinal))
            {
                string ithappyPath = "Assets/Content/Structural/ithappy/" + relative.Substring("ship_structural_v0/".Length);
                model = AssetDatabase.LoadAssetAtPath<GameObject>(ithappyPath);
                if (model != null) assetPath = ithappyPath;
            }
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

        static string ResolveWrapperPath(string kitId, string moduleId, GdDict module)
        {
            var candidates = new List<string>();
            string scene = module.GetString("godot_wrapper_scene");
            const string prefix = "res://scenes/wrappers/structural/";
            if (scene.StartsWith(prefix, StringComparison.Ordinal))
                candidates.Add(Path.Combine(RepoRoot, "fixtures", "godot_wrappers", scene.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar)));
            candidates.Add(Path.Combine(RepoRoot, "fixtures", "godot_wrappers", kitId, moduleId + ".tscn"));
            if (kitId.IndexOf("ithappy", StringComparison.OrdinalIgnoreCase) >= 0)
                candidates.Add(Path.Combine(RepoRoot, "fixtures", "godot_wrappers", "ithappy", moduleId + ".tscn"));
            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }

        static string ResolveContractPath(string kitId, string moduleId)
        {
            string root = Path.Combine(Application.streamingAssetsPath, "data", "placement", "contracts", "structural");
            foreach (string folder in new[] { kitId, "ship_structural_v0" })
            {
                string path = Path.Combine(root, folder, moduleId + "_contract.json");
                if (File.Exists(path)) return path;
            }
            return Path.Combine(root, kitId, moduleId + "_contract.json");
        }

        /// <summary>
        /// KEEP ithappy GLBs live under <c>Content/Structural/ithappy</c> (not a kit-id folder).
        /// Companion <c>{module_id}.asset.json</c> sits beside the GLB.
        /// </summary>
        static string ResolveStructuralContentRoot(string kitId)
        {
            string folder = string.Equals(kitId, KitCatalog.ITHAPPY_KIT_ID, StringComparison.Ordinal)
                ? "ithappy"
                : kitId;
            return Path.Combine(Application.dataPath, "Content", "Structural", folder);
        }

        static KitPrefabCatalog WriteCatalog(string kitId, double gridStep, List<KitPrefabCatalog.Entry> entries)
        {
            const string dir = "Assets/Resources/Catalogs";
            Directory.CreateDirectory(dir);
            string path = $"{dir}/KitCatalog_{kitId}.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>(path);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<KitPrefabCatalog>();
                AssetDatabase.CreateAsset(catalog, path);
            }
            catalog.kitId = kitId;
            catalog.gridStepMetres = (float)gridStep;
            catalog.modules = entries;
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            return catalog;
        }

        static void Finish(string kitId, Report report, bool strict, bool exitWhenDone, KitPrefabCatalog catalog)
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
