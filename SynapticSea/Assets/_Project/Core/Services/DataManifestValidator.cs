using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Services
{
    /// <summary>Result of <see cref="DataManifestValidator.Validate"/>.</summary>
    public sealed class DataManifestReport
    {
        /// <summary>JSON files found under <c>StreamingAssets/data</c>.</summary>
        public int FileCount;

        /// <summary><c>res://</c> references outside <c>res://data/</c> (assets and scenes, resolved by content catalogs).</summary>
        public int AssetReferenceCount;

        /// <summary>Set when <c>StreamingAssets/data</c> itself is missing.</summary>
        public string RootError = "";

        /// <summary>"path: message" for every file the Godot-faithful reader rejects.</summary>
        public readonly List<string> ParseErrors = new List<string>();

        /// <summary><c>res://data/**.json</c> references that do not resolve, sorted ordinally.</summary>
        public readonly SortedSet<string> MissingDataReferences = new SortedSet<string>(StringComparer.Ordinal);

        public bool IsValid => RootError.Length == 0 && ParseErrors.Count == 0 && MissingDataReferences.Count == 0;

        public string Describe()
        {
            if (RootError.Length != 0) return RootError;
            var sb = new StringBuilder();
            sb.Append("files=").Append(FileCount)
              .Append(" parse_errors=").Append(ParseErrors.Count)
              .Append(" missing_data_refs=").Append(MissingDataReferences.Count)
              .Append(" asset_refs=").Append(AssetReferenceCount);
            foreach (string e in ParseErrors) sb.Append("\n  parse: ").Append(e);
            foreach (string m in MissingDataReferences) sb.Append("\n  missing: ").Append(m);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Validates the synced Godot data (plan Phase 12 build step 1). Every <c>StreamingAssets/data/**/*.json</c> must parse
    /// with the Godot-faithful reader, and every <c>res://data/...json</c> string inside it must resolve. Asset references
    /// (<c>res://assets</c>, <c>res://scenes</c>) are counted, not checked: the content catalogs resolve those.
    /// Engine-free, so the Builder and the EditMode/dotnet tests share one implementation.
    /// </summary>
    public static class DataManifestValidator
    {
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <param name="streamingAssetsRoot">The directory that holds <c>data/</c> (Unity: <c>Application.streamingAssetsPath</c>).</param>
        public static DataManifestReport Validate(string streamingAssetsRoot)
        {
            var report = new DataManifestReport();
            string dataRoot = Path.Combine(streamingAssetsRoot ?? "", "data");
            if (!Directory.Exists(dataRoot))
            {
                report.RootError = "StreamingAssets/data missing at " + dataRoot + " (run tools/sync-godot-data.ps1)";
                return report;
            }

            var reader = new FileSystemResourceReader(streamingAssetsRoot);
            foreach (string file in JsonFiles(dataRoot))
            {
                report.FileCount++;
                object doc;
                try
                {
                    doc = GdJson.Parse(File.ReadAllText(file, Utf8NoBom));
                }
                catch (GdJson.ParseError e)
                {
                    report.ParseErrors.Add(file + ": " + e.Message);
                    continue;
                }
                foreach (string s in Strings(doc))
                {
                    if (!s.StartsWith(ResPath.ResScheme, StringComparison.Ordinal)) continue;
                    if (s.StartsWith("res://data/", StringComparison.Ordinal))
                    {
                        // Godot-only resource twins (.tres) and audio clips are intentionally not synced.
                        if (ResPath.GetExtension(s) == "json" && !reader.Exists(s)) report.MissingDataReferences.Add(s);
                    }
                    else
                    {
                        report.AssetReferenceCount++;
                    }
                }
            }
            return report;
        }

        /// <summary>Every <c>*.json</c> under <paramref name="dataRoot"/>, in ordinal path order.</summary>
        public static IEnumerable<string> JsonFiles(string dataRoot)
        {
            return Directory.GetFiles(dataRoot, "*.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal);
        }

        /// <summary>Every string in a parsed Variant tree, including dictionary keys.</summary>
        public static IEnumerable<string> Strings(object variant)
        {
            switch (variant)
            {
                case string s:
                    yield return s;
                    break;
                case GdArray a:
                    foreach (object item in a)
                        foreach (string s in Strings(item)) yield return s;
                    break;
                case GdDict d:
                    foreach (var kv in d)
                    {
                        if (kv.Key is string ks) yield return ks;
                        foreach (string s in Strings(kv.Value)) yield return s;
                    }
                    break;
            }
        }
    }
}
