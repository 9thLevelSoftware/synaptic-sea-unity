using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ManifestationPoolTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void LoadsSchemaAndNarrativeHooks()
        {
            var pool = new ManifestationPool();
            Assert.IsTrue(pool.LoadDefault());
            Assert.AreEqual("manifestation_pool_v1", pool.Schema);
            Assert.That(pool.KindCount(), Is.GreaterThanOrEqualTo(4));
            Assert.That(pool.EntryCount(), Is.GreaterThanOrEqualTo(8));
            Assert.IsTrue(pool.HasKind("whisper"));
            Assert.IsTrue(pool.ForceEntriesForRoom("bridge").Contains("narrative_bridge_mirror"));
            Assert.IsTrue(pool.ForceEntriesForAudioLog("log_captain_last").Contains("narrative_log_confession"));
            var ids = pool.KindIds();
            for (int i = 1; i < ids.Count; i++) Assert.Less(string.CompareOrdinal(ids[i - 1], ids[i]), 0);
        }

        [Test]
        public void WeightedPickSkipsForceOnlyAndAcceptsDataOnlyEntries()
        {
            var pool = new ManifestationPool();
            pool.LoadDefault();
            for (int i = 0; i < 20; i++)
                Assert.AreNotEqual("narrative_bridge_mirror", pool.PickEntryId("phantom", 3, 1000 + i * 17));
            pool.Entries["data_only_new"] = new GdDict
            {
                { "kind", "whisper" }, { "min_tier", 1L }, { "weight", 5L }, { "caption", "New schema entry without code." },
            };
            Assert.IsNotEmpty(pool.PickEntryId("whisper", 2, 42));
            Assert.IsNotEmpty(pool.PickEntryId("whisper", 2, long.MinValue), "absi(INT64_MIN) wraps instead of throwing");
            Assert.AreEqual("", pool.PickEntryId("no_such_kind", 3, 1));
        }
    }
}
