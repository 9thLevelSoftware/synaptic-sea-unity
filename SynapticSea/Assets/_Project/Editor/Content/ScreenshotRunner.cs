using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>
    /// Renders a Godot layout in Unity for visual review: loads it through <see cref="ShipSceneBuilder"/> (structural
    /// wrappers, markers, portal panels, zones, props, room dressing, objective volumes), applies the environment the
    /// Godot loader would have (the layout's biome atmosphere, or Godot's default environment when it names none), the
    /// global volume (ceilings hidden, as in interior play), and captures the locked-iso view plus a whole-ship
    /// overview to artifacts/screenshots/.
    /// Needs a GPU, so run it in batch mode WITHOUT -nographics:
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.ScreenshotRunner.Run
    ///     [-layout procgen/golden/coherent_ship_001/layout.json] [-slice path] [-stem name] [-away]
    ///     [-ceilings hide|show]   hide (default): interior play and the Godot reference captures; show: exterior review
    ///     [-env auto|breach_field] auto (default): biome atmosphere or Godot's default environment
    ///     [-vfx]                   spawn the four VFX prefabs at the focus (review aid)
    ///     [-hallucination 0..1]    hallucination FX intensity for the gameplay capture
    /// Calibration sweep (one load, several renders; see docs/port-status.md):
    ///   ... -executeMethod SynapticSea.EditorTools.Content.ScreenshotRunner.Sweep -layout ...
    ///     -configs "name:tonemap:dirScale:omniScale:ambientScale[:postExposure[:contrast[:saturation]]];..."   tonemap = aces|neutral|none
    /// </summary>
    public static class ScreenshotRunner
    {
        const string GlobalProfilePath = "Assets/Settings/Volumes/SS_GlobalVolume.asset";

        [MenuItem("Synaptic Sea/Content/Capture Layout Screenshots")]
        public static void RunMenu() => Capture(new Options { Layout = "procgen/golden/coherent_ship_001/layout.json" }, exit: false);

        sealed class Options
        {
            public string Layout = "procgen/golden/coherent_ship_001/layout.json";
            public string Slice;
            public string Stem;
            public bool Away;
            public bool ShowCeilings;
            public string Env = "auto";
            public string Configs;
            public bool Vfx;
            public double Hallucination;
        }

        sealed class Context
        {
            public ShipView View;
            public GdDict ShipSummary;
            public GdDict Atmosphere;       // the atmosphere block applied (null = Godot default environment)
            public GdDict AtmosphereSummary;
            public Camera Camera;
            public Bounds Bounds;
            public Volume Volume;
            public string OutDir;
            public string Stem;
        }

        static Options ParseArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            return new Options
            {
                Layout = Arg("-layout") ?? "procgen/golden/coherent_ship_001/layout.json",
                Slice = Arg("-slice"),
                Stem = Arg("-stem"),
                Away = args.Contains("-away"),
                ShowCeilings = Arg("-ceilings") == "show",
                Env = Arg("-env") ?? "auto",
                Configs = Arg("-configs"),
                Vfx = args.Contains("-vfx"),
                Hallucination = Arg("-hallucination") != null ? double.Parse(Arg("-hallucination"), CultureInfo.InvariantCulture) : 0.0,
            };
        }

        /// <summary>
        /// Review aid: the four Godot VFX in a row on the floor under screen point (0.72, 0.3) of the gameplay view
        /// (open floor in the review layouts), particles simulated for one second.
        /// </summary>
        static void SpawnVfxRow(Context ctx)
        {
            ctx.Camera.aspect = 16f / 9f;
            var ray = ctx.Camera.ViewportPointToRay(new Vector3(0.72f, 0.3f, 0f));
            var floor = new Plane(Vector3.up, new Vector3(0f, 0.12f, 0f));
            Vector3 hit = floor.Raycast(ray, out float distance) ? ray.GetPoint(distance) : ctx.Bounds.center;
            Vec3 focus = ctx.View.ToLocalGodot(hit);
            string[] ids = { VfxCatalog.TimedFire, VfxCatalog.BeaconBlue, VfxCatalog.ReactorGreen, VfxCatalog.BiomatterBlockage };
            for (int i = 0; i < ids.Length; i++)
            {
                var fx = VfxCatalog.Spawn(ids[i], ctx.View.transform, new Vec3(focus.X + (i - 1.5f) * 1.8f, focus.Y + 0.1f, focus.Z - (i - 1.5f) * 1.8f));
                if (fx == null) throw new InvalidOperationException("VFX missing: " + ids[i]);
                foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>()) ps.Simulate(1f, true, true);
            }
        }

        public static void Run() => Capture(ParseArgs(), exit: Application.isBatchMode);

        static void Capture(Options o, bool exit)
        {
            int code = 0;
            try
            {
                var ctx = Setup(o);
                if (o.Vfx) SpawnVfxRow(ctx);
                HallucinationFx.SetGlobalIntensity(o.Hallucination);
                Render(ctx.Camera, 1920, 1080, Path.Combine(ctx.OutDir, ctx.Stem + "_iso_gameplay.png"));
                HallucinationFx.SetGlobalIntensity(0.0);
                ctx.Camera.orthographicSize = Mathf.Max(ctx.Bounds.extents.x, ctx.Bounds.extents.z) * 1.15f;
                Render(ctx.Camera, 1920, 1080, Path.Combine(ctx.OutDir, ctx.Stem + "_iso_overview.png"));

                var view = ctx.View;
                Debug.Log($"[ScreenshotRunner] SCREENSHOT PASS layout={o.Layout} modules={view.Modules.Count} portals={view.GetAuthoredPortalNodes().Count} " +
                          $"landmarks={view.GetLandmarkNodes().Count} objectives={view.GetObjectiveVolumes().Count} dressing={view.GetDressingNodes().Count} " +
                          $"ceilings={(o.ShowCeilings ? "shown" : "hidden")} " +
                          $"summary={GdJson.Stringify(ctx.ShipSummary)} atmosphere={GdJson.Stringify(ctx.AtmosphereSummary)} out={Path.GetFullPath(ctx.OutDir)}");
            }
            catch (Exception e)
            {
                Debug.LogError("[ScreenshotRunner] FAIL " + e);
                code = 1;
            }
            if (exit) EditorApplication.Exit(code);
        }

        /// <summary>Renders the iso gameplay view once per calibration config (constants restored afterwards).</summary>
        public static void Sweep()
        {
            var o = ParseArgs();
            int code = 0;
            float dir0 = AtmosphereApplier.DirectionalEnergyScale, omni0 = AtmosphereApplier.OmniEnergyScale, amb0 = AtmosphereApplier.AmbientEnergyScale;
            try
            {
                var ctx = Setup(o);
                var profile = UnityEngine.Object.Instantiate(ctx.Volume.sharedProfile);
                ctx.Volume.profile = profile;
                var pointLights = ctx.View.GetComponentsInChildren<Light>(true).Where(l => l.type == LightType.Point)
                    .Select(l => (light: l, godotEnergy: l.intensity / omni0)).ToList();
                foreach (string config in (o.Configs ?? "base:aces:1:1:1").Split(';').Where(c => c.Trim().Length > 0))
                {
                    string[] p = config.Trim().Split(':');
                    float Num(int i, float fallback) => p.Length > i ? float.Parse(p[i], CultureInfo.InvariantCulture) : fallback;
                    AtmosphereApplier.DirectionalEnergyScale = Num(2, dir0);
                    AtmosphereApplier.OmniEnergyScale = Num(3, omni0);
                    AtmosphereApplier.AmbientEnergyScale = Num(4, amb0);
                    ApplyEnvironment(ctx, o);
                    foreach (var (light, energy) in pointLights) light.intensity = energy * AtmosphereApplier.OmniEnergyScale;
                    if (profile.TryGet(out Tonemapping tone))
                        tone.mode.Override(p.Length > 1 && p[1] == "none" ? TonemappingMode.None : p.Length > 1 && p[1] == "neutral" ? TonemappingMode.Neutral : TonemappingMode.ACES);
                    var adjustments = profile.TryGet(out ColorAdjustments ca) ? ca : profile.Add<ColorAdjustments>(true);
                    adjustments.postExposure.Override(Num(5, 0f));
                    if (p.Length > 6) adjustments.contrast.Override(Num(6, 0f));
                    if (p.Length > 7) adjustments.saturation.Override(Num(7, 0f));
                    ctx.Camera.backgroundColor = AtmosphereApplier.BackgroundColor;
                    Render(ctx.Camera, 1920, 1080, Path.Combine(ctx.OutDir, $"{ctx.Stem}_sweep_{p[0]}.png"));
                    Debug.Log($"[ScreenshotRunner] SWEEP {config}");
                }
                Debug.Log("[ScreenshotRunner] SWEEP PASS out=" + Path.GetFullPath(ctx.OutDir));
            }
            catch (Exception e)
            {
                Debug.LogError("[ScreenshotRunner] FAIL " + e);
                code = 1;
            }
            finally
            {
                AtmosphereApplier.DirectionalEnergyScale = dir0;
                AtmosphereApplier.OmniEnergyScale = omni0;
                AtmosphereApplier.AmbientEnergyScale = amb0;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static Context Setup(Options o)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CatalogRegistry.Clear();

            // The full ship loader: structural wrappers plus markers, portals, zones, props, dressing and objectives.
            string layoutDir = Path.GetDirectoryName(o.Layout).Replace('\\', '/');
            string layoutPath = Path.IsPathRooted(o.Layout) ? o.Layout : "res://data/" + o.Layout;
            string sliceArg = o.Slice ?? layoutDir + "/gameplay_slice.json";
            string sliceFull = Path.IsPathRooted(sliceArg) ? sliceArg : "res://data/" + sliceArg;
            var builder = ShipSceneBuilder.Create(null, "Ship");
            var ctx = new Context();
            builder.ShipLoaded += s => ctx.ShipSummary = s;
            if (!builder.LoadFromPaths(layoutPath, "res://data/kits/ship_structural_v0.json", sliceFull, o.Away))
                throw new InvalidOperationException("ship load failed");
            ctx.View = builder.View;
            ApplyEnvironment(ctx, o);

            var volumeGo = new GameObject("GlobalVolume");
            ctx.Volume = volumeGo.AddComponent<Volume>();
            ctx.Volume.isGlobal = true;
            ctx.Volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(GlobalProfilePath);

            // Frame every structural renderer (ceilings included; they sit inside the walls' footprint).
            var ship = ctx.View.gameObject;
            var bounds = new Bounds(ctx.View.Modules[0].transform.position, Vector3.zero);
            foreach (var r in ship.GetComponentsInChildren<Renderer>())
                if (r.gameObject.layer != PhysicsLayers.Ceiling) bounds.Encapsulate(r.bounds);
            ctx.Bounds = bounds;

            var focus = new GameObject("Focus").transform;
            focus.position = new Vector3(bounds.center.x, 0f, bounds.center.z);

            var rig = new GameObject("IsoCameraRig").AddComponent<IsoCameraRig>();
            ctx.Camera = rig.EnsureCamera();
            rig.SetShowCeilings(o.ShowCeilings);
            var camData = ctx.Camera.GetUniversalAdditionalCameraData();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.None;
            rig.SetFollowTarget(focus);

            ctx.OutDir = Path.Combine(Application.dataPath, "..", "..", "artifacts", "screenshots");
            Directory.CreateDirectory(ctx.OutDir);
            ctx.Stem = (o.Stem ?? Path.GetFileName(Path.GetDirectoryName(o.Layout))) + (o.Away ? "_away" : "");
            return ctx;
        }

        /// <summary>
        /// The loader applies the biome atmosphere itself when the layout names one. Otherwise Godot rendered with its
        /// default environment (clear-colour ambient, no key light, no fog); <c>-env breach_field</c> forces a biome for
        /// review renders.
        /// </summary>
        static void ApplyEnvironment(Context ctx, Options o)
        {
            string biomeId = V.Str(ctx.View.Layout.LayoutDoc.Get("biome_id", ""));
            if (o.Env != "auto" && o.Env.Length > 0) biomeId = o.Env;
            if (biomeId.Length == 0)
            {
                foreach (var l in ctx.View.GetComponentsInChildren<Light>(true))
                    if (l.type == LightType.Directional || l.name == AtmosphereApplier.AccentLightName) UnityEngine.Object.DestroyImmediate(l.gameObject);
                AtmosphereApplier.ApplyGodotDefaultEnvironment();
                ctx.Atmosphere = null;
                ctx.AtmosphereSummary = new GdDict { { "applied", false }, { "environment", "godot_default" } };
                return;
            }
            var biome = CatalogRegistry.LoadDict($"res://data/procgen/biomes/{biomeId}.json");
            ctx.Atmosphere = biome?.GetDict("atmosphere");
            ctx.AtmosphereSummary = AtmosphereApplier.Apply(ctx.View.transform, ctx.Atmosphere, o.Away);
            ctx.AtmosphereSummary["biome"] = biomeId;
        }

        static void Render(Camera cam, int width, int height, string path)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            var previous = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.aspect = (float)width / height;
                cam.backgroundColor = AtmosphereApplier.BackgroundColor;
                // Warm-up frame: the GPU Resident Drawer uploads instance data for renderers created (or re-materialed)
                // since the last render during that render, so a first-ever frame can draw them with stale material
                // batches. Games never notice (the next frame is right); captures render twice.
                cam.Render();
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = previous;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }
    }
}
