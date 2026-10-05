using System;
using NUnit.Framework;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Systems
{
    public sealed class ProjectionLifetimeLedgerTests
    {
        static ProjectionLifetimeState Empty(ulong registry, ulong id) => new ProjectionLifetimeState(new ProjectionNodeId(registry, id), ProjectionNodeKind.Dictionary, 0, new ProjectionNodeIssuer(), null);
        [Test]
        public void PersistentPathsKeepOldPinsAndFullWidthIdentityDistinct()
        {
            var empty = new ProjectionLifetimeLedger(17); var first = Empty(17, 1); var far = Empty(17, ulong.MaxValue);
            var one = empty.Set(first.Id, first); var two = one.Set(far.Id, far);
            Assert.AreEqual(0, empty.Count); Assert.AreEqual(1, one.Count); Assert.AreEqual(2, two.Count);
            Assert.AreSame(first, two.Get(first.Id)); Assert.IsNull(one.Get(far.Id)); Assert.AreSame(far, two.Get(far.Id));
            var removed = two.Set(first.Id, null); Assert.AreSame(first, two.Get(first.Id)); Assert.IsNull(removed.Get(first.Id));
            Assert.LessOrEqual(two.Units - one.Units, ProjectionLifetimeLedger.Height + 1);
            Assert.Throws<ArgumentException>(() => two.Set(new ProjectionNodeId(18, 1), first));
        }
        [Test]
        public void ClosedIssuanceIdentityAndMonotonicAdmissionRemainExact()
        {
            var id = new ProjectionNodeId(1, 1); var issuer = new ProjectionNodeIssuer();
            var v1 = new ProjectionNodeVersion(id, 1, ProjectionNodeKind.Dictionary, Array.Empty<ProjectionEntry>(), 4096, issuer);
            var v2 = new ProjectionNodeVersion(id, 2, ProjectionNodeKind.Dictionary, Array.Empty<ProjectionEntry>(), 4096, issuer);
            var first = new ProjectionLifetimeState(id, ProjectionNodeKind.Dictionary, 1, issuer, v1);
            var second = new ProjectionLifetimeState(id, ProjectionNodeKind.Dictionary, 2, issuer, v2);
            var table = new ProjectionLifetimeLedger(1).Set(id, first).Set(id, second);
            Assert.True(second.IsExactIssued(v2)); Assert.False(second.IsExactIssued(v1));
            Assert.False(second.IsExactIssued(new ProjectionNodeVersion(id, 2, ProjectionNodeKind.Dictionary, Array.Empty<ProjectionEntry>(), 4096, issuer)));
            Assert.Throws<ArgumentException>(() => table.Set(id, first));
            Assert.Throws<ArgumentException>(() => new ProjectionLifetimeState(id, ProjectionNodeKind.Dictionary, 3, issuer, v2));
        }
        [Test]
        public void EntryCapRefusesBeforePathAllocationAndOldLedgerSurvives()
        {
            var table = new ProjectionLifetimeLedger(1);
            for (ulong i = 1; i <= ProjectionLifetimeLedger.MaximumEntries; i++) { var value = Empty(1, i); table = table.Set(value.Id, value); }
            var over = Empty(1, 4097); Assert.Throws<ArgumentException>(() => table.Set(over.Id, over));
            Assert.AreEqual(4096, table.Count); Assert.IsNull(table.Get(over.Id));
        }
    }
}
