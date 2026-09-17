using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.EditorTools.Bootstrap
{
    /// <summary>
    /// Removes the URP / Input System template leftovers (plan G). Idempotent:
    /// <list type="bullet">
    /// <item>the project-wide input actions (<c>com.unity.input.settings.actions</c>) become
    /// <c>Assets/Content/Input/SynapticSea.inputactions</c>, and the template <c>Assets/InputSystem_Actions.inputactions</c>
    /// is deleted once nothing else references it;</item>
    /// <item><c>PC_RPAsset</c> and <c>Mobile_RPAsset</c> use <c>SS_GlobalVolume</c> as their default volume profile, and
    /// <c>Assets/Settings/SampleSceneProfile.asset</c> is deleted once unreferenced.</item>
    /// </list>
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Bootstrap.TemplateCleanup.Apply -quit
    /// </summary>
    public static class TemplateCleanup
    {
        public const string InputActionsConfigKey = "com.unity.input.settings.actions";
        public const string GameActionsPath = "Assets/Content/Input/SynapticSea.inputactions";
        public const string TemplateActionsPath = "Assets/InputSystem_Actions.inputactions";
        public const string TemplateProfilePath = "Assets/Settings/SampleSceneProfile.asset";
        public const string GlobalProfilePath = "Assets/Settings/Volumes/SS_GlobalVolume.asset";
        static readonly string[] PipelineAssets = { "Assets/Settings/PC_RPAsset.asset", "Assets/Settings/Mobile_RPAsset.asset" };

        [MenuItem("Synaptic Sea/Bootstrap/Remove Template Leftovers")]
        public static void Apply()
        {
            bool ok = true;

            Object actions = AssetDatabase.LoadMainAssetAtPath(GameActionsPath);
            if (actions == null)
            {
                Debug.LogError("[TemplateCleanup] missing " + GameActionsPath);
                ok = false;
            }
            else
            {
                EditorBuildSettings.TryGetConfigObject(InputActionsConfigKey, out Object current);
                if (current != actions) EditorBuildSettings.AddConfigObject(InputActionsConfigKey, actions, true);
            }

            Object profile = AssetDatabase.LoadMainAssetAtPath(GlobalProfilePath);
            if (profile == null)
            {
                Debug.LogError("[TemplateCleanup] missing " + GlobalProfilePath);
                ok = false;
            }
            else
            {
                foreach (string path in PipelineAssets)
                {
                    Object pipeline = AssetDatabase.LoadMainAssetAtPath(path);
                    if (pipeline == null) continue;
                    var so = new SerializedObject(pipeline);
                    SerializedProperty prop = so.FindProperty("m_VolumeProfile");
                    if (prop == null)
                    {
                        Debug.LogError("[TemplateCleanup] m_VolumeProfile not found on " + path);
                        ok = false;
                        continue;
                    }
                    if (prop.objectReferenceValue == profile) continue;
                    prop.objectReferenceValue = profile;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(pipeline);
                }
            }
            AssetDatabase.SaveAssets();

            if (ok)
            {
                ok &= DeleteIfUnreferenced(TemplateActionsPath);
                ok &= DeleteIfUnreferenced(TemplateProfilePath);
            }

            Debug.Log($"[TemplateCleanup] TEMPLATE CLEANUP {(ok ? "PASS" : "FAIL")}");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>Deletes <paramref name="path"/> unless a text-serialized asset, scene or setting still names its GUID.</summary>
        static bool DeleteIfUnreferenced(string path)
        {
            if (!File.Exists(path)) return true;
            string guid = AssetDatabase.AssetPathToGUID(path);
            List<string> users = FindGuidUsers(guid, path).ToList();
            if (users.Count > 0)
            {
                Debug.LogWarning($"[TemplateCleanup] keeping {path}; referenced by {string.Join(", ", users)}");
                return false;
            }
            return AssetDatabase.DeleteAsset(path);
        }

        static IEnumerable<string> FindGuidUsers(string guid, string self)
        {
            string[] extensions = { ".asset", ".unity", ".prefab", ".mat", ".controller", ".uss", ".uxml", ".json", ".inputactions" };
            IEnumerable<string> files = Directory.GetFiles("Assets", "*", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("ProjectSettings", "*.asset"));
            foreach (string file in files)
            {
                string normalized = file.Replace('\\', '/');
                if (normalized == self || !extensions.Contains(Path.GetExtension(normalized))) continue;
                if (File.ReadAllText(normalized).Contains(guid)) yield return normalized;
            }
        }
    }
}
