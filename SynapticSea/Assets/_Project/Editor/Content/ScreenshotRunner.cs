using System;
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
    /// wrappers, markers, portal panels, zones, props, room dressing, objective volumes), applies the biome atmosphere
    /// (breach_field when the layout names none) and the global volume, and captures the locked-iso view plus a
    /// whole-ship overview to artifacts/screenshots/.
    /// Needs a GPU, so run it in batch mode WITHOUT -nographics:
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.ScreenshotRunner.Run [-layout procgen/golden/coherent_ship_001/layout.json] [-slice path] [-stem name] [-away]
    /// </summary>
    public static class ScreenshotRunner
    {
        [MenuItem("Synaptic Sea/Content/Capture Layout Screenshots")]
        public static void RunMenu() => Capture("procgen/golden/coherent_ship_001/layout.json", false, exit: false);

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-layout");
            string layout = i >= 0 && i + 1 < args.Length ? args[i + 1] : "procgen/golden/coherent_ship_001/layout.json";
            int s = Array.IndexOf(args, "-slice");
            string slice = s >= 0 && s + 1 < args.Length ? args[s + 1] : null;
            int n = Array.IndexOf(args, "-stem");
            string stem = n >= 0 && n + 1 < args.Length ? args[n + 1] : null;
            Capture(layout, args.Contains("-away"), exit: Application.isBatchMode, slice, stem);
        }

        /// <param name="layoutRel">Path under StreamingAssets/data (e.g. procgen/golden/coherent_ship_001/layout.json) or an
        /// absolute layout path.</param>
        /// <param name="slicePath">Gameplay slice (absolute or data-relative); defaults to the layout folder's gameplay_slice.json.</param>
        static void Capture(string layoutRel, bool isAway, bool exit, string slicePath = null, string stemOverride = null)
        {
            int code = 0;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
                CatalogRegistry.Clear();

                // The full ship loader: structural wrappers plus markers, portals, zones, props, dressing and objectives.
                string layoutDir = Path.GetDirectoryName(layoutRel).Replace('\\', '/');
                string layoutPath = Path.IsPathRooted(layoutRel) ? layoutRel : "res://data/" + layoutRel;
                string sliceArg = slicePath ?? layoutDir + "/gameplay_slice.json";
                string sliceFull = Path.IsPathRooted(sliceArg) ? sliceArg : "res://data/" + sliceArg;
                var builder = ShipSceneBuilder.Create(null, "Ship");
                GdDict shipSummary = null;
                builder.ShipLoaded += s => shipSummary = s;
                if (!builder.LoadFromPaths(layoutPath, "res://data/kits/ship_structural_v0.json", sliceFull, isAway))
                    throw new InvalidOperationException("ship load failed");
                var view = builder.View;
                var ship = view.gameObject;

                // Godot applies atmosphere only for layouts that name a biome (the goldens do not, and render with
                // Godot's default environment). Unity has no default ambient/key light, so fall back to breach_field
                // for review renders of biome-less layouts.
                GdDict summary = view.AtmosphereSummary;
                if (summary == null)
                {
                    var biome = CatalogRegistry.LoadDict("res://data/procgen/biomes/breach_field.json");
                    summary = AtmosphereApplier.Apply(ship.transform, biome?.GetDict("atmosphere"), isAway);
                    summary["fallback_biome"] = "breach_field";
                }

                var volumeGo = new GameObject("GlobalVolume");
                var volume = volumeGo.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/Volumes/SS_GlobalVolume.asset");

                // Ceilings hide the interior from the iso camera; Godot fades them near the player. Hide them for review
                // (the Godot reference capture hides Ceiling_* too) and frame the remaining renderers.
                foreach (var m in view.Modules.Where(m => m.layer == "ceiling")) m.gameObject.SetActive(false);
                var bounds = new Bounds(view.Modules[0].transform.position, Vector3.zero);
                foreach (var r in ship.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);

                var focus = new GameObject("Focus").transform;
                focus.position = new Vector3(bounds.center.x, 0f, bounds.center.z);
                var rig = new GameObject("IsoCameraRig").AddComponent<IsoCameraRig>();
                var cam = rig.EnsureCamera();
                var camData = cam.GetUniversalAdditionalCameraData();
                camData.renderPostProcessing = true;
                camData.antialiasing = AntialiasingMode.None;
                rig.SetFollowTarget(focus);

                string outDir = Path.Combine(Application.dataPath, "..", "..", "artifacts", "screenshots");
                Directory.CreateDirectory(outDir);
                string stem = (stemOverride ?? Path.GetFileName(Path.GetDirectoryName(layoutRel))) + (isAway ? "_away" : "");

                Render(cam, 1920, 1080, Path.Combine(outDir, stem + "_iso_gameplay.png"));
                cam.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.z) * 1.15f;
                Render(cam, 1920, 1080, Path.Combine(outDir, stem + "_iso_overview.png"));

                Debug.Log($"[ScreenshotRunner] SCREENSHOT PASS layout={layoutRel} modules={view.Modules.Count} portals={view.GetAuthoredPortalNodes().Count} " +
                          $"landmarks={view.GetLandmarkNodes().Count} objectives={view.GetObjectiveVolumes().Count} dressing={view.GetDressingNodes().Count} " +
                          $"summary={GdJson.Stringify(shipSummary)} atmosphere={GdJson.Stringify(summary)} out={Path.GetFullPath(outDir)}");
            }
            catch (Exception e)
            {
                Debug.LogError("[ScreenshotRunner] FAIL " + e);
                code = 1;
            }
            if (exit) EditorApplication.Exit(code);
        }

        static void Render(Camera cam, int width, int height, string path)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            var previous = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.aspect = (float)width / height;
                // Warm-up frame: renderers created this frame reach the GPU Resident Drawer's instance data one render
                // late (the first frame draws every runtime cube with the first registered material).
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
