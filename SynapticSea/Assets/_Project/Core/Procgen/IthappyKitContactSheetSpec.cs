// Path, grid, and Art Director locked-iso framing for ithappy_scifi_v0 stills.
using System;
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

        /// <summary>True-iso pitch: atan(1/√2). Camera looks this many degrees down from horizontal.</summary>
        public const float PitchDownDegrees = 35.26438968f;

        /// <summary>45° yaw bias. Play-frame Unity offset is −X/+Z (north up-left after Frame).</summary>
        public const float YawBiasDegrees = 45f;

        public const float OrthographicSizeMin = 18f;
        public const float OrthographicSizeMax = 22f;
        public const float ContactSheetOrthographicSize = 22f;
        public const float PerModuleOrthographicSize = 18f;

        /// <summary>Godot-frame equal-axis offset. <c>Frame.ToUnity</c> yields Unity (−d, +d, +d).</summary>
        public const float CameraDistanceM = 40f;

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

        /// <summary>
        /// Unity-frame camera offset for the style-gate lock: pitch <see cref="PitchDownDegrees"/> down,
        /// yaw bias <see cref="YawBiasDegrees"/>, same octant as play iso after Frame (−X, +Y, +Z).
        /// </summary>
        public static void LockedIsoOffset(float distance, out float x, out float y, out float z)
        {
            double elev = PitchDownDegrees * (Math.PI / 180.0);
            double yaw = -YawBiasDegrees * (Math.PI / 180.0);
            double cosElev = Math.Cos(elev);
            x = (float)(Math.Sin(yaw) * cosElev * distance);
            y = (float)(Math.Sin(elev) * distance);
            z = (float)(Math.Cos(yaw) * cosElev * distance);
        }

        /// <summary>Godot (d, d, d) maps through Frame to Unity (−d, d, d), which is true iso.</summary>
        public static void GodotEqualAxisOffset(float distance, out float x, out float y, out float z)
        {
            x = distance;
            y = distance;
            z = distance;
        }
    }
}
