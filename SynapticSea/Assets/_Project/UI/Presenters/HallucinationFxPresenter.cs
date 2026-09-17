// Ported from scripts/ui/hallucination_fx_overlay.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.UI.Presenters
{
    /// <summary>
    /// The render side of the hallucination screen effect. Implemented later by the URP Full Screen Pass renderer
    /// feature (<c>SG_Hallucination</c>, placed after post-processing so the UI stays clean — docs/unity-port-plan.md
    /// Phase 10). It is deliberately NOT built here.
    /// </summary>
    public interface IHallucinationFxSink
    {
        /// <param name="intensity">Clamped 0..1 hallucination intensity.</param>
        /// <param name="tintAlpha">Godot's v1 tint alpha (intensity × <see cref="HallucinationFxPresenter.MaxTintAlpha"/>).</param>
        /// <param name="reduceMotion">When true the effect must drop warp/pulse motion and keep only static tint.</param>
        void ApplyHallucinationFx(double intensity, double tintAlpha, bool reduceMotion);
    }

    /// <summary>
    /// ADR-0042 Task 5: UI-side intensity presenter. HallucinationManager writes the intensity (derived from the sanity
    /// tier) each frame; this presenter clamps it, derives the Godot tint alpha, and forwards it to the renderer hook.
    /// Godot rendered a full-rect red ColorRect; the Unity effect is a URP renderer feature (see
    /// <see cref="IHallucinationFxSink"/>), so this class owns only the value.
    ///
    /// RENDERER HOOK: the URP feature either registers itself via <see cref="Sink"/> or polls <see cref="Intensity"/> /
    /// <see cref="TintAlpha"/> and sets the global shader float named <see cref="ShaderIntensityProperty"/>.
    /// </summary>
    public sealed class HallucinationFxPresenter
    {
        public const double MaxTintAlpha = 0.35;
        /// <summary>Suggested global shader property for the renderer feature.</summary>
        public const string ShaderIntensityProperty = "_SS_HallucinationIntensity";

        double _intensity;
        bool _reduceMotion;
        IHallucinationFxSink _sink;

        /// <summary>Raised when the clamped intensity or the reduce-motion flag changes.</summary>
        public event Action<double> IntensityChanged;

        public double Intensity => _intensity;

        /// <summary>Tint alpha the Godot v1 overlay would show (0..0.35).</summary>
        public double TintAlpha => _intensity * MaxTintAlpha;

        public bool ReduceMotion => _reduceMotion;

        public IHallucinationFxSink Sink
        {
            get => _sink;
            set
            {
                _sink = value;
                Push();
            }
        }

        /// <summary>Godot's <c>set_meta("hallucination_intensity", v)</c>; clamped to 0..1.</summary>
        public void SetIntensity(double value)
        {
            double clamped = double.IsNaN(value) ? 0.0 : GdMath.Clampf(value, 0.0, 1.0);
            if (clamped == _intensity) return;
            _intensity = clamped;
            Push();
        }

        /// <summary>Reduced-motion preference (AccessibilitySettings.IsMotionReduce). The value is kept; only motion drops.</summary>
        public void SetReduceMotion(bool value)
        {
            if (value == _reduceMotion) return;
            _reduceMotion = value;
            Push();
        }

        void Push()
        {
            _sink?.ApplyHallucinationFx(_intensity, TintAlpha, _reduceMotion);
            IntensityChanged?.Invoke(_intensity);
        }
    }
}
