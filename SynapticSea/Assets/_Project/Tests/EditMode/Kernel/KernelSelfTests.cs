using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Kernel
{
    /// <summary>Known-answer and behaviour tests for the kernel that need no Godot fixtures.</summary>
    public class KernelSelfTests
    {
        [Test]
        public void Pcg32_MatchesReferenceImplementation()
        {
            // pcg32-demo: pcg32_srandom_r(&rng, 42u, 54u) produces these first outputs.
            var rng = new GodotRandom(42UL, 54UL);
            uint[] expected = { 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e };
            foreach (uint e in expected) Assert.AreEqual(e, rng.Next32());
        }

        [Test]
        public void Rng_SameSeedSameSequence_AndReseedRestarts()
        {
            var a = GodotRandom.FromSeed(777);
            var b = GodotRandom.FromSeed(777);
            for (int i = 0; i < 100; i++) Assert.AreEqual(a.Randi(), b.Randi());
            a.Seed = 777;
            var c = GodotRandom.FromSeed(777);
            Assert.AreEqual(c.Randi(), a.Randi());
        }

        [Test]
        public void Rng_RangesStayInBounds()
        {
            var rng = GodotRandom.FromSeed(17);
            for (int i = 0; i < 5000; i++)
            {
                long r = rng.RandiRange(-5, 5);
                Assert.That(r, Is.InRange(-5, 5));
                double f = rng.Randf();
                Assert.That(f, Is.InRange(0.0, 1.0));
                double fr = rng.RandfRange(-2.5, 7.5);
                Assert.That(fr, Is.InRange(-2.5, 7.5));
            }
            Assert.AreEqual(4, rng.RandiRange(4, 4));
            Assert.That(rng.RandiRange(10, 1), Is.InRange(1, 10));
        }

        [Test]
        public void StringHash_IsAdditiveDjb2OverCodePoints()
        {
            Assert.AreEqual(5381, GodotHash.StringHash(""));
            Assert.AreEqual(5381L * 33 + 'a', GodotHash.StringHash("a"));
            // A non-BMP code point hashes as one char32, not two UTF-16 units.
            uint h = 5381;
            h = unchecked(h * 33 + 0x1F680u);
            Assert.AreEqual((long)h, GodotHash.StringHash("\U0001F680"));
        }

        [Test]
        public void Json_AllNumbersParseAsDouble_KeysKeepInsertionOrder()
        {
            var d = (GdDict)GdJson.Parse("{\"z\": 17, \"a\": 17.5, \"m\": [1, -2, 3e2], \"z\": 18}");
            Assert.IsInstanceOf<double>(d["z"]);
            Assert.AreEqual(18.0, d["z"]);
            Assert.AreEqual(new object[] { "z", "a", "m" }, new List<object>(d.Keys).ToArray());
            var arr = (GdArray)d["m"];
            Assert.AreEqual(300.0, arr[2]);
        }

        [Test]
        public void Json_StringifyMatchesGodotLayoutRules()
        {
            var d = new GdDict
            {
                { "b", 1L },
                { "a", new GdDict { { "d", GdArray.Of(1L, 2.0, new GdDict()) }, { "c", "x\ty\"z\\" } } },
                { "e", new GdArray() },
            };
            string compact = GdJson.Stringify(d);
            Assert.AreEqual("{\"a\":{\"c\":\"x\\ty\\\"z\\\\\",\"d\":[1,2.0,{}]},\"b\":1,\"e\":[]}", compact);

            string tabbed = GdJson.Stringify(new GdDict { { "k", GdArray.Of(1L) } }, "\t");
            Assert.AreEqual("{\n\t\"k\": [\n\t\t1\n\t]\n}", tabbed);
        }

        [TestCase(0.0, "0.0")]
        [TestCase(-0.0, "0.0")]
        [TestCase(1.0, "1.0")]
        [TestCase(4.0, "4.0")]
        [TestCase(-2.5, "-2.5")]
        [TestCase(0.1, "0.1")]
        [TestCase(0.5, "0.5")]
        [TestCase(100.0, "100.0")]
        [TestCase(1.0 / 3.0, "0.333333333333333")]
        [TestCase(123456789.123, "123456789.123")]
        public void Json_FloatFormatting(double value, string expected)
        {
            Assert.AreEqual(expected, GdFloatFormat.JsonNumber(value));
        }

        [TestCase(1.0, "1.0")]
        [TestCase(0.25, "0.25")]
        [TestCase(12.5, "12.5")]
        [TestCase(-3.0, "-3.0")]
        public void Str_FloatUsesNumReal(double value, string expected)
        {
            Assert.AreEqual(expected, V.Str(value));
        }

        [Test]
        public void FormatFixed_FollowsWindowsPrintf()
        {
            Assert.AreEqual("0.125", GdFloatFormat.FormatFixed(0.125, 3));
            Assert.AreEqual("0.13", GdFloatFormat.FormatFixed(0.125, 2)); // decimal ties round half up
            Assert.AreEqual("0.38", GdFloatFormat.FormatFixed(0.375, 2));
            Assert.AreEqual("3", GdFloatFormat.FormatFixed(2.5, 0));
            Assert.AreEqual("-1.50", GdFloatFormat.FormatFixed(-1.5, 2));
            Assert.AreEqual("0.1000000000000000100000", GdFloatFormat.FormatFixed(0.1, 22)); // 17 significant digits, then zeros
            Assert.AreEqual("99999999999999992000000", GdFloatFormat.FormatFixed(1e23, 0));
            Assert.AreEqual("0.670987839698792", GdFloatFormat.FormatFixed(0.6709878396987915, 15)); // double rounding
            Assert.AreEqual("295694728730.48437", GdFloatFormat.FormatFixed(295694728730.484375, 5)); // 17th-digit tie rounds down
        }

        [Test]
        public void GodotStrtod_HandlesGodotNumberForms()
        {
            Assert.AreEqual(17.0, GodotStrtod.Parse("17"));
            Assert.AreEqual(-0.5, GodotStrtod.Parse("-0.5"));
            Assert.AreEqual(1000.0, GodotStrtod.Parse("1e3"));
            Assert.AreEqual(0.25, GodotStrtod.Parse("2.5E-1"));
            GodotStrtod.Parse("12abc", 0, out int end);
            Assert.AreEqual(2, end);
        }

        [Test]
        public void GdSort_SortsLikeAComparisonSort()
        {
            var rng = GodotRandom.FromSeed(99);
            for (int n = 0; n < 200; n += 7)
            {
                var items = new List<long>();
                for (int i = 0; i < n; i++) items.Add(rng.RandiRange(0, 20));
                var expected = new List<long>(items);
                expected.Sort();
                GdSort.SortCustom(items, (a, b) => a < b);
                CollectionAssert.AreEqual(expected, items);
            }
        }

        [Test]
        public void Variant_EqualityAndTruthiness()
        {
            Assert.IsTrue(V.VariantEquals(1L, 1.0));
            Assert.IsTrue(V.VariantEquals(GdArray.Of(1L, "a"), GdArray.Of(1.0, "a")));
            Assert.IsFalse(V.Truthy(new GdDict()));
            Assert.IsTrue(V.Truthy("x"));
            var keys = new GdDict { { 1L, "int" }, { 1.0, "float" } };
            Assert.AreEqual(2, keys.Count, "int and float keys are distinct in Godot dictionaries");
        }

        [Test]
        public void GdDict_EraseKeepsOrder_DeepCopyIsIndependent()
        {
            var d = new GdDict { { "a", 1L }, { "b", new GdArray { 1L } }, { "c", 3L } };
            var copy = d.DeepCopy();
            ((GdArray)copy["b"]).Add(2L);
            Assert.AreEqual(1, ((GdArray)d["b"]).Count);
            d.Erase("a");
            d["a"] = 9L;
            Assert.AreEqual(new object[] { "b", "c", "a" }, new List<object>(d.Keys).ToArray());
        }
    }
}
