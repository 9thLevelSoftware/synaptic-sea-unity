using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// GDScript <c>String</c> methods and <c>%</c>-formatting with Godot 4.7 semantics. Use these instead of the
    /// .NET equivalents, which differ in edge cases (<c>Trim</c> strips Unicode whitespace, <c>IndexOf("")</c> is 0,
    /// culture-sensitive formatting, UTF-16 ordering).
    /// Wave 1 ports carry equivalent private helpers (*Compat classes); new code uses this class.
    /// </summary>
    public static class GdString
    {
        /// <summary><c>String.find(what, from)</c>: -1 when either string is empty (Godot never matches an empty needle).</summary>
        public static int Find(string s, string what, int from = 0)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(what) || from < 0 || from >= s.Length) return -1;
            return s.IndexOf(what, from, StringComparison.Ordinal);
        }

        /// <summary><c>String.rfind(what)</c>.</summary>
        public static int RFind(string s, string what)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(what)) return -1;
            return s.LastIndexOf(what, StringComparison.Ordinal);
        }

        /// <summary><c>String.contains(what)</c> / <c>what in s</c>.</summary>
        public static bool Contains(string s, string what) => Find(s, what) != -1;

        public static bool BeginsWith(string s, string prefix) => (s ?? "").StartsWith(prefix ?? "", StringComparison.Ordinal);
        public static bool EndsWith(string s, string suffix) => (s ?? "").EndsWith(suffix ?? "", StringComparison.Ordinal);

        /// <summary><c>String.strip_edges()</c>: trims characters with code &lt;= 32 (not the Unicode whitespace set).</summary>
        public static string StripEdges(string s, bool left = true, bool right = true)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            int beg = 0, end = s.Length;
            if (left) while (beg < end && s[beg] <= 32) beg++;
            if (right) while (end > beg && s[end - 1] <= 32) end--;
            return s.Substring(beg, end - beg);
        }

        public static string TrimPrefix(string s, string prefix) =>
            !string.IsNullOrEmpty(prefix) && (s ?? "").StartsWith(prefix, StringComparison.Ordinal) ? s.Substring(prefix.Length) : s ?? "";

        public static string TrimSuffix(string s, string suffix) =>
            !string.IsNullOrEmpty(suffix) && (s ?? "").EndsWith(suffix, StringComparison.Ordinal) ? s.Substring(0, s.Length - suffix.Length) : s ?? "";

        /// <summary><c>String.is_valid_int()</c>: optional sign (only when longer than one char), then ASCII digits.</summary>
        public static bool IsValidInt(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int from = s.Length != 1 && (s[0] == '+' || s[0] == '-') ? 1 : 0;
            for (int i = from; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        /// <summary><c>String.split(delimiter, allow_empty)</c> for a non-empty delimiter.</summary>
        public static List<string> Split(string s, string delimiter, bool allowEmpty = true)
        {
            var result = new List<string>();
            foreach (string part in (s ?? "").Split(new[] { delimiter }, StringSplitOptions.None))
                if (allowEmpty || part.Length > 0) result.Add(part);
            return result;
        }

        /// <summary><c>String.path_join(file)</c>.</summary>
        public static string PathJoin(string basePath, string file)
        {
            if (string.IsNullOrEmpty(basePath)) return file ?? "";
            file = file ?? "";
            if (basePath[basePath.Length - 1] == '/' || (file.Length > 0 && file[0] == '/')) return basePath + file;
            return basePath + "/" + file;
        }

        /// <summary>Godot <c>String &lt; String</c> (Unicode code point order).</summary>
        public static bool Less(string a, string b) => V.CompareCodePoints(a, b) < 0;

        /// <summary><c>Array[String].sort()</c> with Godot's introsort and string ordering.</summary>
        public static void SortStrings(List<string> list) => GdSort.SortCustom(list, Less);

        /// <summary>
        /// <c>"%.Nf" % value</c> (and <c>"%+.Nf"</c>). Godot's sprintf formats <c>abs(value)</c> with
        /// <c>String::num</c>, pads the decimals back out, then prefixes the sign using <c>signbit</c>
        /// (so <c>-0.0</c> prints as <c>-0.00</c>).
        /// </summary>
        public static string FormatFixed(double value, int decimals, bool showPlus = false)
        {
            bool negative = BitConverter.DoubleToInt64Bits(value) < 0;
            double abs = Math.Abs(value);
            string body = double.IsNaN(abs) || double.IsInfinity(abs)
                ? GdFloatFormat.Num(abs, decimals)
                : GdFloatFormat.FormatFixed(abs, decimals);
            if (negative) return "-" + body;
            return showPlus ? "+" + body : body;
        }

        /// <summary><c>"%d" % value</c>.</summary>
        public static string FormatInt(long value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary><c>"%0Nd" % value</c> (zero padded to <paramref name="width"/>).</summary>
        public static string FormatIntPadded(long value, int width)
        {
            string digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(width - (value < 0 ? 1 : 0), '0');
            return value < 0 ? "-" + digits : digits;
        }

        /// <summary><c>String.sha256_text()</c>: lowercase hex of the UTF-8 bytes.</summary>
        public static string Sha256Text(string s)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>Godot 4.7 <c>String.capitalize()</c>: "mk2_rifle" → "Mk 2 Rifle", "camelCase" → "Camel Case".</summary>
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
                if (i > 0) cap.Append(' ');
                cap.Append(char.ToUpperInvariant(slice[0]));
                for (int j = 1; j < slice.Length; j++) cap.Append(char.ToLowerInvariant(slice[j]));
            }
            return cap.ToString();
        }

        static string CamelcaseToUnderscore(string s)
        {
            var result = new StringBuilder();
            int start = 0;
            bool prevUpper = char.IsUpper(s[0]), prevLower = char.IsLower(s[0]), prevDigit = char.IsDigit(s[0]);
            for (int i = 1; i < s.Length; i++)
            {
                bool currUpper = char.IsUpper(s[i]), currLower = char.IsLower(s[i]), currDigit = s[i] >= '0' && s[i] <= '9';
                bool nextLower = i + 1 < s.Length && char.IsLower(s[i + 1]);
                bool split = (prevLower && currUpper) || ((prevUpper || prevDigit) && currUpper && nextLower) ||
                             (prevDigit && currLower && nextLower) || ((prevUpper || prevLower) && currDigit);
                if (split)
                {
                    result.Append(s, start, i - start).Append('_');
                    start = i;
                }
                prevUpper = currUpper;
                prevLower = currLower;
                prevDigit = currDigit;
            }
            result.Append(s, start, s.Length - start);
            return result.ToString().ToLowerInvariant();
        }

        /// <summary>Copies a string list into a summary-safe <see cref="GdArray"/>.</summary>
        public static GdArray ToGdArray(IEnumerable<string> items)
        {
            var arr = new GdArray();
            if (items != null) foreach (string s in items) arr.Add(s);
            return arr;
        }

        /// <summary>A GdArray of Variants as strings (<c>Array[String](arr)</c>-style conversion via <c>str()</c>).</summary>
        public static List<string> ToStringList(GdArray items)
        {
            var list = new List<string>();
            if (items != null) foreach (object o in items) list.Add(V.Str(o));
            return list;
        }
    }
}
