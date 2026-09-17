// Ported from scripts/systems/settings_state.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The setter surface <see cref="SettingsState.ApplyToAccessibility"/> writes to (Godot duck-typed an
    /// <c>AccessibilitySettings</c> RefCounted via <c>has_method</c>). The AccessibilitySettings port implements it.
    /// </summary>
    public interface IAccessibilitySettingsSink
    {
        void SetTextScale(double newScale);
        void SetColorblindMode(string mode);
        void SetMotionReduce(bool value);
        void SetCaptionsEnabled(bool value);
        void SetHoldToTap(bool value);
        void SetDifficulty(string difficulty);
        void SetGlyphScheme(string scheme);
        void SetPresetId(string id);
    }

    /// <summary>
    /// Pure settings state (REQ-UI-003 / REQ-UI-008 / ADR-0033). A flat, schema-validated payload with typed
    /// getters/setters and a single write-back path to AccessibilitySettings.
    /// </summary>
    public class SettingsState : IStatusLineProvider
    {
        public const string SaveKey = "settings_state";

        GdDict _payload = SettingsStateSchema.DefaultPayload();

        /// <summary>Apply a fully-formed payload; rejected payloads leave the state unchanged.</summary>
        public bool Configure(GdDict payload)
        {
            if (!SettingsStateSchema.Validate(payload)) return false;
            _payload = SettingsStateSchema.Sanitize(payload);
            return true;
        }

        /// <summary>Apply a partial payload. Unknown fields are ignored.</summary>
        public bool ApplyPartial(GdDict partial)
        {
            if (partial == null) return false;
            GdDict merged = _payload.DeepCopy();
            foreach (var key in partial.Keys) merged[V.Str(key)] = partial[key];
            merged = SettingsStateSchema.Sanitize(merged);
            if (!SettingsStateSchema.Validate(merged)) return false;
            _payload = merged;
            ApplyExtensions(partial);
            return true;
        }

        public GdDict GetPayload() => _payload.DeepCopy();

        // --- typed getters ---
        public double GetTextScale() => V.F64(_payload.Get("text_scale", 1.0));
        public string GetColorblindMode() => V.Str(_payload.Get("colorblind_mode", "none"));
        public bool IsMotionReduce() => V.Bool(_payload.Get("motion_reduce", false));
        public bool IsCaptionsEnabled() => V.Bool(_payload.Get("captions", true));
        public bool IsHoldToTap() => V.Bool(_payload.Get("hold_to_tap", false));
        public string GetDifficulty() => V.Str(_payload.Get("difficulty", "standard"));
        public string GetGlyphScheme() => V.Str(_payload.Get("glyph_scheme", "auto"));
        public string GetPresetId() => V.Str(_payload.Get("preset_id", "default"));

        // --- typed setters (return true when the value was accepted) ---
        public bool SetTextScale(double scale)
        {
            if (scale < SettingsStateSchema.MinTextScale || scale > SettingsStateSchema.MaxTextScale) return false;
            _payload["text_scale"] = GdMath.Clampf(scale, SettingsStateSchema.MinTextScale, SettingsStateSchema.MaxTextScale);
            return true;
        }

        public bool SetColorblindMode(string mode)
        {
            if (!SettingsStateSchema.ColorblindModes.Contains(mode)) return false;
            _payload["colorblind_mode"] = mode;
            return true;
        }

        public bool SetMotionReduce(bool value)
        {
            _payload["motion_reduce"] = value;
            return true;
        }

        public bool SetCaptionsEnabled(bool value)
        {
            _payload["captions"] = value;
            return true;
        }

        public bool SetHoldToTap(bool value)
        {
            _payload["hold_to_tap"] = value;
            return true;
        }

        public bool SetDifficulty(string difficulty)
        {
            if (!SettingsStateSchema.Difficulties.Contains(difficulty)) return false;
            _payload["difficulty"] = difficulty;
            return true;
        }

        public bool SetGlyphScheme(string scheme)
        {
            if (!SettingsStateSchema.GlyphSchemes.Contains(scheme)) return false;
            _payload["glyph_scheme"] = scheme;
            return true;
        }

        public bool SetPresetId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            _payload["preset_id"] = id;
            return true;
        }

        /// <summary>Apply a preset dict (from <c>data/ui/accessibility_presets.json</c>); missing fields keep their values.</summary>
        public bool ApplyPresetDict(GdDict preset)
        {
            if (preset == null) return false;
            GdDict merged = _payload.DeepCopy();
            if (preset.Has("id")) merged["preset_id"] = V.Str(preset.Get("id", "default"));
            if (preset.Has("text_scale")) merged["text_scale"] = V.F64(preset.Get("text_scale", 1.0));
            if (preset.Has("colorblind_mode")) merged["colorblind_mode"] = V.Str(preset.Get("colorblind_mode", "none"));
            if (preset.Has("motion_reduce")) merged["motion_reduce"] = V.Bool(preset.Get("motion_reduce", false));
            if (preset.Has("captions")) merged["captions"] = V.Bool(preset.Get("captions", true));
            if (preset.Has("hold_to_tap")) merged["hold_to_tap"] = V.Bool(preset.Get("hold_to_tap", false));
            if (preset.Has("difficulty")) merged["difficulty"] = V.Str(preset.Get("difficulty", "standard"));
            if (preset.Has("glyph_scheme")) merged["glyph_scheme"] = V.Str(preset.Get("glyph_scheme", "auto"));
            merged = SettingsStateSchema.Sanitize(merged);
            if (!SettingsStateSchema.Validate(merged)) return false;
            _payload = merged;
            return true;
        }

        /// <summary>
        /// The ONLY path that writes to AccessibilitySettings from settings code. <paramref name="a11y"/> is any
        /// object; a non-<see cref="IAccessibilitySettingsSink"/> warns and returns false (Godot's has_method guard).
        /// </summary>
        public bool ApplyToAccessibility(object a11y)
        {
            if (a11y == null) return false;
            if (!(a11y is IAccessibilitySettingsSink sink))
            {
                CoreServices.Log.Warning("SettingsState: apply_to_accessibility argument is not an AccessibilitySettings");
                return false;
            }
            sink.SetTextScale(GetTextScale());
            sink.SetColorblindMode(GetColorblindMode());
            sink.SetMotionReduce(IsMotionReduce());
            sink.SetCaptionsEnabled(IsCaptionsEnabled());
            sink.SetHoldToTap(IsHoldToTap());
            sink.SetDifficulty(GetDifficulty());
            sink.SetGlyphScheme(GetGlyphScheme());
            sink.SetPresetId(GetPresetId());
            return true;
        }

        // Round-trip seam.
        public GdDict GetSummary()
        {
            GdDict summary = _payload.DeepCopy();
            // Unity port: preference extensions are written only once set, so Godot-shaped summaries (run saves) are unchanged.
            foreach (var key in _extensions.Keys) summary[key] = V.DeepCopy(_extensions[key]);
            return summary;
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null) return false;
            GdDict sanitized = SettingsStateSchema.Sanitize(summary);
            if (!SettingsStateSchema.Validate(sanitized)) return false;
            _payload = sanitized;
            ApplyExtensions(summary);
            return true;
        }

        // --- Unity port (gap closure B2/B5): user preferences Godot never persisted ---------------------------------
        // Bus volumes / mutes (the audio settings screen) and the language choice live in the same preferences file.
        // They are additive keys outside the Godot schema: absent until set (old files and Godot saves load unchanged),
        // and a summary without them leaves the current values alone.

        public const string AudioBusVolumesKey = "audio_bus_volumes_db";
        public const string AudioBusMutedKey = "audio_bus_muted";
        public const string LanguageKey = "language";
        public const string DefaultLanguage = "en";

        readonly GdDict _extensions = new GdDict();

        void ApplyExtensions(GdDict source)
        {
            if (source.Get(AudioBusVolumesKey, null) is GdDict volumes)
            {
                var clean = new GdDict();
                foreach (var bus in volumes.Keys)
                {
                    object v = volumes[bus];
                    if (v is double || v is long) clean[V.Str(bus)] = GdMath.Clampf(V.F64(v), -60.0, 0.0);
                }
                _extensions[AudioBusVolumesKey] = clean;
            }
            if (source.Get(AudioBusMutedKey, null) is GdDict muted)
            {
                var clean = new GdDict();
                foreach (var bus in muted.Keys)
                {
                    if (muted[bus] is bool b) clean[V.Str(bus)] = b;
                }
                _extensions[AudioBusMutedKey] = clean;
            }
            if (source.Get(LanguageKey, null) is string language && language.Length != 0)
                _extensions[LanguageKey] = language;
        }

        /// <summary>Stored bus volumes (bus id → dB); empty until the player changes one.</summary>
        public GdDict GetAudioBusVolumes() => (_extensions.Get(AudioBusVolumesKey, null) as GdDict ?? new GdDict()).DeepCopy();

        /// <summary>Stored bus mutes (bus id → bool); empty until the player changes one.</summary>
        public GdDict GetAudioBusMutes() => (_extensions.Get(AudioBusMutedKey, null) as GdDict ?? new GdDict()).DeepCopy();

        public bool SetAudioBusVolumeDb(string busId, double volumeDb)
        {
            if (string.IsNullOrEmpty(busId)) return false;
            GdDict volumes = GetAudioBusVolumes();
            volumes[busId] = GdMath.Clampf(volumeDb, -60.0, 0.0);
            _extensions[AudioBusVolumesKey] = volumes;
            return true;
        }

        public bool SetAudioBusMuted(string busId, bool muted)
        {
            if (string.IsNullOrEmpty(busId)) return false;
            GdDict mutes = GetAudioBusMutes();
            mutes[busId] = muted;
            _extensions[AudioBusMutedKey] = mutes;
            return true;
        }

        public string GetLanguage() => V.Str(_extensions.Get(LanguageKey, DefaultLanguage));

        public bool SetLanguage(string languageId)
        {
            if (string.IsNullOrEmpty(languageId)) return false;
            _extensions[LanguageKey] = languageId;
            return true;
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("SettingsState: text_scale=" + GdFloatFormat.FormatFixed(GetTextScale(), 2)
                + " colorblind=" + GetColorblindMode()
                + " motion_reduce=" + V.Str(IsMotionReduce())
                + " captions=" + V.Str(IsCaptionsEnabled())
                + " hold_to_tap=" + V.Str(IsHoldToTap()));
            lines.Add("  difficulty=" + GetDifficulty() + " glyph_scheme=" + GetGlyphScheme() + " preset_id=" + GetPresetId());
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();
    }
}
