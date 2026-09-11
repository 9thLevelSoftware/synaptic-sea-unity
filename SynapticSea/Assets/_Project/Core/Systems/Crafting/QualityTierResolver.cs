// Ported from scripts/systems/quality_tier_resolver.gd @ 96ecb2b0
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure utility: resolves a numeric quality score [0.0, 1.0] into a named tier
    /// with a multiplier. Station level, skill level, and material quality all feed
    /// into the final score. Never touches the scene tree.
    /// </summary>
    public sealed class QualityTierResolver
    {
        public const string RECIPE_DEFINITIONS_PATH = "res://data/recipes/recipe_definitions.json";

        public static readonly GdArray TIER_ORDER = GdArray.Of("poor", "standard", "good", "excellent", "masterwork");

        public static readonly GdDict TIER_THRESHOLDS = new GdDict
        {
            { "poor", 0.0 },
            { "standard", 0.35 },
            { "good", 0.55 },
            { "excellent", 0.75 },
            { "masterwork", 0.90 },
        };

        public static readonly GdDict DEFAULT_MULTIPLIERS = new GdDict
        {
            { "poor", 0.7 },
            { "standard", 1.0 },
            { "good", 1.25 },
            { "excellent", 1.6 },
            { "masterwork", 2.0 },
        };

        GdDict _tiers = new GdDict();
        bool _loaded;

        public QualityTierResolver()
        {
            LoadTiers();
        }

        /// <summary>GDScript <c>_loaded</c>.</summary>
        public bool Loaded => _loaded;

        void LoadTiers()
        {
            string path = RECIPE_DEFINITIONS_PATH;
            if (!CatalogRegistry.Exists(path))
            {
                _tiers = DEFAULT_MULTIPLIERS.ShallowCopy();
                return;
            }
            if (CatalogRegistry.Load(path) is GdDict parsed)
            {
                if (parsed.Get("quality_tiers", new GdDict()) is GdDict qt)
                {
                    foreach (var kv in qt)
                    {
                        if (kv.Value is GdDict tierDef)
                        {
                            string tierName = V.Str(kv.Key);
                            _tiers[tierName] = V.F64(tierDef.Get("multiplier", DEFAULT_MULTIPLIERS.Get(tierName, 1.0)));
                        }
                    }
                }
            }
            if (_tiers.IsEmpty) _tiers = DEFAULT_MULTIPLIERS.ShallowCopy();
            _loaded = true;
        }

        /// <summary>
        /// Computes a final quality score: material_quality * 0.4 + skill_bonus * 0.35 + station_bonus * 0.25,
        /// plus a flat +0.05 when powered. Result is clamped [0.0, 1.0].
        /// </summary>
        public static double ComputeScore(double materialQuality, long skillLevel, long stationLevel, bool powered)
        {
            double mq = GdMath.Clampf(materialQuality, 0.0, 1.0);
            double skillBonus = GdMath.Clampf((double)skillLevel * 0.08, 0.0, 0.35);
            double stationBonus = GdMath.Clampf((double)stationLevel * 0.06, 0.0, 0.25);
            double powerBonus = powered ? 0.05 : 0.0;
            return GdMath.Clampf(mq * 0.4 + skillBonus * 0.35 + stationBonus * 0.25 + powerBonus, 0.0, 1.0);
        }

        /// <summary>Returns the tier name for a given score.</summary>
        public static string TierForScore(double score)
        {
            double s = GdMath.Clampf(score, 0.0, 1.0);
            string chosen = "poor";
            foreach (object tier in TIER_ORDER)
                if (s >= V.F64(TIER_THRESHOLDS.Get(tier, 0.0))) chosen = V.Str(tier);
            return chosen;
        }

        /// <summary>Returns the multiplier for a tier name.</summary>
        public double MultiplierForTier(string tierName) =>
            GdMath.Clampf(V.F64(_tiers.Get(tierName, DEFAULT_MULTIPLIERS.Get(tierName, 1.0))), 0.1, 5.0);

        /// <summary>Full resolve: returns {tier, multiplier, score}.</summary>
        public GdDict Resolve(double materialQuality, long skillLevel, long stationLevel, bool powered)
        {
            double score = ComputeScore(materialQuality, skillLevel, stationLevel, powered);
            string tier = TierForScore(score);
            return new GdDict
            {
                { "tier", tier },
                { "multiplier", MultiplierForTier(tier) },
                { "score", score },
            };
        }

        public GdDict GetSummary()
        {
            return new GdDict { { "tiers", _tiers.ShallowCopy() } };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (summary.Get("tiers", new GdDict()) is GdDict t)
            {
                _tiers = t.ShallowCopy();
                return true;
            }
            return false;
        }
    }
}
