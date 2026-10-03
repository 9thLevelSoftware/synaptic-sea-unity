using System;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    // Artifact-only draft. Codec-level graphs; no gameplay/DomainBundle admission claim.
    public class PaidCraftSnapshotCodecTests
    {
        static string Character(int code) => code < 0 ? "" : ((char)code).ToString();

        // Independent conventional JSON oracle, with no product writer dependency.
        static string StandardQuote(string text)
        {
            var result = new StringBuilder("\"");
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\t': result.Append("\\t"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\f': result.Append("\\f"); break;
                    case '\r': result.Append("\\r"); break;
                    default:
                        if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else result.Append(c);
                        break;
                }
            }
            return result.Append('"').ToString();
        }

        static GdDict Semantic(bool key, int code)
        {
            string text = Character(code);
            return new GdDict { { key ? text : "plain_key", key ? "plain_value" : text } };
        }

        static string IndependentRaw(bool key, int code)
        {
            string text = Character(code);
            return "{" + StandardQuote(key ? text : "plain_key") + ":" + StandardQuote(key ? "plain_value" : text) + "}";
        }

        static string IndependentTyped(bool key, int code)
        {
            string text = Character(code);
            return "{\"schema\":\"component_domain_codec_v1\",\"value\":[\"dictionary\",[[[\"text\"," +
                StandardQuote(key ? text : "plain_key") + "],[\"text\"," +
                StandardQuote(key ? "plain_value" : text) + "]]]]}";
        }

        static void ExactSemantic(GdDict value, bool key, int code)
        {
            Assert.IsNotNull(value); Assert.AreEqual(1, value.Count);
            string name = key ? Character(code) : "plain_key";
            Assert.IsTrue(value.Has(name), "Exact original key, including empty/control characters");
            Assert.IsTrue(value.Get(name) is string);
            Assert.AreEqual(key ? "plain_value" : Character(code), value.Get(name));
        }

        static GdDict Decode(GdDict value)
        {
            Assert.IsTrue(ComponentDomainCodec.TryDecode(value, out GdDict semantic, out string reason), reason);
            return semantic;
        }

        static void BaselineWriter()
        {
            Assert.AreEqual("{\"baseline\":\"value\"}", PaidSnapshotCodec.Stringify(new GdDict { { "baseline", "value" } }));
        }

        static void BaselineReader()
        {
            GdDict result = PaidSnapshotCodec.Parse("{\"baseline\":\"value\"}");
            Assert.IsNotNull(result); Assert.AreEqual("value", result.GetString("baseline"));
        }

        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        [TestCase(true, 3)]
        [TestCase(true, 4)]
        [TestCase(true, 5)]
        [TestCase(true, 6)]
        [TestCase(true, 7)]
        [TestCase(true, 8)]
        [TestCase(true, 9)]
        [TestCase(true, 10)]
        [TestCase(true, 11)]
        [TestCase(true, 12)]
        [TestCase(true, 13)]
        [TestCase(true, 14)]
        [TestCase(true, 15)]
        [TestCase(true, 16)]
        [TestCase(true, 17)]
        [TestCase(true, 18)]
        [TestCase(true, 19)]
        [TestCase(true, 20)]
        [TestCase(true, 21)]
        [TestCase(true, 22)]
        [TestCase(true, 23)]
        [TestCase(true, 24)]
        [TestCase(true, 25)]
        [TestCase(true, 26)]
        [TestCase(true, 27)]
        [TestCase(true, 28)]
        [TestCase(true, 29)]
        [TestCase(true, 30)]
        [TestCase(true, 31)]
        [TestCase(true, 127)]
        [TestCase(true, -1)]
        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(false, 2)]
        [TestCase(false, 3)]
        [TestCase(false, 4)]
        [TestCase(false, 5)]
        [TestCase(false, 6)]
        [TestCase(false, 7)]
        [TestCase(false, 8)]
        [TestCase(false, 9)]
        [TestCase(false, 10)]
        [TestCase(false, 11)]
        [TestCase(false, 12)]
        [TestCase(false, 13)]
        [TestCase(false, 14)]
        [TestCase(false, 15)]
        [TestCase(false, 16)]
        [TestCase(false, 17)]
        [TestCase(false, 18)]
        [TestCase(false, 19)]
        [TestCase(false, 20)]
        [TestCase(false, 21)]
        [TestCase(false, 22)]
        [TestCase(false, 23)]
        [TestCase(false, 24)]
        [TestCase(false, 25)]
        [TestCase(false, 26)]
        [TestCase(false, 27)]
        [TestCase(false, 28)]
        [TestCase(false, 29)]
        [TestCase(false, 30)]
        [TestCase(false, 31)]
        [TestCase(false, 127)]
        [TestCase(false, -1)]
        public void RawWriter_EmitsStandardJsonWithoutChangingText(bool key, int code)
        {
            BaselineWriter();
            GdDict value = Semantic(key, code);
            string expected = IndependentRaw(key, code);
            // Independent expected text must decode before testing the writer; this does not invoke paid key policy.
            ExactSemantic(GdJson.Parse(expected, true) as GdDict, key, code);
            string actual = PaidSnapshotCodec.Stringify(value);
            ExactSemantic(value, key, code);
            Assert.AreEqual(expected, actual, "Writer bytes; separate reader methods classify paid key-policy refusal");
        }

        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        [TestCase(true, 3)]
        [TestCase(true, 4)]
        [TestCase(true, 5)]
        [TestCase(true, 6)]
        [TestCase(true, 7)]
        [TestCase(true, 8)]
        [TestCase(true, 9)]
        [TestCase(true, 10)]
        [TestCase(true, 11)]
        [TestCase(true, 12)]
        [TestCase(true, 13)]
        [TestCase(true, 14)]
        [TestCase(true, 15)]
        [TestCase(true, 16)]
        [TestCase(true, 17)]
        [TestCase(true, 18)]
        [TestCase(true, 19)]
        [TestCase(true, 20)]
        [TestCase(true, 21)]
        [TestCase(true, 22)]
        [TestCase(true, 23)]
        [TestCase(true, 24)]
        [TestCase(true, 25)]
        [TestCase(true, 26)]
        [TestCase(true, 27)]
        [TestCase(true, 28)]
        [TestCase(true, 29)]
        [TestCase(true, 30)]
        [TestCase(true, 31)]
        [TestCase(true, 127)]
        [TestCase(true, -1)]
        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(false, 2)]
        [TestCase(false, 3)]
        [TestCase(false, 4)]
        [TestCase(false, 5)]
        [TestCase(false, 6)]
        [TestCase(false, 7)]
        [TestCase(false, 8)]
        [TestCase(false, 9)]
        [TestCase(false, 10)]
        [TestCase(false, 11)]
        [TestCase(false, 12)]
        [TestCase(false, 13)]
        [TestCase(false, 14)]
        [TestCase(false, 15)]
        [TestCase(false, 16)]
        [TestCase(false, 17)]
        [TestCase(false, 18)]
        [TestCase(false, 19)]
        [TestCase(false, 20)]
        [TestCase(false, 21)]
        [TestCase(false, 22)]
        [TestCase(false, 23)]
        [TestCase(false, 24)]
        [TestCase(false, 25)]
        [TestCase(false, 26)]
        [TestCase(false, 27)]
        [TestCase(false, 28)]
        [TestCase(false, 29)]
        [TestCase(false, 30)]
        [TestCase(false, 31)]
        [TestCase(false, 127)]
        [TestCase(false, -1)]
        public void RawReader_AcceptsIndependentValidJsonWithExactKeyAndValue(bool key, int code)
        {
            BaselineReader();
            string text = IndependentRaw(key, code);
            ExactSemantic(GdJson.Parse(text, true) as GdDict, key, code);
            GdDict actual = PaidSnapshotCodec.Parse(text);
            Assert.IsNotNull(actual, "Independent valid standard JSON must pass paid transport key policy");
            ExactSemantic(actual, key, code);
        }

        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        [TestCase(true, 3)]
        [TestCase(true, 4)]
        [TestCase(true, 5)]
        [TestCase(true, 6)]
        [TestCase(true, 7)]
        [TestCase(true, 8)]
        [TestCase(true, 9)]
        [TestCase(true, 10)]
        [TestCase(true, 11)]
        [TestCase(true, 12)]
        [TestCase(true, 13)]
        [TestCase(true, 14)]
        [TestCase(true, 15)]
        [TestCase(true, 16)]
        [TestCase(true, 17)]
        [TestCase(true, 18)]
        [TestCase(true, 19)]
        [TestCase(true, 20)]
        [TestCase(true, 21)]
        [TestCase(true, 22)]
        [TestCase(true, 23)]
        [TestCase(true, 24)]
        [TestCase(true, 25)]
        [TestCase(true, 26)]
        [TestCase(true, 27)]
        [TestCase(true, 28)]
        [TestCase(true, 29)]
        [TestCase(true, 30)]
        [TestCase(true, 31)]
        [TestCase(true, 127)]
        [TestCase(true, -1)]
        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(false, 2)]
        [TestCase(false, 3)]
        [TestCase(false, 4)]
        [TestCase(false, 5)]
        [TestCase(false, 6)]
        [TestCase(false, 7)]
        [TestCase(false, 8)]
        [TestCase(false, 9)]
        [TestCase(false, 10)]
        [TestCase(false, 11)]
        [TestCase(false, 12)]
        [TestCase(false, 13)]
        [TestCase(false, 14)]
        [TestCase(false, 15)]
        [TestCase(false, 16)]
        [TestCase(false, 17)]
        [TestCase(false, 18)]
        [TestCase(false, 19)]
        [TestCase(false, 20)]
        [TestCase(false, 21)]
        [TestCase(false, 22)]
        [TestCase(false, 23)]
        [TestCase(false, 24)]
        [TestCase(false, 25)]
        [TestCase(false, 26)]
        [TestCase(false, 27)]
        [TestCase(false, 28)]
        [TestCase(false, 29)]
        [TestCase(false, 30)]
        [TestCase(false, 31)]
        [TestCase(false, 127)]
        [TestCase(false, -1)]
        public void TypedWriter_EmitsStandardJsonWithoutChangingSemanticText(bool key, int code)
        {
            BaselineWriter();
            GdDict value = ComponentDomainCodec.Encode(Semantic(key, code));
            ExactSemantic(Decode(value), key, code);
            string expected = IndependentTyped(key, code);
            ExactSemantic(Decode(GdJson.Parse(expected, true) as GdDict), key, code);
            string actual = PaidSnapshotCodec.Stringify(value);
            ExactSemantic(Decode(value), key, code);
            Assert.AreEqual(expected, actual, "Typed wire text is subject to the same standard JSON escaping");
        }

        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        [TestCase(true, 3)]
        [TestCase(true, 4)]
        [TestCase(true, 5)]
        [TestCase(true, 6)]
        [TestCase(true, 7)]
        [TestCase(true, 8)]
        [TestCase(true, 9)]
        [TestCase(true, 10)]
        [TestCase(true, 11)]
        [TestCase(true, 12)]
        [TestCase(true, 13)]
        [TestCase(true, 14)]
        [TestCase(true, 15)]
        [TestCase(true, 16)]
        [TestCase(true, 17)]
        [TestCase(true, 18)]
        [TestCase(true, 19)]
        [TestCase(true, 20)]
        [TestCase(true, 21)]
        [TestCase(true, 22)]
        [TestCase(true, 23)]
        [TestCase(true, 24)]
        [TestCase(true, 25)]
        [TestCase(true, 26)]
        [TestCase(true, 27)]
        [TestCase(true, 28)]
        [TestCase(true, 29)]
        [TestCase(true, 30)]
        [TestCase(true, 31)]
        [TestCase(true, 127)]
        [TestCase(true, -1)]
        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(false, 2)]
        [TestCase(false, 3)]
        [TestCase(false, 4)]
        [TestCase(false, 5)]
        [TestCase(false, 6)]
        [TestCase(false, 7)]
        [TestCase(false, 8)]
        [TestCase(false, 9)]
        [TestCase(false, 10)]
        [TestCase(false, 11)]
        [TestCase(false, 12)]
        [TestCase(false, 13)]
        [TestCase(false, 14)]
        [TestCase(false, 15)]
        [TestCase(false, 16)]
        [TestCase(false, 17)]
        [TestCase(false, 18)]
        [TestCase(false, 19)]
        [TestCase(false, 20)]
        [TestCase(false, 21)]
        [TestCase(false, 22)]
        [TestCase(false, 23)]
        [TestCase(false, 24)]
        [TestCase(false, 25)]
        [TestCase(false, 26)]
        [TestCase(false, 27)]
        [TestCase(false, 28)]
        [TestCase(false, 29)]
        [TestCase(false, 30)]
        [TestCase(false, 31)]
        [TestCase(false, 127)]
        [TestCase(false, -1)]
        public void TypedReader_AcceptsIndependentValidJsonWithExactSemanticText(bool key, int code)
        {
            BaselineReader();
            string text = IndependentTyped(key, code);
            ExactSemantic(Decode(GdJson.Parse(text, true) as GdDict), key, code);
            GdDict actual = PaidSnapshotCodec.Parse(text);
            Assert.IsNotNull(actual, "Typed semantic keys are wire string values, independent of raw JSON key policy");
            ExactSemantic(Decode(actual), key, code);
        }
    }
}

