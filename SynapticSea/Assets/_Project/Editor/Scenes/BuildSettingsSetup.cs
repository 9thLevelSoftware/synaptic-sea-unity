using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.EditorTools.Scenes
{
    /// <summary>
    /// The player scene list, in load order: Boot (index 0), Title, Playable. Scenes are listed by path; a scene that
    /// does not exist yet (Playable on a branch without it) is still listed, and <see cref="ExistingEnabledScenes"/>
    /// skips it with a warning at build time. Idempotent; the Builder calls <see cref="EnsureScenes"/> before building.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Scenes.BuildSettingsSetup.Apply -quit
    /// </summary>
    public static class BuildSettingsSetup
    {
        public const string PlayableScenePath = "Assets/Scenes/Playable.unity";

        public static readonly IReadOnlyList<string> ScenePaths = new[]
        {
            FrontEndSceneBuilder.BootScenePath,
            FrontEndSceneBuilder.TitleScenePath,
            PlayableScenePath,
        };

        [MenuItem("Synaptic Sea/Scenes/Apply Build Settings Scenes")]
        public static void Apply()
        {
            bool changed = EnsureScenes();
            AssetDatabase.SaveAssets();
            Debug.Log($"[BuildSettingsSetup] BUILD SCENES PASS changed={changed} scenes={string.Join(",", ScenePaths)}");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>Sets <see cref="EditorBuildSettings.scenes"/> to exactly <see cref="ScenePaths"/>. True when it changed.</summary>
        public static bool EnsureScenes()
        {
            var wanted = ScenePaths.Select(p => new EditorBuildSettingsScene(p, true)).ToArray();
            EditorBuildSettingsScene[] current = EditorBuildSettings.scenes;
            bool same = current.Length == wanted.Length;
            for (int i = 0; same && i < wanted.Length; i++)
                same = current[i].path == wanted[i].path && current[i].enabled && current[i].guid == wanted[i].guid;
            if (same) return false;
            EditorBuildSettings.scenes = wanted;
            return true;
        }

        /// <summary>Enabled Build Settings scenes whose asset exists; a missing one logs a warning and is skipped.</summary>
        public static string[] ExistingEnabledScenes()
        {
            var result = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (!scene.enabled) continue;
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) == null)
                {
                    Debug.LogWarning("[Builder] skipping missing scene " + scene.path);
                    continue;
                }
                result.Add(scene.path);
            }
            return result.ToArray();
        }
    }
}
