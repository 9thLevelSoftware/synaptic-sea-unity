// Ported from scripts/procgen/template_selector.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Picks a <see cref="TopologyTemplate"/> based on archetype, blueprint size, and seed. If the archetype
    /// specifies a "template" key, that template is loaded directly. Otherwise the selector picks from the
    /// available templates using the blueprint's seed as the RNG source.
    /// <see cref="Select"/> keeps the legacy three templates; <see cref="SelectWithOptions"/> opts into the
    /// derelict / extended sets.
    /// </summary>
    public sealed class TemplateSelector
    {
        public const string TEMPLATE_DIR = "res://data/procgen/templates/";

        public static readonly IReadOnlyList<string> AVAILABLE_TEMPLATES = new[] { "spine", "bifurcated", "stacked" };

        /// <summary>PKG-D5.4: full catalog toward 12–15 templates (legacy three preserved).</summary>
        public static readonly IReadOnlyList<string> EXTENDED_TEMPLATES = new[]
        {
            "spine", "bifurcated", "stacked",
            "stacked_v2", "compact", "dispersed",
            "derelict_a", "derelict_b",
            "ring", "radial", "double_spine", "hangar_wing", "vault",
            "hive",
        };

        public static readonly IReadOnlyList<string> DERELICT_TEMPLATES = new[] { "derelict_a", "derelict_b" };
        public static readonly IReadOnlyList<string> WRECK_TEMPLATES = new[] { "derelict_a", "derelict_b", "vault" };

        public TopologyTemplate Select(ShipBlueprint blueprint, GdDict archetype)
        {
            string templateId = V.Str(archetype.Get("template", ""));

            if (templateId.Length == 0)
            {
                var rng = new GodotRandom();
                rng.Seed = blueprint.SeedValue;
                long idx = rng.RandiRange(0, AVAILABLE_TEMPLATES.Count - 1);
                templateId = AVAILABLE_TEMPLATES[(int)idx];
            }

            return LoadTemplate(templateId);
        }

        /// <summary>
        /// Extended selector. <paramref name="includeDerelict"/> adds the derelict_* templates;
        /// <paramref name="extended"/> adds the whole <see cref="EXTENDED_TEMPLATES"/> catalog.
        /// </summary>
        public TopologyTemplate SelectWithOptions(
            ShipBlueprint blueprint,
            GdDict archetype,
            bool includeDerelict = false,
            bool extended = false)
        {
            string templateId = V.Str(archetype.Get("template", ""));
            if (templateId.Length != 0) return LoadTemplate(templateId);

            List<string> pool = AvailableTemplates(includeDerelict, extended);

            var rng = new GodotRandom();
            rng.Seed = blueprint.SeedValue;
            long idx = rng.RandiRange(0, pool.Count - 1);
            return LoadTemplate(pool[(int)idx]);
        }

        /// <summary>A copy of the registered template ids for the given inclusion options.</summary>
        public List<string> AvailableTemplates(bool includeDerelict = false, bool extended = false)
        {
            var pool = new List<string>(AVAILABLE_TEMPLATES);
            if (extended)
            {
                foreach (string t in EXTENDED_TEMPLATES)
                    if (!pool.Contains(t)) pool.Add(t);
            }
            else if (includeDerelict)
            {
                foreach (string t in DERELICT_TEMPLATES)
                    if (!pool.Contains(t)) pool.Add(t);
            }
            return pool;
        }

        /// <summary>PKG-D5.4: count of JSON templates on disk under <see cref="TEMPLATE_DIR"/>.</summary>
        public long CatalogSizeOnDisk()
        {
            long n = 0;
            foreach (string tid in EXTENDED_TEMPLATES)
            {
                string path = TEMPLATE_DIR + tid + ".json";
                if (CatalogRegistry.Exists(path)) n += 1;
            }
            return n;
        }

        TopologyTemplate LoadTemplate(string templateId)
        {
            string path = TEMPLATE_DIR + templateId + ".json";
            // GDScript globalizes the path for FileAccess; the resource reader resolves res:// itself.
            if (!CatalogRegistry.Exists(path))
            {
                CoreServices.Log.Error("TEMPLATE SELECTOR FAIL template file not found: " + path);
                return null;
            }

            GdDict parsed = CatalogRegistry.LoadDict(path);
            if (parsed == null)
            {
                CoreServices.Log.Error("TEMPLATE SELECTOR FAIL invalid JSON: " + path);
                return null;
            }

            return TopologyTemplate.FromDict(parsed);
        }
    }
}
