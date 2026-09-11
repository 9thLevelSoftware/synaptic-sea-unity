// Ported from scripts/systems/prop_visual_binding_catalog.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public class PropVisualBindingCatalog
    {
        public const string DEFAULT_INDEX_PATH = "res://data/props/visual_bindings.generated.json";
        public const string SCENE_PATH_PREFIX = "res://assets/imported/props/";
        public const string INDEX_DOCUMENT_KIND = "prop_visual_binding_index";
        public const string BINDING_DOCUMENT_KIND = "prop_visual_binding";
        public static readonly IReadOnlyList<string> GROUPS = new[] { "components", "objectives", "dressing" };
        public static readonly IReadOnlyList<string> INDEX_FIELDS = new[] { "schema_version", "document_kind", "components", "objectives", "dressing" };
        public static readonly IReadOnlyList<string> BINDING_FIELDS = new[]
        {
            "asset_id", "binding", "bounds", "collision_policy", "document_kind", "extensions",
            "placement", "prop_kind", "provenance", "schema_version", "source", "visual_scene_path",
        };

        GdDict _componentBindings = new GdDict();
        GdDict _objectiveBindings = new GdDict();
        GdDict _dressingBindings = new GdDict();
        readonly List<string> _errors = new List<string>();
        string _jsonScanText = "";
        int _jsonScanIndex = 0;
        bool _duplicateJsonKeyFound = false;

        /// <summary>
        /// Loads and validates the binding index. <c>res://</c> paths are read through <see cref="CoreServices.Resources"/>,
        /// <c>user://</c> paths through <see cref="CoreServices.UserStorage"/> (Godot's FileAccess handled both).
        /// </summary>
        public bool LoadFromPath(string path = DEFAULT_INDEX_PATH)
        {
            ClearState();
            if (string.IsNullOrEmpty(path))
            {
                _errors.Add("catalog path is empty");
                return false;
            }

            string rawDocument = ReadText(path);
            if (rawDocument == null)
            {
                _errors.Add("cannot open catalog: " + path);
                return false;
            }

            if (ContainsDuplicateJsonKey(rawDocument))
            {
                _errors.Add("catalog JSON contains duplicate JSON object key");
                return false;
            }
            object document;
            try
            {
                document = GdJson.Parse(rawDocument);
            }
            catch (GdJson.ParseError e)
            {
                _errors.Add("catalog JSON parse failed: " + ParseErrorMessage(e));
                return false;
            }

            if (!(document is GdDict root))
            {
                _errors.Add("catalog document must be an object");
                return false;
            }
            foreach (object key in root.Keys)
            {
                if (!Has(INDEX_FIELDS, V.Str(key)))
                    _errors.Add("catalog has unexpected root field: " + V.Str(key));
            }
            if (root.Count != INDEX_FIELDS.Count)
                _errors.Add("catalog root fields are incomplete");
            if (!V.VariantEquals(root.Get("document_kind", ""), INDEX_DOCUMENT_KIND))
                _errors.Add("catalog document_kind must be " + INDEX_DOCUMENT_KIND);
            if (!IsExactSchemaVersion(root.Get("schema_version")))
                _errors.Add("catalog schema_version must be an exact 1.x.y string");

            var loadedGroups = new GdDict();
            foreach (string groupName in GROUPS)
                loadedGroups[groupName] = ValidateGroup(root.Get(groupName), groupName);
            if (_errors.Count != 0)
                return false;

            _componentBindings = (GdDict)loadedGroups["components"];
            _objectiveBindings = (GdDict)loadedGroups["objectives"];
            _dressingBindings = (GdDict)loadedGroups["dressing"];
            return true;
        }

        static string ReadText(string path)
        {
            if (path.StartsWith(ResPath.UserScheme, StringComparison.Ordinal))
                return CoreServices.UserStorage?.ReadText(path);
            return CoreServices.Resources?.ReadText(path);
        }

        /// <summary>Strips the kernel's "JSON parse error at line N: " prefix, leaving Godot's get_error_message() text.</summary>
        static string ParseErrorMessage(GdJson.ParseError e)
        {
            string message = e.Message;
            int colon = message.IndexOf(": ", StringComparison.Ordinal);
            return colon >= 0 ? message.Substring(colon + 2) : message;
        }

        public GdDict GetComponentBinding(string componentId) => GetBinding(_componentBindings, componentId);

        public GdDict GetObjectiveBinding(string placementId) => GetBinding(_objectiveBindings, placementId);

        public GdDict GetDressingBinding(string visualPropId) => GetBinding(_dressingBindings, visualPropId);

        public List<string> GetErrors() => new List<string>(_errors);

        // ------------------------------------------------------------------ duplicate-key pre-scan

        bool ContainsDuplicateJsonKey(string text)
        {
            _jsonScanText = text;
            _jsonScanIndex = 0;
            _duplicateJsonKeyFound = false;
            bool scanValid = ScanJsonValue();
            SkipJsonWhitespace();
            return scanValid && _jsonScanIndex == _jsonScanText.Length && _duplicateJsonKeyFound;
        }

        string Substr(int from, int length)
        {
            // Godot String.substr clamps to the end of the string.
            if (from >= _jsonScanText.Length || length <= 0)
                return "";
            if (from + length > _jsonScanText.Length)
                length = _jsonScanText.Length - from;
            return _jsonScanText.Substring(from, length);
        }

        bool ScanJsonValue()
        {
            SkipJsonWhitespace();
            if (_jsonScanIndex >= _jsonScanText.Length)
                return false;
            string character = Substr(_jsonScanIndex, 1);
            if (character == "{")
                return ScanJsonObject();
            if (character == "[")
                return ScanJsonArray();
            if (character == "\"")
                return ScanJsonStringToken() != null;
            if (character == "t" && ScanJsonLiteral("true"))
                return true;
            if (character == "f" && ScanJsonLiteral("false"))
                return true;
            if (character == "n" && ScanJsonLiteral("null"))
                return true;
            return ScanJsonNumber();
        }

        bool ScanJsonObject()
        {
            _jsonScanIndex += 1;
            SkipJsonWhitespace();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            if (_jsonScanIndex < _jsonScanText.Length && Substr(_jsonScanIndex, 1) == "}")
            {
                _jsonScanIndex += 1;
                return true;
            }
            while (_jsonScanIndex < _jsonScanText.Length)
            {
                SkipJsonWhitespace();
                string key = ScanJsonStringToken();
                if (key == null)
                    return false;
                if (seenKeys.Contains(key))
                    _duplicateJsonKeyFound = true;
                seenKeys.Add(key);
                SkipJsonWhitespace();
                if (_jsonScanIndex >= _jsonScanText.Length || Substr(_jsonScanIndex, 1) != ":")
                    return false;
                _jsonScanIndex += 1;
                if (!ScanJsonValue())
                    return false;
                SkipJsonWhitespace();
                if (_jsonScanIndex >= _jsonScanText.Length)
                    return false;
                string delimiter = Substr(_jsonScanIndex, 1);
                if (delimiter == "}")
                {
                    _jsonScanIndex += 1;
                    return true;
                }
                if (delimiter != ",")
                    return false;
                _jsonScanIndex += 1;
            }
            return false;
        }

        bool ScanJsonArray()
        {
            _jsonScanIndex += 1;
            SkipJsonWhitespace();
            if (_jsonScanIndex < _jsonScanText.Length && Substr(_jsonScanIndex, 1) == "]")
            {
                _jsonScanIndex += 1;
                return true;
            }
            while (_jsonScanIndex < _jsonScanText.Length)
            {
                if (!ScanJsonValue())
                    return false;
                SkipJsonWhitespace();
                if (_jsonScanIndex >= _jsonScanText.Length)
                    return false;
                string delimiter = Substr(_jsonScanIndex, 1);
                if (delimiter == "]")
                {
                    _jsonScanIndex += 1;
                    return true;
                }
                if (delimiter != ",")
                    return false;
                _jsonScanIndex += 1;
            }
            return false;
        }

        /// <summary>Returns the decoded string token, or null (the GDScript returned a null Variant).</summary>
        string ScanJsonStringToken()
        {
            if (_jsonScanIndex >= _jsonScanText.Length || Substr(_jsonScanIndex, 1) != "\"")
                return null;
            int start = _jsonScanIndex;
            _jsonScanIndex += 1;
            while (_jsonScanIndex < _jsonScanText.Length)
            {
                string character = Substr(_jsonScanIndex, 1);
                if (character == "\\")
                {
                    _jsonScanIndex += 2;
                    continue;
                }
                _jsonScanIndex += 1;
                if (character == "\"")
                {
                    string rawString = Substr(start, _jsonScanIndex - start);
                    object decoded = GdJson.ParseString(rawString);
                    return decoded as string;
                }
            }
            return null;
        }

        bool ScanJsonLiteral(string literal)
        {
            if (Substr(_jsonScanIndex, literal.Length) != literal)
                return false;
            _jsonScanIndex += literal.Length;
            return true;
        }

        bool ScanJsonNumber()
        {
            int start = _jsonScanIndex;
            while (_jsonScanIndex < _jsonScanText.Length)
            {
                char character = _jsonScanText[_jsonScanIndex];
                if ("{}[],: \t\r\n".IndexOf(character) >= 0)
                    break;
                _jsonScanIndex += 1;
            }
            return _jsonScanIndex > start;
        }

        void SkipJsonWhitespace()
        {
            while (_jsonScanIndex < _jsonScanText.Length)
            {
                if (" \t\r\n".IndexOf(_jsonScanText[_jsonScanIndex]) < 0)
                    return;
                _jsonScanIndex += 1;
            }
        }

        // ------------------------------------------------------------------ validation

        void ClearState()
        {
            _componentBindings.Clear();
            _objectiveBindings.Clear();
            _dressingBindings.Clear();
            _errors.Clear();
        }

        static GdDict GetBinding(GdDict bindings, string key)
        {
            if (string.IsNullOrEmpty(key) || !bindings.Has(key))
                return new GdDict();
            if (!(bindings[key] is GdDict binding))
                return new GdDict();
            return binding.DeepCopy();
        }

        GdDict ValidateGroup(object value, string groupName)
        {
            var validated = new GdDict();
            if (!(value is GdDict group))
            {
                _errors.Add("catalog group " + groupName + " must be an object");
                return validated;
            }

            var idOwners = new GdDict();
            foreach (object key in group.Keys)
            {
                if (!(key is string keyStr) || keyStr.Length == 0)
                {
                    _errors.Add("catalog group " + groupName + " contains an invalid binding key");
                    continue;
                }
                if (!(group[key] is GdDict binding))
                {
                    _errors.Add("catalog binding " + groupName + "/" + keyStr + " must be an object");
                    continue;
                }
                if (!ValidateBinding(binding, groupName, keyStr))
                    continue;
                GdDict bindingCopy = binding.DeepCopy();
                var bindingMeta = (GdDict)bindingCopy["binding"];
                var ids = (GdArray)bindingMeta["ids"];
                foreach (object idValue in ids)
                {
                    string id = V.Str(idValue);
                    if (idOwners.Has(id))
                    {
                        var owner = (GdDict)idOwners[id];
                        if (!V.VariantEquals(owner.Get("asset_id", ""), bindingCopy.Get("asset_id", ""))
                            || !V.VariantEquals(owner.Get("visual_scene_path", ""), bindingCopy.Get("visual_scene_path", "")))
                            _errors.Add("catalog group " + groupName + " has colliding binding id: " + id);
                    }
                    else
                    {
                        idOwners[id] = new GdDict
                        {
                            { "asset_id", bindingCopy.Get("asset_id", "") },
                            { "visual_scene_path", bindingCopy.Get("visual_scene_path", "") },
                        };
                    }
                }
                validated[keyStr] = bindingCopy;
            }
            return validated;
        }

        bool ValidateBinding(GdDict binding, string groupName, string bindingId)
        {
            string prefix = "catalog binding " + groupName + "/" + bindingId + " ";
            bool valid = true;
            foreach (object key in binding.Keys)
            {
                if (!Has(BINDING_FIELDS, V.Str(key)))
                {
                    _errors.Add(prefix + "has unexpected field: " + V.Str(key));
                    valid = false;
                }
            }
            foreach (string fieldName in BINDING_FIELDS)
            {
                if (!binding.Has(fieldName))
                {
                    _errors.Add(prefix + "is missing field: " + fieldName);
                    valid = false;
                }
            }
            if (binding.Count != BINDING_FIELDS.Count)
                valid = false;

            if (!V.VariantEquals(binding.Get("document_kind", ""), BINDING_DOCUMENT_KIND))
            {
                _errors.Add(prefix + "has invalid document_kind");
                valid = false;
            }
            if (!IsExactSchemaVersion(binding.Get("schema_version")))
            {
                _errors.Add(prefix + "has unsupported schema_version");
                valid = false;
            }
            if (!IsAssetId(binding.Get("asset_id")))
            {
                _errors.Add(prefix + "has an invalid asset_id");
                valid = false;
            }
            if (!V.VariantEquals(binding.Get("prop_kind", ""), ExpectedPropKind(groupName)))
            {
                _errors.Add(prefix + "has invalid prop_kind");
                valid = false;
            }
            if (!V.VariantEquals(binding.Get("collision_policy", ""), "none_visual_only"))
            {
                _errors.Add(prefix + "must use collision_policy none_visual_only");
                valid = false;
            }

            object scenePath = binding.Get("visual_scene_path", "");
            if (!IsCanonicalScenePath(scenePath, groupName))
            {
                _errors.Add(prefix + "scene path must be canonical imported prop GLB");
                valid = false;
            }
            else if (ShipCompat.TrimSuffix(ResPath.GetFile(V.Str(scenePath)), ".glb") != V.Str(binding.Get("asset_id", "")))
            {
                _errors.Add(prefix + "scene basename must match asset_id");
                valid = false;
            }

            object bindingMeta = binding.Get("binding");
            if (!(bindingMeta is GdDict metaDict))
            {
                _errors.Add(prefix + "binding must be an object");
                valid = false;
            }
            else
            {
                valid = ValidateBindingMeta(metaDict, groupName, bindingId) && valid;
                object idsValue = metaDict.Get("ids");
                if (!(idsValue is GdArray ids))
                {
                    _errors.Add(prefix + "ids must include the map key");
                    valid = false;
                }
                else
                {
                    if (!ids.Contains(bindingId))
                    {
                        _errors.Add(prefix + "ids must include the map key");
                        valid = false;
                    }
                    if (groupName != "objectives" && (V.Str(binding.Get("asset_id", "")) != bindingId || ids.Count != 1))
                    {
                        _errors.Add(prefix + "must map one component/dressing id");
                        valid = false;
                    }
                }
            }

            object placement = binding.Get("placement");
            if (!(placement is GdDict placementDict))
            {
                _errors.Add(prefix + "placement must be an object");
                valid = false;
            }
            else
            {
                valid = ValidatePlacement(placementDict, groupName, bindingId) && valid;
            }

            valid = ValidateSource(binding.Get("source"), groupName, bindingId) && valid;
            valid = ValidateBounds(binding.Get("bounds"), groupName, bindingId) && valid;
            valid = ValidateProvenance(binding.Get("provenance"), groupName, bindingId) && valid;
            if (!(binding.Get("extensions") is GdDict))
            {
                _errors.Add(prefix + "extensions must be an object");
                valid = false;
            }
            return valid;
        }

        bool ValidateBindingMeta(GdDict bindingMeta, string groupName, string bindingId)
        {
            string prefix = "catalog binding " + groupName + "/" + bindingId + " ";
            bool valid = true;
            foreach (object key in bindingMeta.Keys)
            {
                string k = V.Str(key);
                if (k != "namespace" && k != "ids")
                {
                    _errors.Add(prefix + "binding has unexpected field: " + k);
                    valid = false;
                }
            }
            if (bindingMeta.Count != 2)
                valid = false;
            string expectedNamespace = ExpectedNamespace(groupName);
            if (!V.VariantEquals(bindingMeta.Get("namespace", ""), expectedNamespace))
            {
                _errors.Add(prefix + "has invalid namespace");
                valid = false;
            }
            object idsValue = bindingMeta.Get("ids");
            if (!(idsValue is GdArray ids) || ids.IsEmpty)
            {
                _errors.Add(prefix + "ids must be a nonempty array");
                return false;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object idValue in ids)
            {
                if (!IsNonemptyString(idValue))
                {
                    _errors.Add(prefix + "contains an invalid binding id");
                    valid = false;
                    continue;
                }
                string id = V.Str(idValue);
                if (seen.Contains(id))
                {
                    _errors.Add(prefix + "contains duplicate binding id: " + id);
                    valid = false;
                }
                seen.Add(id);
            }
            return valid;
        }

        bool ValidatePlacement(GdDict placement, string groupName, string bindingId)
        {
            string prefix = "catalog binding " + groupName + "/" + bindingId + " ";
            bool valid = true;
            var requiredFields = new List<string> { "origin", "offset_m", "rotation_degrees", "allowed_yaw_deg", "scale" };
            bool surfaceAllowed = groupName == "dressing" || groupName == "objectives";
            var allowedFields = new List<string>(requiredFields);
            if (surfaceAllowed)
                allowedFields.Add("surface");
            foreach (object key in placement.Keys)
            {
                if (!allowedFields.Contains(V.Str(key)))
                {
                    _errors.Add(prefix + "placement has unexpected field: " + V.Str(key));
                    valid = false;
                }
            }
            foreach (string fieldName in requiredFields)
            {
                if (!placement.Has(fieldName))
                {
                    _errors.Add(prefix + "placement is missing field: " + fieldName);
                    valid = false;
                }
            }
            if (placement.Count < requiredFields.Count || placement.Count > allowedFields.Count)
                valid = false;
            if (!GdArray.Of("scene_origin", "marker_anchor").Contains(placement.Get("origin", "")))
            {
                _errors.Add(prefix + "placement origin is invalid");
                valid = false;
            }
            if (!IsFiniteVector(placement.Get("offset_m")))
            {
                _errors.Add(prefix + "offset_m must be a finite 3-vector");
                valid = false;
            }
            if (!IsFiniteVector(placement.Get("rotation_degrees")))
            {
                _errors.Add(prefix + "rotation_degrees must be a finite 3-vector");
                valid = false;
            }
            object scaleValue = placement.Get("scale");
            if (!IsFiniteNumber(scaleValue) || V.F64(scaleValue) <= 0.0)
            {
                _errors.Add(prefix + "scale must be finite and strictly positive");
                valid = false;
            }
            object yawValues = placement.Get("allowed_yaw_deg");
            if (!(yawValues is GdArray yaws) || yaws.IsEmpty)
            {
                _errors.Add(prefix + "allowed_yaw_deg must be a nonempty array");
                valid = false;
            }
            else
            {
                var seenYaws = new GdArray();
                foreach (object yaw in yaws)
                {
                    if (!IsFiniteNumber(yaw))
                    {
                        _errors.Add(prefix + "allowed_yaw_deg must be finite");
                        valid = false;
                        continue;
                    }
                    double yawNumber = V.F64(yaw);
                    if (seenYaws.Contains(yawNumber))
                    {
                        _errors.Add(prefix + "allowed_yaw_deg contains duplicate yaw");
                        valid = false;
                    }
                    seenYaws.Add(yawNumber);
                }
            }
            if (placement.Has("surface") && !GdArray.Of("floor", "wall", "ceiling").Contains(placement.Get("surface", "")))
            {
                _errors.Add(prefix + "surface must be floor, wall, or ceiling");
                valid = false;
            }
            return valid;
        }

        bool ValidateSource(object value, string groupName, string bindingId)
        {
            string prefix = "catalog binding " + groupName + "/" + bindingId + " ";
            bool valid = ValidateExactObjectFields(value, new[] { "sha256", "byte_size", "mesh_count", "gltf_version" }, "source", groupName, bindingId);
            if (!(value is GdDict source))
                return false;
            if (!IsSha256(source.Get("sha256")))
            {
                _errors.Add(prefix + "source sha256 must be 64 lowercase hex characters");
                valid = false;
            }
            if (!IsNonnegativeIntegerValue(source.Get("byte_size")))
            {
                _errors.Add(prefix + "source byte_size must be a nonnegative integer");
                valid = false;
            }
            if (!IsPositiveIntegerValue(source.Get("mesh_count")))
            {
                _errors.Add(prefix + "source mesh_count must be a positive integer");
                valid = false;
            }
            if (!V.VariantEquals(source.Get("gltf_version", ""), "2.0"))
            {
                _errors.Add(prefix + "source gltf_version must be 2.0");
                valid = false;
            }
            return valid;
        }

        bool ValidateBounds(object value, string groupName, string bindingId)
        {
            string prefix = "catalog binding " + groupName + "/" + bindingId + " ";
            bool valid = ValidateExactObjectFields(value, new[] { "local_min_m", "local_max_m" }, "bounds", groupName, bindingId);
            if (!(value is GdDict bounds))
                return false;
            object localMin = bounds.Get("local_min_m");
            object localMax = bounds.Get("local_max_m");
            if (!IsFiniteVector(localMin) || !IsFiniteVector(localMax))
            {
                _errors.Add(prefix + "bounds must contain finite 3-vectors");
                return false;
            }
            var minValues = (GdArray)localMin;
            var maxValues = (GdArray)localMax;
            for (int index = 0; index < 3; index++)
            {
                if (V.F64(minValues[index]) > V.F64(maxValues[index]))
                {
                    _errors.Add(prefix + "bounds local_min_m must not exceed local_max_m");
                    return false;
                }
            }
            return valid;
        }

        bool ValidateProvenance(object value, string groupName, string bindingId)
        {
            string prefix = "catalog binding " + groupName + "/" + bindingId + " ";
            bool valid = ValidateExactObjectFields(value, new[] { "license_state", "source_platform" }, "provenance", groupName, bindingId);
            if (!(value is GdDict provenance))
                return false;
            foreach (string fieldName in new[] { "license_state", "source_platform" })
            {
                if (!IsNonemptyString(provenance.Get(fieldName)))
                {
                    _errors.Add(prefix + "provenance " + fieldName + " must be a nonempty string");
                    valid = false;
                }
            }
            return valid;
        }

        bool ValidateExactObjectFields(object value, string[] expectedFields, string nestedName, string groupName, string bindingId)
        {
            string prefix = "catalog binding " + groupName + "/" + bindingId + " " + nestedName + " ";
            if (!(value is GdDict objectValue))
            {
                _errors.Add(prefix + "must be an object");
                return false;
            }
            bool valid = true;
            foreach (object key in objectValue.Keys)
            {
                if (Array.IndexOf(expectedFields, V.Str(key)) < 0)
                {
                    _errors.Add(prefix + "has unexpected field: " + V.Str(key));
                    valid = false;
                }
            }
            foreach (string fieldName in expectedFields)
            {
                if (!objectValue.Has(fieldName))
                {
                    _errors.Add(prefix + "is missing field: " + fieldName);
                    valid = false;
                }
            }
            if (objectValue.Count != expectedFields.Length)
                valid = false;
            return valid;
        }

        // ------------------------------------------------------------------ predicates

        static bool Has(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value)
                    return true;
            return false;
        }

        static bool IsSha256(object value)
        {
            if (!(value is string s) || s.Length != 64)
                return false;
            if (s == new string('0', 64))
                return false;
            foreach (char code in s)
            {
                if (!((code >= 48 && code <= 57) || (code >= 97 && code <= 102)))
                    return false;
            }
            return true;
        }

        static bool IsNonnegativeIntegerValue(object value)
        {
            if (!IsFiniteNumber(value))
                return false;
            double number = V.F64(value);
            return number >= 0.0 && GdMath.IsEqualApprox(number, GdMath.Round(number));
        }

        static bool IsPositiveIntegerValue(object value)
        {
            if (!IsFiniteNumber(value))
                return false;
            double number = V.F64(value);
            return number >= 1.0 && GdMath.IsEqualApprox(number, GdMath.Round(number));
        }

        static bool IsCanonicalScenePath(object value, string groupName)
        {
            if (!IsNonemptyString(value))
                return false;
            string path = (string)value;
            if (!ShipCompat.BeginsWith(path, SCENE_PATH_PREFIX) || ShipCompat.SimplifyPath(path) != path)
                return false;
            string relative = path.Substring(SCENE_PATH_PREFIX.Length);
            string[] parts = relative.Split('/');
            if (parts.Length != 2 || parts[0] != groupName)
                return false;
            if (parts[1].Length == 0 || parts[1] == ".glb" || !ShipCompat.EndsWith(parts[1], ".glb"))
                return false;
            return Array.IndexOf(parts, "") < 0 && Array.IndexOf(parts, ".") < 0 && Array.IndexOf(parts, "..") < 0;
        }

        static string ExpectedNamespace(string groupName)
        {
            switch (groupName)
            {
                case "components": return "component_id";
                case "objectives": return "gameplay_placement_id";
                case "dressing": return "visual_prop_id";
                default: return "";
            }
        }

        static string ExpectedPropKind(string groupName)
        {
            switch (groupName)
            {
                case "components": return "component";
                case "objectives": return "objective";
                case "dressing": return "dressing";
                default: return "";
            }
        }

        static bool IsExactSchemaVersion(object value)
        {
            if (!(value is string s))
                return false;
            string[] parts = s.Split('.');
            return parts.Length == 3 && parts[0] == "1" && IsSemverNumber(parts[1]) && IsSemverNumber(parts[2]);
        }

        static bool IsSemverNumber(string value)
        {
            if (value.Length == 0 || (value.Length > 1 && value.StartsWith("0", StringComparison.Ordinal)))
                return false;
            foreach (char code in value)
            {
                if (code < 48 || code > 57)
                    return false;
            }
            return true;
        }

        static bool IsAssetId(object value)
        {
            if (!IsNonemptyString(value))
                return false;
            string identifier = (string)value;
            for (int index = 0; index < identifier.Length; index++)
            {
                int code = identifier[index];
                bool isLowercaseLetter = code >= 97 && code <= 122;
                bool isDigit = code >= 48 && code <= 57;
                if (index == 0 && !isLowercaseLetter && !isDigit)
                    return false;
                if (!isLowercaseLetter && !isDigit && code != 95 && code != 45)
                    return false;
            }
            return true;
        }

        static bool IsNonemptyString(object value) => value is string s && ShipCompat.StripEdges(s).Length != 0;

        static bool IsFiniteVector(object value)
        {
            if (!(value is GdArray values) || values.Count != 3)
                return false;
            foreach (object item in values)
            {
                if (!IsFiniteNumber(item))
                    return false;
            }
            return true;
        }

        static bool IsFiniteNumber(object value)
        {
            if (!(value is long) && !(value is double))
                return false;
            double d = V.F64(value);
            return !double.IsNaN(d) && !double.IsInfinity(d);
        }
    }
}
