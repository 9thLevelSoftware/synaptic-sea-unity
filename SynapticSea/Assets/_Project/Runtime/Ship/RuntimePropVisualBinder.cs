// Ported from scripts/procgen/runtime_prop_visual_binder.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Mounts imported prop visuals from validated prop-visual bindings (data/props/visual_bindings.generated.json).
    /// Godot loaded the binding's GLB (<c>visual_scene_path</c>) and applied the placement offset / rotation / scale;
    /// the port instantiates the matching <see cref="PropVisual"/> prefab from the <see cref="PropCatalog"/> (looked up
    /// by <c>asset_id</c>), whose <c>Visual</c> child already bakes that placement (Editor PropPrefabBuilder).
    /// The binding is still validated exactly like Godot's <c>_is_safe_binding</c>, and the instance must be visual
    /// only (no colliders, bodies, joints or behaviours other than <see cref="PropVisual"/>).
    /// </summary>
    public static class RuntimePropVisualBinder
    {
        public const string SCENE_PATH_PREFIX = "res://assets/imported/props/";
        public const string IMPORTED_VISUAL_NAME = "ImportedVisual";
        public const string BINDING_DOCUMENT_KIND = "prop_visual_binding";
        public const string COLLISION_POLICY = "none_visual_only";
        public static readonly IReadOnlyList<string> BINDING_FIELDS = new[]
        {
            "asset_id", "binding", "bounds", "collision_policy", "document_kind", "extensions",
            "placement", "prop_kind", "provenance", "schema_version", "source", "visual_scene_path",
        };
        static readonly string[] BindingMetaFields = { "namespace", "ids" };
        static readonly string[] PlacementFields = { "origin", "offset_m", "rotation_degrees", "allowed_yaw_deg", "scale" };
        static readonly string[] SourceFields = { "sha256", "byte_size", "mesh_count", "gltf_version" };
        static readonly string[] BoundsFields = { "local_min_m", "local_max_m" };
        static readonly string[] ProvenanceFields = { "license_state", "source_platform" };
        static readonly string[] AllowedOrigins = { "scene_origin", "marker_anchor" };
        static readonly string[] AllowedSurfaces = { "floor", "wall", "ceiling" };

        /// <summary>The catalog used when callers pass none (Resources/Catalogs/PropCatalog).</summary>
        public static PropCatalog DefaultCatalog => _defaultCatalog != null ? _defaultCatalog : _defaultCatalog = Resources.Load<PropCatalog>("Catalogs/PropCatalog");
        static PropCatalog _defaultCatalog;

        public static bool MountComponentVisual(Transform marker, GdDict binding, PropCatalog catalog = null)
        {
            if (marker == null || binding == null || binding.IsEmpty) return false;
            if (!IsSafeBinding(binding, "component")) return false;
            if (HasImportedVisual(marker)) return false;
            GameObject visual = CreateImportedVisual(binding, catalog);
            if (visual == null) return false;
            visual.transform.SetParent(marker, false);
            return true;
        }

        public static GameObject CreateObjectiveVisual(GdDict binding, PropCatalog catalog = null)
        {
            if (binding == null || binding.IsEmpty || !IsSafeBinding(binding, "objective")) return null;
            return CreateImportedVisual(binding, catalog);
        }

        public static GameObject CreateDressingVisual(GdDict binding, PropCatalog catalog = null)
        {
            if (binding == null || binding.IsEmpty || !IsSafeBinding(binding, "dressing")) return null;
            return CreateImportedVisual(binding, catalog);
        }

        public static void ClearImportedVisuals(Transform root)
        {
            if (root == null) return;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (child.GetComponent<PropVisual>() != null)
                {
                    child.SetParent(null, false);
                    if (Application.isPlaying) Object.Destroy(child.gameObject);
                    else Object.DestroyImmediate(child.gameObject);
                    continue;
                }
                ClearImportedVisuals(child);
            }
        }

        /// <summary>
        /// Port of <c>validate_visual_tree</c>: an imported visual may carry renderers and transforms only (plus the
        /// <see cref="PropVisual"/> marker) — no colliders, rigidbodies, joints, character controllers or behaviours.
        /// </summary>
        public static bool ValidateVisualTree(GameObject root)
        {
            if (root == null) return false;
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) return false; // missing script
                if (c is Collider || c is Rigidbody || c is Joint || c is CharacterController) return false;
                if (c is MonoBehaviour && !(c is PropVisual)) return false;
            }
            return true;
        }

        static GameObject CreateImportedVisual(GdDict binding, PropCatalog catalog)
        {
            if (!IsSafeBinding(binding, V.Str(binding.Get("prop_kind", "")))) return null;
            catalog = catalog != null ? catalog : DefaultCatalog;
            if (catalog == null || !catalog.TryGetByAssetId(V.Str(binding.Get("asset_id", "")), out PropVisual prefab) || prefab == null) return null;
            if (!ValidateVisualTree(prefab.gameObject)) return null;
            var instance = Object.Instantiate(prefab);
            GameObject visual = instance.gameObject;
            visual.name = IMPORTED_VISUAL_NAME;
            foreach (Transform t in visual.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = PhysicsLayers.Prop;
            return visual;
        }

        static bool HasImportedVisual(Transform root)
        {
            if (root == null) return false;
            foreach (Transform child in root)
            {
                if (child.name == IMPORTED_VISUAL_NAME || child.GetComponent<PropVisual>() != null) return true;
                if (HasImportedVisual(child)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ binding validation (_is_safe_binding)

        public static bool IsSafeBinding(GdDict binding, string expectedPropKind)
        {
            if (!HasExactFields(binding, BINDING_FIELDS)) return false;
            if (!IsExactSchemaVersion(binding.Get("schema_version"))) return false;
            if (!V.VariantEquals(binding.Get("document_kind", ""), BINDING_DOCUMENT_KIND)) return false;
            if (!V.VariantEquals(binding.Get("collision_policy", ""), COLLISION_POLICY)) return false;
            if (!V.VariantEquals(binding.Get("prop_kind", ""), expectedPropKind)) return false;
            if (!IsAssetId(binding.Get("asset_id"))) return false;
            string expectedNamespace = expectedPropKind == "component" ? "component_id"
                : expectedPropKind == "objective" ? "gameplay_placement_id"
                : expectedPropKind == "dressing" ? "visual_prop_id" : "";
            if (expectedNamespace.Length == 0) return false;
            object bindingMeta = binding.Get("binding");
            if (!HasExactFields(bindingMeta, BindingMetaFields)) return false;
            var bindingDictionary = (GdDict)bindingMeta;
            if (!V.VariantEquals(bindingDictionary.Get("namespace", ""), expectedNamespace)) return false;
            if (!(bindingDictionary.Get("ids") is GdArray ids) || ids.IsEmpty) return false;
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (object idValue in ids)
            {
                if (!(idValue is string id) || GdString.StripEdges(id).Length == 0) return false;
                if (!seenIds.Add(id)) return false;
            }
            if (expectedPropKind == "component")
            {
                if (!(binding.Get("asset_id") is string assetId) || ids.Count != 1 || !V.VariantEquals(ids[0], assetId)) return false;
            }
            object scenePath = binding.Get("visual_scene_path", "");
            string expectedGroup = expectedPropKind == "component" ? "components" : expectedPropKind == "objective" ? "objectives" : "dressing";
            if (!IsCanonicalScenePath(scenePath, expectedGroup)) return false;
            if (GdString.TrimSuffix(GetFile((string)scenePath), ".glb") != V.Str(binding.Get("asset_id", ""))) return false;
            if (!IsValidPlacement(binding.Get("placement"), expectedPropKind)) return false;
            if (!IsValidSource(binding.Get("source"))) return false;
            if (!IsValidBounds(binding.Get("bounds"))) return false;
            if (!IsValidProvenance(binding.Get("provenance"))) return false;
            return binding.Get("extensions") is GdDict;
        }

        static string GetFile(string path)
        {
            int i = path.LastIndexOf('/');
            return i < 0 ? path : path.Substring(i + 1);
        }

        static bool IsCanonicalScenePath(object value, string expectedGroup)
        {
            if (!(value is string path) || path.Length == 0) return false;
            // Godot also required path.simplify_path() == path; the empty / "." / ".." part checks below cover it.
            if (!GdString.BeginsWith(path, SCENE_PATH_PREFIX)) return false;
            string relative = path.Substring(SCENE_PATH_PREFIX.Length);
            string[] parts = relative.Split('/');
            if (parts.Length != 2) return false;
            if (parts[0] != "components" && parts[0] != "objectives" && parts[0] != "dressing") return false;
            if (expectedGroup.Length > 0 && parts[0] != expectedGroup) return false;
            if (parts[1].Length == 0 || parts[1] == ".glb" || !GdString.EndsWith(parts[1], ".glb")) return false;
            return Array.IndexOf(parts, "") < 0 && Array.IndexOf(parts, ".") < 0 && Array.IndexOf(parts, "..") < 0;
        }

        static bool IsValidPlacement(object placementValue, string expectedPropKind)
        {
            if (!(placementValue is GdDict placement)) return false;
            bool surfaceAllowed = expectedPropKind == "dressing" || expectedPropKind == "objective";
            int allowedCount = PlacementFields.Length + (surfaceAllowed ? 1 : 0);
            foreach (object key in placement.Keys)
            {
                string k = V.Str(key);
                if (Array.IndexOf(PlacementFields, k) < 0 && !(surfaceAllowed && k == "surface")) return false;
            }
            if (placement.Count < PlacementFields.Length || placement.Count > allowedCount) return false;
            foreach (string field in PlacementFields)
                if (!placement.Has(field)) return false;
            if (placement.Has("surface") && !(placement.Get("surface") is string surface && Array.IndexOf(AllowedSurfaces, surface) >= 0)) return false;
            if (!(placement.Get("origin") is string origin) || Array.IndexOf(AllowedOrigins, origin) < 0) return false;
            if (!IsFiniteVector(placement["offset_m"]) || !IsFiniteVector(placement["rotation_degrees"])) return false;
            if (!IsFiniteNumber(placement["scale"]) || V.F64(placement["scale"]) <= 0.0) return false;
            if (!(placement["allowed_yaw_deg"] is GdArray yaws) || yaws.IsEmpty) return false;
            var seenYaws = new HashSet<double>();
            foreach (object yaw in yaws)
            {
                if (!IsFiniteNumber(yaw)) return false;
                if (!seenYaws.Add(V.F64(yaw))) return false;
            }
            return true;
        }

        static bool IsValidSource(object sourceValue)
        {
            if (!HasExactFields(sourceValue, SourceFields)) return false;
            var source = (GdDict)sourceValue;
            if (!IsSha256(source.Get("sha256"))) return false;
            if (!IsIntegerValue(source.Get("byte_size"), 0.0)) return false;
            if (!IsIntegerValue(source.Get("mesh_count"), 1.0)) return false;
            return V.VariantEquals(source.Get("gltf_version"), "2.0");
        }

        static bool IsValidBounds(object boundsValue)
        {
            if (!HasExactFields(boundsValue, BoundsFields)) return false;
            var bounds = (GdDict)boundsValue;
            object localMin = bounds.Get("local_min_m"), localMax = bounds.Get("local_max_m");
            if (!IsFiniteVector(localMin) || !IsFiniteVector(localMax)) return false;
            var mins = (GdArray)localMin;
            var maxs = (GdArray)localMax;
            for (int i = 0; i < 3; i++)
                if (V.F64(mins[i]) > V.F64(maxs[i])) return false;
            return true;
        }

        static bool IsValidProvenance(object value)
        {
            if (!HasExactFields(value, ProvenanceFields)) return false;
            var provenance = (GdDict)value;
            return IsNonemptyString(provenance.Get("license_state")) && IsNonemptyString(provenance.Get("source_platform"));
        }

        static bool HasExactFields(object value, IReadOnlyList<string> expected)
        {
            if (!(value is GdDict d) || d.Count != expected.Count) return false;
            foreach (object key in d.Keys)
            {
                bool found = false;
                for (int i = 0; i < expected.Count; i++)
                    if (expected[i] == V.Str(key)) { found = true; break; }
                if (!found) return false;
            }
            foreach (string field in expected)
                if (!d.Has(field)) return false;
            return true;
        }

        static bool IsExactSchemaVersion(object value)
        {
            if (!(value is string s)) return false;
            string[] parts = s.Split('.');
            return parts.Length == 3 && parts[0] == "1" && IsSemverNumber(parts[1]) && IsSemverNumber(parts[2]);
        }

        static bool IsSemverNumber(string value)
        {
            if (value.Length == 0 || (value.Length > 1 && value[0] == '0')) return false;
            foreach (char c in value)
                if (c < '0' || c > '9') return false;
            return true;
        }

        static bool IsAssetId(object value)
        {
            if (!IsNonemptyString(value)) return false;
            string id = (string)value;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool lower = c >= 'a' && c <= 'z', digit = c >= '0' && c <= '9';
                if (i == 0 && !lower && !digit) return false;
                if (!lower && !digit && c != '_' && c != '-') return false;
            }
            return true;
        }

        static bool IsNonemptyString(object value) => value is string s && GdString.StripEdges(s).Length > 0;

        static bool IsSha256(object value)
        {
            if (!(value is string s) || s.Length != 64 || s == new string('0', 64)) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        static bool IsIntegerValue(object value, double minimum)
        {
            if (!IsFiniteNumber(value)) return false;
            double n = V.F64(value);
            return n >= minimum && GdMath.IsEqualApprox(n, GdMath.Round(n));
        }

        static bool IsFiniteVector(object value)
        {
            if (!(value is GdArray a) || a.Count != 3) return false;
            foreach (object item in a)
                if (!IsFiniteNumber(item)) return false;
            return true;
        }

        static bool IsFiniteNumber(object value)
        {
            if (!(value is long) && !(value is double)) return false;
            double d = V.F64(value);
            return !double.IsNaN(d) && !double.IsInfinity(d);
        }
    }
}
