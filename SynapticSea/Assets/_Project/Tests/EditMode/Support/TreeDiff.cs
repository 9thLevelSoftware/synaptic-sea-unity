using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests
{
    /// <summary>
    /// Type-aware structural comparison of Variant trees for Godot parity. Ints must stay ints and floats must stay
    /// floats (that decides <c>17</c> versus <c>17.0</c> in saved JSON), and floats compare bit-exactly unless a
    /// tolerance is given. Reports readable paths such as <c>$.systems.power.health</c>.
    /// </summary>
    public static class TreeDiff
    {
        public sealed class Options
        {
            public double FloatTolerance;
            public bool IntFloatEquivalent;
            public HashSet<string> IgnoreKeys = new HashSet<string>(StringComparer.Ordinal);
            public int MaxDifferences = 25;
        }

        public static List<string> Compare(object expected, object actual, Options options = null)
        {
            options = options ?? new Options();
            var diffs = new List<string>();
            Walk(expected, V.Normalize(actual), "$", options, diffs);
            return diffs;
        }

        public static string Format(List<string> diffs) =>
            diffs.Count == 0 ? "" : $"{diffs.Count} difference(s):\n  " + string.Join("\n  ", diffs);

        static void Walk(object e, object a, string path, Options o, List<string> diffs)
        {
            if (diffs.Count >= o.MaxDifferences) return;
            if (e == null || a == null)
            {
                if (e != null || a != null) diffs.Add($"{path}: expected {Show(e)}, got {Show(a)}");
                return;
            }

            if (V.IsNumber(e) && V.IsNumber(a))
            {
                bool sameKind = e.GetType() == a.GetType();
                if (!sameKind && !o.IntFloatEquivalent)
                {
                    diffs.Add($"{path}: expected {Kind(e)} {Show(e)}, got {Kind(a)} {Show(a)}");
                    return;
                }
                if (e is long le && a is long la)
                {
                    if (le != la) diffs.Add($"{path}: expected {le}, got {la}");
                    return;
                }
                double de = V.F64(e), da = V.F64(a);
                bool equal = o.FloatTolerance > 0
                    ? Math.Abs(de - da) <= o.FloatTolerance || de == da || (double.IsNaN(de) && double.IsNaN(da))
                    : BitConverter.DoubleToInt64Bits(de) == BitConverter.DoubleToInt64Bits(da) || de == da || (double.IsNaN(de) && double.IsNaN(da));
                if (!equal) diffs.Add($"{path}: expected {Show(de)}, got {Show(da)} (delta {Show(da - de)})");
                return;
            }

            if (e.GetType() != a.GetType())
            {
                diffs.Add($"{path}: expected {Kind(e)} {Show(e)}, got {Kind(a)} {Show(a)}");
                return;
            }

            switch (e)
            {
                case string se:
                    if (!string.Equals(se, (string)a, StringComparison.Ordinal)) diffs.Add($"{path}: expected \"{se}\", got \"{a}\"");
                    return;
                case bool be:
                    if (be != (bool)a) diffs.Add($"{path}: expected {be}, got {a}");
                    return;
                case GdArray ea:
                    {
                        var aa = (GdArray)a;
                        if (ea.Count != aa.Count) diffs.Add($"{path}: expected array length {ea.Count}, got {aa.Count}");
                        for (int i = 0; i < Math.Min(ea.Count, aa.Count); i++) Walk(ea[i], aa[i], $"{path}[{i}]", o, diffs);
                        return;
                    }
                case GdDict ed:
                    {
                        var ad = (GdDict)a;
                        foreach (var k in ed.Keys)
                        {
                            string ks = V.Str(k);
                            if (o.IgnoreKeys.Contains(ks)) continue;
                            if (!ad.TryGetValue(k, out object av))
                            {
                                if (!ad.TryGetValue(ks, out av))
                                {
                                    diffs.Add($"{path}.{ks}: missing (expected {Show(ed[k])})");
                                    continue;
                                }
                            }
                            Walk(ed[k], av, $"{path}.{ks}", o, diffs);
                        }
                        foreach (var k in ad.Keys)
                        {
                            string ks = V.Str(k);
                            if (o.IgnoreKeys.Contains(ks)) continue;
                            if (!ed.Has(k) && !ed.Has(ks)) diffs.Add($"{path}.{ks}: unexpected key (value {Show(ad[k])})");
                        }
                        return;
                    }
                default:
                    if (!V.VariantEquals(e, a)) diffs.Add($"{path}: expected {Show(e)}, got {Show(a)}");
                    return;
            }
        }

        static string Kind(object v) => v is long ? "int" : v is double ? "float" : v?.GetType().Name ?? "null";

        static string Show(object v)
        {
            switch (v)
            {
                case null: return "null";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case string s: return "\"" + (s.Length > 60 ? s.Substring(0, 60) + "…" : s) + "\"";
                case GdDict d: return "{" + string.Join(",", d.Keys.Take(6).Select(V.Str)) + (d.Count > 6 ? ",…" : "") + "}";
                case GdArray a: return $"[{a.Count} items]";
                default: return V.Str(v);
            }
        }
    }
}
