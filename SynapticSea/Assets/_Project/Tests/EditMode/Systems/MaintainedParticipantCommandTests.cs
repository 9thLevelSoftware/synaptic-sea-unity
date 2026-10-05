using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Systems
{
    public sealed class MaintainedParticipantCommandTests
    {
        [Test]
        public void BareCallerCannotMintModelReplacementUsingMatchingModelAndForeignIssuer()
        {
            var inventory = InventoryState.CreateTracked(new GdDict());
            var progression = PlayerProgressionState.CreateTracked();
            var training = TrainingEventBus.CreateTracked();
            Assert.Throws<ArgumentException>(() => new InventoryState.PreparedReplacement(inventory, new object(), null, null,
                Array.Empty<KeyValuePair<string,long>>()));
            Assert.Throws<ArgumentException>(() => new PlayerProgressionState.PreparedReplacement(progression, new object(), null));
            Assert.Throws<ArgumentException>(() => new TrainingEventBus.PreparedReplacement(training, new object(), null, 0, 0, 0, 0));
            Assert.Throws<ArgumentException>(() => new InventoryState.PreparedReplacement(null, null, null, null,
                Array.Empty<KeyValuePair<string,long>>()));
        }
        [Test]
        public void ActualInventoryIssuerBindsModelAndPrivateCandidateWithoutChangingLiveItems()
        {
            var model = InventoryState.CreateTracked(new GdDict());
            model.Items["scrap"] = 4L;
            var unrelated = InventoryState.CreateTracked(new GdDict());
            var summary = model.CaptureTrackedSummary(out ulong stamp);
            summary.GetDictOrEmpty("items")["scrap"] = 2L;
            var prepared = model.PrepareTrackedReplacement(summary, stamp);
            Assert.True(prepared.IsForModel(model)); Assert.False(prepared.IsForModel(unrelated));
            var root = prepared.CaptureProjectionOrigin().CaptureCandidateRoot(0);
            summary.GetDictOrEmpty("items")["scrap"] = 99L;
            Assert.AreEqual(2L, root.CaptureEntry(0).Scalar.ToNormalized());
            Assert.AreEqual(4L, model.Items.Get("scrap"));
            model.Items["scrap"] = 5L;
            Assert.Throws<InvalidOperationException>(() => root.CaptureEntryCount());
        }
    }
}
