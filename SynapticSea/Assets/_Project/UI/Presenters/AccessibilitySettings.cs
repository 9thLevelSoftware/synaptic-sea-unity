// Ported from scripts/ui/accessibility_settings.gd @ 96ecb2b0
using System;
using System.Globalization;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.UI.Presenters
{
    /// <summary>
    /// A11Y-P1-001 / REQ-UI-003 / ADR-0033: the single runtime sink <see cref="SettingsState.ApplyToAccessibility"/>
    /// writes to. Pure model (no engine types): it owns the text-scale multiplier plus the mirrored settings fields.
    ///
    /// Unity departure (ui_presentation_program.md "Accessible layout"): Godot multiplied whole fixed rectangles by the
    /// scale. The Unity UI reflows instead — the scale selects one of the USS size steps (<see cref="ReflowClass"/>),
    /// which redefine font/row variables while layouts wrap. <see cref="ScaledHudFontSize"/> and friends are kept for
    /// callers that still size things numerically (world labels, parity checks).
    /// </summary>
    public sealed class AccessibilitySettings : IAccessibilitySettingsSink
    {
        public const double MIN_TEXT_SCALE = 1.0;
        public const double MAX_TEXT_SCALE = 2.0;
        public const double DEFAULT_TEXT_SCALE = 1.0;
        /// <summary>Godot read a project setting here; Unity has no equivalent, so the key is kept for documentation only.</summary>
        public const string PROJECT_SETTING_KEY = "synaptic-sea/accessibility/text_scale";
        public const string ENV_VAR_NAME = "SYNAPTIC_SEA_TEXT_SCALE";

        public const string DEFAULT_COLORBLIND_MODE = "none";
        public const bool DEFAULT_MOTION_REDUCE = false;
        public const bool DEFAULT_CAPTIONS_ENABLED = true;
        public const bool DEFAULT_HOLD_TO_TAP = false;
        public const string DEFAULT_DIFFICULTY = "standard";
        public const string DEFAULT_GLYPH_SCHEME = "auto";
        public const string DEFAULT_PRESET_ID = "default";

        /// <summary>USS classes the reflow steps use (Content/UI/Theme/tokens.uss).</summary>
        public const string ClassScale150 = "scale-150";
        public const string ClassScale200 = "scale-200";

        double _textScale;
        string _colorblindMode = DEFAULT_COLORBLIND_MODE;
        bool _motionReduce = DEFAULT_MOTION_REDUCE;
        bool _captionsEnabled = DEFAULT_CAPTIONS_ENABLED;
        bool _holdToTap = DEFAULT_HOLD_TO_TAP;
        string _difficulty = DEFAULT_DIFFICULTY;
        string _glyphScheme = DEFAULT_GLYPH_SCHEME;
        string _presetId = DEFAULT_PRESET_ID;

        /// <summary>Raised after any setter changes a value, so views can re-apply classes without polling.</summary>
        public event Action Changed;

        /// <param name="environmentReader">Reads an environment variable (defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>).</param>
        public AccessibilitySettings(Func<string, string> environmentReader = null)
        {
            _textScale = ResolveTextScale(environmentReader);
        }

        public double GetTextScale() => _textScale;

        /// <summary>Clamps to [1, 2]; ignores values &lt;= 0.</summary>
        public void SetTextScale(double newScale)
        {
            if (newScale <= 0.0) return;
            _textScale = GdMath.Clampf(newScale, MIN_TEXT_SCALE, MAX_TEXT_SCALE);
            Changed?.Invoke();
        }

        public void SetColorblindMode(string mode) { _colorblindMode = mode; Changed?.Invoke(); }
        public string GetColorblindMode() => _colorblindMode;
        public void SetMotionReduce(bool value) { _motionReduce = value; Changed?.Invoke(); }
        public bool IsMotionReduce() => _motionReduce;
        public void SetCaptionsEnabled(bool value) { _captionsEnabled = value; Changed?.Invoke(); }
        public bool IsCaptionsEnabled() => _captionsEnabled;
        public void SetHoldToTap(bool value) { _holdToTap = value; Changed?.Invoke(); }
        public bool IsHoldToTap() => _holdToTap;
        public void SetDifficulty(string difficulty) { _difficulty = difficulty; Changed?.Invoke(); }
        public string GetDifficulty() => _difficulty;
        public void SetGlyphScheme(string scheme) { _glyphScheme = scheme; Changed?.Invoke(); }
        public string GetGlyphScheme() => _glyphScheme;
        public void SetPresetId(string id) { _presetId = id; Changed?.Invoke(); }
        public string GetPresetId() => _presetId;

        /// <summary>HUD font size for a base pixel value at the current scale, rounded half away from zero.</summary>
        public long ScaledHudFontSize(long baseFontSize) => (long)GdMath.Round(baseFontSize * _textScale);

        /// <summary>(x, y) scaled; Godot returned a Vector2.</summary>
        public (double x, double y) ScaledHudMinimumSize(double x, double y) => (x * _textScale, y * _textScale);

        public (double x, double y) ScaledHudPanelSize(double x, double y) => (x * _textScale, y * _textScale);

        /// <summary>World Label3D pixel_size (world units per screen pixel) divided by the scale, floored at 0.0005.</summary>
        public double ScaledWorldPixelSize(double basePixelSize) => Math.Max(basePixelSize / _textScale, 0.0005);

        /// <summary>
        /// The reflow step for the current scale: 100, 150 or 200. A scale between steps rounds UP so the requested
        /// text size is never reduced (spec: "without ... reducing the requested text size").
        /// </summary>
        public int ReflowStep()
        {
            if (_textScale <= 1.0 + 1e-6) return 100;
            if (_textScale <= 1.5 + 1e-6) return 150;
            return 200;
        }

        /// <summary>The USS class for <see cref="ReflowStep"/> ("" at 1x).</summary>
        public string ReflowClass()
        {
            switch (ReflowStep())
            {
                case 150: return ClassScale150;
                case 200: return ClassScale200;
                default: return "";
            }
        }

        /// <summary>Env var, then default (Godot's project setting has no Unity equivalent), clamped to [1, 2].</summary>
        public static double ResolveTextScale(Func<string, string> environmentReader = null)
        {
            return ClampScale(ReadEnvScale(DEFAULT_TEXT_SCALE, environmentReader ?? Environment.GetEnvironmentVariable));
        }

        static double ReadEnvScale(double fallback, Func<string, string> reader)
        {
            string raw = reader(ENV_VAR_NAME);
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return fallback;
            if (double.IsNaN(parsed) || parsed <= 0.0) return fallback;
            return parsed;
        }

        static double ClampScale(double value) => GdMath.Clampf(value, MIN_TEXT_SCALE, MAX_TEXT_SCALE);
    }
}
