using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class DerelictObjectiveControllerTests
    {
        static GdArray Specs() => GdArray.Of(
            new GdDict { { "id", "obj_salvage_cargo_01" }, { "sequence", 1L }, { "type", "salvage" }, { "kind", "single" }, { "room_id", "cargo_01" } },
            new GdDict
            {
                { "id", "obj_repair_eng_01" }, { "sequence", 2L }, { "type", "restore_systems" }, { "kind", "repair_junction" },
                { "room_id", "eng_01" },
                { "steps", GdArray.Of(new GdDict { { "step_id", "primary_coupling" } }, new GdDict { { "step_id", "secondary_coupling" } }) },
            },
            new GdDict { { "id", "obj_reach_goal" }, { "sequence", 3L }, { "type", "interact" }, { "kind", "single" }, { "room_id", "bridge_01" } });

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var c = DerelictObjectiveController.Create();
            c.Configure(Specs());
            c.Complete(1);
            c.Complete(2, "primary_coupling");
            c.Complete(3);
            GdDict summary = c.GetSummary();
            var restored = DerelictObjectiveController.Create();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsTrue(restored.IsCleared());
            Assert.IsTrue(restored.IsObjectiveComplete(1));
        }

        [Test]
        public void Complete_MatchesSmoke()
        {
            var c = DerelictObjectiveController.Create();
            Assert.IsFalse(c.IsConfigured());
            c.Configure(Specs());
            Assert.IsTrue(c.IsConfigured());
            Assert.IsFalse(c.IsCleared());
            Assert.IsTrue(c.Complete(1));
            Assert.IsTrue(c.IsObjectiveComplete(1));
            Assert.IsFalse(c.IsCleared());
            Assert.IsFalse(c.Complete(1));
            Assert.IsTrue(c.Complete(2, "primary_coupling"));
            Assert.IsFalse(c.IsObjectiveComplete(2));
            GdDict partial = c.GetStepProgress(2);
            Assert.AreEqual(2, partial.GetInt("required_steps"));
            Assert.AreEqual(1, partial.GetInt("completed_steps"));
            Assert.IsTrue(c.IsStepComplete(2, "primary_coupling"));
            Assert.IsTrue(c.Complete(3));
            Assert.IsTrue(c.IsCleared());
            c.Configure(Specs()); // idempotent once configured
            Assert.IsTrue(c.IsObjectiveComplete(1));
            Assert.IsTrue(c.IsCleared());
        }
    }
}
