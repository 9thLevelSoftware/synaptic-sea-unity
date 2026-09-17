using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Kernel
{
    /// <summary>
    /// The synced Godot data (StreamingAssets/data) must parse with the Godot-compatible reader, and every
    /// <c>res://data/...</c> reference inside it must resolve. Asset references (<c>res://assets</c>, <c>res://scenes</c>)
    /// are resolved by content catalogs in a later phase; their count is reported, not asserted.
    /// </summary>
    public class DataManifestTests
    {
        static string DataRoot => Path.Combine(Fixtures.StreamingDataRoot, "data");

        static IEnumerable<string> JsonFiles()
        {
            // StreamingAssets/data is checked in; only a checkout without StreamingAssets at all is skipped.
            if (!Directory.Exists(Fixtures.StreamingDataRoot)) Assert.Ignore("SynapticSea/Assets/StreamingAssets is absent (stripped checkout)");
            Assert.IsTrue(Directory.Exists(DataRoot), "StreamingAssets/data is missing; run tools/sync-godot-data.ps1");
            return Directory.GetFiles(DataRoot, "*.json", SearchOption.AllDirectories).OrderBy(p => p, System.StringComparer.Ordinal);
        }

        [Test]
        public void EveryDataFileParses()
        {
            var failures = new List<string>();
            int count = 0;
            foreach (string file in JsonFiles())
            {
                count++;
                try
                {
                    GdJson.Parse(File.ReadAllText(file, new UTF8Encoding(false)));
                }
                catch (GdJson.ParseError e)
                {
                    failures.Add($"{file}: {e.Message}");
                }
            }
            Assert.That(count, Is.GreaterThan(100), "expected the full Godot data set");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void EveryResDataReferenceResolves()
        {
            var reader = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            var missing = new SortedSet<string>(System.StringComparer.Ordinal);
            int assetRefs = 0;
            foreach (string file in JsonFiles())
            {
                var doc = GdJson.Parse(File.ReadAllText(file, new UTF8Encoding(false)));
                foreach (string s in Strings(doc))
                {
                    if (!s.StartsWith("res://", System.StringComparison.Ordinal)) continue;
                    if (s.StartsWith("res://data/", System.StringComparison.Ordinal))
                    {
                        // Godot-only resource twins and audio clips are intentionally not synced.
                        string ext = ResPath.GetExtension(s);
                        if (ext == "json" && !reader.Exists(s)) missing.Add(s);
                    }
                    else
                    {
                        assetRefs++;
                    }
                }
            }
            TestContext.WriteLine($"non-data res:// references (resolved by content catalogs later): {assetRefs}");
            Assert.IsEmpty(missing, "unresolved res://data JSON references:\n" + string.Join("\n", missing));
        }

        static IEnumerable<string> Strings(object v)
        {
            switch (v)
            {
                case string s:
                    yield return s;
                    break;
                case GdArray a:
                    foreach (var item in a)
                        foreach (var s in Strings(item)) yield return s;
                    break;
                case GdDict d:
                    foreach (var kv in d)
                    {
                        if (kv.Key is string ks) yield return ks;
                        foreach (var s in Strings(kv.Value)) yield return s;
                    }
                    break;
            }
        }
    }
}
