using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class SkillTreeStateTests : ProgressionDataTestBase
    {
        static SkillTreeState MakeTree(out GdDict catalog, out GdDict books)
        {
            catalog = SkillTreeState.LoadSkillsCatalog();
            books = SkillTreeState.LoadBooksCatalog();
            var tree = new SkillTreeState();
            tree.Configure(catalog, books);
            Assert.IsTrue(tree.LoadPrerequisites());
            return tree;
        }

        [Test]
        public void SummaryRoundTrips()
        {
            SkillTreeState tree = MakeTree(out GdDict catalog, out GdDict books);
            Assert.IsTrue(tree.Unlock("fabrication"));
            Assert.IsTrue(tree.Unlock("surgery"));
            GdDict summary = tree.GetSummary();
            var restored = new SkillTreeState();
            restored.Configure(catalog, books);
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.AreEqual(2L, summary["unlocked_count"]);
            Assert.IsTrue(V.VariantEquals(tree.ToDict(), restored.ToDict()));
        }

        [Test]
        public void WeldingMastery_NeedsSkillLevelAndBook()
        {
            SkillTreeState tree = MakeTree(out GdDict catalog, out GdDict books);
            Assert.That(catalog.Count, Is.GreaterThanOrEqualTo(20));
            var prog = new PlayerProgressionState();
            prog.Configure(ClassDefinition.LoadAll()["engineer"], catalog, books);
            GdDict low = tree.CanUnlock("welding_mastery", prog);
            Assert.IsFalse(low.GetBool("can"));
            Assert.AreEqual("missing_prereqs", low.GetString("reason"));
            Assert.AreEqual(2, low.GetArray("missing").Count, "skill level + book");

            while (prog.GetSkillLevel("welding") < 5) prog.GrantXp("welding", 1000);
            prog.GrantXpFromBook("advanced_welding_schematic");
            GdDict high = tree.CanUnlock("welding_mastery", prog);
            Assert.IsTrue(high.GetBool("can"), GdJson.Stringify(high));
            Assert.IsTrue(tree.Unlock("welding_mastery"));
            Assert.IsFalse(tree.Unlock("welding_mastery"), "second unlock is a no-op");
            Assert.AreEqual("already_unlocked", tree.CanUnlock("welding_mastery", prog).GetString("reason"));
            Assert.AreEqual("unknown_skill", tree.CanUnlock("not_a_skill", prog).GetString("reason"));
        }

        [Test]
        public void MissingProgression_ReportsZeroLevels_AndEntriesAreSorted()
        {
            SkillTreeState tree = MakeTree(out GdDict catalog, out _);
            GdDict res = tree.CanUnlock("surgery", null);
            var missing = (GdDict)res.GetArray("missing")[0];
            Assert.AreEqual(0L, missing["current"]);
            Assert.AreEqual(4L, missing["min_level"]);

            GdArray entries = tree.GetSkillEntries();
            Assert.AreEqual(catalog.Count, entries.Count);
            for (int i = 1; i < entries.Count; i++)
                Assert.Less(string.CompareOrdinal(((GdDict)entries[i - 1]).GetString("skill_id"), ((GdDict)entries[i]).GetString("skill_id")), 0);
            Assert.AreEqual("Skill Tree: 0 / " + catalog.Count + " unlocked", tree.GetStatusLines()[0]);
        }
    }
}
