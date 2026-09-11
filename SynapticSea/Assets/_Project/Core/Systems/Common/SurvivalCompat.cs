// Helpers for the group-A system ports (survival, hazards, perception, combat, audio).
// Not a port of a single .gd file: small Godot String semantics the kernel does not expose yet.
using System.Globalization;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Godot <c>String</c> behaviours used by the group-A models that the kernel does not cover:
    /// <c>"%.Nf" %</c> / <c>"%d" %</c> formatting, <c>find</c>, <c>strip_edges</c>, <c>is_valid_int</c>, and <c>&lt;</c>.
    /// </summary>
    internal static class SurvivalCompat
    {
        /// <summary>GDScript <c>"%.Nf" % value</c> (Godot sprintf: <c>String::num(abs, N)</c> padded, sign re-applied).</summary>
        public static string FormatF(double value, int decimals)
        {
            if (double.IsNaN(value)) return "nan";
            if (double.IsInfinity(value)) return value < 0 ? "-inf" : "inf";
            return GdFloatFormat.FormatFixed(value, decimals);
        }

        /// <summary>GDScript <c>"%d" % value</c> for an int.</summary>
        public static string FormatD(long value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary><c>String.find(what)</c>: -1 when either string is empty (Godot never matches an empty needle).</summary>
        public static int Find(string s, string what)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(what)) return -1;
            return s.IndexOf(what, System.StringComparison.Ordinal);
        }

        /// <summary><c>String.contains(what)</c> (== <c>find(what) != -1</c>).</summary>
        public static bool Contains(string s, string what) => Find(s, what) != -1;

        /// <summary><c>String.begins_with(prefix)</c>.</summary>
        public static bool BeginsWith(string s, string prefix) => s.StartsWith(prefix, System.StringComparison.Ordinal);

        /// <summary><c>String.ends_with(suffix)</c>.</summary>
        public static bool EndsWith(string s, string suffix) => s.EndsWith(suffix, System.StringComparison.Ordinal);

        /// <summary>Godot String <c>a &lt; b</c> (code-point order).</summary>
        public static bool Less(string a, string b) => V.CompareCodePoints(a, b) < 0;

        /// <summary><c>String.strip_edges()</c>: trims every char &lt;= 32 from both ends.</summary>
        public static string StripEdges(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            int beg = 0;
            int end = s.Length;
            while (beg < end && s[beg] <= 32) beg++;
            while (end > beg && s[end - 1] <= 32) end--;
            return s.Substring(beg, end - beg);
        }

        /// <summary><c>String.is_valid_int()</c>: optional leading sign (only when longer than one char), then ASCII digits.</summary>
        public static bool IsValidInt(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int from = 0;
            if (s.Length != 1 && (s[0] == '+' || s[0] == '-')) from++;
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9') return false;
            }
            return true;
        }

        /// <summary><c>String.trim_prefix(prefix)</c>.</summary>
        public static string TrimPrefix(string s, string prefix) =>
            !string.IsNullOrEmpty(prefix) && s.StartsWith(prefix, System.StringComparison.Ordinal) ? s.Substring(prefix.Length) : s;
    }
}
