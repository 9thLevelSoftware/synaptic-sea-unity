using System;
using System.Reflection;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public class AdmissionHashMemoTests
    {
        static readonly Type Memo = typeof(PaidCraftingState).Assembly.GetType("SynapticSea.Core.Systems.AdmissionHashMemo");
        static IDisposable Scope() => (IDisposable)Memo.GetMethod("Begin", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        static void MatchOriginal(object value)
        {
            string expected = PaidCraftingState.Hash(value);
            using (Scope()) { Assert.AreEqual(expected, PaidCraftingState.Hash(value)); Assert.AreEqual(expected, PaidCraftingState.Hash(value)); }
        }
        [Test]
        public void DetachedCompleteInputRejectsNestedMutationAndRestoresOriginalDigest()
        {
            const string key = "scalar-key";
            var array = GdArray.Of(2L, new GdDict { { "value", "old" } });
            var value = new GdDict { { "array", array }, { key, "key-value" } };
            string original = PaidCraftingState.Hash(value);
            using (Scope())
            {
                Assert.AreEqual(original, PaidCraftingState.Hash(value));
                ((GdDict)array[1])["value"] = "new"; string changed = PaidCraftingState.Hash(value); Assert.AreNotEqual(original, changed);
                ((GdDict)array[1])["value"] = "old"; Assert.AreEqual(original, PaidCraftingState.Hash(value));
                value[key] = "changed-key-value"; Assert.AreNotEqual(original, PaidCraftingState.Hash(value));
                value[key] = "key-value"; Assert.AreEqual(original, PaidCraftingState.Hash(value));
                array[0] = 2.0; Assert.AreNotEqual(original, PaidCraftingState.Hash(value));
            }
        }
        [Test]
        public void ScalarBitsTypesArrayOrderAndStableSortKeyTiesMatchCanonicalHash()
        {
            object[] values = { 0.0, -0.0, 0.0f, -0.0f, 1L, 1, true, "1", null,
                new Vec3(-0.0, 0.0, 1), new Vec3(0.0, -0.0, 1), GdArray.Of(1L, 2L), GdArray.Of(2L, 1L),
                new GdDict { { 1L, "integer" }, { "1", "text" } }, new GdDict { { "1", "text" }, { 1L, "integer" } } };
            foreach (object value in values) MatchOriginal(value);
            string[] expected = values.Select(PaidCraftingState.Hash).ToArray();
            using (Scope()) for (int i = 0; i < values.Length; i++) { Assert.AreEqual(expected[i], PaidCraftingState.Hash(values[i])); Assert.AreEqual(expected[i], PaidCraftingState.Hash(values[i])); }
        }
        [Test]
        public void UnsupportedAndNonfiniteInputsStillRefuseAfterValidMemoEntries()
        {
            object[] bad = { double.NaN, double.PositiveInfinity, float.NaN, new Vec3(double.NaN, 0, 0), new object(), new GdDict { { new GdDict { { "nested", 1L } }, "invalid-key" } } };
            foreach (object value in bad)
            {
                Assert.Throws<ArgumentException>(() => PaidCraftingState.Hash(value));
                using (Scope()) { PaidCraftingState.Hash(new GdDict { { "valid", 1L } }); Assert.Throws<ArgumentException>(() => PaidCraftingState.Hash(value)); }
            }
        }
        [Test]
        public void NestedAndNewAdmissionScopesNeverShareMutableInputAuthority()
        {
            var value = new GdDict { { "value", 1L } }; string original = PaidCraftingState.Hash(value);
            using (Scope())
            {
                Assert.AreEqual(original, PaidCraftingState.Hash(value));
                value["value"] = 2L;
                using (Scope()) Assert.AreNotEqual(original, PaidCraftingState.Hash(value));
                Assert.AreNotEqual(original, PaidCraftingState.Hash(value));
                value["value"] = 1L; Assert.AreEqual(original, PaidCraftingState.Hash(value));
            }
            value["value"] = 3L; string changed = PaidCraftingState.Hash(value);
            using (Scope()) Assert.AreEqual(changed, PaidCraftingState.Hash(value));
        }
    }
}


namespace SynapticSea.Tests.Session
{
    public class BitExactCodecTests
    {
        static readonly ulong[] FiniteBits = { 0UL, 0x8000000000000000UL, 1UL, 0x8000000000000001UL,
            0x000fffffffffffffUL, 0x0010000000000000UL, 0x7fefffffffffffffUL, 0xffefffffffffffffUL,
            0x3fa81f8b6a300d00UL, 0x3fb0bf99b9505fedUL, 0x3ff0000000000001UL };
        static SynapticSea.Core.Variant.GdDict Corpus()
        {
            var values = new GdArray(FiniteBits.Select(bits => (object)BitConverter.Int64BitsToDouble(unchecked((long)bits))));
            return new GdDict { { "values", values }, { "vector", new Vec3(-0.0, float.Epsilon, float.MaxValue) },
                { "typed_keys", new GdDict { { 1L, "integer" }, { "1", "text" }, { 1.0, "real" } } },
                { "unicode", "sea \u263a \ud83c\udf0a" }, { "escapes", "quote\" slash\\ newline\n tab\t control\u0001" },
                { "all_controls", new string(Enumerable.Range(0, 32).Select(n => (char)n).ToArray()) } };
        }
        static GdDict Wire(GdDict value) => SynapticSea.Core.Session.ComponentDomainCodec.Encode(value, SynapticSea.Core.Session.ComponentDomainCodec.BitExactSchema);
        [Test]
        public void FiniteBinary64AndFloatVectorRoundTripExactBits()
        {
            var input = Corpus(); Assert.IsTrue(SynapticSea.Core.Session.ComponentDomainCodec.TryDecode(Wire(input), out GdDict output, out _));
            var values = output.GetArrayOrEmpty("values");
            for (int i = 0; i < FiniteBits.Length; i++) Assert.AreEqual(FiniteBits[i], unchecked((ulong)BitConverter.DoubleToInt64Bits((double)values[i])));
            Assert.AreEqual(GdJson.Stringify(Wire(input)), GdJson.Stringify(Wire(output)));
        }
        [TestCase("7ff0000000000000")][TestCase("7ff8000000000000")][TestCase("fff0000000000000")]
        [TestCase("000000000000000A")][TestCase("000000000000000")][TestCase("+000000000000000")][TestCase("0.0")]
        public void NonfiniteOrAlternateRealWireRefuses(string token)
        {
            var encoded = new GdDict { { "schema", SynapticSea.Core.Session.ComponentDomainCodec.BitExactSchema },
                { "value", GdArray.Of("dictionary", GdArray.Of(GdArray.Of(GdArray.Of("text", "x"), GdArray.Of("real", token)))) } };
            Assert.IsFalse(SynapticSea.Core.Session.ComponentDomainCodec.TryDecode(encoded, out _, out _));
        }
        [Test]
        public void ExplicitVersionHashAndMemoKeepZeroSignsTypesAndKeyOrder()
        {
            string algorithm = PaidCraftingState.BitExactHashAlgorithm;
            Assert.AreNotEqual(PaidCraftingState.Hash(0.0, algorithm), PaidCraftingState.Hash(BitConverter.Int64BitsToDouble(long.MinValue), algorithm));
            Assert.AreNotEqual(PaidCraftingState.Hash(1L, algorithm), PaidCraftingState.Hash(1.0, algorithm));
            var a = new GdDict { { 1.0, "r" }, { "1", "t" }, { 1L, "i" } };
            var b = new GdDict { { 1L, "i" }, { 1.0, "r" }, { "1", "t" } };
            Assert.AreEqual(PaidCraftingState.Hash(a, algorithm), PaidCraftingState.Hash(b, algorithm));
            var memo = typeof(PaidCraftingState).Assembly.GetType("SynapticSea.Core.Systems.AdmissionHashMemo");
            using ((IDisposable)memo.GetMethod("Begin", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null))
            {
                string old = PaidCraftingState.Hash(a); string next = PaidCraftingState.Hash(a, algorithm);
                Assert.AreNotEqual(old, next); Assert.AreEqual(old, PaidCraftingState.Hash(a)); Assert.AreEqual(next, PaidCraftingState.Hash(a, algorithm));
            }
            Assert.Throws<ArgumentException>(() => PaidCraftingState.Hash(a, "unknown"));
            Assert.Throws<System.Text.EncoderFallbackException>(() => Wire(new GdDict { { "bad", "\ud800" } }));
            Assert.Throws<ArgumentException>(() => PaidCraftingState.Hash(new GdDict { { 1, "small" }, { 1L, "large" } }, algorithm));
        }
        [Test]
        public void V2RefusesExtraVectorPrecisionInvalidUnicodeAndStructuralBounds()
        {
            var badVector = new GdDict { { "schema", SynapticSea.Core.Session.ComponentDomainCodec.BitExactSchema },
                { "value", GdArray.Of("dictionary", GdArray.Of(GdArray.Of(GdArray.Of("text", "x"),
                    GdArray.Of("vector3", "3ff0000000000001", "0000000000000000", "0000000000000000")))) } };
            Assert.IsFalse(SynapticSea.Core.Session.ComponentDomainCodec.TryDecode(badVector, out _, out _));
            var badText = new GdDict { { "schema", SynapticSea.Core.Session.ComponentDomainCodec.BitExactSchema },
                { "value", GdArray.Of("dictionary", GdArray.Of(GdArray.Of(GdArray.Of("text", "x"), GdArray.Of("text", "\ud800")))) } };
            Assert.IsFalse(SynapticSea.Core.Session.ComponentDomainCodec.TryDecode(badText, out _, out _));
            var repeated = Wire(new GdDict { { "x", 1L } });
            var pairs = (GdArray)((GdArray)repeated.Get("value"))[1]; pairs.Add(V.DeepCopy(pairs[0]));
            Assert.IsFalse(SynapticSea.Core.Session.ComponentDomainCodec.TryDecode(repeated, out _, out _));
            object nested = 1L; for (int i = 0; i < 130; i++) nested = GdArray.Of(nested);
            Assert.Throws<ArgumentException>(() => Wire(new GdDict { { "deep", nested } }));
            Assert.Throws<ArgumentException>(() => Wire(new GdDict { { "wide", new GdArray(Enumerable.Repeat((object)1L, 100001)) } }));
        }
        [Test, Explicit("Requires owned cross-runtime exchange directory.")]
        public void ExchangeNewTypedPayloadAndCanonicalHashAcrossActualRuntimes()
        {
            string directory = System.Environment.GetEnvironmentVariable("SYNAPTIC_BITS_EXCHANGE_DIR");
            Assert.IsTrue(System.IO.Directory.Exists(directory));
            string text = PaidSnapshotCodec.Stringify(Wire(Corpus()), PaidSnapshotCodec.Policy.TypedOwner); string digest = PaidCraftingState.Hash(Corpus(), PaidCraftingState.BitExactHashAlgorithm);
            var raw = new GdDict { { "binding", new GdDict { { "hash_algorithm", PaidCraftingState.BitExactHashAlgorithm } } },
                { "values", Corpus().Get("values") } };
            string rawText = PaidSnapshotCodec.Stringify(raw);
            Assert.IsTrue(PaidSnapshotCodec.Same(raw, PaidSnapshotCodec.Parse(rawText), SynapticSea.Core.Session.PaidHashContext.BitsV2));
            string legacyText = GdJson.Stringify(SynapticSea.Core.Session.ComponentDomainCodec.Encode(Corpus()));
            string legacyHash = PaidCraftingState.Hash(Corpus());
            string peer = System.Environment.GetEnvironmentVariable("SYNAPTIC_BITS_PEER_FILE");
            if (!string.IsNullOrEmpty(peer))
            {
                var artifact = GdJson.ParseString(System.IO.File.ReadAllText(peer)) as GdDict;
                Assert.IsTrue(PaidSnapshotCodec.Same(raw, PaidSnapshotCodec.Parse(artifact.GetString("raw_text")), SynapticSea.Core.Session.PaidHashContext.BitsV2), "Foreign raw paid metadata retains finite bits including zero signs.");
                Assert.AreEqual(text, artifact.GetString("wire_text")); Assert.AreEqual(digest, artifact.GetString("canonical_hash"));
                Assert.IsFalse(SynapticSea.Core.Session.ComponentDomainCodec.TryDecode(GdJson.ParseString(artifact.GetString("legacy_wire_text")) as GdDict, out _, out _), "Foreign v1 spelling still refuses; this package does not migrate legacy histories.");
                Assert.AreNotEqual(legacyHash, artifact.GetString("legacy_hash"));
                Assert.IsTrue(SynapticSea.Core.Session.ComponentDomainCodec.TryDecode(GdJson.ParseString(artifact.GetString("wire_text")) as GdDict, out GdDict decoded, out _));
                Assert.AreEqual(digest, PaidCraftingState.Hash(decoded, PaidCraftingState.BitExactHashAlgorithm));
            }
            string framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            string file = framework.StartsWith("Mono", StringComparison.Ordinal) ? "mono.json" : "dotnet.json";
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, file), PaidSnapshotCodec.Stringify(new GdDict {
                { "runtime", framework }, { "algorithm", PaidCraftingState.BitExactHashAlgorithm }, { "wire_text", text }, { "canonical_hash", digest }, { "legacy_wire_text", legacyText }, { "legacy_hash", legacyHash }, { "raw_text", rawText } }), new System.Text.UTF8Encoding(false, true));
            TestContext.WriteLine("BITS_EXCHANGE=" + framework + " sha256=" + digest + " peer=" + peer);
        }
    }
}
