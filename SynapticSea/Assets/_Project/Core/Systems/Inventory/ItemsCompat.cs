// Kernel-gap helpers for the item / food / loot ports (batch B). Not a port of any single .gd file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Small Godot String / file helpers the kernel does not (yet) provide:
    /// <c>String.capitalize()</c>, <c>"%.Nf"</c> / <c>"%+.Nf"</c> formatting, quiet <c>res://</c> JSON reads
    /// (<c>FileAccess.file_exists</c> + parse without a warning), and overflow-safe <c>abs</c>/<c>absi</c>.
    /// </summary>
    internal static class ItemsCompat
    {
        /// <summary>
        /// <c>FileAccess.file_exists(path)</c> then <c>JSON.parse_string</c>: parsed Variant, or null when the file
        /// is missing or malformed. Unlike <see cref="CatalogRegistry.Load"/> alone, a missing file is not logged
        /// (Godot's per-model loaders returned silently).
        /// </summary>
        public static object ReadJson(string resPath)
        {
            if (string.IsNullOrEmpty(resPath) || !CatalogRegistry.Exists(resPath)) return null;
            return CatalogRegistry.Load(resPath);
        }

        /// <summary>The common <c>_read_json_dict(path)</c>: the parsed dictionary or a new empty one.</summary>
        public static GdDict ReadJsonDict(string resPath) => ReadJson(resPath) as GdDict ?? new GdDict();

        /// <summary>GDScript <c>abs(int)</c> / <c>absi()</c>: wraps on <c>long.MinValue</c> instead of throwing.</summary>
        public static long AbsI(long v) => v < 0 ? unchecked(-v) : v;

        /// <summary>GDScript <c>"%.Nf" % v</c> (and <c>"%+.Nf"</c> when <paramref name="showSign"/>).</summary>
        public static string Fmt(double v, int decimals, bool showSign = false)
        {
            bool negative = double.IsNegative(v); // std::signbit, like Godot's sprintf
            double abs = Math.Abs(v);
            string body = (double.IsNaN(abs) || double.IsInfinity(abs))
                ? GdFloatFormat.Num(abs, decimals)
                : GdFloatFormat.FormatFixed(abs, decimals);
            if (negative) return "-" + body;
            return showSign ? "+" + body : body;
        }

        /// <summary>GDScript <c>"%d" % i</c>.</summary>
        public static string D(long v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Godot 4.7 <c>String.capitalize()</c> (camelCase/snake_case to "Capitalized Words").</summary>
        public static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            string aux = StripEdges(CamelcaseToUnderscore(s).Replace('_', ' '));
            string[] slices = aux.Split(' ');
            var cap = new StringBuilder();
            for (int i = 0; i < slices.Length; i++)
            {
                string slice = slices[i];
                if (slice.Length == 0) continue;
                var sb = new StringBuilder(slice.Length);
                sb.Append(char.ToUpperInvariant(slice[0]));
                for (int j = 1; j < slice.Length; j++) sb.Append(char.ToLowerInvariant(slice[j]));
                if (i > 0) cap.Append(' ');
                cap.Append(sb);
            }
            return cap.ToString();
        }

        /// <summary>Godot <c>String.strip_edges()</c>: trims characters with code &lt;= 32 on both sides.</summary>
        public static string StripEdges(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            int beg = 0, end = s.Length;
            while (beg < end && s[beg] <= 32) beg++;
            while (end > beg && s[end - 1] <= 32) end--;
            return s.Substring(beg, end - beg);
        }

        static bool IsDigit(char c) => c >= '0' && c <= '9';

        /// <summary>Godot <c>String::_camelcase_to_underscore()</c> (already lower-cased).</summary>
        static string CamelcaseToUnderscore(string s)
        {
            if (s.Length == 0) return s;
            var result = new StringBuilder();
            int startIndex = 0;
            bool isPrevUpper = char.IsUpper(s[0]);
            bool isPrevLower = char.IsLower(s[0]);
            bool isPrevDigit = IsDigit(s[0]);
            for (int i = 1; i < s.Length; i++)
            {
                bool isCurrUpper = char.IsUpper(s[i]);
                bool isCurrLower = char.IsLower(s[i]);
                bool isCurrDigit = IsDigit(s[i]);
                bool isNextLower = i + 1 < s.Length && char.IsLower(s[i + 1]);

                bool condA = isPrevLower && isCurrUpper;
                bool condB = (isPrevUpper || isPrevDigit) && isCurrUpper && isNextLower;
                bool condC = isPrevDigit && isCurrLower && isNextLower;
                bool condD = (isPrevUpper || isPrevLower) && isCurrDigit;
                if (condA || condB || condC || condD)
                {
                    result.Append(s, startIndex, i - startIndex).Append('_');
                    startIndex = i;
                }
                isPrevUpper = isCurrUpper;
                isPrevLower = isCurrLower;
                isPrevDigit = isCurrDigit;
            }
            result.Append(s, startIndex, s.Length - startIndex);
            return result.ToString().ToLowerInvariant();
        }

        /// <summary><c>PackedStringArray</c> / <c>Array[String]</c> copied into a summary-safe <see cref="GdArray"/>.</summary>
        public static GdArray ToGdArray(IEnumerable<string> items)
        {
            var arr = new GdArray();
            if (items == null) return arr;
            foreach (string s in items) arr.Add(s);
            return arr;
        }
    }
}
