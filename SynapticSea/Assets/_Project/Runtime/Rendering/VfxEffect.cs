// Ported from scenes/vfx/*.tscn @ 96ecb2b0 (the AnimationPlayer "pulse"/"flicker" autoplay tracks)
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Root of a VFX prefab built by the editor VfxPrefabBuilder from a Godot <c>scenes/vfx/*.tscn</c>. Plays the
    /// scene's autoplay animation: looping, linearly interpolated keyframes on an OmniLight3D <c>light_energy</c> (point
    /// light intensity = energy × <see cref="AtmosphereApplier.OmniEnergyScale"/>) or on a material's
    /// <c>emission_energy_multiplier</c> (the renderer gets its own material instance on first use, so shared prefab
    /// materials are never modified). Lights are set from their Godot energy on enable, so a recalibrated scale applies
    /// to every spawned effect.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VfxEffect : MonoBehaviour
    {
        public enum TrackTarget { LightEnergy, EmissionEnergy }

        [Serializable]
        public sealed class Track
        {
            public TrackTarget target;
            /// <summary>The Light (LightEnergy) or Renderer (EmissionEnergy) the Godot NodePath resolved to.</summary>
            public Component component;
            public float[] times = Array.Empty<float>();
            public float[] values = Array.Empty<float>();
        }

        [Serializable]
        public sealed class LightBinding
        {
            public Light light;
            public float godotEnergy;
        }

        [Serializable]
        public sealed class EmissionBinding
        {
            public Renderer renderer;
            /// <summary>Godot <c>emission</c> colour (sRGB) before <c>emission_energy_multiplier</c>.</summary>
            public Color godotEmission;
        }

        public string vfxId;
        public string godotScene;
        public string animationName;
        public float animationLength;
        public bool animationLoops = true;
        public List<LightBinding> lights = new List<LightBinding>();
        public List<EmissionBinding> emissions = new List<EmissionBinding>();
        public List<Track> tracks = new List<Track>();

        float _time;
        readonly Dictionary<Renderer, Material> _instances = new Dictionary<Renderer, Material>();
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        void OnEnable()
        {
            ApplyLightEnergies();
            _time = 0f;
            Evaluate(0f);
        }

        /// <summary>Point-light intensity = Godot energy × the current <see cref="AtmosphereApplier.OmniEnergyScale"/>.</summary>
        public void ApplyLightEnergies()
        {
            foreach (var l in lights)
                if (l.light != null) l.light.intensity = l.godotEnergy * AtmosphereApplier.OmniEnergyScale;
        }

        void Update()
        {
            if (tracks.Count == 0 || animationLength <= 0f) return;
            _time += Time.deltaTime;
            if (animationLoops) _time %= animationLength;
            Evaluate(Mathf.Min(_time, animationLength));
        }

        void OnDestroy()
        {
            foreach (var m in _instances.Values)
                if (m != null) Destroy(m);
        }

        /// <summary>Applies every track at <paramref name="time"/> seconds into the animation.</summary>
        public void Evaluate(float time)
        {
            foreach (var t in tracks)
            {
                if (t.component == null || t.times.Length == 0) continue;
                float v = Sample(t, time);
                if (t.target == TrackTarget.LightEnergy && t.component is Light light)
                    light.intensity = v * AtmosphereApplier.OmniEnergyScale;
                else if (t.target == TrackTarget.EmissionEnergy && t.component is Renderer renderer && Application.isPlaying)
                    SetEmissionEnergy(renderer, v);
            }
        }

        public static float Sample(Track t, float time)
        {
            if (time <= t.times[0]) return t.values[0];
            for (int i = 1; i < t.times.Length; i++)
            {
                if (time > t.times[i]) continue;
                float span = t.times[i] - t.times[i - 1];
                float k = span > 0f ? (time - t.times[i - 1]) / span : 1f;
                return Mathf.Lerp(t.values[i - 1], t.values[i], k);
            }
            return t.values[t.values.Length - 1];
        }

        void SetEmissionEnergy(Renderer renderer, float energy)
        {
            Color baseEmission = Color.black;
            foreach (var e in emissions)
                if (e.renderer == renderer) { baseEmission = e.godotEmission; break; }
            if (!_instances.TryGetValue(renderer, out Material m) || m == null)
            {
                m = renderer.material; // per-instance copy
                _instances[renderer] = m;
            }
            m.SetColor(EmissionColorId, EmissionColor(baseEmission, energy));
        }

        /// <summary>Godot emission: linear(emission) × energy; returned in the gamma form <c>Material.SetColor</c> expects.</summary>
        public static Color EmissionColor(Color godotEmission, float energy) =>
            (new Color(godotEmission.r, godotEmission.g, godotEmission.b, 1f).linear * energy).gamma;
    }
}
