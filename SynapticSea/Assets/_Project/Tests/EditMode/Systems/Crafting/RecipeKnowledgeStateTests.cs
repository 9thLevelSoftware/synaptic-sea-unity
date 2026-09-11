using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class RecipeKnowledgeStateTests
    {
        static GdDict Recipes() => new GdDict
        {
            { "craft_patch", new GdDict() },
            { "craft_filter", new GdDict { { "knowledge_source", "book" }, { "knowledge_book_id", "manual_filters" } } },
            { "craft_lens", new GdDict { { "knowledge_source", "codex" }, { "knowledge_codex_id", "codex_optics" } } },
            { "craft_valve", new GdDict { { "knowledge_source", "reverse_engineer" }, { "reverse_engineer_component", "pneumatic_valve" }, { "reverse_engineer_count", 2.0 } } },
        };

        [Test]
        public void SummaryRoundTrips()
        {
            var state = new RecipeKnowledgeState();
            state.SeedFromRecipes(Recipes());
            state.RegisterDismantle("pneumatic_valve");
            GdDict summary = state.GetSummary();
            var restored = new RecipeKnowledgeState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void LearnsFromBookCodexAndDismantles()
        {
            var state = new RecipeKnowledgeState();
            state.SeedFromRecipes(Recipes());
            Assert.IsTrue(state.IsKnown("craft_patch"));
            Assert.IsFalse(state.IsKnown("craft_filter"));
            Assert.IsTrue(V.VariantEquals(GdArray.Of("craft_filter"), state.LearnFromBook("manual_filters", Recipes())));
            Assert.IsTrue(V.VariantEquals(GdArray.Of("craft_lens"), state.LearnFromCodex("codex_optics", Recipes())));
            Assert.IsTrue(state.RegisterDismantle("pneumatic_valve").IsEmpty);
            Assert.IsTrue(V.VariantEquals(GdArray.Of("craft_valve"), state.RegisterDismantle("pneumatic_valve")));
            Assert.AreEqual(4L, state.KnownCount());
        }
    }
}
