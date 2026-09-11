using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Full-screen pass for the hallucination FX (<c>SynapticSea/Hallucination</c>), injected after post-processing so
    /// it distorts the tonemapped frame but not UI Toolkit panels (they composite after the camera). The pass is only
    /// enqueued while <see cref="HallucinationFx.Intensity"/> is above zero, so the idle cost is one float compare.
    /// Added to PC_Renderer by the editor RenderingSetup.
    /// </summary>
    public sealed class HallucinationRendererFeature : ScriptableRendererFeature
    {
        public const string ShaderName = "SynapticSea/Hallucination";

        [SerializeField] Shader shader;
        [SerializeField] RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

        Material _material;
        HallucinationPass _pass;

        public Shader Shader { get => shader; set => shader = value; }
        public RenderPassEvent InjectionPoint => injectionPoint;

        /// <summary>True when the pass would be enqueued this frame (intensity above zero and a shader present).</summary>
        public bool WillRender => HallucinationFx.Intensity > 0f && isActive && (shader != null || Shader.Find(ShaderName) != null);

        public override void Create()
        {
            _pass = new HallucinationPass { renderPassEvent = injectionPoint };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            float intensity = HallucinationFx.Intensity;
            if (intensity <= 0f) return;
            var cameraType = renderingData.cameraData.cameraType;
            if (cameraType != CameraType.Game && cameraType != CameraType.SceneView) return;
            if (_material == null)
            {
                if (shader == null) shader = Shader.Find(ShaderName);
                if (shader == null) return;
                _material = CoreUtils.CreateEngineMaterial(shader);
            }
            _pass.renderPassEvent = injectionPoint;
            _pass.Setup(_material, intensity, HallucinationFx.MotionReduce);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_material);
            _material = null;
        }

        sealed class HallucinationPass : ScriptableRenderPass
        {
            static readonly int IntensityId = Shader.PropertyToID("_Intensity");
            static readonly int MotionReduceId = Shader.PropertyToID("_MotionReduce");

            Material _material;

            public HallucinationPass()
            {
                profilingSampler = new ProfilingSampler("SS Hallucination");
                // The effect reads the post-processed colour, so URP must keep an intermediate target.
                requiresIntermediateTexture = true;
            }

            public void Setup(Material material, float intensity, bool motionReduce)
            {
                _material = material;
                _material.SetFloat(IntensityId, intensity);
                _material.SetFloat(MotionReduceId, motionReduce ? 1f : 0f);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (_material == null || resources.isActiveTargetBackBuffer) return;
                TextureHandle source = resources.activeColorTexture;
                var desc = renderGraph.GetTextureDesc(source);
                desc.name = "_SS_HallucinationColor";
                desc.clearBuffer = false;
                TextureHandle destination = renderGraph.CreateTexture(desc);
                renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(source, destination, _material, 0), "SS Hallucination");
                resources.cameraColor = destination;
            }
        }
    }
}
