using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SynapticSea.Tests.Unity
{
    /// <summary>Locks the URP configuration applied by RenderingSetup (plan Phase 9).</summary>
    public class RenderingSettingsTests
    {
        [Test]
        public void PipelineIsConfiguredForTheInteriorLook()
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            Assert.IsNotNull(pipeline);
            Assert.AreSame(pipeline, GraphicsSettings.defaultRenderPipeline);
            Assert.IsTrue(pipeline.supportsHDR, "HDR is required for tonemapping and emissive bloom");
            Assert.AreEqual(4, pipeline.msaaSampleCount);
            Assert.AreEqual(2, pipeline.shadowCascadeCount);

            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset");
            Assert.AreEqual(RenderingMode.ForwardPlus, renderer.renderingMode);
            Assert.IsTrue(renderer.rendererFeatures.OfType<ScreenSpaceAmbientOcclusion>().Any(f => f.isActive));
            Assert.IsTrue(renderer.rendererFeatures.OfType<DecalRendererFeature>().Any(f => f.isActive));
        }

        [Test]
        public void GlobalVolumeProfileHasTheCoreOverrides()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/Volumes/SS_GlobalVolume.asset");
            Assert.IsNotNull(profile);
            Assert.IsTrue(profile.TryGet(out Tonemapping tone) && tone.mode.overrideState && tone.mode.value == TonemappingMode.Neutral,
                "Neutral matches the Godot light level (ACES crushed it; docs/port-status.md)");
            Assert.IsTrue(profile.TryGet(out Bloom bloom) && bloom.intensity.overrideState);
            Assert.IsTrue(profile.TryGet(out Vignette vignette) && vignette.intensity.overrideState);
            Assert.IsTrue(profile.TryGet(out ColorAdjustments color) && color.contrast.overrideState);
        }
    }
}
