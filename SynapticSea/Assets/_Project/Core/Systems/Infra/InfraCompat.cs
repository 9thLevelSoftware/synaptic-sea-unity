// Group-C port helpers (not a ported file). Small Godot built-ins the kernel does not cover yet.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Kernel-gap helpers used by the progression / infra / save ports:
    /// Godot <c>typeof()</c> ids, <c>String.strip_edges</c>, <c>String.is_valid_int</c>,
    /// <c>String.sha256_text</c>, the recurring <c>_as_array</c>/<c>_as_dict</c> coercions,
    /// path-scheme-aware text reads (<c>user://</c> through <see cref="IStorage"/>, <c>res://</c> through
    /// <see cref="CoreServices.Resources"/>), and <c>ProjectSettings application/config/version</c>.
    /// </summary>
    internal static class InfraCompat
    {
        /// <summary>
        /// RUNTIME: <c>ProjectSettings.get_setting("application/config/version", "0.0.0")</c>.
        /// project.godot @ 96ecb2b0 does not set it, so Godot returns the "0.0.0" fallback.
        /// The Runtime bootstrap may overwrite this with Application.version.
        /// </summary>
        public static string ProjectVersion = "0.0.0";

        /// <summary>Godot <c>typeof(x)</c> (Variant.Type ids) for the Variant leaf set.</summary>
        public static int TypeOf(object v)
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
                default: return 0;
            }
        }

        /// <summary><c>String.strip_edges()</c>: trims every char &lt;= 32 on both sides.</summary>
        public static string StripEdges(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            int begin = 0;
            int end = s.Length;
            while (begin < end && s[begin] <= 32) begin++;
            while (end > begin && s[end - 1] <= 32) end--;
            return s.Substring(begin, end - begin);
        }

        /// <summary><c>String.is_valid_int()</c>: optional sign (only when length &gt; 1), then ASCII digits.</summary>
        public static bool IsValidInt(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int from = 0;
            if (s.Length != 1 && (s[0] == '+' || s[0] == '-')) from = 1;
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9') return false;
            }
            return true;
        }

        /// <summary><c>String.sha256_text()</c>: lowercase hex SHA-256 of the UTF-8 bytes.</summary>
        public static string Sha256Text(string s)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>The recurring <c>_as_array(value)</c>: arrays pass through, null is empty, anything else is wrapped.</summary>
        public static GdArray AsArray(object value)
        {
            if (value is GdArray a) return a;
            if (value == null) return new GdArray();
            return GdArray.Of(value);
        }

        /// <summary>The recurring <c>_as_dict(value)</c>: dictionaries pass through, anything else is empty.</summary>
        public static GdDict AsDict(object value) => value as GdDict ?? new GdDict();

        /// <summary><c>for item in value: out.append(str(item))</c> over <see cref="AsArray"/>.</summary>
        public static GdArray ToStringArray(object value)
        {
            var output = new GdArray();
            foreach (var item in AsArray(value)) output.Add(V.Str(item));
            return output;
        }

        /// <summary>Sorted copy of dictionary keys (<c>var k = d.keys(); k.sort()</c>).</summary>
        public static GdArray SortedKeys(GdDict d)
        {
            var keys = new GdArray(d.Keys);
            GdSort.Sort(keys);
            return keys;
        }

        /// <summary><c>"%d %s" % [...]</c> as an invariant-culture <c>string.Format</c>.
        /// Pass strings for %s (use <see cref="V.Str"/> on Variants) and longs/ints for %d.</summary>
        public static string Fmt(string format, params object[] args) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);

        static bool IsUser(string path) => path.StartsWith(ResPath.UserScheme, StringComparison.Ordinal);

        /// <summary><c>FileAccess.file_exists(path)</c> for a <c>user://</c> or <c>res://</c> path.</summary>
        public static bool FileExists(string path, IStorage storage)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (IsUser(path)) return (storage ?? CoreServices.UserStorage).FileExists(path);
            return CoreServices.Resources?.Exists(path) ?? false;
        }

        /// <summary><c>FileAccess.get_file_as_string(path)</c>: null when missing.</summary>
        public static string ReadText(string path, IStorage storage)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (IsUser(path)) return (storage ?? CoreServices.UserStorage).ReadText(path);
            return CoreServices.Resources?.ReadText(path);
        }

        /// <summary>
        /// <c>JSON.parse_string(FileAccess.get_file_as_string(path))</c>: <c>res://</c> goes through
        /// <see cref="CatalogRegistry"/> (deep copy), <c>user://</c> through <paramref name="storage"/>. Null when
        /// missing or malformed.
        /// </summary>
        public static object LoadJson(string path, IStorage storage)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (IsUser(path))
            {
                string text = (storage ?? CoreServices.UserStorage).ReadText(path);
                return text == null ? null : GdJson.ParseString(text);
            }
            return CatalogRegistry.Load(path);
        }

        /// <summary>
        /// <c>DirAccess</c> listing of a <c>res://</c> directory: <c>*.json</c> file names directly inside, sorted
        /// ordinally. Null when the resource reader cannot list (Godot: <c>DirAccess.open</c> returned null).
        /// </summary>
        public static List<string> ListResFiles(string dirPath)
        {
            if (!(CoreServices.Resources is FileSystemResourceReader reader)) return null;
            string full = Path.Combine(reader.Root, ResPath.StripRes(dirPath).Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(full)) return null;
            return Directory.GetFiles(full).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
    }
}
