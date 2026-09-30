using System;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace SynapticSea.EditorTools.Build
{
    public static class RuntimeVisualMaterialBuilder
    {
        public const string LibraryPath = "Assets/Resources/Catalogs/RuntimeVisualMaterials.asset";
        const string Folder = "Assets/Resources/RuntimeVisualMaterials";

        [MenuItem("Synaptic Sea/Build/Rebuild Runtime Visual Materials")]
        public static void BuildAll()
        {
            System.IO.Directory.CreateDirectory(Folder);
            System.IO.Directory.CreateDirectory("Assets/Resources/Catalogs");
            AssetDatabase.Refresh();
            var library = AssetDatabase.LoadAssetAtPath<RuntimeVisualMaterialLibrary>(LibraryPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<RuntimeVisualMaterialLibrary>();
                AssetDatabase.CreateAsset(library, LibraryPath);
            }
            library.litOpaque = Template(false, false, false);
            library.litTransparent = Template(false, true, false);
            library.litEmissive = Template(false, false, true);
            library.litTransparentEmissive = Template(false, true, true);
            library.unlitOpaque = Template(true, false, false);
            library.unlitTransparent = Template(true, true, false);
            library.Validate();
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            Debug.Log("[RuntimeVisualMaterialBuilder] six serialized URP templates ready");
        }

        static Material Template(bool unlit, bool transparent, bool emissive)
        {
            string name = (unlit ? "Unlit" : "Lit") + (transparent ? "Transparent" : "Opaque") + (emissive ? "Emissive" : "");
            string path = Folder + "/" + name + ".mat";
            var shader = Shader.Find(unlit ? RuntimeVisualCatalog.UnlitShaderName : RuntimeVisualCatalog.LitShaderName);
            if (shader == null) throw new InvalidOperationException("Required URP shader missing: " + name);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            material.shader = shader;
            material.shaderKeywords = Array.Empty<string>();
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Surface", transparent ? 1 : 0);
            material.SetFloat("_Blend", 0);
            material.SetFloat("_SrcBlend", (float)(transparent ? BlendMode.SrcAlpha : BlendMode.One));
            material.SetFloat("_DstBlend", (float)(transparent ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero));
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)(transparent ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero));
            material.SetFloat("_ZWrite", transparent ? 0 : 1);
            material.SetFloat("_Cull", (float)CullMode.Back);
            material.SetOverrideTag("RenderType", transparent ? "Transparent" : "Opaque");
            material.SetShaderPassEnabled("ShadowCaster", !transparent);
            material.SetShaderPassEnabled("DepthOnly", !transparent);
            material.renderQueue = (int)(transparent ? RenderQueue.Transparent : RenderQueue.Geometry);
            if (transparent) material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            if (!unlit)
            {
                material.SetFloat("_Smoothness", 0);
                material.SetFloat("_Metallic", 0);
                material.SetColor("_EmissionColor", emissive ? Color.white : Color.black);
                // URP's material importer derives _EMISSION from the GI flags. A retained emissive
                // template must survive reimport; runtime clones still use the catalog's GI=None.
                material.globalIlluminationFlags = emissive ? MaterialGlobalIlluminationFlags.BakedEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                if (emissive) material.EnableKeyword("_EMISSION");
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        public static void ValidateForBuild()
        {
            var library = AssetDatabase.LoadAssetAtPath<RuntimeVisualMaterialLibrary>(LibraryPath);
            if (library == null) throw new BuildFailedException("Missing runtime visual materials. Run Synaptic Sea/Build/Rebuild Runtime Visual Materials.");
            try { library.Validate(); }
            catch (InvalidOperationException error) { throw new BuildFailedException(error.Message); }
        }
    }

    // Applies to manual builds as well as the batch Builder entry point.
    public sealed class RuntimeVisualMaterialBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report) => RuntimeVisualMaterialBuilder.ValidateForBuild();
    }
}
