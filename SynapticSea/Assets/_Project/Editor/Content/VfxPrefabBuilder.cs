// Ported from scenes/vfx/{timed_fire,beacon_blue,reactor_green,biomatter_blockage}.tscn @ 96ecb2b0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>
    /// Builds Shuriken VFX prefabs from the Godot VFX scenes (copied to <c>fixtures/godot_vfx/</c>) and the
    /// <see cref="VfxCatalog"/> in <c>Resources/Catalogs</c>. Idempotent: prefabs and materials are rewritten in place,
    /// keeping their GUIDs.
    ///
    /// Mapping:
    /// <list type="bullet">
    /// <item>GPUParticles3D + ParticleProcessMaterial → ParticleSystem. <c>amount</c> → max particles and
    /// rate = amount / lifetime (Godot emits <c>amount</c> per <c>lifetime</c>); <c>lifetime</c> → start lifetime;
    /// <c>preprocess</c> &gt; 0 → prewarm; <c>direction</c> + <c>spread</c> → point cone; <c>initial_velocity_min/max</c> →
    /// start speed; <c>gravity</c> → world-space force over lifetime (exact, independent of Physics.gravity);
    /// <c>scale_min/max</c> × the draw-pass sphere diameter → start size; <c>scale_curve</c> → size over lifetime;
    /// <c>color</c> → start colour; <c>color_ramp</c> → colour over lifetime; simulation in world space (Godot
    /// <c>local_coords = false</c>). The draw-pass SphereMesh is rendered as a mesh particle with an unlit transparent
    /// URP particle material (albedo × vertex colour, plus emission × energy). Godot's <c>randomness</c> (emission
    /// timing jitter) has no Shuriken equivalent and is dropped.</item>
    /// <item>MeshInstance3D + SphereMesh + StandardMaterial3D → Unity sphere scaled to (2r, h, 2r) with a URP Lit
    /// material (albedo, metallic, roughness → smoothness, emission × energy). Subsurface scattering is dropped.</item>
    /// <item>OmniLight3D → point light (colour, range, no shadows); intensity is set by <see cref="VfxEffect"/> from the
    /// Godot energy × <see cref="AtmosphereApplier.OmniEnergyScale"/>.</item>
    /// <item>AnimationPlayer autoplay → <see cref="VfxEffect"/> tracks (linear keys on light energy / emission energy).</item>
    /// </list>
    /// Positions go through <see cref="Frame"/>.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.VfxPrefabBuilder.BuildAll -quit
    /// </summary>
    public static class VfxPrefabBuilder
    {
        public const string PrefabDir = "Assets/Content/VFX";
        public const string MaterialDir = "Assets/Content/VFX/Materials";
        public const string CatalogPath = "Assets/Resources/Catalogs/VfxCatalog.asset";

        /// <summary>id (scene file name) and the gameplay element the Godot scene was authored for.</summary>
        public static readonly (string id, string usage)[] Scenes =
        {
            (VfxCatalog.TimedFire, "Fire hazard zones: layout fire_zones of kind \"timed_fire\" (ShipView.GetFireZoneSpecs / GetFireZoneMarkers). Not instanced by Godot at 96ecb2b0."),
            (VfxCatalog.BeaconBlue, "Blue landmark glow (landmarks with color \"blue\", the EntryBeacon affordance; metadata glow_variant). Not instanced by Godot at 96ecb2b0."),
            (VfxCatalog.ReactorGreen, "Green reactor-core glow (landmark \"reactor_green_core\", the DestinationReactorCore affordance; metadata glow_variant). Not instanced by Godot at 96ecb2b0."),
            (VfxCatalog.BiomatterBlockage, "Biomatter route blockage (BlockedBiomatter affordance / blocked_links). Not instanced by Godot at 96ecb2b0."),
        };

        const int PropLayer = 11;
        const string ParticleShader = "Universal Render Pipeline/Particles/Unlit";
        const string LitShader = "Universal Render Pipeline/Lit";

        static readonly Regex HeaderRx = new Regex(@"^\[(?<kind>sub_resource|node|ext_resource|gd_scene)(?<attrs>[^\]]*)\]\s*$", RegexOptions.Multiline);
        static readonly Regex AttrRx = new Regex(@"(?<k>\w+)=(?:""(?<v>[^""]*)""|(?<v>[^\s\]]+))");

        sealed class Block
        {
            public string Kind;
            public Dictionary<string, string> Attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> Props = new Dictionary<string, string>(StringComparer.Ordinal);
            public string Get(string key, string fallback = null) => Props.TryGetValue(key, out string v) ? v : fallback;
        }

        sealed class Scene
        {
            public Dictionary<string, Block> SubResources = new Dictionary<string, Block>(StringComparer.Ordinal);
            public List<Block> Nodes = new List<Block>();
        }

        [MenuItem("Synaptic Sea/Content/Build VFX Prefabs")]
        public static void BuildAllMenu() => Build(exitWhenDone: false);

        public static void BuildAll() => Build(exitWhenDone: Application.isBatchMode);

        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        public static VfxCatalog Build(bool exitWhenDone)
        {
            var errors = new List<string>();
            var entries = new List<VfxCatalog.Entry>();
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(MaterialDir);
            foreach (var (id, usage) in Scenes)
            {
                string tscnPath = Path.Combine(RepoRoot, "fixtures", "godot_vfx", id + ".tscn");
                try
                {
                    if (!File.Exists(tscnPath)) throw new FileNotFoundException("Godot VFX scene missing", tscnPath);
                    var prefab = BuildScene(id, Parse(File.ReadAllText(tscnPath)));
                    entries.Add(new VfxCatalog.Entry { id = id, prefab = prefab, godotScene = $"res://scenes/vfx/{id}.tscn", usage = usage });
                }
                catch (Exception e)
                {
                    errors.Add($"{id}: {e.GetType().Name}: {e.Message}");
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
            var catalog = AssetDatabase.LoadAssetAtPath<VfxCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<VfxCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            catalog.entries = entries;
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            foreach (var e in errors) Debug.LogError("[VfxPrefabBuilder] " + e);
            Debug.Log($"[VfxPrefabBuilder] VFX PREFABS {(errors.Count == 0 ? "PASS" : "FAIL")} prefabs={entries.Count} errors={errors.Count} ids={string.Join(",", entries.Select(x => x.id))}");
            if (exitWhenDone) EditorApplication.Exit(errors.Count == 0 ? 0 : 1);
            return catalog;
        }

        // ------------------------------------------------------------------ tscn parsing

        static Scene Parse(string text)
        {
            var scene = new Scene();
            var headers = HeaderRx.Matches(text).Cast<Match>().ToList();
            for (int i = 0; i < headers.Count; i++)
            {
                var block = new Block { Kind = headers[i].Groups["kind"].Value };
                foreach (Match a in AttrRx.Matches(headers[i].Groups["attrs"].Value)) block.Attrs[a.Groups["k"].Value] = a.Groups["v"].Value;
                int start = headers[i].Index + headers[i].Length;
                int end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
                ParseProps(text.Substring(start, end - start), block.Props);
                if (block.Kind == "sub_resource") scene.SubResources[block.Attrs["id"]] = block;
                else if (block.Kind == "node") scene.Nodes.Add(block);
            }
            return scene;
        }

        /// <summary><c>key = value</c> lines; a value opening <c>{</c> or <c>[</c> continues until its brackets balance.</summary>
        static void ParseProps(string body, Dictionary<string, string> props)
        {
            var lines = body.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int eq = lines[i].IndexOf(" = ", StringComparison.Ordinal);
                if (eq <= 0) continue;
                string key = lines[i].Substring(0, eq).Trim();
                string value = lines[i].Substring(eq + 3);
                int depth = Balance(value);
                while (depth > 0 && i + 1 < lines.Length)
                {
                    value += "\n" + lines[++i];
                    depth = Balance(value);
                }
                props[key] = value.Trim();
            }
        }

        static int Balance(string s) => s.Count(c => c == '{' || c == '[' || c == '(') - s.Count(c => c == '}' || c == ']' || c == ')');

        static float F(string s, float fallback = 0f) =>
            s != null && float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

        static float[] Numbers(string csv) =>
            csv.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).Select(p => F(p)).ToArray();

        static float[] Call(string value, string fn)
        {
            if (value == null) return null;
            var m = Regex.Match(value, fn + @"\((?<a>[^)]*)\)");
            return m.Success ? Numbers(m.Groups["a"].Value) : null;
        }

        static Color Col(string value, Color fallback)
        {
            var c = Call(value, "Color");
            return c != null && c.Length >= 3 ? new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f) : fallback;
        }

        static Vec3 V3(string value, Vec3 fallback)
        {
            var v = Call(value, "Vector3");
            return v != null && v.Length == 3 ? new Vec3(v[0], v[1], v[2]) : fallback;
        }

        static string Ref(string value)
        {
            if (value == null) return null;
            var m = Regex.Match(value, @"SubResource\(""(?<id>[^""]+)""\)");
            return m.Success ? m.Groups["id"].Value : null;
        }

        static string Str(string value) => value == null ? null : value.Trim().Trim('"').TrimStart('&').Trim('"');

        // ------------------------------------------------------------------ build

        static GameObject BuildScene(string id, Scene scene)
        {
            var rootNode = scene.Nodes.First(n => !n.Attrs.ContainsKey("parent"));
            var root = new GameObject(rootNode.Attrs["name"]) { layer = PropLayer };
            try
            {
                var effect = root.AddComponent<VfxEffect>();
                effect.vfxId = id;
                effect.godotScene = $"res://scenes/vfx/{id}.tscn";
                var byPath = new Dictionary<string, GameObject>(StringComparer.Ordinal);

                foreach (var node in scene.Nodes)
                {
                    if (!node.Attrs.TryGetValue("parent", out string parent)) continue;
                    string name = node.Attrs["name"];
                    string type = node.Attrs.TryGetValue("type", out string t) ? t : "";
                    Transform parentTransform = parent == "." ? root.transform : byPath.TryGetValue(parent, out GameObject p) ? p.transform : root.transform;
                    if (type == "AnimationPlayer")
                    {
                        BuildAnimation(scene, node, effect, byPath);
                        continue;
                    }
                    var go = new GameObject(name) { layer = PropLayer };
                    go.transform.SetParent(parentTransform, false);
                    go.transform.localPosition = Frame.ToUnity(V3(node.Get("position"), Vec3.Zero));
                    var s = V3(node.Get("scale"), new Vec3(1f, 1f, 1f));
                    go.transform.localScale = new Vector3(s.X, s.Y, s.Z);
                    byPath[parent == "." ? name : parent + "/" + name] = go;

                    switch (type)
                    {
                        case "GPUParticles3D": BuildParticles(id, scene, node, go); break;
                        case "MeshInstance3D": BuildMesh(id, scene, node, go, effect); break;
                        case "OmniLight3D": BuildOmniLight(node, go, effect); break;
                    }
                }

                string path = $"{PrefabDir}/{id}.prefab";
                var saved = PrefabUtility.SaveAsPrefabAsset(root, path, out bool ok);
                if (!ok || saved == null) throw new InvalidOperationException($"failed to save {path}");
                return saved;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static void BuildParticles(string id, Scene scene, Block node, GameObject go)
        {
            var process = scene.SubResources.TryGetValue(Ref(node.Get("process_material")) ?? "", out Block pm) ? pm : new Block();
            var drawMesh = scene.SubResources.TryGetValue(Ref(node.Get("draw_pass_1")) ?? "", out Block dm) ? dm : null;
            float amount = F(node.Get("amount"), 8f);
            float lifetime = F(node.Get("lifetime"), 1f);
            float preprocess = F(node.Get("preprocess"), 0f);
            float radius = drawMesh != null ? F(drawMesh.Get("radius"), 0.5f) : 0.5f;
            float height = drawMesh != null ? F(drawMesh.Get("height"), radius * 2f) : 1f;
            float diameter = Mathf.Max(radius * 2f, height);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = lifetime;
            main.loop = node.Get("one_shot") != "true";
            main.prewarm = preprocess > 0f;
            main.playOnAwake = node.Get("emitting", "true") != "false";
            main.startLifetime = lifetime;
            main.startSpeed = new ParticleSystem.MinMaxCurve(F(process.Get("initial_velocity_min"), 0f), F(process.Get("initial_velocity_max"), 0f));
            main.startSize = new ParticleSystem.MinMaxCurve(diameter * F(process.Get("scale_min"), 1f), diameter * F(process.Get("scale_max"), 1f));
            main.startColor = Col(process.Get("color"), Color.white);
            main.maxParticles = Mathf.Max(1, Mathf.RoundToInt(amount));
            main.simulationSpace = node.Get("local_coords") == "true" ? ParticleSystemSimulationSpace.Local : ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.gravityModifier = 0f;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = amount / Mathf.Max(lifetime, 1e-3f);

            // Direction + spread from a point (Godot's default emission shape).
            Vector3 direction = Frame.ToUnity(V3(process.Get("direction"), new Vec3(1f, 0f, 0f)));
            float spread = F(process.Get("spread"), 45f);
            var shape = ps.shape;
            shape.enabled = true;
            string emissionShape = process.Get("emission_shape", "0");
            if (emissionShape == "1" || emissionShape == "2")
            {
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = F(process.Get("emission_sphere_radius"), 1f);
            }
            else if (emissionShape == "3")
            {
                shape.shapeType = ParticleSystemShapeType.Box;
                var e = V3(process.Get("emission_box_extents"), new Vec3(1f, 1f, 1f));
                shape.scale = new Vector3(e.X * 2f, e.Y * 2f, e.Z * 2f);
            }
            else if (spread >= 90f)
            {
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 0.001f;
            }
            else
            {
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = spread;
                shape.radius = 0.0001f;
                shape.radiusThickness = 1f;
            }
            shape.rotation = Quaternion.FromToRotation(Vector3.forward, direction.sqrMagnitude > 0f ? direction.normalized : Vector3.up).eulerAngles;

            var gravity = Frame.ToUnity(V3(process.Get("gravity"), new Vec3(0f, -9.8f, 0f)));
            var force = ps.forceOverLifetime;
            force.enabled = gravity.sqrMagnitude > 0f;
            force.space = ParticleSystemSimulationSpace.World;
            force.x = gravity.x;
            force.y = gravity.y;
            force.z = gravity.z;

            var ramp = RampGradient(scene, process.Get("color_ramp"));
            var col = ps.colorOverLifetime;
            col.enabled = ramp != null;
            if (ramp != null) col.color = new ParticleSystem.MinMaxGradient(ramp);

            var scaleCurve = ScaleCurve(scene, process.Get("scale_curve"));
            var size = ps.sizeOverLifetime;
            size.enabled = scaleCurve != null;
            if (scaleCurve != null) size.size = new ParticleSystem.MinMaxCurve(1f, scaleCurve);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.mesh = SphereMesh();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var matBlock = drawMesh != null && scene.SubResources.TryGetValue(Ref(drawMesh.Get("material")) ?? "", out Block mb) ? mb : null;
            renderer.sharedMaterial = ParticleMaterial(id, go.name, matBlock);
        }

        static Gradient RampGradient(Scene scene, string rampRef)
        {
            if (!scene.SubResources.TryGetValue(Ref(rampRef) ?? "", out Block texture)) return null;
            if (!scene.SubResources.TryGetValue(Ref(texture.Get("gradient")) ?? "", out Block gradient)) return null;
            float[] offsets = Call(gradient.Get("offsets"), "PackedFloat32Array") ?? new[] { 0f, 1f };
            float[] colors = Call(gradient.Get("colors"), "PackedColorArray") ?? new[] { 0f, 0f, 0f, 1f, 1f, 1f, 1f, 1f };
            int n = Mathf.Min(8, Mathf.Min(offsets.Length, colors.Length / 4));
            var ck = new GradientColorKey[n];
            var ak = new GradientAlphaKey[n];
            for (int i = 0; i < n; i++)
            {
                ck[i] = new GradientColorKey(new Color(colors[i * 4], colors[i * 4 + 1], colors[i * 4 + 2]), offsets[i]);
                ak[i] = new GradientAlphaKey(colors[i * 4 + 3], offsets[i]);
            }
            var g = new Gradient { mode = GradientMode.Blend };
            g.SetKeys(ck, ak);
            return g;
        }

        /// <summary>CurveTexture → Curve <c>_data = [Vector2(x, y), left, right, lmode, rmode, ...]</c>.</summary>
        static AnimationCurve ScaleCurve(Scene scene, string curveRef)
        {
            if (!scene.SubResources.TryGetValue(Ref(curveRef) ?? "", out Block texture)) return null;
            var curve = scene.SubResources.TryGetValue(Ref(texture.Get("curve")) ?? "", out Block c) ? c : null;
            string data = curve?.Get("_data");
            if (data == null) return null;
            var points = Regex.Matches(data, @"Vector2\((?<x>[^,]+),(?<y>[^)]+)\)").Cast<Match>().Select(m => new Keyframe(F(m.Groups["x"].Value), F(m.Groups["y"].Value))).ToArray();
            if (points.Length == 0) return null;
            var anim = new AnimationCurve(points);
            for (int i = 0; i < anim.length; i++) AnimationUtility.SetKeyLeftTangentMode(anim, i, AnimationUtility.TangentMode.Linear);
            for (int i = 0; i < anim.length; i++) AnimationUtility.SetKeyRightTangentMode(anim, i, AnimationUtility.TangentMode.Linear);
            return anim;
        }

        static void BuildMesh(string id, Scene scene, Block node, GameObject go, VfxEffect effect)
        {
            var meshBlock = scene.SubResources.TryGetValue(Ref(node.Get("mesh")) ?? "", out Block mb) ? mb : null;
            var materialBlock = scene.SubResources.TryGetValue(Ref(node.Get("material_override")) ?? Ref(meshBlock?.Get("material")) ?? "", out Block mat) ? mat : null;
            var child = new GameObject("Mesh") { layer = PropLayer };
            child.transform.SetParent(go.transform, false);
            if (meshBlock != null && meshBlock.Attrs.TryGetValue("type", out string meshType) && meshType == "SphereMesh")
            {
                float r = F(meshBlock.Get("radius"), 0.5f);
                float h = F(meshBlock.Get("height"), 1f);
                child.transform.localScale = new Vector3(r * 2f, h, r * 2f);
            }
            child.AddComponent<MeshFilter>().sharedMesh = SphereMesh();
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = LitMaterial(id, materialBlock);
            renderer.shadowCastingMode = ShadowCastingMode.On;
            if (materialBlock != null && materialBlock.Get("emission_enabled") == "true")
                effect.emissions.Add(new VfxEffect.EmissionBinding { renderer = renderer, godotEmission = Col(materialBlock.Get("emission"), Color.black) });
        }

        static void BuildOmniLight(Block node, GameObject go, VfxEffect effect)
        {
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = Col(node.Get("light_color"), Color.white);
            light.range = F(node.Get("omni_range"), 5f);
            float energy = F(node.Get("light_energy"), 1f);
            light.intensity = energy * AtmosphereApplier.OmniEnergyScale;
            light.shadows = node.Get("shadow_enabled") == "true" ? LightShadows.Soft : LightShadows.None;
            effect.lights.Add(new VfxEffect.LightBinding { light = light, godotEnergy = energy });
        }

        static void BuildAnimation(Scene scene, Block player, VfxEffect effect, Dictionary<string, GameObject> byPath)
        {
            string autoplay = Str(player.Get("autoplay"));
            if (string.IsNullOrEmpty(autoplay)) return;
            string libraries = player.Get("libraries") ?? "";
            var libMatch = Regex.Match(libraries, @"SubResource\(""(?<id>[^""]+)""\)");
            if (!libMatch.Success || !scene.SubResources.TryGetValue(libMatch.Groups["id"].Value, out Block library)) return;
            var animMatch = Regex.Match(library.Get("_data") ?? "", "&\"" + Regex.Escape(autoplay) + @""":\s*SubResource\(""(?<id>[^""]+)""\)");
            if (!animMatch.Success || !scene.SubResources.TryGetValue(animMatch.Groups["id"].Value, out Block anim)) return;

            effect.animationName = autoplay;
            effect.animationLength = F(anim.Get("length"), 1f);
            effect.animationLoops = anim.Get("loop_mode", "0") != "0";
            effect.tracks.Clear();
            for (int i = 0; anim.Props.ContainsKey($"tracks/{i}/type"); i++)
            {
                var pathMatch = Regex.Match(anim.Get($"tracks/{i}/path") ?? "", @"NodePath\(""(?<p>[^""]+)""\)");
                if (!pathMatch.Success) continue;
                string[] parts = pathMatch.Groups["p"].Value.Split(':');
                if (!byPath.TryGetValue(parts[0], out GameObject target)) continue;
                string property = parts[parts.Length - 1];
                string keys = anim.Get($"tracks/{i}/keys") ?? "";
                var times = Regex.Match(keys, @"""times"":\s*PackedFloat32Array\((?<a>[^)]*)\)");
                var values = Regex.Match(keys, @"""values"":\s*\[(?<a>[^\]]*)\]");
                if (!times.Success || !values.Success) continue;
                var track = new VfxEffect.Track { times = Numbers(times.Groups["a"].Value), values = Numbers(values.Groups["a"].Value) };
                if (property == "light_energy")
                {
                    track.target = VfxEffect.TrackTarget.LightEnergy;
                    track.component = target.GetComponent<Light>();
                }
                else if (property == "emission_energy_multiplier")
                {
                    track.target = VfxEffect.TrackTarget.EmissionEnergy;
                    track.component = target.GetComponentInChildren<Renderer>();
                }
                else continue;
                if (track.component != null && track.times.Length == track.values.Length && track.times.Length > 0) effect.tracks.Add(track);
            }
        }

        // ------------------------------------------------------------------ assets

        static Mesh _sphere;

        /// <summary>Unity's built-in unit-diameter sphere (a persistent asset, so prefabs can reference it).</summary>
        static Mesh SphereMesh()
        {
            if (_sphere != null) return _sphere;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphere = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(go);
            return _sphere;
        }

        static Material LoadOrCreate(string path, string shaderName)
        {
            var shader = Shader.Find(shaderName) ?? throw new InvalidOperationException($"shader {shaderName} not found");
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            else
            {
                m.shader = shader;
                m.shaderKeywords = Array.Empty<string>();
            }
            return m;
        }

        /// <summary>StandardMaterial3D (unshaded, alpha, vertex colour as albedo) → URP Particles/Unlit, transparent.</summary>
        static Material ParticleMaterial(string id, string emitter, Block block)
        {
            var m = LoadOrCreate($"{MaterialDir}/VFX_{id}_{emitter}.mat", ParticleShader);
            Color albedo = block != null ? Col(block.Get("albedo_color"), Color.white) : Color.white;
            m.SetColor("_BaseColor", albedo);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_ColorMode", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", (float)CullMode.Back);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            ApplyEmission(m, block);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>StandardMaterial3D → URP Lit (roughness 1 - smoothness, emission × energy).</summary>
        static Material LitMaterial(string id, Block block)
        {
            string name = block != null ? block.Attrs["id"] : "Default";
            var m = LoadOrCreate($"{MaterialDir}/VFX_{id}_{name}.mat", LitShader);
            m.SetColor("_BaseColor", block != null ? Col(block.Get("albedo_color"), Color.white) : Color.white);
            m.SetFloat("_Metallic", block != null ? F(block.Get("metallic"), 0f) : 0f);
            m.SetFloat("_Smoothness", 1f - (block != null ? F(block.Get("roughness"), 1f) : 1f));
            ApplyEmission(m, block);
            EditorUtility.SetDirty(m);
            return m;
        }

        static void ApplyEmission(Material m, Block block)
        {
            if (block == null || block.Get("emission_enabled") != "true")
            {
                m.DisableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", Color.black);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                return;
            }
            m.EnableKeyword("_EMISSION");
            // URP's material validation re-derives _EMISSION from these flags on import; None would strip it.
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.SetColor("_EmissionColor", VfxEffect.EmissionColor(Col(block.Get("emission"), Color.black), F(block.Get("emission_energy_multiplier"), 1f)));
        }
    }
}
