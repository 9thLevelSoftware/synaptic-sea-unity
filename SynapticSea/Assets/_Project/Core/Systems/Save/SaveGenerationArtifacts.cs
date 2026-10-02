using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Detached immutable JSON overlay. Static assets remain supplied by the ordinary resource reader.</summary>
    public static class SaveGenerationArtifacts
    {
        public static string Hash(string text)
        { using (var sha = SHA256.Create()) { var b = sha.ComputeHash(new UTF8Encoding(false, true).GetBytes(text)); return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); } }
        public static bool TryCreateReader(GdDict selection, IResourceReader fallback, out IResourceReader reader, out string reason)
        {
            reader = null; reason = "invalid_selection";
            if (selection == null || !selection.GetBool("ok") || !(selection.Get("payloads") is GdDict payloads) ||
                selection.GetString("payloads_sha256") != Hash(GdJson.Stringify(payloads))) return false;
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object value in payloads.GetArrayOrEmpty("artifacts"))
            {
                if (!(value is GdDict a) || !(a.Get("text") is string text) || !SafeLogicalPath(a.GetString("logical_path")) ||
                    !identities.Add(a.GetString("logical_path")) || !(GdJson.ParseString(text) is GdDict)) return false;
                texts.Add(a.GetString("logical_path"), text);
            }
            if (texts.Count == 0) return false;
            reader = new Reader(texts, fallback); reason = ""; return true;
        }
        public static bool SafeLogicalPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\\') >= 0) return false;
            string relative = path.StartsWith("res://", StringComparison.Ordinal) ? path.Substring(6) : path.StartsWith("user://", StringComparison.Ordinal) ? path.Substring(7) : "";
            if (relative.Length == 0 || relative.IndexOf(':') >= 0) return false;
            foreach (string p in relative.Split('/')) if (p.Length == 0 || p == "." || p == "..") return false;
            return true;
        }
        sealed class Reader : IResourceReader
        {
            readonly Dictionary<string, string> _texts; readonly IResourceReader _fallback;
            public Reader(Dictionary<string, string> texts, IResourceReader fallback) { _texts = texts; _fallback = fallback; }
            public bool Exists(string path) => _texts.ContainsKey(path) || _fallback?.Exists(path) == true;
            public string ReadText(string path) => _texts.TryGetValue(path, out string text) ? text : _fallback?.ReadText(path);
        }
    }
}
