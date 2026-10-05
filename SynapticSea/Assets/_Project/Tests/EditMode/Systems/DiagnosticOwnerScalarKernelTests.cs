using System;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    [NonParallelizable]
    public sealed class DiagnosticOwnerScalarKernelTests
    {
        static ProjectionScalar S(object value) => ProjectionScalar.FromNormalized(value);
        static ProjectionEntry E(object key, object value) => new ProjectionEntry(S(key), new ProjectionValue(S(value)));
        static DiagnosticOwnerScalarKernel Dict(bool attached = true)
            => DiagnosticOwnerScalarKernel.CreateSynthetic(ProjectionNodeKind.Dictionary, new[] { E("a", 1L), E("b", 2.0) }, attached);
        static void Commit(DiagnosticOwnerScalarKernel.PreparedDiagnosticScalarDelta plan)
        {
            using var attempt = CommonParticipantGate.BeginAttempt();
            Assert.True(plan.MatchesUnderGate(attempt)); plan.InstallUnderGate(attempt); plan.CompleteUnderGate(attempt);
        }
        [Test]
        public void ScalarCommitChangesLiveAndCutTogetherWhileOldCutStaysImmutable()
        {
            var owner = Dict(); var old = owner.Capture(); ulong stamp = owner.OwnerStamp;
            var plan = owner.Prepare(0, S(7L)); Assert.AreEqual(1L, owner.ReadLive(0));
            Commit(plan); var next = owner.Capture();
            Assert.AreEqual(7L, owner.ReadLive(0)); Assert.AreEqual(7L, next.Value(0).ToNormalized());
            Assert.AreEqual(1L, old.Value(0).ToNormalized()); Assert.AreEqual(2.0, next.Value(1).ToNormalized());
            Assert.AreEqual(stamp + 1, owner.OwnerStamp); Assert.Greater(next.Revision, old.Revision);
            Assert.AreEqual(old.ReadinessEpoch, next.ReadinessEpoch);
        }
        [Test]
        public void ArraySlotKeepsOtherValuesAndExactBits()
        {
            var entries = new[] { new ProjectionEntry(new ProjectionValue(S(0.0))), new ProjectionEntry(new ProjectionValue(S(4L))) };
            var owner = DiagnosticOwnerScalarKernel.CreateSynthetic(ProjectionNodeKind.Array, entries);
            double minusZero = BitConverter.Int64BitsToDouble(long.MinValue);
            Commit(owner.Prepare(0, S(minusZero))); var cut = owner.Capture();
            Assert.AreEqual(0x8000000000000000UL, cut.Value(0).Bits0); Assert.AreEqual(4L, cut.Value(1).ToNormalized());
        }
        [Test]
        public void SameValueWriteStillAdvancesTrackingAndNodeFreshness()
        {
            var owner = Dict(); ulong stamp = owner.OwnerStamp, epoch = owner.NodeEpoch;
            Commit(owner.Prepare(0, S(1L)));
            Assert.AreEqual(stamp + 1, owner.OwnerStamp); Assert.AreEqual(epoch + 1, owner.NodeEpoch);
        }
        [Test]
        public void LatestIssuedObjectInvalidatesEarlierPreparedPlanEvenForEqualValues()
        {
            var owner = Dict(); var first = owner.Prepare(0, S(3L)); var second = owner.Prepare(0, S(3L));
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                Assert.False(first.MatchesUnderGate(attempt));
                Assert.Throws<InvalidOperationException>(() => first.InstallUnderGate(attempt));
            }
            Assert.AreEqual(1L, owner.ReadLive(0)); Commit(second); Assert.AreEqual(3L, owner.ReadLive(0));
        }
        [Test]
        public void ScalarApiCannotMintChildConversionAndGenesisRejectsUnenrolledChildren()
        {
            var entries = new[] { new ProjectionEntry(S("child"), new ProjectionValue(new ProjectionNodeId(77, 2))) };
            Assert.Throws<ArgumentException>(() => DiagnosticOwnerScalarKernel.CreateSynthetic(ProjectionNodeKind.Dictionary, entries));
            Assert.Throws<ArgumentException>(() => S(new GdDict()));
            Assert.Throws<ArgumentException>(() => Dict().Prepare(4, S(2L)));
        }
        [Test]
        public void TypedDictionaryKeysAndNaNPayloadsArePreserved()
        {
            double nan = BitConverter.Int64BitsToDouble(0x7ff8000000000011L);
            var owner = DiagnosticOwnerScalarKernel.CreateSynthetic(ProjectionNodeKind.Dictionary, new[] { E(1L, 8L), E(1.0, nan) });
            Commit(owner.Prepare(0, S(9.0))); var cut = owner.Capture();
            Assert.AreEqual(ProjectionScalarKind.Float64, cut.Value(0).Kind);
            Assert.AreEqual(0x7ff8000000000011UL, cut.Value(1).Bits0);
            Assert.Throws<ArgumentException>(() => DiagnosticOwnerScalarKernel.CreateSynthetic(ProjectionNodeKind.Dictionary, new[] { E(1L, 1L), E(1L, 2L) }));
        }
        [Test]
        public void UnsupportedWriterRevokesBeforeMutationAndNeverDropsTheWrite()
        {
            var owner = Dict(); var old = owner.Capture(); var plan = owner.Prepare(0, S(7L));
            owner.WriteUnsupported(0, 99L); Assert.False(owner.Ready); Assert.AreEqual(99L, owner.ReadLive(0));
            using (var attempt = CommonParticipantGate.BeginAttempt()) Assert.False(plan.MatchesUnderGate(attempt));
            Assert.Throws<InvalidOperationException>(() => owner.Capture()); Assert.AreEqual(1L, old.Value(0).ToNormalized());
        }
        [Test]
        public void DetachedWriterChangesLocalFreshnessWithoutAdvancingOwnerStamp()
        {
            var owner = Dict(false); ulong stamp = owner.OwnerStamp, epoch = owner.NodeEpoch;
            var plan = owner.Prepare(0, S(3L)); owner.WriteUnsupported(0, 4L);
            Assert.AreEqual(stamp, owner.OwnerStamp); Assert.AreEqual(epoch + 1, owner.NodeEpoch);
            using var attempt = CommonParticipantGate.BeginAttempt(); Assert.False(plan.MatchesUnderGate(attempt));
        }
        [Test]
        public void SyntheticDependencyRevocationRefusesPreparedPublication()
        {
            var owner = Dict(); var plan = owner.Prepare(0, S(3L)); owner.RevokeSyntheticOrigin();
            using var attempt = CommonParticipantGate.BeginAttempt(); Assert.False(plan.MatchesUnderGate(attempt));
            Assert.AreEqual(1L, owner.ReadLive(0));
        }
        [Test]
        public void ImmediateRollbackRestoresBitsWithNewClocksAndMonotonicAdmission()
        {
            var owner = Dict(); var old = owner.Capture(); ulong stamp = owner.OwnerStamp, epoch = owner.NodeEpoch;
            var plan = owner.Prepare(0, S(3L));
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                plan.InstallUnderGate(attempt);
                Assert.Throws<InvalidOperationException>(() => owner.Capture());
                plan.RollbackUnderGate(attempt);
            }
            Assert.AreEqual(1L, owner.ReadLive(0)); Assert.AreEqual(stamp + 2, owner.OwnerStamp);
            Assert.AreEqual(epoch + 2, owner.NodeEpoch); Assert.AreEqual(2UL, owner.LastAdmitted);
            Assert.Greater(owner.Capture().Revision, old.Revision);
            Commit(owner.Prepare(0, S(9L))); Assert.AreEqual(9L, owner.ReadLive(0)); Assert.AreEqual(3UL, owner.LastAdmitted);
        }
        [Test]
        public void CompletedOrDelayedAttemptCannotRollbackOrReinstall()
        {
            var owner = Dict(); var plan = owner.Prepare(0, S(3L)); Commit(plan);
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                Assert.False(plan.MatchesUnderGate(attempt));
                Assert.Throws<InvalidOperationException>(() => plan.RollbackUnderGate(attempt));
            }
            var dangling = owner.Prepare(0, S(5L));
            using (var attempt = CommonParticipantGate.BeginAttempt()) dangling.InstallUnderGate(attempt);
            using (var later = CommonParticipantGate.BeginAttempt())
                Assert.Throws<InvalidOperationException>(() => dangling.RollbackUnderGate(later));
            Assert.Throws<InvalidOperationException>(() => owner.Capture());
        }
        [Test]
        public void GenesisRejectsOversizedInputBeforeImport()
        {
            var tooMany = new ProjectionEntry[4097];
            var error = Assert.Throws<ArgumentException>(() => DiagnosticOwnerScalarKernel.CreateSynthetic(ProjectionNodeKind.Array, tooMany));
            Assert.AreEqual("synthetic_genesis_entry_capacity", error.Message);
        }
        [Test]
        public void EmptyAndMaximumSizedGenesisRespectTheDeclaredBoundary()
        {
            var empty = DiagnosticOwnerScalarKernel.CreateSynthetic(ProjectionNodeKind.Array, Array.Empty<ProjectionEntry>());
            Assert.True(empty.Ready); Assert.Throws<ArgumentException>(() => empty.Prepare(0, S(1L)));
            var full = new ProjectionEntry[4096];
            for (int i = 0; i < full.Length; i++) full[i] = new ProjectionEntry(new ProjectionValue(S((long)i)));
            var owner = DiagnosticOwnerScalarKernel.CreateSynthetic(ProjectionNodeKind.Array, full);
            Commit(owner.Prepare(4095, S(-1L)));
            Assert.AreEqual(-1L, owner.Capture().Value(4095).ToNormalized());
            Assert.AreEqual(4094L, owner.Capture().Value(4094).ToNormalized());
        }
        [TestCase("_nodeEpoch")]
        [TestCase("_revision")]
        [TestCase("_issued")]
        public void CounterExhaustionInvalidatesWithoutChangingLiveData(string field)
        {
            var owner = Dict(); typeof(DiagnosticOwnerScalarKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, ulong.MaxValue);
            Assert.Throws<InvalidOperationException>(() => owner.Prepare(0, S(3L)));
            Assert.False(owner.Ready); Assert.AreEqual(1L, owner.ReadLive(0));
            owner.WriteUnsupported(0, 8L); Assert.AreEqual(8L, owner.ReadLive(0));
        }
    }
}
