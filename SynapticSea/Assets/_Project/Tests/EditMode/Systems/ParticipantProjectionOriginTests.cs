using System;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Systems
{
    [NonParallelizable]
    public sealed class ParticipantProjectionOriginTests
    {
        [Test]
        public void ForgedBackingWithCorrectOwnerRootStampOrForeignIssuerCannotMintAuthority()
        {
            var owner = new TrackedParticipantOwner(); var root = owner.NewDict(root: true); root["count"] = 1L;
            var malicious = new GdDict(); malicious["cycle"] = malicious;
            lock (CommonParticipantGate.SyncRoot)
            {
                Assert.Throws<ArgumentException>(() => new PreparedVariantBacking(owner, new object[] { root },
                    new object[] { root.RawStorage }, new object[] { malicious.RawStorage }, owner.Stamp));
                Assert.Throws<ArgumentException>(() => new PreparedVariantBacking(owner, new object[] { root },
                    new object[] { root.RawStorage }, new object[] { malicious.RawStorage }, owner.Stamp, new object()));
            }
            Assert.AreEqual(1L, root["count"]);
        }
        [Test]
        public void ValidatedMintOwnsCallerArraysAndDetachedInputs()
        {
            var owner = new TrackedParticipantOwner(); var root = owner.NewDict(root: true); root["count"] = 1L;
            var targetRoots = new object[] { root }; var value = new GdDict { { "count", 2L } };
            var values = new object[] { value };
            var prepared = owner.PrepareReplacements(targetRoots, values, owner.Stamp);
            targetRoots[0] = new GdDict(); values[0] = new GdDict(); value["count"] = 999L;
            var origin = prepared.CaptureProjectionOrigin();
            using (var attempt = CommonParticipantGate.BeginAttempt())
            { Assert.True(origin.RootMatchesUnderGate(0, root)); prepared.InstallUnderGate(attempt); }
            Assert.AreEqual(2L, root["count"]);
        }
        [Test]
        public void OpaqueOriginBindsExactOwnerRootAndReservedClocksWithoutMutableViews()
        {
            var owner = new TrackedParticipantOwner();
            var root = owner.NewDict(root: true); root["count"] = 1L;
            var expected = owner.Stamp;
            var prepared = owner.PrepareReplacements(new object[] { root }, new object[] { new GdDict { { "count", 2L } } }, expected);
            var origin = prepared.CaptureProjectionOrigin();
            lock (CommonParticipantGate.SyncRoot)
            {
                Assert.True(origin.MatchesUnderGate(owner)); Assert.False(origin.MatchesUnderGate(new TrackedParticipantOwner()));
                Assert.True(origin.RootMatchesUnderGate(0, root)); Assert.False(origin.RootMatchesUnderGate(0, new GdDict()));
                Assert.False(origin.RootMatchesUnderGate(-1, root)); Assert.False(origin.RootMatchesUnderGate(1, root));
            }
            Assert.AreEqual(expected + 1, origin.InstalledStamp); Assert.AreEqual(expected + 2, origin.RollbackStamp);
            root["count"] = 3L;
            lock (CommonParticipantGate.SyncRoot) Assert.False(origin.MatchesUnderGate(owner));
            Assert.Throws<InvalidOperationException>(() => prepared.CaptureProjectionOrigin());
        }
        [Test]
        public void InstalledAndRolledBackOriginCannotBeReusedAndOldRootIdentitySurvives()
        {
            var owner = new TrackedParticipantOwner(); var root = owner.NewDict(root: true); root["count"] = 1L;
            var expected = owner.Stamp;
            var prepared = owner.PrepareReplacements(new object[] { root }, new object[] { new GdDict { { "count", 2L } } }, expected);
            var origin = prepared.CaptureProjectionOrigin();
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                prepared.InstallUnderGate(attempt); Assert.False(origin.MatchesUnderGate(owner));
                Assert.AreEqual(2L, root["count"]); prepared.RollbackUnderGate(attempt);
                Assert.AreEqual(1L, root["count"]); Assert.AreEqual(origin.RollbackStamp, owner.Stamp);
                Assert.False(origin.MatchesUnderGate(owner));
            }
            Assert.Throws<InvalidOperationException>(() => prepared.CaptureProjectionOrigin());
        }
    }
}
