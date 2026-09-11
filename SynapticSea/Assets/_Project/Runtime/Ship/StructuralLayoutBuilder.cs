// Ported from scripts/procgen/generated_ship_loader.gd (_instance_structural_wrappers,
// _instantiate_structural_record, _read_placement_position, _apply_module_damage_visuals) @ 96ecb2b0
using System;
using System.Collections.Generic;
using System.Globalization;
using SynapticSea.Core.Variant;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Instantiates a layout's <c>structural_plan</c> (edge placements, floor placements, ceiling placements) from a
    /// <see cref="KitPrefabCatalog"/>, converting Godot positions/yaws through <see cref="Frame"/> and stamping the metadata
    /// the Godot loader stored with <c>set_meta</c> onto <see cref="StructuralModule"/>. Atomic like Godot: every
    /// record is built under an inactive root, and nothing is published if any record fails.
    /// </summary>
    public sealed class StructuralLayoutBuilder
    {
        public sealed class Result
        {
            public GameObject Root;
            public readonly List<StructuralModule> Modules = new List<StructuralModule>();
            public readonly Dictionary<string, StructuralModule> ByModuleKey = new Dictionary<string, StructuralModule>(StringComparer.Ordinal);
            public int EdgeCount, FloorCount, CeilingCount;
        }

        public string LastError { get; private set; } = "";

        /// <summary>
        /// Builds the structural tree under <paramref name="parent"/>. Returns null (and sets <see cref="LastError"/>)
        /// when the plan is missing or any record cannot be instantiated.
        /// </summary>
        public Result Build(GdDict layout, KitPrefabCatalog kit, Transform parent)
        {
            LastError = "";
            var plan = layout?.GetDict("structural_plan");
            var edges = plan?.GetArray("placements");
            var floors = plan?.GetArray("floor_placements");
            if (plan == null || edges == null || floors == null)
            {
                LastError = "layout has no structural_plan placements/floor_placements";
                return null;
            }
            var ceilings = plan.GetArray("ceiling_placements") ?? new GdArray();

            var result = new Result { Root = new GameObject("StructuralRoot") };
            result.Root.SetActive(false);
            result.Root.transform.SetParent(parent, false);
            try
            {
                if (!Place(edges, "edge", kit, result) || !Place(floors, "floor", kit, result) || !Place(ceilings, "ceiling", kit, result))
                {
                    Object.DestroyImmediate(result.Root);
                    return null;
                }
                ApplyModuleDamage(layout, result);
                result.Root.SetActive(true);
                return result;
            }
            catch
            {
                Object.DestroyImmediate(result.Root);
                throw;
            }
        }

        bool Place(GdArray records, string layer, KitPrefabCatalog kit, Result result)
        {
            foreach (object recordVariant in records)
            {
                if (!(recordVariant is GdDict record))
                {
                    LastError = $"{layer} placement is not a dictionary";
                    return false;
                }
                string moduleId = record.GetString("module_id");
                if (string.IsNullOrEmpty(moduleId) || !kit.TryGetPrefab(moduleId, out StructuralModule prefab))
                {
                    LastError = $"{layer} placement {record.GetString("placement_id")}: no prefab for module '{moduleId}' in kit '{kit.kitId}'";
                    return false;
                }
                if (!TryReadPlacementPosition(record, out Vec3 godotPos))
                {
                    LastError = $"{layer} placement {record.GetString("placement_id")}: unreadable position";
                    return false;
                }
                float yaw = (float)record.GetFloat("yaw_degrees", 0.0);

                var module = Object.Instantiate(prefab, result.Root.transform, false);
                module.transform.localPosition = Frame.ToUnity(godotPos);
                module.transform.localRotation = Frame.YawRotation(yaw);
                module.godotPosition = new Vector3(godotPos.X, godotPos.Y, godotPos.Z);
                module.godotYawDegrees = yaw;
                module.layer = layer;
                module.placementId = V.Str(record.Get("placement_id", record.Get("id", "")));

                var roomIdsVariant = record.Get("room_ids");
                var roomIds = roomIdsVariant is GdArray ra ? ra : new GdArray();
                module.roomIds = new string[roomIds.Count];
                for (int i = 0; i < roomIds.Count; i++) module.roomIds[i] = V.Str(roomIds[i]);

                switch (layer)
                {
                    case "floor":
                        module.placementKey = record.GetString("cell_key");
                        module.name = "Floor_" + module.placementKey.Replace("|", "_");
                        module.structuralKind = "FLOOR";
                        module.moduleKey = "floor/" + module.placementKey;
                        module.roomId = record.GetString("room_id");
                        result.FloorCount++;
                        break;
                    case "ceiling":
                        module.placementKey = record.GetString("cell_key");
                        module.name = "Ceiling_" + module.placementKey.Replace("|", "_");
                        module.structuralKind = "CEILING";
                        module.moduleKey = "ceiling/" + module.placementKey;
                        module.roomId = record.GetString("room_id");
                        result.CeilingCount++;
                        break;
                    default:
                        module.placementKey = record.GetString("edge_key");
                        module.name = "StructuralEdge_" + module.placementKey.Replace("|", "_");
                        module.structuralKind = record.GetString("kind");
                        module.moduleKey = "edge/" + module.placementKey;
                        module.roomId = roomIds.Count > 0 ? V.Str(roomIds[0]) : "";
                        result.EdgeCount++;
                        break;
                }
                module.SetIntegrity(StructuralModule.IntegrityIntact);
                result.Modules.Add(module);
                result.ByModuleKey[module.moduleKey] = module;
            }
            return true;
        }

        /// <summary>Port of <c>_apply_module_damage_visuals</c>: <c>module_damage</c> rows keyed by module_key or placement_id.</summary>
        static void ApplyModuleDamage(GdDict layout, Result result)
        {
            var rows = layout.GetArray("module_damage");
            if (rows == null || rows.Count == 0) return;
            var lookup = new Dictionary<string, GdDict>(StringComparer.Ordinal);
            foreach (object rowVariant in rows)
            {
                if (!(rowVariant is GdDict row)) continue;
                string key = V.Str(row.Get("module_key", row.Get("module_id", "")));
                if (key.Length > 0) lookup[key] = row;
                string placementId = row.GetString("placement_id");
                if (placementId.Length > 0) lookup[placementId] = row;
            }
            foreach (var module in result.Modules)
            {
                if (!lookup.TryGetValue(module.moduleKey, out GdDict row) && !lookup.TryGetValue(module.placementId ?? "", out row)) continue;
                string state = row.GetString("state", StructuralModule.IntegrityIntact);
                if (state.Length == 0) state = StructuralModule.IntegrityIntact;
                module.SetIntegrity(state);
            }
        }

        /// <summary>Port of <c>_read_placement_position</c>: array, "(x, y, z)" string, or Vec3; falls back to world_position.</summary>
        public static bool TryReadPlacementPosition(GdDict placement, out Vec3 position)
        {
            position = Vec3.Zero;
            object raw = placement.Get("position") ?? placement.Get("world_position");
            switch (raw)
            {
                case Vec3 v:
                    position = v;
                    return true;
                case string s:
                    return TryParseVectorString(s, out position);
                case GdArray a when a.Count >= 3:
                    position = new Vec3(V.F64(a[0]), V.F64(a[1]), V.F64(a[2]));
                    return true;
                default:
                    return false;
            }
        }

        static bool TryParseVectorString(string s, out Vec3 v)
        {
            v = Vec3.Zero;
            string inner = s.Trim().TrimStart('(').TrimEnd(')');
            string[] parts = inner.Split(',');
            if (parts.Length != 3) return false;
            var f = new double[3];
            for (int i = 0; i < 3; i++)
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) return false;
            v = new Vec3(f[0], f[1], f[2]);
            return true;
        }
    }
}
