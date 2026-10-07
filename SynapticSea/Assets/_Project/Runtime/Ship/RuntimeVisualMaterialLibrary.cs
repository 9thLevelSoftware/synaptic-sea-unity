using System;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>Build dependencies for every shader keyword combination used by RuntimeVisualCatalog.</summary>
    public sealed class RuntimeVisualMaterialLibrary : ScriptableObject
    {
        public const string ResourcePath = "Catalogs/RuntimeVisualMaterials";
        public Material litOpaque, litTransparent, litEmissive, litTransparentEmissive;
        public Material unlitOpaque, unlitTransparent;
        public Material playerOcclusionSilhouette;

        public static RuntimeVisualMaterialLibrary Load()
        {
            var library = Resources.Load<RuntimeVisualMaterialLibrary>(ResourcePath);
            if (library == null) throw new InvalidOperationException("RuntimeVisualCatalog: Resources/" + ResourcePath + " is missing; rebuild runtime visual materials in the Editor.");
            library.Validate();
            return library;
        }

        public Material Select(bool unshaded, bool transparent, bool emissive) => unshaded
            ? (transparent ? unlitTransparent : unlitOpaque)
            : transparent ? (emissive ? litTransparentEmissive : litTransparent)
            : (emissive ? litEmissive : litOpaque);

        public void Validate()
        {
            foreach (bool unshaded in new[] { false, true })
            foreach (bool transparent in new[] { false, true })
            foreach (bool emissive in new[] { false, true })
            {
                var material = Select(unshaded, transparent, emissive);
                string shaderName = unshaded ? RuntimeVisualCatalog.UnlitShaderName : RuntimeVisualCatalog.LitShaderName;
                if (material == null || material.shader == null || material.shader.name != shaderName
                    || material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") != transparent
                    || (!unshaded && material.IsKeywordEnabled("_EMISSION") != emissive))
                    throw new InvalidOperationException($"RuntimeVisualCatalog: invalid template unshaded={unshaded} transparent={transparent} emissive={emissive}; expected {shaderName} and matching feature keywords.");
            }
        }
    }
}
