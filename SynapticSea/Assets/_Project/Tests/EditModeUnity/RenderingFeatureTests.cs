using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SynapticSea.Tests.Unity
{
    /// <summary>Phase 9 rendering pieces: the ceiling dither shader and the hallucination full-screen pass.</summary>
    public class RenderingFeatureTests
    {
        const string DitherShaderPath = "Assets/Content/Shaders/SS_LitDitherFade.shader";
        const string HallucinationShaderPath = "Assets/Content/Shaders/SS_Hallucination.shader";

        /// <summary>
        /// LightMode tags of the URP subshader. Read from the shader data rather than <c>Shader.passCount</c>: until URP
        /// renders a frame (batch-mode tests), <c>Shader.globalRenderPipeline</c> is empty and every UniversalPipeline
        /// subshader, URP Lit's included, reports the fallback's single pass.
        /// </summary>
        static List<string> LightModes(Shader shader)
        {
            var data = ShaderUtil.GetShaderData(shader);
            var modes = new List<string>();
            for (int s = 0; s < data.SubshaderCount; s++)
            {
                var sub = data.GetSubshader(s);
                if (sub.FindTagValue(new ShaderTagId("RenderPipeline")).name != "UniversalPipeline") continue;
                for (int p = 0; p < sub.PassCount; p++)
                {
                    var pass = sub.GetPass(p);
                    // Unity reports built-in LightModes upper-cased (SHADOWCASTER).
                    modes.Add(pass.FindTagValue(new ShaderTagId("LightMode")).name.ToLowerInvariant());
                    foreach (var type in new[] { ShaderType.Vertex, ShaderType.Fragment })
                    {
                        var info = pass.CompileVariant(type, new string[0], ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
                        Assert.IsTrue(info.Success, $"{pass.Name} {type}: " + string.Join("\n", info.Messages.Select(m => m.message)));
                    }
                }
                break; // the shader's own subshader; later ones come from the FallBack
            }
            return modes;
        }

        static void AssertCompiles(Shader shader)
        {
            Assert.IsNotNull(shader);
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader), "shader has errors");
            var errors = ShaderUtil.GetShaderMessages(shader).Where(m => m.severity == ShaderCompilerMessageSeverity.Error).ToList();
            Assert.IsEmpty(errors, string.Join("\n", errors.Select(e => $"{e.file}:{e.line} {e.message}")));
            Assert.IsTrue(shader.isSupported);
        }

        [Test]
        public void DitherFadeShaderCompilesWithEveryPassItNeeds()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(DitherShaderPath);
            AssertCompiles(shader);
            Assert.AreEqual(CeilingFadeController.ShaderName, shader.name);
            var modes = LightModes(shader);
            foreach (var required in new[] { "universalforward", "shadowcaster", "depthonly", "depthnormals" })
                Assert.Contains(required, modes, $"missing {required} pass");
            foreach (var property in new[] { "_Fade", "_BaseColor", "_BaseMap", "_Metallic", "_Smoothness", "_EmissionColor", "_Cull" })
                Assert.GreaterOrEqual(shader.FindPropertyIndex(property), 0, property);
        }

        [Test]
        public void HallucinationShaderCompiles()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(HallucinationShaderPath);
            AssertCompiles(shader);
            Assert.AreEqual(HallucinationRendererFeature.ShaderName, shader.name);
            Assert.AreEqual(1, LightModes(shader).Count, "one full-screen pass, compiled");
            Assert.GreaterOrEqual(shader.FindPropertyIndex("_Intensity"), 0);
            Assert.GreaterOrEqual(shader.FindPropertyIndex("_MotionReduce"), 0);
        }

        [Test]
        public void RendererHasTheHallucinationPassAfterPostProcessingAndItIdlesAtZero()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset");
            var feature = renderer.rendererFeatures.OfType<HallucinationRendererFeature>().SingleOrDefault();
            Assert.IsNotNull(feature, "PC_Renderer lacks the hallucination feature (run RenderingSetup.Apply)");
            Assert.IsTrue(feature.isActive);
            Assert.IsNotNull(feature.Shader);
            Assert.AreEqual(RenderPassEvent.AfterRenderingPostProcessing, feature.InjectionPoint);
            try
            {
                HallucinationFx.SetGlobalIntensity(0.0);
                Assert.IsFalse(feature.WillRender, "intensity 0 must skip the pass");
                HallucinationFx.SetGlobalIntensity(0.6);
                Assert.IsTrue(feature.WillRender);
                HallucinationFx.SetGlobalIntensity(3.0);
                Assert.AreEqual(1f, HallucinationFx.Intensity, "clamped like clampf(v, 0, 1)");
                Assert.AreEqual(HallucinationFx.MaxTintAlpha, HallucinationFx.TintAlpha, 1e-6f, "Godot tint alpha = intensity * 0.35");
                HallucinationFx.SetGlobalIntensity(-1.0);
                Assert.AreEqual(0f, HallucinationFx.Intensity);
            }
            finally
            {
                HallucinationFx.SetGlobalIntensity(0.0);
            }
        }

        [Test]
        public void HallucinationComponentForwardsIntensityAndMotionReduce()
        {
            var go = new GameObject("HallucinationFx");
            try
            {
                var fx = go.AddComponent<HallucinationFx>();
                fx.SetIntensity(0.25);
                fx.SetMotionReduce(true);
                Assert.AreEqual(0.25f, HallucinationFx.Intensity, 1e-6f);
                Assert.IsTrue(HallucinationFx.MotionReduce);
            }
            finally
            {
                Object.DestroyImmediate(go);
                HallucinationFx.SetGlobalIntensity(0.0);
                HallucinationFx.SetGlobalMotionReduce(false);
            }
        }
    }
}
