// Hallucination screen FX (port of scripts/ui/hallucination_fx_overlay.gd @ 96ecb2b0).
// Godot drew a full-rect ColorRect (0.6, 0, 0.05) whose alpha was hallucination_intensity * 0.35. That tint is kept
// (blended in sRGB like Godot's canvas) and the plan's Phase 9 distortion is layered on top, all driven by the same
// 0..1 intensity: chromatic offset, a slow UV warp, desaturation and a pulsing vignette. _MotionReduce = 1 removes the
// warp and the pulse (the vignette stays, static). Runs after post-processing via HallucinationRendererFeature.
Shader "SynapticSea/Hallucination"
{
    Properties
    {
        _Intensity("Intensity", Range(0, 1)) = 0
        _MotionReduce("Motion Reduce", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "Hallucination"

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Intensity;
            float _MotionReduce;

            static const half3 kTintColor = half3(0.6h, 0.0h, 0.05h); // Godot _tint.color
            static const half kMaxTintAlpha = 0.35h;                  // Godot MAX_TINT_ALPHA
            static const float kChromaOffset = 0.006;                 // UV units at intensity 1, screen edge
            static const float kWarpAmplitude = 0.0045;
            static const half kDesaturation = 0.55h;
            static const half kVignette = 0.45h;
            static const half kVignettePulse = 0.2h;

            half3 SampleSource(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, clamp(uv, 0.0, 1.0)).rgb;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float intensity = saturate(_Intensity);
                float motion = 1.0 - saturate(_MotionReduce);
                float t = _Time.y;
                float2 uv = input.texcoord;
                float2 fromCenter = uv - 0.5;

                // Warp: two slow, crossed sine fields.
                float2 warp = float2(sin(uv.y * 23.0 + t * 1.7), cos(uv.x * 19.0 - t * 1.3)) * (kWarpAmplitude * intensity * motion);
                float2 baseUv = uv + warp;

                // Chromatic offset grows toward the screen edge.
                float2 chroma = fromCenter * (kChromaOffset * 2.0 * intensity);
                half3 color;
                color.r = SampleSource(baseUv + chroma).r;
                color.g = SampleSource(baseUv).g;
                color.b = SampleSource(baseUv - chroma).b;

                // Desaturate toward luma.
                half luma = dot(color, half3(0.2126h, 0.7152h, 0.0722h));
                color = lerp(color, luma.xxx, kDesaturation * intensity);

                // Godot tint: ColorRect alpha blend in sRGB.
                half3 srgb = LinearToSRGB(color);
                srgb = lerp(srgb, kTintColor, kMaxTintAlpha * intensity);
                color = SRGBToLinear(srgb);

                // Vignette, pulsing at ~0.35 Hz unless motion is reduced.
                half pulse = 1.0h + kVignettePulse * (half)sin(t * 2.2) * (half)motion;
                half edge = saturate((half)dot(fromCenter, fromCenter) * 2.2h);
                color *= 1.0h - saturate(kVignette * intensity * pulse * edge);

                return half4(color, 1.0h);
            }
            ENDHLSL
        }
    }
}
