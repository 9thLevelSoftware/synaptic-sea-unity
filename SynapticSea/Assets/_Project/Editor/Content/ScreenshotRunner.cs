using System;
using System.IO;
using System.Linq;
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
    /// Renders a Godot layout in Unity for visual review: builds the structural plan, applies the biome atmosphere and
    /// the global volume, and captures the locked-iso view plus a whole-ship overview to artifacts/screenshots/.
    /// Needs a GPU, so run it in batch mode WITHOUT -nographics:
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.ScreenshotRunner.Run [-layout procgen/golden/coherent_ship_001/layout.json] [-away]
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
            Capture(layout, args.Contains("-away"), exit: Application.isBatchMode);
        }

        static void Capture(string layoutRel, bool isAway, bool exit)
        {
            int code = 0;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var layout = GdJson.ParseDict(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "data", layoutRel)));
                var kit = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>("Assets/Resources/Catalogs/KitCatalog_ship_structural_v0.asset");

                var ship = new GameObject("Ship");
                var built = new StructuralLayoutBuilder().Build(layout, kit, ship.transform);
                if (built == null) throw new InvalidOperationException("structural build failed");

                string biomeId = layout.GetString("biome_id", "breach_field");
                var biome = GdJson.ParseDict(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "data", "procgen", "biomes", (biomeId.Length > 0 ? biomeId : "breach_field") + ".json")));
                var summary = AtmosphereApplier.Apply(ship.transform, biome?.GetDict("atmosphere"), isAway);

                var volumeGo = new GameObject("GlobalVolume");
                var volume = volumeGo.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/Volumes/SS_GlobalVolume.asset");

                var bounds = new Bounds(built.Modules[0].transform.position, Vector3.zero);
                foreach (var r in ship.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);

                // Ceilings hide the interior from the iso camera; Godot fades them near the player. Hide them for review.
                foreach (var m in built.Modules.Where(m => m.layer == "ceiling")) m.gameObject.SetActive(false);

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
                string stem = Path.GetFileName(Path.GetDirectoryName(layoutRel)) + (isAway ? "_away" : "");

                Render(cam, 1920, 1080, Path.Combine(outDir, stem + "_iso_gameplay.png"));
                cam.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.z) * 1.15f;
                Render(cam, 1920, 1080, Path.Combine(outDir, stem + "_iso_overview.png"));

                Debug.Log($"[ScreenshotRunner] SCREENSHOT PASS layout={layoutRel} modules={built.Modules.Count} atmosphere={GdJson.Stringify(summary)} out={Path.GetFullPath(outDir)}");
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
