using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Kernel
{
    /// <summary>
    /// The exporter's <c>fixtures/godot/meta.json</c> lists every file it wrote. Parity suites enumerate folders or
    /// read fixed names, so a deleted capture would otherwise just shrink a suite; this pins the whole capture set.
    /// </summary>
    public class FixtureManifestTests
    {
        const string MetaPath = "godot/meta.json";

        [Test]
        public void EveryExportedFixtureIsPresent()
        {
            Fixtures.Require(MetaPath);
            GdDict meta = Fixtures.ReadDict(MetaPath);
            Assert.IsEmpty(meta.GetArrayOrEmpty("errors"), "exporter recorded errors");

            GdArray files = meta.GetArray("files");
            Assert.IsNotNull(files, "meta.json has no files list");
            var missing = files.Select(V.Str).Where(f => !Fixtures.Exists("godot/" + f)).ToList();
            Assert.IsEmpty(missing, "exported fixtures missing from fixtures/godot:\n" + string.Join("\n", missing));

            var counts = new Dictionary<string, long>();
            foreach (string f in files.Select(V.Str))
            {
                string folder = f.Split('/')[0];
                counts[folder] = counts.TryGetValue(folder, out long c) ? c + 1 : 1;
            }
            foreach (var kv in meta.GetDict("file_counts_by_folder"))
                Assert.AreEqual(V.I64(kv.Value), counts.TryGetValue(V.Str(kv.Key), out long n) ? n : 0, $"file count for {kv.Key}/");
        }
    }
}
