using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class WorkActionChannelTests
    {
        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            WorkActionChannel.ResetSharedCatalog();
        }

        [TearDown]
        public void TearDown()
        {
            WorkActionChannel.ResetSharedCatalog();
            CatalogRegistry.Clear();
        }

        static GdDict PatchContext() => new GdDict
        {
            { "tool_class", "sealant" },
            { "skill_id", "repair" },
            { "skill_level", 1L },
            { "inventory", new GdDict { { "hull_sealant", 1L } } },
        };

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var channel = new WorkActionChannel();
            GdDict idle = channel.GetSummary();
            Assert.AreEqual(WorkActionState.STATUS_IDLE, idle.GetString("status"));
            Assert.IsTrue(channel.Begin("patch_breach", "breach_01", 2.0, PatchContext()));
            channel.Tick(0.5);
            GdDict summary = channel.GetSummary();
            var restored = new WorkActionState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.AreEqual(0.25, channel.ProgressRatio(), 1e-9);
        }

        [Test]
        public void Begin_TickInterruptAndCancel()
        {
            var channel = new WorkActionChannel();
            Assert.IsFalse(channel.Begin("not_an_action", "x", 1.0));
            Assert.IsFalse(channel.Begin("patch_breach", "breach_01", 1.0)); // missing tool/skill/materials
            Assert.IsTrue(channel.Begin("patch_breach", "breach_01", 1.0, PatchContext()));
            Assert.IsTrue(channel.IsActive());
            Assert.AreEqual(WorkActionState.STATUS_COMPLETED, channel.Tick(1.0));
            Assert.IsTrue(channel.IsCompleted());

            Assert.IsTrue(channel.Begin("patch_breach", "breach_02", 1.0, PatchContext()));
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, channel.Tick(0.1, new GdDict { { "damaged", true } }));
            Assert.IsTrue(channel.IsInterrupted());
            channel.Cancel();
            Assert.IsNull(channel.Work);
            Assert.AreEqual("", channel.ActionId);
            Assert.AreEqual(WorkActionState.STATUS_IDLE, channel.Tick(1.0));
        }
    }
}
