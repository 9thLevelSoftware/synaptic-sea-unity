using System;
using System.Collections.Generic;
using System.IO;
using SynapticSea.Core.Procgen;
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
    /// Locked-iso stills of every baked <c>ithappy_scifi_v0</c> structural prefab for Art Director style QA.
    /// Needs a GPU — run in batchmode WITHOUT <c>-nographics</c>:
    ///   F:\Unity\6000.6.0f1\Editor\Unity.exe -batchmode -projectPath SynapticSea
    ///     -executeMethod SynapticSea.EditorTools.Content.IthappyKitContactSheet.Run -quit
    ///     -logFile builds/logs/ithappy-contact.log
    /// Writes <c>artifacts/screenshots/ithappy_scifi_v0/contact_sheet.png</c> plus one PNG per module.
    /// </summary>
    public static class IthappyKitContactSheet
    {
        const string GlobalProfilePath = "Assets/Settings/Volumes/SS_GlobalVolume.asset";
        const int SheetWidth = 1920;
        const int SheetHeight = 1080;
        const int ModuleWidth = 1024;
        const int ModuleHeight = 1024;

        [MenuItem("Synaptic Sea/Content/Capture Ithappy Kit Contact Sheet")]
        public static void RunMenu() => Capture(exit: false);

        public static void Run() => Capture(exit: Application.isBatchMode);

        static void Capture(bool exit)
        {
            int code = 0;
            try
            {
                string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
                List<string> moduleIds = IthappyKitContactSheetSpec.ModuleIdsFromKit(Application.streamingAssetsPath);
                if (moduleIds.Count == 0)
                    throw new InvalidOperationException("ithappy_scifi_v0 kit has no modules");

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var root = new GameObject("IthappyContactSheet");
                AtmosphereApplier.Apply(root.transform, new GdDict
                {
                    { "fog_enabled", false },
                    { "ambient_energy", 0.45 },
                    { "key_light_energy", 0.55 },
                }, false);

                var volumeGo = new GameObject("GlobalVolume");
                var volume = volumeGo.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(GlobalProfilePath);

                var placed = new List<(string id, GameObject go)>();
                for (int i = 0; i < moduleIds.Count; i++)
                {
                    string id = moduleIds[i];
                    string assetPath = IthappyKitContactSheetSpec.PrefabAssetPath(id);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null || prefab.GetComponent<StructuralModule>() == null)
                        throw new InvalidOperationException("missing baked prefab " + assetPath + " (run StructuralPrefabBuilder -kit ithappy_scifi_v0)");
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
                    instance.name = id;
                    IthappyKitContactSheetSpec.GridOffset(i, out float x, out float z);
                    instance.transform.localPosition = new Vector3(x, 0f, z);
                    placed.Add((id, instance.gameObject));
                }

                var renderers = root.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0)
                    throw new InvalidOperationException("ithappy contact sheet has no renderers");
                var sheetBounds = new Bounds(renderers[0].bounds.center, Vector3.zero);
                foreach (var r in renderers) sheetBounds.Encapsulate(r.bounds);

                var focus = new GameObject("Focus").transform;
                focus.position = new Vector3(sheetBounds.center.x, 0f, sheetBounds.center.z);
                var rig = new GameObject("IsoCameraRig").AddComponent<IsoCameraRig>();
                Camera cam = rig.EnsureCamera();
                rig.SetShowCeilings(true);
                var camData = cam.GetUniversalAdditionalCameraData();
                camData.renderPostProcessing = true;
                camData.antialiasing = AntialiasingMode.None;
                rig.SetFollowTarget(focus);
                cam.orthographicSize = Mathf.Max(sheetBounds.extents.x, sheetBounds.extents.z) * 1.15f;
                rig.SyncToTarget();

                string outDir = IthappyKitContactSheetSpec.OutputDirectory(repoRoot);
                Directory.CreateDirectory(outDir);
                Render(cam, SheetWidth, SheetHeight, IthappyKitContactSheetSpec.ContactSheetPath(repoRoot));

                foreach (var (id, go) in placed)
                {
                    foreach (var other in placed)
                        other.go.SetActive(other.id == id);
                    var moduleRenderers = go.GetComponentsInChildren<Renderer>();
                    if (moduleRenderers.Length == 0) continue;
                    var b = new Bounds(moduleRenderers[0].bounds.center, Vector3.zero);
                    foreach (var r in moduleRenderers) b.Encapsulate(r.bounds);
                    focus.position = new Vector3(b.center.x, 0f, b.center.z);
                    cam.orthographicSize = Mathf.Max(Mathf.Max(b.extents.x, b.extents.z) * 1.35f, 3f);
                    rig.SyncToTarget();
                    Render(cam, ModuleWidth, ModuleHeight, IthappyKitContactSheetSpec.PerModulePath(repoRoot, id));
                }

                Debug.Log("[IthappyKitContactSheet] CONTACT SHEET PASS kit=" + KitCatalog.ITHAPPY_KIT_ID +
                          " modules=" + placed.Count + " out=" + outDir);
            }
            catch (Exception e)
            {
                Debug.LogError("[IthappyKitContactSheet] FAIL " + e);
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
                cam.backgroundColor = AtmosphereApplier.BackgroundColor;
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
