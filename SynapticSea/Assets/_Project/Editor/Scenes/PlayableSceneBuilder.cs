using System;
using System.Collections;
using System.IO;
using SynapticSea.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace SynapticSea.EditorTools.Scenes
{
    /// <summary>
    /// Writes <c>Assets/Scenes/Playable.unity</c>: one <see cref="PlayableBootstrap"/> object with its asset references
    /// (HUD and menu PanelSettings, the global volume profile) and neutral scene lighting. Everything else is built at
    /// runtime. Idempotent: the scene is regenerated from scratch every time. Does not touch Build Settings.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Scenes.PlayableSceneBuilder.Build
    /// </summary>
    public static class PlayableSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Playable.unity";
        public const string HudPanelSettingsPath = "Assets/Content/UI/PanelSettings_HUD.asset";
        public const string MenuPanelSettingsPath = "Assets/Content/UI/PanelSettings_Menu.asset";
        public const string GlobalVolumePath = "Assets/Settings/Volumes/SS_GlobalVolume.asset";

        [MenuItem("Synaptic Sea/Scenes/Build Playable Scene")]
        public static void BuildMenu() => BuildScene();

        public static void Build()
        {
            int code = 0;
            try
            {
                BuildScene();
            }
            catch (Exception e)
            {
                Debug.LogError("[PlayableSceneBuilder] FAIL " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static void BuildScene()
        {
            // Load after NewScene: a Single-mode new scene unloads assets nothing references yet.
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var hud = AssetDatabase.LoadAssetAtPath<PanelSettings>(HudPanelSettingsPath);
            var menu = AssetDatabase.LoadAssetAtPath<PanelSettings>(MenuPanelSettingsPath);
            var volume = AssetDatabase.LoadAssetAtPath<VolumeProfile>(GlobalVolumePath);
            if (hud == null || menu == null) throw new InvalidOperationException("PanelSettings assets missing");
            if (volume == null) throw new InvalidOperationException("global volume profile missing: " + GlobalVolumePath);
            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.05f, 0.05f, 0.07f, 1f);

            var go = new GameObject("PlayableBootstrap");
            var bootstrap = go.AddComponent<PlayableBootstrap>();
            var so = new SerializedObject(bootstrap);
            so.FindProperty("hudPanelSettings").objectReferenceValue = hud;
            so.FindProperty("menuPanelSettings").objectReferenceValue = menu;
            so.FindProperty("globalVolumeProfile").objectReferenceValue = volume;
            so.FindProperty("bootOnStart").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            if (bootstrap.HudPanelSettings != hud || bootstrap.MenuPanelSettings != menu || bootstrap.GlobalVolumeProfile != volume)
                throw new InvalidOperationException("bootstrap asset references did not stick");

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("could not save " + ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("[PlayableSceneBuilder] PASS scene=" + ScenePath);
        }
    }

    /// <summary>
    /// Batch capture of the running Playable scene with the HUD (needs a GPU: run WITHOUT -nographics):
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Scenes.PlayableScreenshotRunner.Run [-frames 90] [-out path]
    /// Opens the scene, enters play mode, waits for the boot and the given number of frames, writes
    /// <c>artifacts/screenshots/playable_hud.png</c> (1920×1080) and exits.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayableScreenshotRunner
    {
        const string StateKey = "SynapticSea.PlayableScreenshotRunner.Active";
        const string OutKey = "SynapticSea.PlayableScreenshotRunner.Out";
        const string FramesKey = "SynapticSea.PlayableScreenshotRunner.Frames";

        static PlayableScreenshotRunner()
        {
            if (!SessionState.GetBool(StateKey, false)) return;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (EditorApplication.isPlaying) StartCapture();
        }

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string outPath = Arg("-out") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "artifacts", "screenshots", "playable_hud.png"));
            SessionState.SetString(OutKey, outPath);
            SessionState.SetInt(FramesKey, int.TryParse(Arg("-frames"), out int f) ? f : 90);
            SessionState.SetBool(StateKey, true);
            if (!File.Exists(PlayableSceneBuilder.ScenePath)) PlayableSceneBuilder.BuildScene();
            EditorSceneManager.OpenScene(PlayableSceneBuilder.ScenePath, OpenSceneMode.Single);
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.EnterPlaymode();
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode) StartCapture();
        }

        static void StartCapture()
        {
            if (GameObject.Find("PlayableScreenshotDriver") != null) return;
            var driver = new GameObject("PlayableScreenshotDriver").AddComponent<CaptureDriver>();
            driver.OutPath = SessionState.GetString(OutKey, "playable_hud.png");
            driver.Frames = SessionState.GetInt(FramesKey, 90);
        }

        internal static void Finish(bool ok, string path)
        {
            SessionState.SetBool(StateKey, false);
            Debug.Log(ok ? "[PlayableScreenshotRunner] SCREENSHOT PASS out=" + path : "[PlayableScreenshotRunner] SCREENSHOT FAIL");
            EditorApplication.ExitPlaymode();
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        sealed class CaptureDriver : MonoBehaviour
        {
            public string OutPath;
            public int Frames;

            IEnumerator Start()
            {
                float deadline = Time.realtimeSinceStartup + 120f;
                while ((PlayableBootstrap.Current == null || !PlayableBootstrap.Current.IsBooted) && Time.realtimeSinceStartup < deadline)
                    yield return null;
                PlayableBootstrap boot = PlayableBootstrap.Current;
                if (boot == null || !boot.IsBooted)
                {
                    Finish(false, OutPath);
                    yield break;
                }
                // No threat clearing: the default start is a generated New Run whose encounters sit in their own rooms
                // (idle at boot), so the capture shows the live run exactly as a player gets it.
                for (int i = 0; i < Frames; i++) yield return null;
                bool ok = false;
                yield return PlayableCapture.Capture(boot, OutPath, 1920, 1080, r => ok = r);
                Finish(ok, OutPath);
            }
        }
    }
}
