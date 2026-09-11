// Ported from scripts/systems/rarity_tier.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using System.Globalization;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public static class RarityTier
    {
        /// <summary>
        /// Engine-free stand-in for the Godot <c>Color</c> values in <see cref="COLORS"/> (float32 channels in 0..1).
        /// Runtime code converts it to a UnityEngine.Color.
        /// </summary>
        public readonly struct RarityColor : IEquatable<RarityColor>
        {
            public readonly float R, G, B, A;

            public RarityColor(float r, float g, float b, float a = 1f)
            {
                R = r;
                G = g;
                B = b;
                A = a;
            }

            /// <summary>Godot <c>Color("#RRGGBB")</c> / <c>Color("#RRGGBBAA")</c> (8-bit channels divided by 255).</summary>
            public static RarityColor FromHtml(string html)
            {
                string h = html.StartsWith("#", StringComparison.Ordinal) ? html.Substring(1) : html;
                float Channel(int i) => int.Parse(h.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f;
                return new RarityColor(Channel(0), Channel(1), Channel(2), h.Length >= 8 ? Channel(3) : 1f);
            }

            /// <summary>Godot <c>Color.to_html(with_alpha = true)</c>: lowercase <c>rrggbb[aa]</c>, no '#'.</summary>
            public string ToHtml(bool withAlpha = true)
            {
                string txt = ToHex(R) + ToHex(G) + ToHex(B);
                if (withAlpha) txt += ToHex(A);
                return txt;
            }

            static string ToHex(float v)
            {
                // Color::_to_hex: int v = Math::round(p_val * 255.0f); CLAMP(v, 0, 255)
                int iv = (int)Math.Round((double)(float)(v * 255f), MidpointRounding.AwayFromZero);
                iv = Math.Max(0, Math.Min(255, iv));
                return iv.ToString("x2", CultureInfo.InvariantCulture);
            }

            public bool Equals(RarityColor o) => R == o.R && G == o.G && B == o.B && A == o.A;
            public override bool Equals(object obj) => obj is RarityColor o && Equals(o);
            public override int GetHashCode() => (R, G, B, A).GetHashCode();
        }

        public static readonly GdArray ORDER = GdArray.Of("common", "uncommon", "rare", "epic", "legendary");

        public static readonly GdDict LABELS = new GdDict
        {
            { "common", "Common" },
            { "uncommon", "Uncommon" },
            { "rare", "Rare" },
            { "epic", "Epic" },
            { "legendary", "Legendary" },
        };

        public static readonly IReadOnlyDictionary<string, RarityColor> COLORS = new Dictionary<string, RarityColor>(StringComparer.Ordinal)
        {
            { "common", RarityColor.FromHtml("#9AA4AF") },
            { "uncommon", RarityColor.FromHtml("#55C271") },
            { "rare", RarityColor.FromHtml("#4D9BFF") },
            { "epic", RarityColor.FromHtml("#A56DFF") },
            { "legendary", RarityColor.FromHtml("#FFB347") },
        };

        public static readonly GdDict WEIGHT_MULTIPLIERS = new GdDict
        {
            { "common", 1.00 },
            { "uncommon", 0.72 },
            { "rare", 0.45 },
            { "epic", 0.22 },
            { "legendary", 0.10 },
        };

        public const string DEFAULT_RARITY = "common";

        public static string Normalize(string value)
        {
            string rarity = ItemsCompat.StripEdges(value ?? "").ToLowerInvariant();
            return ORDER.Contains(rarity) ? rarity : DEFAULT_RARITY;
        }

        public static string Label(string value)
        {
            string rarity = Normalize(value);
            return V.Str(LABELS.Get(rarity, LABELS[DEFAULT_RARITY]));
        }

        public static RarityColor Color(string value)
        {
            string rarity = Normalize(value);
            return COLORS.TryGetValue(rarity, out RarityColor c) ? c : COLORS[DEFAULT_RARITY];
        }

        public static string Hex(string value) => Color(value).ToHtml();

        public static double WeightMultiplier(string value)
        {
            string rarity = Normalize(value);
            return V.F64(WEIGHT_MULTIPLIERS.Get(rarity, 1.0));
        }

        public static long Rank(string value) => ORDER.IndexOf(Normalize(value));

        public static string MaxRarity(string a, string b) => Rank(a) >= Rank(b) ? Normalize(a) : Normalize(b);

        public static string FromRoll(double score)
        {
            double s = GdMath.Clampf(score, 0.0, 1.0);
            if (s >= 0.96) return "legendary";
            if (s >= 0.84) return "epic";
            if (s >= 0.64) return "rare";
            if (s >= 0.34) return "uncommon";
            return "common";
        }

        public static List<string> GetStatusLines()
        {
            var lines = new List<string>();
            foreach (object rarity in ORDER)
                lines.Add(V.Str(rarity) + "=" + Hex(V.Str(rarity)));
            return lines;
        }
    }
}
