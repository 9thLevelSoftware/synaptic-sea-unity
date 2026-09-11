// Ported from scripts/schemas/settings_state_schema.gd @ 96ecb2b0
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Static validation for <see cref="SettingsState"/> payloads (REQ-UI-003 / ADR-0033).
    /// Unknown extra fields are ignored (forward-compat).
    /// </summary>
    public static class SettingsStateSchema
    {
        public const string SchemaVersion = "settings-state-1";

        public static readonly GdArray ColorblindModes = GdArray.Of("none", "protanopia", "deuteranopia", "tritanopia");
        public static readonly GdArray Difficulties = GdArray.Of("standard", "hardened", "deep_dive");
        public static readonly GdArray GlyphSchemes = GdArray.Of("auto", "keyboard", "gamepad_xbox", "gamepad_ps");

        public const double MinTextScale = 1.0;
        public const double MaxTextScale = 2.0;
        public const string DefaultPreset = "default";

        static readonly string[] BoolFields = { "motion_reduce", "captions", "hold_to_tap" };

        public static bool Validate(object payload)
        {
            if (payload == null || !(payload is GdDict dict))
            {
                CoreServices.Log.Error("SettingsStateSchema: payload must be a Dictionary; got " + InfraCompat.TypeOf(payload));
                return false;
            }
            if (V.Str(dict.Get("schema", "")) != SchemaVersion)
            {
                CoreServices.Log.Error("SettingsStateSchema: schema version mismatch (expected " + SchemaVersion + ")");
                return false;
            }
            object textScale = dict.Get("text_scale", 1.0);
            if (!(textScale is double) && !(textScale is long))
            {
                CoreServices.Log.Error("SettingsStateSchema: text_scale must be a number");
                return false;
            }
            double scaleValue = V.F64(textScale);
            if (scaleValue < MinTextScale || scaleValue > MaxTextScale)
            {
                CoreServices.Log.Error("SettingsStateSchema: text_scale " + GdFloatFormat.FormatFixed(scaleValue, 3)
                    + " out of range [" + GdFloatFormat.FormatFixed(MinTextScale, 1) + ", " + GdFloatFormat.FormatFixed(MaxTextScale, 1) + "]");
                return false;
            }
            string colorblind = V.Str(dict.Get("colorblind_mode", "none"));
            if (!ColorblindModes.Contains(colorblind))
            {
                CoreServices.Log.Error("SettingsStateSchema: colorblind_mode '" + colorblind + "' is not in allowlist");
                return false;
            }
            string difficulty = V.Str(dict.Get("difficulty", "standard"));
            if (!Difficulties.Contains(difficulty))
            {
                CoreServices.Log.Error("SettingsStateSchema: difficulty '" + difficulty + "' is not in allowlist");
                return false;
            }
            string scheme = V.Str(dict.Get("glyph_scheme", "auto"));
            if (!GlyphSchemes.Contains(scheme))
            {
                CoreServices.Log.Error("SettingsStateSchema: glyph_scheme '" + scheme + "' is not in allowlist");
                return false;
            }
            string presetId = V.Str(dict.Get("preset_id", DefaultPreset));
            if (presetId.Length == 0)
            {
                CoreServices.Log.Error("SettingsStateSchema: preset_id must not be empty");
                return false;
            }
            foreach (string boolField in BoolFields)
            {
                object value = dict.Get(boolField, false);
                if (!(value is bool))
                {
                    CoreServices.Log.Error("SettingsStateSchema: " + boolField + " must be a bool (got " + InfraCompat.TypeOf(value) + ")");
                    return false;
                }
            }
            return true;
        }

        /// <summary>The defaults dict used to bootstrap a brand-new state.</summary>
        public static GdDict DefaultPayload()
        {
            return new GdDict
            {
                { "schema", SchemaVersion },
                { "text_scale", 1.0 },
                { "colorblind_mode", "none" },
                { "motion_reduce", false },
                { "captions", true },
                { "hold_to_tap", false },
                { "difficulty", "standard" },
                { "glyph_scheme", "auto" },
                { "preset_id", DefaultPreset },
            };
        }

        /// <summary>
        /// Sanitized payload: missing fields filled from defaults, out-of-range fields clamped.
        /// Never produces an invalid payload.
        /// </summary>
        public static GdDict Sanitize(object payload)
        {
            GdDict result = DefaultPayload();
            if (payload == null || !(payload is GdDict dict)) return result;
            object scaleValue = dict.Get("text_scale", result["text_scale"]);
            if (scaleValue is double || scaleValue is long)
                result["text_scale"] = GdMath.Clampf(V.F64(scaleValue), MinTextScale, MaxTextScale);
            string colorblind = V.Str(dict.Get("colorblind_mode", result["colorblind_mode"]));
            if (ColorblindModes.Contains(colorblind)) result["colorblind_mode"] = colorblind;
            string difficulty = V.Str(dict.Get("difficulty", result["difficulty"]));
            if (Difficulties.Contains(difficulty)) result["difficulty"] = difficulty;
            string scheme = V.Str(dict.Get("glyph_scheme", result["glyph_scheme"]));
            if (GlyphSchemes.Contains(scheme)) result["glyph_scheme"] = scheme;
            string presetId = V.Str(dict.Get("preset_id", result["preset_id"]));
            if (presetId.Length != 0) result["preset_id"] = presetId;
            foreach (string boolField in BoolFields)
            {
                object raw = dict.Get(boolField, result[boolField]);
                if (raw is bool) result[boolField] = raw;
            }
            return result;
        }
    }
}
