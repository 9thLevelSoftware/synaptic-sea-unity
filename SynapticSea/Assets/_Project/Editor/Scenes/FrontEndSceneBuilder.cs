using System.IO;
using SynapticSea.App;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace SynapticSea.EditorTools.Scenes
{
    /// <summary>
    /// Writes the front-end scenes: <c>Boot.unity</c> (composes <see cref="AppServices"/>, then loads Title) and
    /// <c>Title.unity</c> (camera + menu UIDocument + <see cref="TitleScreen"/>), then the Build Settings scene list.
    /// Idempotent: existing scenes are opened and reconciled by object name, so re-running changes nothing.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Scenes.FrontEndSceneBuilder.Apply -quit
    /// </summary>
    public static class FrontEndSceneBuilder
    {
        public const string BootScenePath = "Assets/Scenes/Boot.unity";
        public const string TitleScenePath = "Assets/Scenes/Title.unity";
        public const string MenuPanelSettingsPath = "Assets/Content/UI/PanelSettings_Menu.asset";

        /// <summary>Godot <c>environment/defaults/default_clear_color</c>.</summary>
        public static readonly Color ClearColor = new Color(0.05f, 0.05f, 0.07f, 1f);

        [MenuItem("Synaptic Sea/Scenes/Build Front End Scenes")]
        public static void Apply()
        {
            bool ok = BuildAll();
            Debug.Log($"[FrontEndSceneBuilder] FRONT END SCENES {(ok ? "PASS" : "FAIL")} boot={BootScenePath} title={TitleScenePath}");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        public static bool BuildAll()
        {
            if (EditorSceneManager.GetActiveScene().isDirty && !Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return false;
            Directory.CreateDirectory("Assets/Scenes");
            bool ok = BuildBoot() && BuildTitle();
            BuildSettingsSetup.EnsureScenes();
            AssetDatabase.SaveAssets();
            return ok;
        }

        static Scene OpenOrCreate(string path)
        {
            return File.Exists(path)
                ? EditorSceneManager.OpenScene(path, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        static GameObject Root(Scene scene, string name)
        {
            foreach (GameObject go in scene.GetRootGameObjects())
            {
                if (go.name == name) return go;
            }
            return new GameObject(name);
        }

        static T Ensure<T>(GameObject go) where T : Component
        {
            var component = go.GetComponent<T>();
            return component != null ? component : go.AddComponent<T>();
        }

        static bool Save(Scene scene, string path)
        {
            if (File.Exists(path) && !scene.isDirty) return true;
            return EditorSceneManager.SaveScene(scene, path);
        }

        static bool BuildBoot()
        {
            Scene scene = OpenOrCreate(BootScenePath);
            GameObject boot = Root(scene, "Boot");
            if (boot.GetComponent<BootLoader>() == null)
            {
                boot.AddComponent<BootLoader>();
                EditorSceneManager.MarkSceneDirty(scene);
            }
            return Save(scene, BootScenePath);
        }

        static bool BuildTitle()
        {
            // Load assets after opening the scene: a Single-mode open unloads unused assets loaded before it.
            Scene scene = OpenOrCreate(TitleScenePath);
            var panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(MenuPanelSettingsPath);
            if (panelSettings == null)
            {
                Debug.LogError("[FrontEndSceneBuilder] missing " + MenuPanelSettingsPath + " (run Bootstrap/Create UI Panel Settings)");
                return false;
            }

            GameObject cameraGo = Root(scene, "Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = Ensure<Camera>(cameraGo);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = ClearColor;
            camera.orthographic = true;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 10f;
            var cameraData = Ensure<UniversalAdditionalCameraData>(cameraGo);
            cameraData.renderPostProcessing = false;
            cameraData.renderShadows = false;
            EditorUtility.SetDirty(camera);
            EditorUtility.SetDirty(cameraData);

            GameObject title = Root(scene, "TitleScreen");
            var document = Ensure<UIDocument>(title);
            // Through the serialized property: the runtime setter does not persist the reference outside play mode.
            var so = new SerializedObject(document);
            so.FindProperty("m_PanelSettings").objectReferenceValue = panelSettings;
            so.ApplyModifiedPropertiesWithoutUndo();
            if (document.panelSettings != panelSettings)
            {
                Debug.LogError("[FrontEndSceneBuilder] could not assign " + MenuPanelSettingsPath + " to the title UIDocument");
                return false;
            }
            Ensure<TitleScreen>(title);
            EditorUtility.SetDirty(document);

            EditorSceneManager.MarkSceneDirty(scene);
            return EditorSceneManager.SaveScene(scene, TitleScenePath);
        }
    }
}
