using System;
using System.Globalization;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Parity
{
    /// <summary>
    /// fixtures/godot/kernel/fnv1a_fixture.json: SeedDeterminismContract.fnv1a_64 over code points reproduces every
    /// recorded hash (strings, golden/smoke file texts, re-serialized texts), and the contract pipeline
    /// (record_golden / assert_layout_match) regenerates the three recorded pipeline texts byte-for-byte.
    /// </summary>
    public class ProcgenDeterminismHashTests
    {
        const string FixturePath = "godot/kernel/fnv1a_fixture.json";

        [SetUp]
        public void SetUp()
        {
            Fixtures.Require(FixturePath);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
        }

        static long Hex(GdDict entry) =>
            unchecked((long)ulong.Parse(entry.GetString("fnv1a_64_hex"), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        static string Lf(string text) => text.Replace("\r\n", "\n");

        [Test]
        public void StringHashesMatchGodot()
        {
            GdArray strings = Fixtures.ReadDict(FixturePath).GetArray("strings");
            Assert.That(strings.Count, Is.GreaterThan(0));
            foreach (GdDict entry in strings)
            {
                string input = entry.GetString("input");
                Assert.AreEqual(Hex(entry), SeedDeterminismContract.Fnv1a64(input), "input: " + input);
                Assert.AreEqual(entry.GetInt("fnv1a_64"), SeedDeterminismContract.Fnv1a64(input), "signed int64 form: " + input);
            }
        }

        [Test]
        public void FileHashesMatchGodot()
        {
            GdArray files = Fixtures.ReadDict(FixturePath).GetArray("files");
            int n = 0;
            foreach (GdDict entry in files)
            {
                string text;
                if (entry.Has("hashed_text_file"))
                {
                    text = Fixtures.ReadText("godot/" + entry.GetString("hashed_text_file"));
                }
                else
                {
                    // res://data/procgen/golden/<name>/layout.json -> fixtures/golden/<name>/layout.json;
                    // res://data/procgen/smoke/seed_000017/layout.json -> fixtures/golden/smoke_seed_000017/layout.json.
                    string source = entry.GetString("source");
                    string rel = source.Contains("/smoke/")
                        ? "golden/smoke_" + source.Split('/')[source.Split('/').Length - 2] + "/layout.json"
                        : "golden/" + source.Split('/')[source.Split('/').Length - 2] + "/layout.json";
                    Fixtures.Require(rel);
                    text = Lf(Fixtures.ReadText(rel));
                }
                Assert.AreEqual(Hex(entry), SeedDeterminismContract.Fnv1a64(text), entry.GetString("label"));
                n++;
            }
            Assert.That(n, Is.GreaterThan(0));
        }

        [Test]
        public void ContractPipelineGoldensMatchGodot()
        {
            GdArray goldens = Fixtures.ReadDict(FixturePath).GetArray("pipeline_goldens");
            Assert.That(goldens.Count, Is.GreaterThan(0));
            foreach (GdDict golden in goldens)
            {
                string tag = golden.GetString("tag");
                GdDict inputs = golden.GetDict("inputs");
                var blueprint = new ShipBlueprint(inputs.GetInt("size"), inputs.GetInt("condition"), inputs.GetInt("seed"));
                GdDict archetype = inputs.GetDictOrEmpty("archetype_dict");
                string biome = inputs.GetString("biome_id");
                string difficulty = inputs.GetString("difficulty_id");

                string expectedText = Fixtures.ReadText("godot/" + golden.GetString("hashed_text_file"));
                string text = SeedDeterminismContract.GoldenText(blueprint, archetype.DeepCopy(), biome, difficulty);
                if (text != expectedText)
                {
                    int i = 0;
                    while (i < Math.Min(text.Length, expectedText.Length) && text[i] == expectedText[i]) i++;
                    int from = Math.Max(0, i - 120);
                    Assert.Fail($"{tag}: pipeline text differs at char {i}\nexpected: {expectedText.Substring(from, Math.Min(240, expectedText.Length - from))}\nactual:   {text.Substring(from, Math.Min(240, text.Length - from))}");
                }

                GdDict record = SeedDeterminismContract.RecordGolden(blueprint, archetype.DeepCopy(), biome, difficulty);
                long expectedHash = unchecked((long)ulong.Parse(golden.GetString("golden_hash_hex"), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                Assert.AreEqual(expectedHash, record.GetInt("golden_hash"), tag + " golden_hash");
                Assert.AreEqual(golden.GetDict("record_golden").GetInt("golden_text_length"), record.GetInt("golden_text_length"), tag + " length");

                GdDict match = SeedDeterminismContract.AssertLayoutMatch(blueprint, archetype.DeepCopy(), biome, difficulty);
                Assert.IsTrue(match.GetBool("match"), tag + " assert_layout_match");
                Assert.AreEqual(expectedHash, match.GetInt("hash_a"), tag + " hash_a");
                Assert.AreEqual(-1L, match.GetInt("diff_first_char"));
            }
        }
    }
}
