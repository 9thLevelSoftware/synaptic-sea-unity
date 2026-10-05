using System;
using NUnit.Framework;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Systems
{
    public sealed class ParticipantProjectionCandidateTests
    {
        [Test]
        public void ExhaustedLifetimeEpochRefusesIssuanceAndRetirementBeforeHandleMutation()
        {
            using var registry = new ParticipantProjectionRegistry();
            Assert.True(registry.TryCreateNode(ProjectionNodeKind.Dictionary, out var node, out _));
            var entries = new[] { new ProjectionEntry(ProjectionScalar.FromNormalized("value"), new ProjectionValue(ProjectionScalar.FromNormalized(1L))) };
            Assert.True(node.TryCreateVersion(entries, out var issued, out _));
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(ParticipantProjectionRegistry).GetField("_maintenanceLifetimeEpoch", flags).SetValue(registry, ulong.MaxValue);
            Assert.Throws<InvalidOperationException>(() => node.TryCreateVersion(entries, out _, out _));
            Assert.Throws<InvalidOperationException>(() => node.TryPrepareInitialVersion(entries, out _));
            Assert.Throws<InvalidOperationException>(() => node.TryPrepareScalarVersion(issued, 0, new ProjectionValue(ProjectionScalar.FromNormalized(2L)), out _, out _));
            Assert.Throws<InvalidOperationException>(() => node.TryReplaceValue(issued, 0, new ProjectionValue(ProjectionScalar.FromNormalized(2L)), out _, out _));
            Assert.AreEqual(1UL, typeof(ProjectionNodeHandle).GetField("_version", flags).GetValue(node));
            Assert.Throws<InvalidOperationException>(() => node.TryRetire(out _));
            Assert.AreEqual(false, typeof(ProjectionNodeHandle).GetField("_retired", flags).GetValue(node));
            Assert.AreEqual(1, typeof(ParticipantProjectionRegistry).GetField("_activeHandles", flags).GetValue(registry));
            typeof(ParticipantProjectionRegistry).GetField("_maintenanceLifetimeEpoch", flags).SetValue(registry, 0UL);
            Assert.True(registry.TryPrepare(new[] { issued }, null, new ProjectionRootDescriptor("overflow-test", "synthetic-policy", 1,
                new[] { new ProjectionRootBinding("root", node.Id) }), out var prep, out var reason), reason);
            using (prep) { while (prep.Status == ProjectionCursorStatus.Pending) prep.Advance(1); Assert.True(registry.TryPublish(prep, out reason), reason); }
        }
        [Test]
        public void ClosedCandidateReadsPrivateImportedEntriesAndAliasIdentityWithoutLiveViews()
        {
            var owner = new TrackedParticipantOwner(); var root = owner.NewDict(root: true); root["old"] = 1L;
            var child = new GdDict { { "xp", -0.0 } }; var incoming = new GdDict { { "first", child }, { "second", child }, { "count", 2L } };
            var prepared = owner.PrepareReplacements(new object[] { root }, new object[] { incoming }, owner.Stamp);
            var origin = prepared.CaptureProjectionOrigin(); var node = origin.CaptureCandidateRoot(0);
            child["xp"] = 99.0; incoming["count"] = 999L;
            Assert.True(node.MatchesLiveIdentity(root)); Assert.AreEqual(3, node.CaptureEntryCount());
            var first = node.CaptureEntry(0); var second = node.CaptureEntry(1);
            Assert.True(first.IsChild); Assert.True(first.Child.IsSameNode(second.Child));
            Assert.AreEqual("first", first.Key.ToNormalized()); Assert.AreEqual(2L, node.CaptureEntry(2).Scalar.ToNormalized());
            Assert.True(ProjectionScalar.FromNormalized(-0.0).Equals(first.Child.CaptureEntry(0).Scalar));
            Assert.Throws<ArgumentOutOfRangeException>(() => node.CaptureEntry(3));
            using (var attempt = CommonParticipantGate.BeginAttempt()) prepared.InstallUnderGate(attempt);
            Assert.Throws<InvalidOperationException>(() => node.CaptureEntryCount());
            Assert.Throws<InvalidOperationException>(() => first.Child.CaptureEntry(0));
        }
        [Test]
        public void RootEntryReaderRefusesOwnerEvolutionBeforeAnyPreparationCanUseIt()
        {
            var owner = new TrackedParticipantOwner(); var root = owner.NewArray(root: true); root.Add(1L);
            var prepared = owner.PrepareReplacements(new object[] { root }, new object[] { new GdArray { 2L } }, owner.Stamp);
            var origin = prepared.CaptureProjectionOrigin(); var node = origin.CaptureCandidateRoot(0);
            Assert.AreEqual(ProjectionNodeKind.Array, node.Kind); Assert.AreEqual(2L, node.CaptureEntry(0).Scalar.ToNormalized());
            root[0] = 3L;
            Assert.Throws<InvalidOperationException>(() => node.CaptureEntry(0));
            Assert.Throws<InvalidOperationException>(() => origin.CaptureCandidateRoot(0));
        }
    }
}
