// Kit prefab catalog resolution for the kit document Godot actually loads
// (scripts/procgen/ship_generator.gd kit_path_for_layout / _kit_has_wrapper_map @ 96ecb2b0).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Picks the <see cref="KitPrefabCatalog"/> for a structural kit document.
    ///
    /// Godot instantiates the wrapper scenes named by the kit's <c>modules[].godot_wrapper_scene</c>, so the prefab set
    /// is identified by those scenes' folder (<c>res://scenes/wrappers/structural/ship_structural_v0/floor_1x1.tscn</c>
    /// → <c>KitCatalog_ship_structural_v0</c>), never by the kit id alone: <c>ship_structural_biomatter</c> reuses the
    /// v0 wrappers. A kit without a complete wrapper map (<c>ship_structural_hazard</c>, <c>_industrial</c>) falls back
    /// to v0, like <c>kit_path_for_layout</c>.
    /// </summary>
    public static class KitCatalogResolver
    {
        public const string DefaultKitId = "ship_structural_v0";
        public const string DefaultKitPath = "res://data/kits/ship_structural_v0.json";
        const string CatalogPrefix = "Catalogs/KitCatalog_";

        /// <summary>Resources loader; tests may swap it.</summary>
        public static Func<string, KitPrefabCatalog> LoadCatalog = id => Resources.Load<KitPrefabCatalog>(CatalogPrefix + id);

        /// <summary>The catalog for a loaded kit document; the v0 catalog when the wrapper folder has none.</summary>
        public static KitPrefabCatalog ForKitDocument(GdDict kit)
        {
            string id = CatalogIdForKitDocument(kit);
            return LoadCatalog(id) ?? (id != DefaultKitId ? LoadCatalog(DefaultKitId) : null);
        }

        /// <summary>
        /// Catalog id for a kit document: the most common wrapper-scene folder of its modules (first seen wins a tie),
        /// or <see cref="DefaultKitId"/> when the kit has no complete wrapper map.
        /// </summary>
        public static string CatalogIdForKitDocument(GdDict kit)
        {
            if (!HasWrapperMap(kit)) return DefaultKitId;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (object entry in (GdArray)kit["modules"])
            {
                string folder = WrapperFolder(V.Str(((GdDict)entry).Get("godot_wrapper_scene", "")));
                if (folder.Length == 0) continue;
                if (!counts.ContainsKey(folder))
                {
                    counts[folder] = 0;
                    order.Add(folder);
                }
                counts[folder]++;
            }
            string best = DefaultKitId;
            int bestCount = 0;
            foreach (string folder in order)
            {
                if (counts[folder] > bestCount)
                {
                    best = folder;
                    bestCount = counts[folder];
                }
            }
            return best;
        }

        /// <summary>Port of <c>_kit_has_wrapper_map</c> on a parsed document: non-empty modules, each with an id and a wrapper scene.</summary>
        public static bool HasWrapperMap(GdDict kit)
        {
            if (kit == null || !(kit.Get("modules", null) is GdArray modules) || modules.IsEmpty) return false;
            foreach (object entry in modules)
            {
                if (!(entry is GdDict e) || V.Str(e.Get("module_id", "")).Length == 0 || V.Str(e.Get("godot_wrapper_scene", "")).Length == 0)
                    return false;
            }
            return true;
        }

        /// <summary>Port of <c>kit_path_for_layout</c> (reads through <see cref="CatalogRegistry"/>).</summary>
        public static string KitPathForLayout(GdDict layout)
        {
            string kitId = V.Str(layout?.Get("kit_id", DefaultKitId) ?? DefaultKitId);
            if (kitId.Length == 0) kitId = DefaultKitId;
            string kitPath = "res://data/kits/" + kitId + ".json";
            if (!CatalogRegistry.Exists(kitPath) || !HasWrapperMap(CatalogRegistry.LoadDict(kitPath))) kitPath = DefaultKitPath;
            return kitPath;
        }

        /// <summary>The catalog for a layout without a loaded kit document (life boat): its kit path, then the wrapper folder.</summary>
        public static KitPrefabCatalog ForLayout(GdDict layout)
        {
            string kitPath = KitPathForLayout(layout);
            return ForKitDocument(CatalogRegistry.Exists(kitPath) ? CatalogRegistry.LoadDict(kitPath) : null);
        }

        /// <summary><c>res://scenes/wrappers/structural/ship_structural_v0/floor_1x1.tscn</c> → <c>ship_structural_v0</c>.</summary>
        public static string WrapperFolder(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath)) return "";
            int slash = scenePath.LastIndexOf('/');
            if (slash <= 0) return "";
            string dir = scenePath.Substring(0, slash);
            int prev = dir.LastIndexOf('/');
            return prev < 0 ? dir : dir.Substring(prev + 1);
        }
    }
}
