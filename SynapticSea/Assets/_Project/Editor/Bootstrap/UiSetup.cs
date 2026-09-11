using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SynapticSea.EditorTools.Bootstrap
{
    /// <summary>
    /// Creates the UI Toolkit PanelSettings assets (HUD sort order 0, menus 10) on the Synaptic Sea theme with
    /// Constant Physical Size at 96 DPI, so one logical pixel equals one CSS pixel at 100% OS scaling (the spec's
    /// "logical pixels"). Idempotent.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Bootstrap.UiSetup.Apply -quit
    /// </summary>
    public static class UiSetup
    {
        public const string ThemePath = "Assets/Content/UI/Theme/SynapticSea.tss";
        public const string HudPanelPath = "Assets/Content/UI/PanelSettings_HUD.asset";
        public const string MenuPanelPath = "Assets/Content/UI/PanelSettings_Menu.asset";

        [MenuItem("Synaptic Sea/Bootstrap/Create UI Panel Settings")]
        public static void Apply()
        {
            AssetDatabase.ImportAsset(ThemePath, ImportAssetOptions.ForceUpdate);
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            if (theme == null)
            {
                Debug.LogError("[UiSetup] theme not found: " + ThemePath);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            Ensure(HudPanelPath, theme, sortOrder: 0);
            Ensure(MenuPanelPath, theme, sortOrder: 10);
            AssetDatabase.SaveAssets();
            Debug.Log("[UiSetup] UI SETUP PASS panels=2 theme=" + ThemePath);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static void Ensure(string path, ThemeStyleSheet theme, int sortOrder)
        {
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (ps == null)
            {
                ps = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(ps, path);
            }
            ps.themeStyleSheet = theme;
            ps.scaleMode = PanelScaleMode.ConstantPhysicalSize;
            ps.referenceDpi = 96f;
            ps.fallbackDpi = 96f;
            ps.sortingOrder = sortOrder;
            EditorUtility.SetDirty(ps);
        }
    }
}
