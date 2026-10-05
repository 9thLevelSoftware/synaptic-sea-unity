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
