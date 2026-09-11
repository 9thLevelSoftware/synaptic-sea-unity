// Ported from scripts/procgen/difficulty_profile.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Anything with a <c>modifier(dial) -&gt; float</c> method (GDScript duck typing via
    /// <c>has_method("modifier")</c> in <see cref="DifficultyProfile.CombinedModifier"/>).
    /// </summary>
    public interface IModifierSource
    {
        double Modifier(string dial);
    }

    /// <summary>
    /// Pure data class describing a difficulty preset. <see cref="CombinedModifier"/> multiplies a biome and
    /// difficulty modifier and clamps to [0.0, 3.0] so the composition never produces an impossible seed.
    /// </summary>
    public sealed class DifficultyProfile : IModifierSource
    {
        public const string DIAL_HAZARD = "hazard_modifier";
        public const string DIAL_LOOT = "loot_quality_modifier";
        public const string DIAL_ENCOUNTER = "encounter_density_modifier";
        public const string DIAL_AMBIENT = "ambient_intensity";

        public static readonly IReadOnlyList<string> ALL_DIALS = new[] { DIAL_HAZARD, DIAL_LOOT, DIAL_ENCOUNTER, DIAL_AMBIENT };

        public const double COMBINED_MODIFIER_MIN = 0.0;
        public const double COMBINED_MODIFIER_MAX = 3.0;

        public const string STANDARD_ID = "standard";
        public const string HARDENED_ID = "hardened";
        public const string DEEP_DIVE_ID = "deep_dive";

        public string Id = STANDARD_ID;
        public string Description = "";
        public double HazardModifier = 1.0;
        public double LootQualityModifier = 1.0;
        public double EncounterDensityModifier = 1.0;
        public double AmbientIntensity = 1.0;

        public static DifficultyProfile FromDict(GdDict data)
        {
            var diff = new DifficultyProfile();
            if (data == null) return diff;
            diff.Id = V.Str(data.Get("id", STANDARD_ID));
            diff.Description = V.Str(data.Get("description", ""));
            diff.HazardModifier = SafeFloat(data.Get("hazard_modifier", 1.0), 1.0);
            diff.LootQualityModifier = SafeFloat(data.Get("loot_quality_modifier", 1.0), 1.0);
            diff.EncounterDensityModifier = SafeFloat(data.Get("encounter_density_modifier", 1.0), 1.0);
            diff.AmbientIntensity = SafeFloat(data.Get("ambient_intensity", 1.0), 1.0);
            return diff;
        }

        public static DifficultyProfile FromFile(string absPath)
        {
            if (!CatalogRegistry.Exists(absPath)) return null;
            GdDict parsed = CatalogRegistry.LoadDict(absPath);
            if (parsed == null) return null;
            return FromDict(parsed);
        }

        /// <summary>
        /// Canonical difficulty_id -&gt; profile-dict resolution. Order: authored JSON override at
        /// <c>res://data/procgen/difficulty/&lt;id&gt;.json</c> -&gt; built-in presets -&gt; standard.
        /// </summary>
        public static GdDict ResolveDict(string difficultyId)
        {
            if (string.IsNullOrEmpty(difficultyId)) return new GdDict { { "id", STANDARD_ID } };
            string relPath = "res://data/procgen/difficulty/" + difficultyId + ".json";
            if (CatalogRegistry.Exists(relPath))
            {
                GdDict parsed = CatalogRegistry.LoadDict(relPath);
                if (parsed != null) return parsed;
                CoreServices.Log.Warning("DifficultyProfile: override file is not a JSON object, falling back to built-ins: " + relPath);
            }
            switch (difficultyId)
            {
                case HARDENED_ID:
                    return new GdDict
                    {
                        { "id", HARDENED_ID },
                        { "hazard_modifier", 1.4 },
                        { "loot_quality_modifier", 0.85 },
                        { "encounter_density_modifier", 1.3 },
                        { "ambient_intensity", 1.0 },
                    };
                case DEEP_DIVE_ID:
                    return new GdDict
                    {
                        { "id", DEEP_DIVE_ID },
                        { "hazard_modifier", 1.7 },
                        { "loot_quality_modifier", 1.1 },
                        { "encounter_density_modifier", 1.6 },
                        { "ambient_intensity", 1.0 },
                    };
                default:
                    return new GdDict
                    {
                        { "id", STANDARD_ID },
                        { "hazard_modifier", 1.0 },
                        { "loot_quality_modifier", 1.0 },
                        { "encounter_density_modifier", 1.0 },
                        { "ambient_intensity", 1.0 },
                    };
            }
        }

        public static DifficultyProfile ForId(string difficultyId) => FromDict(ResolveDict(difficultyId));

        public double Modifier(string dial)
        {
            switch (dial)
            {
                case DIAL_HAZARD: return HazardModifier;
                case DIAL_LOOT: return LootQualityModifier;
                case DIAL_ENCOUNTER: return EncounterDensityModifier;
                case DIAL_AMBIENT: return AmbientIntensity;
                default: return 1.0;
            }
        }

        /// <summary><c>biome.modifier(dial) * difficulty.modifier(dial)</c> clamped; a null side is identity.</summary>
        public static double CombinedModifier(IModifierSource biome, IModifierSource difficulty, string dial)
        {
            double b = 1.0;
            if (biome != null) b = biome.Modifier(dial);
            double d = 1.0;
            if (difficulty != null) d = difficulty.Modifier(dial);
            double combined = b * d;
            if (combined < COMBINED_MODIFIER_MIN) combined = COMBINED_MODIFIER_MIN;
            if (combined > COMBINED_MODIFIER_MAX) combined = COMBINED_MODIFIER_MAX;
            return combined;
        }

        public static string SelectDifficulty(long seedValue, IReadOnlyList<string> difficultyIds)
        {
            if (difficultyIds == null || difficultyIds.Count == 0) return STANDARD_ID;
            if (difficultyIds.Count == 1) return difficultyIds[0];
            var rng = GodotRandom.FromSeed((seedValue ^ 0x5A5A5A5A) & 0x7FFFFFFF);
            if (rng.Seed == 0) rng.Seed = 1;
            long idx = rng.RandiRange(0, difficultyIds.Count - 1);
            return difficultyIds[(int)idx];
        }

        public GdDict ToDict()
        {
            return new GdDict
            {
                { "id", Id },
                { "description", Description },
                { "hazard_modifier", HazardModifier },
                { "loot_quality_modifier", LootQualityModifier },
                { "encounter_density_modifier", EncounterDensityModifier },
                { "ambient_intensity", AmbientIntensity },
            };
        }

        static double SafeFloat(object v, double fallback) => BiomeProfile.SafeFloat(v, fallback);
    }
}
