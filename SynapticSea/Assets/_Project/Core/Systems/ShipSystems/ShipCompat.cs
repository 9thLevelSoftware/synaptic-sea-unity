// Helpers for Godot String semantics used by the group D ports (ship systems / travel / work actions).
// Not a port of a single .gd file; lives here because the kernel has no equivalent yet.

using System;
using System.Collections.Generic;
using System.Text;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    internal static class ShipCompat
    {
        /// <summary>Godot <c>String.find(what)</c> presence check: an empty needle or haystack never matches.</summary>
        public static bool GdContains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(haystack)) return false;
            return haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        /// <summary>Godot <c>String.begins_with()</c> (ordinal).</summary>
        public static bool BeginsWith(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);

        /// <summary>Godot <c>String.ends_with()</c> (ordinal).</summary>
        public static bool EndsWith(string s, string suffix) => s.EndsWith(suffix, StringComparison.Ordinal);

        /// <summary>Godot <c>String.trim_suffix()</c>.</summary>
        public static string TrimSuffix(string s, string suffix) =>
            suffix.Length > 0 && s.EndsWith(suffix, StringComparison.Ordinal) ? s.Substring(0, s.Length - suffix.Length) : s;

        /// <summary>Godot <c>String.strip_edges()</c>: strips characters with code &lt;= 32 at both ends.</summary>
        public static string StripEdges(string s)
        {
            int start = 0;
            int end = s.Length - 1;
            while (start <= end && s[start] <= 32) start++;
            while (end >= start && s[end] <= 32) end--;
            return start > end ? string.Empty : s.Substring(start, end - start + 1);
        }

        /// <summary>
        /// Godot <c>String.simplify_path()</c> (core/string/ustring.cpp). Handles the protocol ("res://"),
        /// network-share, absolute and Windows-drive prefixes, collapses "//", removes "." and folds "..".
        /// </summary>
        public static string SimplifyPath(string path)
        {
            string s = path;
            string drive = string.Empty;
            int p = s.IndexOf("://", StringComparison.Ordinal);
            bool found = false;
            if (p > 0)
            {
                bool onlyChars = true;
                for (int i = 0; i < p; i++)
                {
                    char c = s[i];
                    bool alnum = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                    if (!alnum)
                    {
                        onlyChars = false;
                        break;
                    }
                }
                if (onlyChars)
                {
                    found = true;
                    drive = s.Substring(0, p + 3);
                    s = s.Substring(p + 3);
                }
            }
            if (!found)
            {
                if (s.StartsWith("//", StringComparison.Ordinal) || s.StartsWith("\\\\", StringComparison.Ordinal))
                {
                    drive = s.Substring(0, 2);
                    s = s.Substring(2);
                }
                else if (s.StartsWith("/", StringComparison.Ordinal) || s.StartsWith("\\", StringComparison.Ordinal))
                {
                    drive = s.Substring(0, 1);
                    s = s.Substring(1);
                }
                else
                {
                    p = s.IndexOf(":/", StringComparison.Ordinal);
                    if (p == -1) p = s.IndexOf(":\\", StringComparison.Ordinal);
                    if (p != -1 && p < s.IndexOf('/'))
                    {
                        drive = s.Substring(0, p + 2);
                        s = s.Substring(p + 2);
                    }
                }
            }

            s = s.Replace('\\', '/');
            while (true)
            {
                string compare = s.Replace("//", "/");
                if (s == compare) break;
                s = compare;
            }
            var dirs = new List<string>();
            foreach (string part in s.Split('/'))
                if (part.Length > 0) dirs.Add(part);

            for (int i = 0; i < dirs.Count; i++)
            {
                string d = dirs[i];
                if (d == ".")
                {
                    dirs.RemoveAt(i);
                    i--;
                }
                else if (d == "..")
                {
                    // Godot 4.7.1 (probed with the parity binary): ".." folds its predecessor unless that is also
                    // ".."; a leading ".." is dropped under any drive except none and "res://".
                    if (i == 0)
                    {
                        if (drive.Length > 0 && drive != "res://")
                        {
                            dirs.RemoveAt(i);
                            i--;
                        }
                    }
                    else if (dirs[i - 1] != "..")
                    {
                        dirs.RemoveAt(i);
                        dirs.RemoveAt(i - 1);
                        i -= 2;
                    }
                }
            }

            var sb = new StringBuilder();
            for (int i = 0; i < dirs.Count; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(dirs[i]);
            }
            return drive + sb;
        }

        /// <summary>GDScript <c>"%.Nf" % value</c> (String::sprintf 'f': String::num then pad_decimals).</summary>
        public static string FormatF(double value, int decimals)
        {
            if (double.IsNaN(value)) return "nan";
            if (double.IsInfinity(value)) return value < 0 ? "-inf" : "inf";
            return GdFloatFormat.FormatFixed(value, decimals);
        }

        /// <summary>Sorts strings with Godot's <c>PackedStringArray.sort()</c> ordering (code points).</summary>
        public static void SortStrings(List<string> list) =>
            GdSort.SortCustom(list, (a, b) => V.CompareCodePoints(a, b) < 0);

        /// <summary>Wraps a string list into a <see cref="GdArray"/> (<c>Array[String].duplicate()</c> reaching a summary).</summary>
        public static GdArray ToGdArray(IEnumerable<string> items)
        {
            var arr = new GdArray();
            foreach (string s in items) arr.Add(s);
            return arr;
        }
    }
}
