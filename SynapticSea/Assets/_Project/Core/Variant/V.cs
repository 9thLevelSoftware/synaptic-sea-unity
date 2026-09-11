using System;
using System.Collections;
using System.Globalization;
using System.Text;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// GDScript Variant helpers: value normalization, the <c>int()/float()/str()/bool()</c> coercions,
    /// <c>typeof</c>-style checks, truthiness, equality, ordering, and deep copy.
    /// </summary>
    public static class V
    {
        // ------------------------------------------------------------------ normalization

        /// <summary>Maps CLR values onto the Variant leaf set (ints become long, floats become double).</summary>
        public static object Normalize(object value)
        {
            switch (value)
            {
                case null:
                case long _:
                case double _:
                case string _:
                case bool _:
                case GdDict _:
                case GdArray _:
                case Vec2i _:
                case Vec3 _:
                    return value;
                case int i: return (long)i;
                case short s: return (long)s;
                case byte b: return (long)b;
                case sbyte sb: return (long)sb;
                case uint ui: return (long)ui;
                case ushort us: return (long)us;
                case ulong ul: return unchecked((long)ul);
                case float f: return (double)f;
                case decimal m: return (double)m;
                case char c: return c.ToString();
                case Enum e: return Convert.ToInt64(e, CultureInfo.InvariantCulture);
                case IDictionary _:
                    throw new ArgumentException("Use GdDict for dictionary Variant values.");
                case IEnumerable seq:
                    return new GdArray(seq);
                default:
                    throw new ArgumentException($"Type {value.GetType().Name} is not a Variant leaf type.");
            }
        }

        public static object NormalizeKey(object key) => Normalize(key);

        public static object DeepCopy(object value)
        {
            switch (value)
            {
                case GdDict d: return d.DeepCopy();
                case GdArray a: return a.DeepCopy();
                default: return value;
            }
        }

        // ------------------------------------------------------------------ typeof checks

        public static bool IsInt(object v) => v is long;
        public static bool IsFloat(object v) => v is double;
        public static bool IsNumber(object v) => v is long || v is double;
        public static bool IsString(object v) => v is string;
        public static bool IsBool(object v) => v is bool;
        public static bool IsDict(object v) => v is GdDict;
        public static bool IsArray(object v) => v is GdArray;

        /// <summary>Returns the value as a <see cref="GdDict"/> or null (the <c>typeof(x) == TYPE_DICTIONARY</c> guard).</summary>
        public static GdDict Dict(object v) => v as GdDict;

        /// <summary>Returns the value as a <see cref="GdArray"/> or null (the <c>typeof(x) == TYPE_ARRAY</c> guard).</summary>
        public static GdArray Arr(object v) => v as GdArray;

        // ------------------------------------------------------------------ coercions

        /// <summary>GDScript <c>int(x)</c>. Unconvertible values return <paramref name="fallback"/>.</summary>
        public static long I64(object v, long fallback = 0)
        {
            switch (v)
            {
                case long l: return l;
                case double d: return GdMath.Trunc(d);
                case bool b: return b ? 1 : 0;
                case string s: return StringToInt(s);
                default: return fallback;
            }
        }

        public static int I32(object v, int fallback = 0) => unchecked((int)I64(v, fallback));

        /// <summary>GDScript <c>float(x)</c>. Unconvertible values return <paramref name="fallback"/>.</summary>
        public static double F64(object v, double fallback = 0.0)
        {
            switch (v)
            {
                case double d: return d;
                case long l: return l;
                case bool b: return b ? 1.0 : 0.0;
                case string s: return StringToFloat(s);
                default: return fallback;
            }
        }

        /// <summary>GDScript <c>bool(x)</c> / Variant booleanization.</summary>
        public static bool Bool(object v, bool fallback = false)
        {
            switch (v)
            {
                case null: return fallback;
                case bool b: return b;
                case long l: return l != 0;
                case double d: return d != 0.0;
                case string s: return s.Length != 0;
                default: return Truthy(v);
            }
        }

        /// <summary>GDScript truthiness used by <c>if x:</c> and <c>not x</c>.</summary>
        public static bool Truthy(object v)
        {
            switch (v)
            {
                case null: return false;
                case bool b: return b;
                case long l: return l != 0;
                case double d: return d != 0.0;
                case string s: return s.Length != 0;
                case GdDict dict: return !dict.IsEmpty;
                case GdArray arr: return !arr.IsEmpty;
                case Vec2i v2: return v2 != Vec2i.Zero;
                case Vec3 v3: return v3 != Vec3.Zero;
                default: return true;
            }
        }

        /// <summary>GDScript <c>str(x)</c>.</summary>
        public static string Str(object v)
        {
            switch (v)
            {
                case null: return "<null>";
                case string s: return s;
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case double d: return GdFloatFormat.NumReal(d, true);
                case bool b: return b ? "true" : "false";
                case GdArray a: return ArrayToString(a);
                case GdDict dict: return DictToString(dict);
                default: return v.ToString();
            }
        }

        static string ArrayToString(GdArray a)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < a.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(a[i] is string s ? "\"" + s + "\"" : Str(a[i]));
            }
            return sb.Append(']').ToString();
        }

        static string DictToString(GdDict d)
        {
            if (d.IsEmpty) return "{  }";
            var sb = new StringBuilder("{ ");
            bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(kv.Key is string ks ? "\"" + ks + "\"" : Str(kv.Key));
                sb.Append(": ");
                sb.Append(kv.Value is string vs ? "\"" + vs + "\"" : Str(kv.Value));
            }
            return sb.Append(" }").ToString();
        }

        /// <summary><c>String.to_int()</c>: skips non-digits, honours a leading '-', stops at the first '.'.</summary>
        public static long StringToInt(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int to = s.IndexOf('.');
            if (to < 0) to = s.Length;
            long integer = 0;
            long sign = 1;
            bool seenDigit = false;
            for (int i = 0; i < to; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9')
                {
                    seenDigit = true;
                    integer = unchecked(integer * 10 + (c - '0'));
                }
                else if (c == '-' && !seenDigit)
                {
                    sign = -sign;
                }
            }
            return integer * sign;
        }

        /// <summary><c>String.to_float()</c>: Godot's built-in strtod over the numeric prefix; 0 when none.</summary>
        public static double StringToFloat(string s) => string.IsNullOrEmpty(s) ? 0.0 : GodotStrtod.Parse(s);

        // ------------------------------------------------------------------ equality and ordering

        /// <summary>GDScript <c>==</c> between Variants: int/float compare numerically; containers compare deeply.</summary>
        public static bool VariantEquals(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (IsNumber(a) && IsNumber(b))
            {
                if (a is long la && b is long lb) return la == lb;
                return F64(a) == F64(b);
            }
            switch (a)
            {
                case string sa: return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
                case bool ba: return b is bool bb && ba == bb;
                case Vec2i va: return b is Vec2i vb && va == vb;
                case Vec3 v3a: return b is Vec3 v3b && v3a == v3b;
                case GdArray aa:
                    {
                        if (!(b is GdArray ab) || aa.Count != ab.Count) return false;
                        for (int i = 0; i < aa.Count; i++)
                            if (!VariantEquals(aa[i], ab[i])) return false;
                        return true;
                    }
                case GdDict da:
                    {
                        if (!(b is GdDict db) || da.Count != db.Count) return false;
                        foreach (var kv in da)
                        {
                            if (!db.TryGetValue(kv.Key, out object other)) return false;
                            if (!VariantEquals(kv.Value, other)) return false;
                        }
                        return true;
                    }
                default:
                    return a.Equals(b);
            }
        }

        /// <summary>Godot Variant <c>&lt;</c> as used by <c>Array.sort()</c>.</summary>
        public static bool VariantLess(object a, object b)
        {
            if (IsNumber(a) && IsNumber(b))
            {
                if (a is long la && b is long lb) return la < lb;
                return F64(a) < F64(b);
            }
            if (a is string sa && b is string sb) return CompareCodePoints(sa, sb) < 0;
            if (a is Vec2i va && b is Vec2i vb) return va < vb;
            if (a is bool ba && b is bool bb) return !ba && bb;
            return TypeRank(a) < TypeRank(b);
        }

        static int TypeRank(object v)
        {
            switch (v)
            {
                case null: return 0;
                case bool _: return 1;
                case long _: return 2;
                case double _: return 3;
                case string _: return 4;
                case Vec2i _: return 6;
                case Vec3 _: return 9;
                case GdDict _: return 27;
                case GdArray _: return 28;
                default: return 99;
            }
        }

        /// <summary>Godot <c>String</c> ordering: compares Unicode code points (char32), not UTF-16 units.</summary>
        public static int CompareCodePoints(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                int ca = NextCodePoint(a, ref i);
                int cb = NextCodePoint(b, ref j);
                if (ca != cb) return ca < cb ? -1 : 1;
            }
            if (i < a.Length) return 1;
            if (j < b.Length) return -1;
            return 0;
        }

        /// <summary>Reads one Unicode code point (joining surrogate pairs) and advances <paramref name="index"/>.</summary>
        public static int NextCodePoint(string s, ref int index)
        {
            char c = s[index];
            if (char.IsHighSurrogate(c) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]))
            {
                int cp = char.ConvertToUtf32(c, s[index + 1]);
                index += 2;
                return cp;
            }
            index += 1;
            return c;
        }
    }
}
