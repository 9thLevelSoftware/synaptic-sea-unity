// Ported from scripts/systems/armor_resolver.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Flat reduction + resistance damage resolution with durability wear.</summary>
    public sealed class ArmorResolver : ISimModel, IStatusLineProvider
    {
        public const double DEFAULT_DURABILITY = 0.0;
        public const double DEFAULT_WEAR_FACTOR = 0.35;

        public GdDict ArmorProfile = new GdDict
        {
            { "flat_reduction", new GdDict() },
            { "resistance", new GdDict() },
            { "durability", DEFAULT_DURABILITY },
            { "max_durability", DEFAULT_DURABILITY },
            { "wear_factor", DEFAULT_WEAR_FACTOR },
        };

        public GdDict LastResolution = new GdDict();

        public void Configure(GdDict config = null)
        {
            ArmorProfile = NormalizeProfile(config ?? new GdDict());
            LastResolution = new GdDict();
        }

        public GdDict ResolveDamage(GdDict @event, GdDict profileOverride = null)
        {
            GdDict profile = NormalizeProfile(profileOverride != null && !profileOverride.IsEmpty ? profileOverride : ArmorProfile);
            string damageType = V.Str(@event.Get("damage_type", "physical"));
            double incoming = Math.Max(0.0, V.F64(@event.Get("amount", 0.0)));
            double flat = Math.Max(0.0, V.F64(((GdDict)profile.Get("flat_reduction", new GdDict())).Get(damageType, 0.0)));
            double resistance = GdMath.Clampf(V.F64(((GdDict)profile.Get("resistance", new GdDict())).Get(damageType, 0.0)), -0.9, 0.95);
            double afterFlat = Math.Max(0.0, incoming - flat);
            double finalDamage = Math.Max(0.0, afterFlat * (1.0 - resistance));
            double absorbed = Math.Max(0.0, incoming - finalDamage);
            double durability = Math.Max(0.0, V.F64(profile.Get("durability", 0.0)) -
                                              (absorbed * V.F64(profile.Get("wear_factor", DEFAULT_WEAR_FACTOR))));
            profile["durability"] = durability;
            LastResolution = new GdDict
            {
                { "damage_type", damageType },
                { "incoming", incoming },
                { "flat_reduction", flat },
                { "resistance", resistance },
                { "absorbed", absorbed },
                { "final_damage", finalDamage },
                { "durability", durability },
                { "profile", profile.DeepCopy() },
            };
            ArmorProfile = profile.DeepCopy();
            return LastResolution.DeepCopy();
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "armor_profile", ArmorProfile.DeepCopy() },
                { "last_resolution", LastResolution.DeepCopy() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            // GDScript passes the value to a Dictionary-typed parameter; a non-dictionary would be a script error,
            // so it falls back to the current profile here.
            GdDict profile = NormalizeProfile(summary.Get("armor_profile", ArmorProfile) as GdDict ?? ArmorProfile);
            if (GdJson.Stringify(profile) != GdJson.Stringify(ArmorProfile))
            {
                ArmorProfile = profile;
                changed = true;
            }
            GdDict last = summary.Get("last_resolution", new GdDict()) as GdDict ?? new GdDict();
            if (GdJson.Stringify(last) != GdJson.Stringify(LastResolution))
            {
                LastResolution = last.DeepCopy();
                changed = true;
            }
            return changed;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Armor durability: " + SurvivalCompat.FormatF(V.F64(ArmorProfile.Get("durability", 0.0)), 1));
            if (!LastResolution.IsEmpty)
            {
                lines.Add("Last hit: " + V.Str(LastResolution.Get("damage_type", "physical")) + " " +
                          SurvivalCompat.FormatF(V.F64(LastResolution.Get("incoming", 0.0)), 1) + " -> " +
                          SurvivalCompat.FormatF(V.F64(LastResolution.Get("final_damage", 0.0)), 1));
            }
            return lines;
        }

        static GdDict NormalizeProfile(GdDict src)
        {
            GdDict flat = src.Get("flat_reduction", new GdDict()) as GdDict ?? new GdDict();
            GdDict resist = src.Get("resistance", new GdDict()) as GdDict ?? new GdDict();
            return new GdDict
            {
                { "flat_reduction", flat.DeepCopy() },
                { "resistance", resist.DeepCopy() },
                { "durability", Math.Max(0.0, V.F64(src.Get("durability", DEFAULT_DURABILITY))) },
                { "max_durability", Math.Max(0.0, V.F64(src.Get("max_durability", src.Get("durability", DEFAULT_DURABILITY)))) },
                { "wear_factor", Math.Max(0.0, V.F64(src.Get("wear_factor", DEFAULT_WEAR_FACTOR))) },
            };
        }
    }
}
