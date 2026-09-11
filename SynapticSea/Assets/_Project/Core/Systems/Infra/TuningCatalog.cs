// Ported from scripts/systems/tuning_catalog.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Externalized balance numbers with const-friendly fallbacks (PKG-A4).
    /// Keys are flat strings; nested JSON objects flatten to dotted keys.
    /// </summary>
    public class TuningCatalog
    {
        public const string DefaultBalanceDir = "res://data/balance/";

        /// <summary>Explicit paths that work under PCK export.</summary>
        public static readonly GdArray DefaultBalanceFiles = GdArray.Of(
            "res://data/balance/shell.json"
        );

        GdDict _values = new GdDict();
        List<string> _loadedPaths = new List<string>();
        IStorage _storage;

        public TuningCatalog(IStorage storage = null)
        {
            _storage = storage;
        }

        /// <summary><c>user://</c> overrides are read through this storage.</summary>
        public IStorage Storage
        {
            get => _storage ?? CoreServices.UserStorage;
            set => _storage = value;
        }

        public void Clear()
        {
            _values.Clear();
            _loadedPaths = new List<string>();
        }

        /// <summary>
        /// Load one JSON object file. Keys are flat strings (or dotted paths as single keys).
        /// Non-dictionary roots and missing files leave existing values untouched and return false.
        /// </summary>
        public bool LoadFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !InfraCompat.FileExists(path, Storage)) return false;
            object parsed = InfraCompat.LoadJson(path, Storage);
            if (parsed == null || !(parsed is GdDict)) return false;
            MergeDict((GdDict)parsed, "");
            if (!_loadedPaths.Contains(path)) _loadedPaths.Add(path);
            return true;
        }

        /// <summary>Load the known balance file list (export-safe). Prefer this for production boot.</summary>
        public long LoadDefaults()
        {
            long loaded = 0;
            foreach (var path in DefaultBalanceFiles)
            {
                if (LoadFile(V.Str(path))) loaded += 1;
            }
            return loaded;
        }

        /// <summary>
        /// Load every <c>*.json</c> directly under dirPath (non-recursive).
        /// Falls back to LoadDefaults() when the directory cannot be listed.
        /// </summary>
        public long LoadDirectory(string dirPath = DefaultBalanceDir)
        {
            long loaded = 0;
            // RUNTIME: DirAccess.open(res://...) listing. Only a filesystem-backed resource reader can list;
            // any other reader behaves like Godot's "DirAccess cannot list res://" branch.
            List<string> entries = InfraCompat.ListResFiles(dirPath);
            if (entries == null) return LoadDefaults();
            foreach (string entry in entries)
            {
                if (entry.EndsWith(".json", StringComparison.Ordinal))
                {
                    string full = dirPath.TrimEnd('/') + "/" + entry;
                    if (LoadFile(full)) loaded += 1;
                }
            }
            if (loaded == 0) return LoadDefaults();
            return loaded;
        }

        public bool HasKey(string key) => _values.Has(key);

        public double GetFloat(string key, double defaultValue)
        {
            if (!_values.Has(key)) return defaultValue;
            return V.F64(_values[key]);
        }

        public long GetInt(string key, long defaultValue)
        {
            if (!_values.Has(key)) return defaultValue;
            return V.I64(_values[key]);
        }

        public bool GetBool(string key, bool defaultValue)
        {
            if (!_values.Has(key)) return defaultValue;
            return V.Bool(_values[key]);
        }

        public string GetString(string key, string defaultValue)
        {
            if (!_values.Has(key)) return defaultValue;
            return V.Str(_values[key]);
        }

        public object GetValue(string key, object defaultValue = null)
        {
            if (!_values.Has(key)) return defaultValue;
            return _values[key];
        }

        public List<string> GetLoadedPaths() => new List<string>(_loadedPaths);

        public int KeyCount() => _values.Count;

        void MergeDict(GdDict d, string prefix)
        {
            foreach (var k in new List<object>(d.Keys))
            {
                string keyStr = V.Str(k);
                string fullKey = prefix.Length == 0 ? keyStr : prefix + "." + keyStr;
                object v = d[k];
                if (v is GdDict child)
                    MergeDict(child, fullKey);
                else
                    _values[fullKey] = v;
            }
        }
    }
}
