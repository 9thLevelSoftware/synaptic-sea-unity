// Procgen-local helpers for GDScript behaviours the kernel does not cover (no GDScript source).
using System;
using System.Collections.Generic;
using System.IO;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Optional directory listing for <c>res://</c> resources. The kernel <see cref="IResourceReader"/> has no
    /// <c>DirAccess</c> equivalent; a Runtime reader that cannot enumerate the filesystem (Android StreamingAssets)
    /// should implement this from a build-time index.
    /// </summary>
    public interface IResourceDirectoryLister
    {
        bool DirExists(string resDir);

        /// <summary>File names (not paths, no directories) directly inside <paramref name="resDir"/>, in any order.</summary>
        IReadOnlyList<string> ListFiles(string resDir);
    }

    internal static class ProcgenCompat
    {
        /// <summary>
        /// GDScript <c>String.strip_edges()</c>: strips leading/trailing characters with code &lt;= 32
        /// (not the Unicode whitespace set used by <c>string.Trim()</c>).
        /// </summary>
        public static string StripEdges(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            int beg = 0, end = s.Length;
            while (beg < end && s[beg] <= 32) beg++;
            while (end > beg && s[end - 1] <= 32) end--;
            return s.Substring(beg, end - beg);
        }

        /// <summary>GDScript <c>String.path_join()</c>.</summary>
        public static string PathJoin(string basePath, string file)
        {
            if (string.IsNullOrEmpty(basePath)) return file ?? string.Empty;
            file = file ?? string.Empty;
            if (basePath[basePath.Length - 1] == '/' || (file.Length > 0 && file[0] == '/')) return basePath + file;
            return basePath + "/" + file;
        }

        /// <summary>GDScript <c>String &lt; String</c> (char32 code point order).</summary>
        public static bool StringLess(string a, string b) => V.CompareCodePoints(a, b) < 0;

        /// <summary>GDScript <c>Array.sort()</c> over strings (Godot introsort; ties cannot occur between distinct strings).</summary>
        public static void SortStrings(List<string> list) => GdSort.SortCustom(list, StringLess);

        /// <summary><c>DirAccess.dir_exists_absolute</c> / <c>DirAccess.open(...) != null</c> for a <c>res://</c> directory.</summary>
        public static bool ResDirExists(string resDir)
        {
            var reader = CoreServices.Resources;
            if (reader is IResourceDirectoryLister lister) return lister.DirExists(resDir);
            if (reader is FileSystemResourceReader fs) return Directory.Exists(FullPath(fs, resDir));
            return false;
        }

        /// <summary>
        /// Files directly inside a <c>res://</c> directory, standing in for a
        /// <c>DirAccess.list_dir_begin()</c> / <c>get_next()</c> walk that skips directories.
        /// Godot's walk order is the OS order; the reference captures were produced on Windows/NTFS, which
        /// enumerates in upper-cased ordinal order, so that order is reproduced here.
        /// </summary>
        public static List<string> ListResFiles(string resDir)
        {
            var names = new List<string>();
            var reader = CoreServices.Resources;
            if (reader is IResourceDirectoryLister lister)
            {
                names.AddRange(lister.ListFiles(resDir));
            }
            else if (reader is FileSystemResourceReader fs)
            {
                string full = FullPath(fs, resDir);
                if (!Directory.Exists(full)) return names;
                foreach (string path in Directory.GetFiles(full)) names.Add(Path.GetFileName(path));
            }
            names.Sort(NtfsOrder);
            return names;
        }

        static int NtfsOrder(string a, string b)
        {
            int c = string.CompareOrdinal(a.ToUpperInvariant(), b.ToUpperInvariant());
            return c != 0 ? c : string.CompareOrdinal(a, b);
        }

        static string FullPath(FileSystemResourceReader fs, string resDir)
        {
            string rel = ResPath.StripRes(resDir).Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(fs.Root, rel);
        }
    }
}
