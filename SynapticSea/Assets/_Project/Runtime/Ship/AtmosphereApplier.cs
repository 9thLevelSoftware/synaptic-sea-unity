// Ported from scripts/procgen/slice_atmosphere_applier.gd @ 96ecb2b0
using SynapticSea.Core.Variant;
using UnityEngine;
using UnityEngine.Rendering;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Applies a biome's <c>atmosphere</c> block (data/procgen/biomes/*.json) to the Unity scene: flat ambient light,
    /// exponential fog (denser when away), the key directional light, and the emergency accent point light.
    /// Godot's WorldEnvironment maps onto <see cref="RenderSettings"/>; light energies go through the calibration
    /// scales below. Returns the same summary keys as the Godot applier.
    /// </summary>
    public static class AtmosphereApplier
    {
        public static readonly Color DefaultAmbientColor = Hex("1a2430");
        public const float DefaultAmbientEnergy = 0.35f;
        public static readonly Color DefaultFogColor = Hex("2a3540");
        public const float DefaultFogDensity = 0.02f;
        public static readonly Color DefaultKeyColor = Hex("c8d4e0");
        public const float DefaultKeyEnergy = 0.55f;
        public const float DefaultAwayFogMultiplier = 1.6f;
        public const float DefaultEmergencyAccentEnergy = 0.16f;

        /// <summary>Godot DirectionalLight3D energy → URP intensity. Calibrated against Godot captures.</summary>
        public static float DirectionalEnergyScale = 1.0f;

        /// <summary>Godot OmniLight3D energy → URP point-light intensity. Calibrated against Godot captures.</summary>
        public static float OmniEnergyScale = 1.0f;

        public const string KeyLightName = "SliceAtmosphereKeyLight";
        public const string AccentLightName = "SliceAtmosphereEmergencyAccent";

        public static GdDict Apply(Transform root, GdDict atmosphere, bool isAway)
        {
            if (root == null) return new GdDict { { "applied", false }, { "reason", "null_target" } };
            atmosphere = atmosphere ?? new GdDict();

            Color ambient = ColorValue(atmosphere.Get("ambient_color", "#1a2430"), DefaultAmbientColor);
            float ambientEnergy = NonNegative(atmosphere.Get("ambient_energy", (double)DefaultAmbientEnergy), DefaultAmbientEnergy);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = ambient * ambientEnergy;

            bool fogEnabled = V.Bool(atmosphere.Get("fog_enabled", false));
            float fogDensity = NonNegative(atmosphere.Get("fog_density", (double)DefaultFogDensity), DefaultFogDensity);
            if (isAway)
            {
                float mult = NonNegative(atmosphere.Get("away_fog_density_mult", (double)DefaultAwayFogMultiplier), DefaultAwayFogMultiplier);
                fogDensity *= Mathf.Max(1f, mult);
            }
            RenderSettings.fog = fogEnabled;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.fogColor = ColorValue(atmosphere.Get("fog_light_color", "#2a3540"), DefaultFogColor);

            var key = ResolveKeyLight(root);
            key.color = ColorValue(atmosphere.Get("key_light_color", "#c8d4e0"), DefaultKeyColor);
            key.intensity = NonNegative(atmosphere.Get("key_light_energy", (double)DefaultKeyEnergy), DefaultKeyEnergy) * DirectionalEnergyScale;

            Light accent = ApplyEmergencyAccent(root, atmosphere);
            return new GdDict
            {
                { "applied", true },
                { "fog_enabled", fogEnabled },
                { "fog_density", (double)fogDensity },
                { "ambient_energy", (double)ambientEnergy },
                { "key_light_applied", true },
                { "emergency_accent_applied", accent != null },
                { "is_away", isAway },
            };
        }

        static Light ResolveKeyLight(Transform root)
        {
            foreach (var l in root.GetComponentsInChildren<Light>(true))
                if (l.type == LightType.Directional) return l;
            var go = new GameObject(KeyLightName);
            go.transform.SetParent(root, false);
            go.transform.localRotation = Frame.LightRotation(new Vec3(-55f, -35f, 0f));
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            return light;
        }

        static Light ApplyEmergencyAccent(Transform root, GdDict atmosphere)
        {
            object raw = atmosphere.Get("emergency_accent");
            if (raw == null || V.Str(raw).Length == 0) return null;
            var existing = root.Find(AccentLightName);
            Light accent = existing != null ? existing.GetComponent<Light>() : null;
            if (accent == null)
            {
                var go = new GameObject(AccentLightName);
                go.transform.SetParent(root, false);
                go.transform.localPosition = Frame.ToUnity(new Vec3(0f, 2.5f, 0f));
                accent = go.AddComponent<Light>();
                accent.type = LightType.Point;
                accent.range = 12f;
                accent.shadows = LightShadows.None;
            }
            accent.color = ColorValue(raw, Hex("ff6a3d"));
            accent.intensity = NonNegative(atmosphere.Get("emergency_accent_energy", (double)DefaultEmergencyAccentEnergy), DefaultEmergencyAccentEnergy) * OmniEnergyScale;
            return accent;
        }

        /// <summary>Godot <c>Color.from_string</c> for "#rrggbb"/"rrggbb"/"#rrggbbaa", or [r, g, b] arrays.</summary>
        public static Color ColorValue(object value, Color fallback)
        {
            if (value is string s)
            {
                string hex = s.StartsWith("#") ? s : "#" + s;
                return ColorUtility.TryParseHtmlString(hex, out Color c) ? c : fallback;
            }
            if (value is GdArray a && a.Count >= 3)
                return new Color((float)V.F64(a[0]), (float)V.F64(a[1]), (float)V.F64(a[2]), 1f);
            return fallback;
        }

        static float NonNegative(object value, float fallback)
        {
            double parsed = fallback;
            if (value is double || value is long) parsed = V.F64(value);
            else if (value is string str) parsed = V.StringToFloat(str);
            if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return fallback;
            return (float)System.Math.Max(0.0, parsed);
        }

        static Color Hex(string hex) => ColorUtility.TryParseHtmlString("#" + hex, out Color c) ? c : Color.magenta;
    }
}
