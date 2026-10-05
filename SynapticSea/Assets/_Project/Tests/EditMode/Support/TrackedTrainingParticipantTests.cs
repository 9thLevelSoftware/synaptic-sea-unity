using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.EditMode
{
    [NonParallelizable]
    public class TrackedTrainingParticipantTests
    {
        IResourceReader _before;
        [SetUp] public void SetUp()
        {
            _before = ResourceAuthorityPublication.Reader;
            ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(new Dictionary<string, string>()));
        }
        [TearDown] public void TearDown() => ResourceAuthorityPublication.ReplaceReader(_before);
        static GdDict Actions() => new GdDict { { "training_actions", new GdArray { new GdDict {
            { "event_id", "repair" }, { "target_skill", "repair" }, { "base_xp", 60L }, { "category", "technical" } } } } };
        static GdDict Receipt() => new GdDict { { "event_id", "repair" }, { "target_id", "utility" },
            { "skill_id", "repair" }, { "base_xp", 60L }, { "category", "technical" },
            { "is_cross_training", false }, { "gated", false }, { "metadata", new GdDict { { "value", 1L } } } };
        [Test] public void LegacyAliasesAndCountersRemainOrdinary()
        {
            var bus = new TrainingEventBus(); bus.Configure(Actions());
            var row = bus.Emit("repair", "utility", null); row["target_id"] = "changed";
            Assert.AreEqual("changed", ((GdDict)bus.GetLog()[0]).GetString("target_id"));
            Assert.AreEqual(60L, bus.GetTotalXpDelivered());
        }
        [Test] public void CallbackRetainedRecordNestedMutationInvalidatesTrackedStamp()
        {
            var bus = TrainingEventBus.CreateTracked(); bus.Configure(Actions());
            GdDict retained = null; bus.OnEventResolved = row => retained = row;
            Assert.AreSame(bus.Emit("repair", "utility", null), retained);
            ulong stamp = bus.CaptureTrackedStamp(); retained["target_id"] = "changed";
            Assert.AreNotEqual(stamp, bus.CaptureTrackedStamp());
            Assert.AreEqual(60L, bus.GetTotalXpDelivered());
        }
        [Test] public void UnknownDropAndResetAdvanceAndPreserveCounters()
        {
            var bus = TrainingEventBus.CreateTracked(); bus.Configure(Actions());
            ulong before = bus.CaptureTrackedStamp(); Assert.IsNull(bus.Emit("unknown", "utility", null));
            Assert.AreNotEqual(before, bus.CaptureTrackedStamp()); Assert.AreEqual(1L, bus.GetDroppedCount());
            bus.Reset(); Assert.AreEqual(0L, bus.GetDroppedCount()); Assert.AreEqual(0L, bus.GetEventCount());
        }
        [Test] public void CallbackPolicyAndImplicitCatalogRefuseBeforeMutation()
        {
            var bus = TrainingEventBus.CreateTracked(); bus.Configure(Actions());
            Assert.Throws<InvalidOperationException>(() => bus.Configure());
            bus.EventFilter = (_, __) => false;
            Assert.Throws<InvalidOperationException>(() => bus.Emit("repair", "utility", null));
            Assert.AreEqual(0L, bus.GetEventCount()); Assert.AreEqual(0L, bus.GetTotalXpDelivered());
        }
        [Test] public void RetainedReceiptConflictDoesNotInstallAndInputAliasesAreIsolated()
        {
            var bus = TrainingEventBus.CreateTracked(); var row = Receipt(); bus.RecordApplied(row, "receipt");
            row.GetDictOrEmpty("metadata")["value"] = 2L;
            Assert.AreEqual(1L, ((GdDict)bus.GetLog()[0]).GetDictOrEmpty("metadata").GetInt("value"));
            ulong before = bus.CaptureTrackedStamp(); var incoming = bus.ToDict();
            ((GdDict)incoming.GetArrayOrEmpty("log")[0])["target_id"] = "conflict";
            Assert.IsFalse(bus.ApplySummary(incoming)); Assert.AreEqual(before, bus.CaptureTrackedStamp());
        }
        [Test] public void PreparedReplacementPreservesBusAndRollsBackOnlySameOpenAttempt()
        {
            var bus = TrainingEventBus.CreateTracked(); ulong stamp = bus.CaptureTrackedStamp();
            var candidate = new TrainingEventBus(); candidate.RecordApplied(Receipt(), "receipt");
            var prepared = bus.PrepareTrackedReplacement(candidate.ToDict(), stamp);
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                Assert.IsTrue(prepared.MatchesUnderGate()); prepared.InstallUnderGate(attempt);
                Assert.AreEqual(1L, bus.GetEventCount()); prepared.RollbackUnderGate(attempt);
                Assert.AreEqual(0L, bus.GetEventCount());
            }
            using (var later = CommonParticipantGate.BeginAttempt())
                Assert.Throws<InvalidOperationException>(() => prepared.RollbackUnderGate(later));
        }
    }
}
