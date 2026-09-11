using System.Linq;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SynapticSea.EditorTools.Bootstrap
{
    /// <summary>
    /// Configures URP for the locked-isometric interior look (plan Phase 9): Forward+, HDR, MSAA 4x, main-light
    /// soft shadows sized for the orthographic follow camera, GPU Resident Drawer, SSAO + Decal renderer features,
    /// the hallucination full-screen pass, and the global post-processing profile (Neutral tonemapping, bloom for
    /// emissives, vignette, colour adjustments).
    /// Idempotent.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Bootstrap.RenderingSetup.Apply -quit
    /// </summary>
    public static class RenderingSetup
    {
        public const string PipelineAssetPath = "Assets/Settings/PC_RPAsset.asset";
        public const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        public const string GlobalProfilePath = "Assets/Settings/Volumes/SS_GlobalVolume.asset";
        public const string DitherFadeShaderPath = "Assets/Content/Shaders/SS_LitDitherFade.shader";
        public const string HallucinationShaderPath = "Assets/Content/Shaders/SS_Hallucination.shader";

        [MenuItem("Synaptic Sea/Bootstrap/Apply Rendering Settings")]
        public static void Apply()
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (pipeline == null || renderer == null)
            {
                Debug.LogError("[RenderingSetup] PC_RPAsset / PC_Renderer not found (URP template assets).");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            // Desktop-only game: every quality level renders with the PC pipeline asset.
            GraphicsSettings.defaultRenderPipeline = pipeline;
            string[] levels = QualitySettings.names;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < levels.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipeline;
            }
            QualitySettings.SetQualityLevel(current, false);

            pipeline.supportsHDR = true;
            pipeline.msaaSampleCount = 4;
            pipeline.renderScale = 1f;
            pipeline.shadowDistance = 60f;
            pipeline.shadowCascadeCount = 2;
            pipeline.mainLightShadowmapResolution = 2048;
            pipeline.gpuResidentDrawerMode = GPUResidentDrawerMode.InstancedDrawing;
            var pso = new SerializedObject(pipeline);
            SetBool(pso, "m_SoftShadowsSupported", true);
            SetBool(pso, "m_RequireDepthTexture", true);
            SetBool(pso, "m_RequireOpaqueTexture", true);
            SetBool(pso, "m_UseSRPBatcher", true);
            SetBool(pso, "m_SupportsDynamicBatching", false);
            pso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);

            renderer.renderingMode = RenderingMode.ForwardPlus;
            var ssao = EnsureFeature<ScreenSpaceAmbientOcclusion>(renderer, "SSAO");
            var sso = new SerializedObject(ssao);
            SetEnum(sso, "m_Settings.AOMethod", 1); // InterleavedGradient
            SetBool(sso, "m_Settings.Downsample", true);
            SetFloat(sso, "m_Settings.Intensity", 1.5f);
            SetFloat(sso, "m_Settings.Radius", 0.35f);
            SetFloat(sso, "m_Settings.Falloff", 40f);
            sso.ApplyModifiedPropertiesWithoutUndo();
            EnsureFeature<DecalRendererFeature>(renderer, "Decals");
            var hallucination = EnsureFeature<HallucinationRendererFeature>(renderer, "Hallucination");
            hallucination.Shader = AssetDatabase.LoadAssetAtPath<Shader>(HallucinationShaderPath);
            EditorUtility.SetDirty(hallucination);
            EditorUtility.SetDirty(renderer);

            // Shaders only looked up by name at runtime must be in builds.
            EnsureAlwaysIncluded(AssetDatabase.LoadAssetAtPath<Shader>(DitherFadeShaderPath));
            EnsureAlwaysIncluded(AssetDatabase.LoadAssetAtPath<Shader>(HallucinationShaderPath));

            var profile = EnsureGlobalProfile();

            AssetDatabase.SaveAssets();
            Debug.Log($"[RenderingSetup] RENDERING SETUP PASS mode={renderer.renderingMode} hdr={pipeline.supportsHDR} msaa={pipeline.msaaSampleCount} " +
                      $"features={string.Join(",", renderer.rendererFeatures.Select(f => f.name))} profile={AssetDatabase.GetAssetPath(profile)}");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static T EnsureFeature<T>(UniversalRendererData data, string name) where T : ScriptableRendererFeature
        {
            var existing = data.rendererFeatures.OfType<T>().FirstOrDefault();
            if (existing != null)
            {
                existing.SetActive(true);
                return existing;
            }
            var feature = ScriptableObject.CreateInstance<T>();
            feature.name = name;
            AssetDatabase.AddObjectToAsset(feature, data);
            data.rendererFeatures.Add(feature);
            var so = new SerializedObject(data);
            var map = so.FindProperty("m_RendererFeatureMap");
            if (map != null)
            {
                map.arraySize++;
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            feature.SetActive(true);
            return feature;
        }

        static void EnsureAlwaysIncluded(Shader shader)
        {
            if (shader == null)
            {
                Debug.LogWarning("[RenderingSetup] shader to always include not found");
                return;
            }
            var graphics = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset").FirstOrDefault();
            if (graphics == null) return;
            var so = new SerializedObject(graphics);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
            list.arraySize++;
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static VolumeProfile EnsureGlobalProfile()
        {
            System.IO.Directory.CreateDirectory("Assets/Settings/Volumes");
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(GlobalProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, GlobalProfilePath);
            }

            // Neutral, not the plan's ACES: Godot rendered with linear tonemapping, and ACES's toe crushed the Godot
            // palette (clear colour 0.05 → black, mid-tones about -55%). Neutral stays within about 10% of the Godot
            // captures and still rolls off HDR emissives (docs/port-status.md, calibration).
            var tone = Get<Tonemapping>(profile);
            tone.mode.Override(TonemappingMode.Neutral);

            var bloom = Get<Bloom>(profile);
            bloom.threshold.Override(1.0f);
            bloom.intensity.Override(0.35f);
            bloom.scatter.Override(0.6f);

            var vignette = Get<Vignette>(profile);
            vignette.intensity.Override(0.25f);
            vignette.smoothness.Override(0.4f);

            // Present but neutral: contrast/saturation grading darkened the calibrated Godot light levels.
            var color = Get<ColorAdjustments>(profile);
            color.postExposure.Override(0f);
            color.contrast.Override(0f);
            color.saturation.Override(0f);

            EditorUtility.SetDirty(profile);
            return profile;
        }

        static T Get<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet(out T component)) return component;
            component = profile.Add<T>(true);
            component.name = typeof(T).Name;
            AssetDatabase.AddObjectToAsset(component, profile);
            return component;
        }

        static void SetBool(SerializedObject so, string path, bool value)
        {
            var p = so.FindProperty(path);
            if (p != null) p.boolValue = value;
            else Debug.LogWarning($"[RenderingSetup] property {path} not found on {so.targetObject.GetType().Name}");
        }

        static void SetFloat(SerializedObject so, string path, float value)
        {
            var p = so.FindProperty(path);
            if (p != null) p.floatValue = value;
            else Debug.LogWarning($"[RenderingSetup] property {path} not found on {so.targetObject.GetType().Name}");
        }

        static void SetEnum(SerializedObject so, string path, int index)
        {
            var p = so.FindProperty(path);
            if (p != null) p.enumValueIndex = index;
            else Debug.LogWarning($"[RenderingSetup] property {path} not found on {so.targetObject.GetType().Name}");
        }
    }
}
