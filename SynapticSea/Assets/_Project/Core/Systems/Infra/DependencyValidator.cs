// Ported from scripts/systems/dependency_validator.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Content-diagnostics entry point over <see cref="CatalogSourceValidator"/>. The matrix-based evidence checks were removed in Phase 0.4.</summary>
    public class DependencyValidator
    {
        /// <summary>Content diagnostics use the catalog authority; this result is not a launch gate.</summary>
        public GdDict VerifyCatalogSources(GdDict catalog, GdDict sourceGraph, GdDict exposureManifest) =>
            new CatalogSourceValidator().Validate(catalog, sourceGraph, exposureManifest);
    }
}
