using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Kernel
{
    /// <summary>
    /// Bit-exact parity of the kernel against captures from Godot 4.7.1 (fixtures/godot/kernel, see fixtures/README.md).
    /// </summary>
    public class KernelParityTests
    {
        static double FromBits(object hex) =>
            BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(V.Str(hex), 16)));

        static string Bits(double d) => unchecked((ulong)BitConverter.DoubleToInt64Bits(d)).ToString("x16");

        static IEnumerable<GdDict> Seeds()
        {
            Fixtures.Require("godot/kernel/rng_fixture.json");
            foreach (object s in Fixtures.ReadDict("godot/kernel/rng_fixture.json").GetArrayOrEmpty("seeds")) yield return (GdDict)s;
        }

        [Test]
        public void Rng_SeedStateAndRandiSequences()
        {
            foreach (var s in Seeds())
            {
                long seed = V.I64(s["seed"]);
                var rng = GodotRandom.FromSeed(seed);
                Assert.AreEqual(V.I64(s["state_after_seed"]), rng.State, $"seed {seed}: state after seeding");
                var randi = s.GetArray("randi");
                for (int i = 0; i < randi.Count; i++) Assert.AreEqual(V.I64(randi[i]), rng.Randi(), $"seed {seed} randi[{i}]");
                Assert.AreEqual(V.I64(s["state_after_64_randi"]), rng.State, $"seed {seed}: state after 64 randi");
                Assert.AreEqual(V.I64(s["seed_readback"]), rng.Seed, $"seed {seed}: readback");
            }
        }

        [Test]
        public void Rng_RandiRange()
        {
            foreach (var s in Seeds())
            {
                long seed = V.I64(s["seed"]);
                var a = GodotRandom.FromSeed(seed);
                foreach (object v in s.GetArray("randi_range_1_100")) Assert.AreEqual(V.I64(v), a.RandiRange(1, 100), $"seed {seed} 1..100");
                var b = GodotRandom.FromSeed(seed);
                foreach (object v in s.GetArray("randi_range_m5_5")) Assert.AreEqual(V.I64(v), b.RandiRange(-5, 5), $"seed {seed} -5..5");
            }
        }

        [Test]
        public void Rng_FloatPathsAreBitExact()
        {
            foreach (var s in Seeds())
            {
                long seed = V.I64(s["seed"]);
                var f = GodotRandom.FromSeed(seed);
                var bits = s.GetArray("randf_bits");
                for (int i = 0; i < bits.Count; i++) Assert.AreEqual(V.Str(bits[i]), Bits(f.Randf()), $"seed {seed} randf[{i}]");

                var r = GodotRandom.FromSeed(seed);
                bits = s.GetArray("randf_range_m2_5_7_5_bits");
                for (int i = 0; i < bits.Count; i++) Assert.AreEqual(V.Str(bits[i]), Bits(r.RandfRange(-2.5, 7.5)), $"seed {seed} randf_range[{i}]");

                var n = GodotRandom.FromSeed(seed);
                bits = s.GetArray("randfn_0_1_bits");
                for (int i = 0; i < bits.Count; i++) Assert.AreEqual(V.Str(bits[i]), Bits(n.Randfn(0.0, 1.0)), $"seed {seed} randfn[{i}]");
            }
        }

        [Test]
        public void Rng_InterleavedCallsConsumeStateLikeGodot()
        {
            foreach (var s in Seeds())
            {
                long seed = V.I64(s["seed"]);
                var rng = GodotRandom.FromSeed(seed);
                var rows = s.GetArray("interleaved");
                var fbits = s.GetArray("interleaved_randf_bits");
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = (GdArray)rows[i];
                    Assert.AreEqual(V.I64(row[0]), rng.Randi(), $"seed {seed} row {i} randi");
                    Assert.AreEqual(V.Str(fbits[i]), Bits(rng.Randf()), $"seed {seed} row {i} randf");
                    Assert.AreEqual(V.I64(row[2]), rng.RandiRange(0, 9), $"seed {seed} row {i} randi_range");
                }
            }
        }

        [Test]
        public void Rng_Seed42RandiModulo()
        {
            var fixture = Fixtures.ReadDict("godot/kernel/rng_fixture.json");
            var rng = GodotRandom.FromSeed(42);
            foreach (object v in fixture.GetArray("seed_42_randi_mod_7")) Assert.AreEqual(V.I64(v), rng.Randi() % 7);
        }

        [Test]
        public void StringAndIntHashes()
        {
            Fixtures.Require("godot/kernel/string_hash_fixture.json");
            var fixture = Fixtures.ReadDict("godot/kernel/string_hash_fixture.json");
            foreach (object o in fixture.GetArray("strings"))
            {
                var e = (GdDict)o;
                string s = e.GetString("string");
                Assert.AreEqual(V.I64(e["s_hash"]), GodotHash.StringHash(s), $"String.hash('{s}')");
                Assert.AreEqual(V.I64(e["hash_s"]), GodotHash.Hash(s), $"hash('{s}')");
            }
            foreach (object o in fixture.GetArray("ints"))
            {
                var e = (GdDict)o;
                Assert.AreEqual(V.I64(e["hash"]), GodotHash.Hash(V.I64(e["value"])), $"hash({e["value"]})");
            }
            Assert.AreEqual(V.I64(fixture.GetDict("string_name_abc")["hash"]), GodotHash.StringHash("abc"));
        }

        [Test]
        public void FloatFormatting_JsonAndStr()
        {
            Fixtures.Require("godot/kernel/float_format_fixture.json");
            var fixture = Fixtures.ReadDict("godot/kernel/float_format_fixture.json");
            var values = new GdArray();
            foreach (object o in fixture.GetArray("each"))
            {
                var e = (GdDict)o;
                if (e.GetString("type") == "int")
                {
                    long l = long.Parse(e.GetString("json"), CultureInfo.InvariantCulture);
                    values.Add(l);
                    Assert.AreEqual(e.GetString("str"), V.Str(l));
                    continue;
                }
                double d = FromBits(e["float64_bits"]);
                values.Add(d);
                Assert.AreEqual(e.GetString("json"), GdFloatFormat.JsonNumber(d), $"JSON number for bits {e["float64_bits"]}");
                Assert.AreEqual(e.GetString("str"), V.Str(d), $"str() for bits {e["float64_bits"]}");
            }
            Assert.AreEqual(fixture.GetString("values_stringified_compact"), GdJson.Stringify(values));
            Assert.AreEqual(fixture.GetString("values_stringified_tab"), GdJson.Stringify(values, "\t"));
            Assert.AreEqual(Fixtures.ReadText("godot/kernel/float_format_raw.json"), GdJson.Stringify(values, "\t"));
        }

        /// <summary>
        /// <c>String.num(v, d)</c> and <c>JSON.stringify(v)</c> for 4,000 random doubles plus rounding edge cases, captured
        /// by float_format_msvcrt_probe.gd. Pins the Windows C runtime printf behaviour (17 significant digits, then
        /// half-up decimal rounding). Godot returns "" when the text overflows its buffer (DBL_MAX at 15+ decimals).
        /// </summary>
        [Test]
        public void FloatFormatting_WindowsPrintfRounding()
        {
            Fixtures.Require("godot/kernel/float_format_msvcrt.json");
            var cases = GdJson.Parse(Fixtures.ReadText("godot/kernel/float_format_msvcrt.json")) as GdArray;
            Assert.IsNotNull(cases);
            var misses = new List<string>();
            int checkedCount = 0;
            foreach (object o in cases)
            {
                var c = (GdDict)o;
                string expected = c.GetString("s");
                if (expected.Length == 0) continue;
                double v = FromBits(c["b"]);
                int d = (int)c.GetInt("d");
                checkedCount++;
                string num = GdFloatFormat.Num(v, d);
                if (num != expected) misses.Add($"num({c["b"]}, {d}) godot={expected} kernel={num}");
                if (c.Has("j") && GdFloatFormat.JsonNumber(v) != c.GetString("j"))
                    misses.Add($"json({c["b"]}) godot={c.GetString("j")} kernel={GdFloatFormat.JsonNumber(v)}");
                if (misses.Count >= 20) break;
            }
            Assert.Greater(checkedCount, 4000);
            Assert.IsEmpty(misses, string.Join("\n", misses));
        }

        [Test]
        public void JsonParse_EveryNumberIsFloat()
        {
            var fixture = Fixtures.ReadDict("godot/kernel/float_format_fixture.json");
            var parsed = (GdDict)GdJson.Parse(fixture.GetString("parse_source"));
            foreach (var kv in fixture.GetDict("parse_types"))
            {
                Assert.AreEqual("float", ((GdDict)kv.Value).GetString("type_name"));
                Assert.IsInstanceOf<double>(parsed[kv.Key], $"key {kv.Key}");
                Assert.AreEqual(((GdDict)kv.Value).GetString("var_to_str"), V.Str(parsed[kv.Key]));
            }
        }

        [Test]
        public void Stringify_SampleDocumentIsByteExact()
        {
            Fixtures.Require("godot/kernel/stringify_sample_raw.json");
            var doc = new GdDict
            {
                { "b", 1L },
                { "a", new GdDict { { "d", GdArray.Of(1L, 2L, new GdDict { { "z", 0L }, { "y", 1L } }) }, { "c", "x\ty\"z\\" } } },
                { "é", "ü" },
                { "ctrl", "" },
            };
            Assert.AreEqual(Fixtures.ReadText("godot/kernel/stringify_sample_raw.json"), GdJson.Stringify(doc, "\t"));
            Assert.AreEqual(Fixtures.ReadText("godot/kernel/stringify_sample_raw_2space.json"), GdJson.Stringify(doc, "  "));
        }
    }
}
