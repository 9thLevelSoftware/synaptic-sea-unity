using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SynapticSea.Core.Services
{
    /// <summary>
    /// Replaces <c>FileAccess</c>/<c>DirAccess</c> on <c>user://</c> paths. Paths are Godot-style
    /// (<c>user://saves/world.json</c>) or relative (<c>saves/world.json</c>); '/' separators.
    /// </summary>
    public interface IStorage
    {
        bool FileExists(string path);
        bool DirExists(string path);
        string ReadText(string path);
        void WriteText(string path, string text);
        bool Delete(string path);
        bool Rename(string from, string to);
        void MakeDirRecursive(string path);

        /// <summary>File names (not paths) directly inside <paramref name="dir"/>, sorted ordinally.</summary>
        IReadOnlyList<string> ListFiles(string dir);

        /// <summary>Absolute OS path for diagnostics (<c>ProjectSettings.globalize_path</c>).</summary>
        string Globalize(string path);
    }

    /// <summary>Replaces reads of <c>res://</c> resources (the game's JSON data).</summary>
    public interface IResourceReader
    {
        bool Exists(string resPath);
        string ReadText(string resPath);
    }

    /// <summary>Godot path helpers.</summary>
    public static class ResPath
    {
        public const string ResScheme = "res://";
        public const string UserScheme = "user://";

        /// <summary><c>res://data/items/x.json</c> becomes <c>data/items/x.json</c>.</summary>
        public static string StripRes(string path)
        {
            if (path == null) return string.Empty;
            return path.StartsWith(ResScheme, StringComparison.Ordinal) ? path.Substring(ResScheme.Length) : path.TrimStart('/');
        }

        /// <summary><c>user://saves/a.json</c> becomes <c>saves/a.json</c>.</summary>
        public static string StripUser(string path)
        {
            if (path == null) return string.Empty;
            return path.StartsWith(UserScheme, StringComparison.Ordinal) ? path.Substring(UserScheme.Length) : path.TrimStart('/');
        }

        /// <summary><c>String.get_file()</c>.</summary>
        public static string GetFile(string path)
        {
            int i = path.LastIndexOf('/');
            return i < 0 ? path : path.Substring(i + 1);
        }

        /// <summary><c>String.get_base_dir()</c>.</summary>
        public static string GetBaseDir(string path)
        {
            int i = path.LastIndexOf('/');
            if (i < 0) return string.Empty;
            string dir = path.Substring(0, i);
            return dir.EndsWith(":/", StringComparison.Ordinal) ? dir + "/" : dir;
        }

        /// <summary><c>String.get_basename()</c> (strips the last extension).</summary>
        public static string GetBasename(string path)
        {
            int slash = path.LastIndexOf('/');
            int dot = path.LastIndexOf('.');
            return dot > slash ? path.Substring(0, dot) : path;
        }

        /// <summary><c>String.get_extension()</c>.</summary>
        public static string GetExtension(string path)
        {
            int slash = path.LastIndexOf('/');
            int dot = path.LastIndexOf('.');
            return dot > slash ? path.Substring(dot + 1) : string.Empty;
        }
    }

    /// <summary>In-memory <c>user://</c> for tests.</summary>
    public sealed class MemoryStorage : IStorage
    {
        readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly HashSet<string> _dirs = new HashSet<string>(StringComparer.Ordinal) { "" };

        static string Key(string path) => ResPath.StripUser(path).Trim('/');

        public bool FileExists(string path) => _files.ContainsKey(Key(path));
        public bool DirExists(string path) => _dirs.Contains(Key(path));

        public string ReadText(string path) => _files.TryGetValue(Key(path), out string t) ? t : null;

        public void WriteText(string path, string text)
        {
            string k = Key(path);
            MakeDirRecursive(ResPath.GetBaseDir(k));
            _files[k] = text ?? string.Empty;
        }

        public bool Delete(string path) => _files.Remove(Key(path));

        public bool Rename(string from, string to)
        {
            string f = Key(from);
            if (!_files.TryGetValue(f, out string text)) return false;
            _files.Remove(f);
            WriteText(to, text);
            return true;
        }

        public void MakeDirRecursive(string path)
        {
            string k = Key(path);
            while (true)
            {
                _dirs.Add(k);
                int i = k.LastIndexOf('/');
                if (i < 0) break;
                k = k.Substring(0, i);
            }
            _dirs.Add("");
        }

        public IReadOnlyList<string> ListFiles(string dir)
        {
            string prefix = Key(dir);
            if (prefix.Length > 0) prefix += "/";
            return _files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('/', prefix.Length) < 0)
                .Select(k => k.Substring(prefix.Length))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
        }

        public string Globalize(string path) => "memory://" + Key(path);
    }

    /// <summary><c>user://</c> mapped onto a real directory (Unity: <c>Application.persistentDataPath</c>).</summary>
    public sealed class FileSystemStorage : IStorage
    {
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        readonly string _root;

        public FileSystemStorage(string rootDirectory)
        {
            _root = Path.GetFullPath(rootDirectory);
            Directory.CreateDirectory(_root);
        }

        public string Globalize(string path) => Path.Combine(_root, ResPath.StripUser(path).Replace('/', Path.DirectorySeparatorChar));

        public bool FileExists(string path) => File.Exists(Globalize(path));
        public bool DirExists(string path) => Directory.Exists(Globalize(path));
        public string ReadText(string path) => File.Exists(Globalize(path)) ? File.ReadAllText(Globalize(path), Utf8NoBom) : null;

        public void WriteText(string path, string text)
        {
            string full = Globalize(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            // Write-then-rename so a crash mid-write never leaves a truncated save.
            string tmp = full + ".tmp";
            File.WriteAllText(tmp, text ?? string.Empty, Utf8NoBom);
            if (File.Exists(full)) File.Delete(full);
            File.Move(tmp, full);
        }

        public bool Delete(string path)
        {
            string full = Globalize(path);
            if (!File.Exists(full)) return false;
            File.Delete(full);
            return true;
        }

        public bool Rename(string from, string to)
        {
            string f = Globalize(from), t = Globalize(to);
            if (!File.Exists(f)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(t));
            if (File.Exists(t)) File.Delete(t);
            File.Move(f, t);
            return true;
        }

        public void MakeDirRecursive(string path) => Directory.CreateDirectory(Globalize(path));

        public IReadOnlyList<string> ListFiles(string dir)
        {
            string full = Globalize(dir);
            if (!Directory.Exists(full)) return Array.Empty<string>();
            return Directory.GetFiles(full).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// <c>res://</c> reader over a directory that mirrors the Godot project root
    /// (Unity: <c>StreamingAssets</c>, where <c>data/**</c> is copied verbatim).
    /// <c>user://</c> paths (Godot's <c>FileAccess</c> read both schemes; the port writes generated run layouts under
    /// <c>user://runs/</c>) resolve through <see cref="UserStorage"/>, which defaults to <see cref="CoreServices.UserStorage"/>
    /// at call time. Directory listing (<c>ProcgenCompat</c>, <c>InfraCompat</c>) keeps using <see cref="Root"/> for
    /// <c>res://</c> directories.
    /// </summary>
    public sealed class FileSystemResourceReader : IResourceReader
    {
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        readonly string _root;
        readonly IStorage _userStorage;

        public FileSystemResourceReader(string rootDirectory, IStorage userStorage = null)
        {
            _root = Path.GetFullPath(rootDirectory);
            _userStorage = userStorage;
        }

        public string Root => _root;

        /// <summary>The storage <c>user://</c> paths read from (the explicit one, else <see cref="CoreServices.UserStorage"/>).</summary>
        public IStorage UserStorage => _userStorage ?? CoreServices.UserStorage;

        public static bool IsUserPath(string path) => path != null && path.StartsWith(ResPath.UserScheme, StringComparison.Ordinal);

        string Full(string resPath) => Path.Combine(_root, ResPath.StripRes(resPath).Replace('/', Path.DirectorySeparatorChar));

        public bool Exists(string resPath)
        {
            if (IsUserPath(resPath)) return UserStorage?.FileExists(resPath) ?? false;
            return File.Exists(Full(resPath));
        }

        /// <summary>File names directly inside a <c>res://</c> or <c>user://</c> directory, sorted ordinally (empty when missing).</summary>
        public IReadOnlyList<string> ListFiles(string dir)
        {
            if (IsUserPath(dir)) return UserStorage?.ListFiles(dir) ?? (IReadOnlyList<string>)Array.Empty<string>();
            string full = Full(dir);
            if (!Directory.Exists(full)) return Array.Empty<string>();
            return Directory.GetFiles(full).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        public string ReadText(string resPath)
        {
            if (IsUserPath(resPath)) return UserStorage != null && UserStorage.FileExists(resPath) ? UserStorage.ReadText(resPath) : null;
            return File.Exists(Full(resPath)) ? File.ReadAllText(Full(resPath), Utf8NoBom) : null;
        }
    }
}
