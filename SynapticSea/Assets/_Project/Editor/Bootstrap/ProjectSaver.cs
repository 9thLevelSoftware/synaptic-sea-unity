using UnityEditor;
using UnityEngine;

namespace SynapticSea.EditorTools.Bootstrap
{
    /// <summary>Imports everything (generating .meta files) and saves. Synchronous; safe under unity run.</summary>
    public static class ProjectSaver
    {
        public static void SaveAll()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            Debug.Log("[ProjectSaver] Assets imported and saved.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
