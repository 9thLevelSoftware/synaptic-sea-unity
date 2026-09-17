// Path and grid contract for Art Director iso stills of baked ithappy_scifi_v0 prefabs.
using System.Collections.Generic;
using System.IO;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Engine-free layout for <c>IthappyKitContactSheet.Run</c>. The Editor runner instantiates
    /// baked prefabs; this type only names modules, asset paths, and PNG destinations.
    /// </summary>
    public static class IthappyKitContactSheetSpec
    {
        public const string PrefabFolder = "Assets/Content/Prefabs/Structural/ithappy_scifi_v0";
        public const string RelativeOutDir = "artifacts/screenshots/ithappy_scifi_v0";
        public const string ContactSheetFileName = "contact_sheet.png";
        public const float CellSpacingM = 8f;
        public const int GridColumns = 4;

        public static readonly IReadOnlyList<string> HighlightModuleIds = new[]
        {
            "floor_1x1",
            "wall_straight_1x1",
            "wall_t_junction",
            "wall_x_junction",
        };

        public static List<string> ModuleIdsFromKit(string streamingAssetsRoot)
        {
            var ids = new List<string>();
            string kitPath = Path.Combine(streamingAssetsRoot ?? "", "data", "kits", KitCatalog.ITHAPPY_KIT_ID + ".json");
            if (!File.Exists(kitPath)) return ids;
            GdDict kit = GdJson.ParseDict(File.ReadAllText(kitPath));
            if (kit == null) return ids;
            foreach (object entry in kit.GetArrayOrEmpty("modules"))
            {
                if (!(entry is GdDict row)) continue;
                string id = row.GetString("module_id");
                if (id.Length != 0) ids.Add(id);
            }
            return ids;
        }

        public static string PrefabAssetPath(string moduleId) =>
            PrefabFolder + "/" + (moduleId ?? "") + ".prefab";

        public static string OutputDirectory(string repoRoot) =>
            Path.Combine(repoRoot ?? "", "artifacts", "screenshots", KitCatalog.ITHAPPY_KIT_ID);

        public static string ContactSheetPath(string repoRoot) =>
            Path.Combine(OutputDirectory(repoRoot), ContactSheetFileName);

        public static string PerModulePath(string repoRoot, string moduleId) =>
            Path.Combine(OutputDirectory(repoRoot), (moduleId ?? "") + ".png");

        public static void GridOffset(int index, out float x, out float z)
        {
            int col = index % GridColumns;
            int row = index / GridColumns;
            x = col * CellSpacingM;
            z = row * CellSpacingM;
        }
    }
}
