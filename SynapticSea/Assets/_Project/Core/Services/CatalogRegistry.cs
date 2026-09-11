using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Services
{
    /// <summary>
    /// Parsed-JSON cache keyed by <c>res://</c> path. Replaces the per-model <c>static func load_*()</c> +
    /// <c>FileAccess</c> + <c>JSON.parse_string</c> pattern. Callers receive a deep copy by default so a model
    /// can never mutate shared catalog data (GDScript re-parsed per call, so each caller had its own copy).
    /// </summary>
    public static class CatalogRegistry
    {
        static readonly Dictionary<string, object> Cache = new Dictionary<string, object>(StringComparer.Ordinal);
        static readonly object Gate = new object();

        /// <summary>Parsed document or null when missing/malformed (Godot returned null / pushed an error).</summary>
        public static object Load(string resPath, bool copy = true)
        {
            object parsed;
            lock (Gate)
            {
                if (!Cache.TryGetValue(resPath, out parsed))
                {
                    var reader = CoreServices.Resources;
                    string text = reader?.ReadText(resPath);
                    if (text == null)
                    {
                        CoreServices.Log.Warning($"CatalogRegistry: missing resource {resPath}");
                        parsed = null;
                    }
                    else
                    {
                        parsed = GdJson.ParseString(text);
                        if (parsed == null) CoreServices.Log.Error($"CatalogRegistry: failed to parse {resPath}");
                    }
                    Cache[resPath] = parsed;
                }
            }
            return copy ? V.DeepCopy(parsed) : parsed;
        }

        public static GdDict LoadDict(string resPath, bool copy = true) => Load(resPath, copy) as GdDict;
        public static GdArray LoadArray(string resPath, bool copy = true) => Load(resPath, copy) as GdArray;

        public static bool Exists(string resPath) => CoreServices.Resources?.Exists(resPath) ?? false;

        /// <summary>Drops cached documents (tests, DataSync hot reload).</summary>
        public static void Clear()
        {
            lock (Gate) Cache.Clear();
        }
    }
}
