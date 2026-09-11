// Ported from scripts/procgen/biome_profile.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Pure data class describing a Synaptic Sea biome and its multipliers on hazard density, loot quality, and
    /// encounter density. Composition with <see cref="DifficultyProfile"/> happens in
    /// <see cref="DifficultyProfile.CombinedModifier"/>.
    /// </summary>
    public sealed class BiomeProfile : IModifierSource
    {
        public const string DIAL_HAZARD = "hazard_modifier";
        public const string DIAL_LOOT = "loot_quality_modifier";
        public const string DIAL_ENCOUNTER = "encounter_density_modifier";
        public const string DIAL_AMBIENT = "ambient_intensity";

        public static readonly IReadOnlyList<string> ALL_DIALS = new[] { DIAL_HAZARD, DIAL_LOOT, DIAL_ENCOUNTER, DIAL_AMBIENT };

        public static readonly GdArray FALLBACK_AMBIENT_COLOR = GdArray.Of(0.6, 0.65, 0.75);

        public string Id = "";
        public string Description = "";
        public double HazardModifier = 1.0;
        public double LootQualityModifier = 1.0;
        public double EncounterDensityModifier = 1.0;
        public GdArray AmbientColor = FALLBACK_AMBIENT_COLOR.ShallowCopy();
        public double AmbientIntensity = 1.0;
        public GdDict HazardOverrides = new GdDict();
        public string EncounterTableId = "";
        public GdDict LootTableOverrides = new GdDict();

        /// <summary>Builds a profile from parsed JSON; every missing field falls back to its default.</summary>
        public static BiomeProfile FromDict(GdDict data)
        {
            var biome = new BiomeProfile();
            if (data == null)
            {
                biome.Id = "unknown";
                return biome;
            }
            biome.Id = V.Str(data.Get("id", "unknown"));
            biome.Description = V.Str(data.Get("description", ""));
            biome.HazardModifier = SafeFloat(data.Get("hazard_modifier", 1.0), 1.0);
            biome.LootQualityModifier = SafeFloat(data.Get("loot_quality_modifier", 1.0), 1.0);
            biome.EncounterDensityModifier = SafeFloat(data.Get("encounter_density_modifier", 1.0), 1.0);
            biome.AmbientIntensity = SafeFloat(data.Get("ambient_intensity", 1.0), 1.0);

            if (data.Get("ambient_color", new GdArray()) is GdArray ambientRaw && ambientRaw.Count >= 3)
            {
                biome.AmbientColor = GdArray.Of(
                    SafeFloat(ambientRaw[0], 0.6),
                    SafeFloat(ambientRaw[1], 0.65),
                    SafeFloat(ambientRaw[2], 0.75));
            }

            if (data.Get("hazard_overrides", new GdDict()) is GdDict overridesRaw)
            {
                foreach (var kv in overridesRaw)
                    biome.HazardOverrides[V.Str(kv.Key)] = SafeFloat(kv.Value, 1.0);
            }

            if (data.Get("loot_table_overrides", new GdDict()) is GdDict lootRaw)
            {
                foreach (var kv in lootRaw)
                    biome.LootTableOverrides[V.Str(kv.Key)] = V.Str(kv.Value);
            }

            biome.EncounterTableId = V.Str(data.Get("encounter_table_id", ""));
            return biome;
        }

        /// <summary>Loads a profile from a <c>res://</c> JSON file; null when missing or not a JSON object.</summary>
        public static BiomeProfile FromFile(string absPath)
        {
            if (!CatalogRegistry.Exists(absPath)) return null;
            GdDict parsed = CatalogRegistry.LoadDict(absPath);
            if (parsed == null) return null;
            return FromDict(parsed);
        }

        /// <summary>The modifier for <paramref name="dial"/>; unknown dials return 1.0.</summary>
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

        public double HazardOverride(string hazardId)
        {
            if (HazardOverrides.Has(hazardId)) return V.F64(HazardOverrides[hazardId]);
            return 1.0;
        }

        public string LootTableForRole(string role)
        {
            if (LootTableOverrides.Has(role)) return V.Str(LootTableOverrides[role]);
            return "";
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
                { "ambient_color", AmbientColor.ShallowCopy() },
                { "ambient_intensity", AmbientIntensity },
                { "hazard_overrides", HazardOverrides.ShallowCopy() },
                { "encounter_table_id", EncounterTableId },
                { "loot_table_overrides", LootTableOverrides.ShallowCopy() },
            };
        }

        /// <summary>Deterministic biome selection: same seed, same id.</summary>
        public static string SelectBiome(long seedValue, IReadOnlyList<string> biomeIds)
        {
            if (biomeIds == null || biomeIds.Count == 0) return "";
            if (biomeIds.Count == 1) return biomeIds[0];
            var rng = GodotRandom.FromSeed(seedValue & 0x7FFFFFFF);
            if (rng.Seed == 0) rng.Seed = 1;
            long idx = rng.RandiRange(0, biomeIds.Count - 1);
            return biomeIds[(int)idx];
        }

        internal static double SafeFloat(object v, double fallback)
        {
            if (v == null) return fallback;
            if (v is double || v is long) return V.F64(v);
            if (v is string s)
            {
                double parsed = V.StringToFloat(s);
                if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return fallback;
                return parsed;
            }
            return fallback;
        }
    }
}
