// Ported from scripts/ui/hallucination_fx_overlay.gd @ 96ecb2b0
using System;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Hallucination screen FX driver. Godot's <c>HallucinationFXOverlay</c> was a CanvasLayer whose red ColorRect alpha
    /// followed the <c>hallucination_intensity</c> meta (0..1) that HallucinationManager wrote each frame. Here the
    /// manager calls <see cref="SetIntensity"/>; <see cref="HallucinationRendererFeature"/> renders the effect after
    /// post-processing and skips entirely while the intensity is 0. <see cref="SetMotionReduce"/> mirrors the
    /// accessibility <c>motion_reduce</c> setting (no warp, no vignette pulse).
    ///
    /// The values are process-wide statics because the renderer feature lives on the pipeline asset, not in a scene;
    /// the component only resets them when it is disabled so a torn-down scene never leaves the screen tinted.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HallucinationFx : MonoBehaviour
    {
        /// <summary>Godot MAX_TINT_ALPHA: the red tint's alpha at intensity 1.</summary>
        public const float MaxTintAlpha = 0.35f;

        /// <summary>Clamped 0..1 intensity the renderer feature reads.</summary>
        public static float Intensity { get; private set; }

        public static bool MotionReduce { get; private set; }

        /// <summary>Godot: <c>set_meta("hallucination_intensity", v)</c>; clamped like <c>clampf(v, 0, 1)</c>.</summary>
        public void SetIntensity(double intensity) => SetGlobalIntensity(intensity);

        public void SetMotionReduce(bool motionReduce) => SetGlobalMotionReduce(motionReduce);

        public static void SetGlobalMotionReduce(bool motionReduce) => MotionReduce = motionReduce;

        public static void SetGlobalIntensity(double intensity) =>
            Intensity = double.IsNaN(intensity) ? 0f : (float)Math.Min(1.0, Math.Max(0.0, intensity));

        /// <summary>The Godot overlay's tint alpha for the current intensity (<c>intensity * MAX_TINT_ALPHA</c>).</summary>
        public static float TintAlpha => Intensity * MaxTintAlpha;

        void OnDisable() => Intensity = 0f;
    }
}
