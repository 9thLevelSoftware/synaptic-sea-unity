using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>
    /// The internal Godot String helpers used by the system ports (<see cref="SurvivalCompat"/>, <see cref="ItemsCompat"/>,
    /// <see cref="ShipCompat"/>). Every expected value was printed by Godot 4.7.1 (<c>--headless --script</c>, the same
    /// binary as the parity fixtures), not derived from the C# code. Where the three classes duplicate a helper
    /// (strip_edges, "%.Nf", find) each copy is checked against the same Godot table.
    ///
    /// Not covered, as thin ordinal pass-throughs with nothing Godot-specific: <c>FormatD</c>/<c>D</c>
    /// (<c>long.ToString</c>), <c>BeginsWith</c>/<c>EndsWith</c>, and <c>ToGdArray</c>.
    /// </summary>
    public class GodotStringCompatTests
    {
        // ------------------------------------------------------------------ String.capitalize()

        static readonly (string input, string godot)[] CapitalizeCases =
        {
            ("ration pack", "Ration Pack"),
            ("scrap_metal", "Scrap Metal"),
            ("bytes2var", "Bytes 2 Var"),
            ("HTML5", "Html 5"),
            ("Node2DPosition", "Node 2d Position"),
            ("PascalCase", "Pascal Case"),
            ("snake_casE", "Snake Cas E"),
            ("  padded  words ", "Padded Words"),
            ("", ""),
            ("a", "A"),
            ("medkit mk2", "Medkit Mk 2"),
            ("o2 canister", "O 2 Canister"),
            ("2D", "2d"),
            ("sha256", "Sha 256"),
            ("ALLCAPS", "Allcaps"),
            ("x_y__z", "X Y Z"),
        };

        [Test]
        public void Capitalize_MatchesGodot()
        {
            foreach (var (input, godot) in CapitalizeCases)
                Assert.AreEqual(godot, ItemsCompat.Capitalize(input), $"\"{input}\".capitalize()");
        }

        // ------------------------------------------------------------------ strip_edges / is_valid_int / find

        static readonly (string input, string godot)[] StripCases =
        {
            (" \t a b \n", "a b"),
            ("\u00a0x\u00a0", "\u00a0x\u00a0"), // NBSP is > 32: Godot keeps it (string.Trim() would not)
            ("", ""),
            ("   ", ""),
            ("\u001fq\u0020", "q"),
        };

        [Test]
        public void StripEdges_TrimsOnlyCodesUpTo32_InEveryCopy()
        {
            foreach (var (input, godot) in StripCases)
            {
                Assert.AreEqual(godot, SurvivalCompat.StripEdges(input), $"SurvivalCompat \"{input}\"");
                Assert.AreEqual(godot, ItemsCompat.StripEdges(input), $"ItemsCompat \"{input}\"");
                Assert.AreEqual(godot, ShipCompat.StripEdges(input), $"ShipCompat \"{input}\"");
            }
        }

        [Test]
        public void IsValidInt_MatchesGodot()
        {
            var cases = new Dictionary<string, bool>
            {
                { "", false }, { "0", true }, { "-", false }, { "+", false }, { "-5", true }, { "+7", true },
                { "12a", false }, { " 1", false }, { "007", true }, { "1.0", false }, { "--1", false },
                { "99999999999999999999", true }, // digits only: Godot does not range-check
            };
            foreach (var kv in cases) Assert.AreEqual(kv.Value, SurvivalCompat.IsValidInt(kv.Key), $"\"{kv.Key}\".is_valid_int()");
        }

        [Test]
        public void FindAndContains_NeverMatchAnEmptyNeedle()
        {
            Assert.AreEqual(-1, SurvivalCompat.Find("abc", ""));
            Assert.AreEqual(-1, SurvivalCompat.Find("", "a"));
            Assert.AreEqual(-1, SurvivalCompat.Find("", ""));
            Assert.AreEqual(2, SurvivalCompat.Find("abcabc", "c"));
            Assert.IsFalse(SurvivalCompat.Contains("abc", ""), "\"abc\".contains(\"\") is false in Godot");
            Assert.IsTrue(SurvivalCompat.Contains("abcabc", "ca"));
            Assert.IsFalse(ShipCompat.GdContains("abc", ""));
            Assert.IsFalse(ShipCompat.GdContains("", "a"));
            Assert.IsTrue(ShipCompat.GdContains("reactor_core", "core"));
        }

        [Test]
        public void TrimPrefixAndSuffix_MatchGodot()
        {
            Assert.AreEqual("12", SurvivalCompat.TrimPrefix("w12", "w"));
            Assert.AreEqual("w12", SurvivalCompat.TrimPrefix("w12", ""));
            Assert.AreEqual("", SurvivalCompat.TrimPrefix("w", "w"));
            Assert.AreEqual("a", ShipCompat.TrimSuffix("a.json", ".json"));
            Assert.AreEqual("", ShipCompat.TrimSuffix(".json", ".json"));
            Assert.AreEqual("a.JSON", ShipCompat.TrimSuffix("a.JSON", ".json"), "case-sensitive");
        }

        // ------------------------------------------------------------------ "%.Nf" / "%+.Nf"

        static readonly double PositiveNaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000L); // Godot's NAN constant

        static readonly (double value, string f2, string f0, string plus1)[] FormatCases =
        {
            (1.005, "1.00", "1", "+1.0"), // binary 1.00499999...: no decimal round-up
            (0.0, "0.00", "0", "+0.0"),
            (-0.0, "-0.00", "-0", "-0.0"),
            (2.5, "2.50", "3", "+2.5"),
            (0.125, "0.13", "0", "+0.1"), // exact ties round away from zero
            (0.375, "0.38", "0", "+0.4"),
            (-1.999, "-2.00", "-2", "-2.0"),
            (1e20, "100000000000000000000.00", "100000000000000000000", "+100000000000000000000.0"),
            (123456.789, "123456.79", "123457", "+123456.8"),
            (-0.004, "-0.00", "-0", "-0.0"), // the sign survives rounding to zero
            (double.PositiveInfinity, "inf", "inf", "+inf"),
            (double.NegativeInfinity, "-inf", "-inf", "-inf"),
        };

        [Test]
        public void FormatFixed_MatchesGodotSprintf_InEveryCopy()
        {
            foreach (var (value, f2, f0, plus1) in FormatCases)
            {
                Assert.AreEqual(f2, SurvivalCompat.FormatF(value, 2), $"SurvivalCompat %.2f {value:R}");
                Assert.AreEqual(f0, SurvivalCompat.FormatF(value, 0), $"SurvivalCompat %.0f {value:R}");
                Assert.AreEqual(f2, ShipCompat.FormatF(value, 2), $"ShipCompat %.2f {value:R}");
                Assert.AreEqual(f2, ItemsCompat.Fmt(value, 2), $"ItemsCompat %.2f {value:R}");
                Assert.AreEqual(f0, ItemsCompat.Fmt(value, 0), $"ItemsCompat %.0f {value:R}");
                Assert.AreEqual(plus1, ItemsCompat.Fmt(value, 1, showSign: true), $"ItemsCompat %+.1f {value:R}");
            }
            Assert.AreEqual("0.3", ItemsCompat.Fmt(0.25, 1), "exact tie 0.25 rounds away");
            Assert.AreEqual("2.67", ItemsCompat.Fmt(2.675, 2), "2.675 is below the tie in binary");

            Assert.AreEqual("nan", SurvivalCompat.FormatF(PositiveNaN, 2));
            Assert.AreEqual("nan", ShipCompat.FormatF(PositiveNaN, 0));
            Assert.AreEqual("nan", ItemsCompat.Fmt(PositiveNaN, 2));
            Assert.AreEqual("+nan", ItemsCompat.Fmt(PositiveNaN, 1, showSign: true));
        }

        [Test]
        public void AbsI_WrapsOnMinValueLikeGodot()
        {
            Assert.AreEqual(long.MinValue, ItemsCompat.AbsI(long.MinValue), "absi(-9223372036854775808) wraps");
            Assert.AreEqual(5L, ItemsCompat.AbsI(-5));
            Assert.AreEqual(long.MaxValue, ItemsCompat.AbsI(long.MaxValue));
        }

        // ------------------------------------------------------------------ ordering

        [Test]
        public void StringOrdering_IsCodePointOrder()
        {
            Assert.IsTrue(SurvivalCompat.Less("B", "a"));
            Assert.IsFalse(SurvivalCompat.Less("é", "z"));
            var list = new List<string> { "b", "B", "a", "é", "Z", "aa", "", "_x", "a0" };
            ShipCompat.SortStrings(list);
            CollectionAssert.AreEqual(new[] { "", "B", "Z", "_x", "a", "a0", "aa", "b", "é" }, list, "PackedStringArray.sort()");
        }

        // ------------------------------------------------------------------ String.simplify_path()

        static readonly (string input, string godot)[] SimplifyCases =
        {
            ("res://a/./b/../c", "res://a/c"),
            ("res://a//b/", "res://a/b"),
            ("/../a", "/a"),
            ("../a/b", "../a/b"),
            ("a/b/../../..", ".."),
            ("a/b/../../../c", "../c"),
            ("C:\\x\\..\\y", "C:/y"),
            ("C:/x/../y", "C:/y"),
            ("//server/share/../x", "//server/x"),
            ("\\\\srv\\x", "\\\\srv/x"),
            ("res://", "res://"),
            ("user://x/../../y", "user://y"),
            ("./a/.", "a"),
            ("a\\b\\\\c", "a/b/c"),
            ("1x://a/../b", "1x://b"),
            ("a:b/c/../d", "a:b/d"),
            ("a_b://c/../d", "a_b:/d"), // '_' is not a protocol character
            // Leading and repeated "..": kept without a drive and under res://, dropped under any other drive.
            ("../../a", "../../a"),
            ("x/../../../y", "../../y"),
            ("../a/../b", "../b"),
            ("/../../a", "/a"),
            ("/..", "/"),
            ("//..", "//"),
            ("//srv/../x", "//x"),
            ("\\\\../x", "\\\\x"),
            ("C:/a/../../../b", "C:/b"),
            ("C:\\..\\x", "x"), // "C:\" is not a drive once no '/' follows; "C:" folds with ".."
            ("a:/../x", "a:/x"),
            ("user://../a/../..", "user://"),
            ("uid://../a", "uid://a"),
            ("Res://../a", "Res://a"),
            ("res://../../a", "res://../../a"),
            ("res://a/../../../b", "res://../../b"),
            ("res://../a/..", "res://.."),
            ("res:///../a", "res://../a"),
            ("..a/../b", "b"),
        };

        [Test]
        public void SimplifyPath_MatchesGodot()
        {
            foreach (var (input, godot) in SimplifyCases)
                Assert.AreEqual(godot, ShipCompat.SimplifyPath(input), $"\"{input}\".simplify_path()");
        }

        // ------------------------------------------------------------------ quiet JSON reads

        [Test]
        public void ReadJson_MissingFileIsNullWithoutLogging_AndReturnsCopies()
        {
            var log = new CollectingLog();
            ILog previousLog = CoreServices.Log;
            IResourceReader previousReader = CoreServices.Resources;
            CoreServices.Log = log;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
            try
            {
                Assert.IsNull(ItemsCompat.ReadJson("res://data/does_not_exist.json"));
                Assert.IsNull(ItemsCompat.ReadJson(""));
                GdDict empty = ItemsCompat.ReadJsonDict("res://data/does_not_exist.json");
                Assert.IsNotNull(empty);
                Assert.IsTrue(empty.IsEmpty);
                CollectionAssert.IsEmpty(log.Warnings, "Godot's per-model loaders returned silently");
                CollectionAssert.IsEmpty(log.Errors);

                GdDict first = ItemsCompat.ReadJsonDict("res://data/ui/tooltip_catalog.json");
                Assert.IsFalse(first.IsEmpty);
                first.Clear();
                Assert.IsFalse(ItemsCompat.ReadJsonDict("res://data/ui/tooltip_catalog.json").IsEmpty, "callers get a copy, not the cache");
            }
            finally
            {
                CatalogRegistry.Clear();
                CoreServices.Log = previousLog;
                CoreServices.Resources = previousReader;
            }
        }
    }
}
