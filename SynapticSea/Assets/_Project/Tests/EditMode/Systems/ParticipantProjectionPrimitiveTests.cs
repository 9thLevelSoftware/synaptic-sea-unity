using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    [NonParallelizable]
    public sealed class ParticipantProjectionPrimitiveTests
    {
        static ProjectionScalar S(object value)=>ProjectionScalar.FromNormalized(value);
        static ProjectionEntry E(string key,object value)=>new ProjectionEntry(S(key),new ProjectionValue(S(value)));
        static ProjectionEntry Child(string key,ProjectionNodeHandle child)=>new ProjectionEntry(S(key),new ProjectionValue(child.Id));
        static ProjectionNodeHandle Node(ParticipantProjectionRegistry registry,ProjectionNodeKind kind=ProjectionNodeKind.Dictionary)
        {Assert.True(registry.TryCreateNode(kind,out var node,out var reason),reason);return node;}
        static ProjectionNodeVersion Version(ProjectionNodeHandle handle,params ProjectionEntry[] entries)
        {Assert.True(handle.TryCreateVersion(entries,out var version,out var reason),reason);return version;}
        static ProjectionRootDescriptor Roots(ProjectionNodeHandle root,ulong stamp=1)
            =>new ProjectionRootDescriptor("diagnostic-cohort","synthetic-policy-not-authenticated",stamp,new[]{new ProjectionRootBinding("items",root.Id)},new[]{new ProjectionScalarBinding("capacity",S(3.5))});
        static ProjectionRootDescriptor Empty()=>new ProjectionRootDescriptor("diagnostic-cohort","synthetic-policy-not-authenticated",1,Array.Empty<ProjectionRootBinding>());
        static void Publish(ParticipantProjectionRegistry registry,ProjectionRootDescriptor roots,params ProjectionNodeVersion[] versions)
        {
            Assert.True(registry.TryPrepare(versions,null,roots,out var cursor,out var reason),reason);
            while(cursor.Status==ProjectionCursorStatus.Pending){cursor.Advance(7);Assert.LessOrEqual(cursor.LastWorkUnits,7);}
            Assert.AreEqual(ProjectionCursorStatus.Complete,cursor.Status,cursor.Reason);
            Assert.True(registry.TryPublish(cursor,out reason),reason);cursor.Dispose();
        }
        static ProjectionCaptureResult Capture(ParticipantProjectionRegistry registry)
        {
            Assert.True(registry.TryPin(out var pin,out var reason),reason);
            Assert.True(pin.TryCreateCursor(out var cursor,out reason),reason);
            while(cursor.Status==ProjectionCursorStatus.Pending){cursor.Advance(3);Assert.LessOrEqual(cursor.LastWorkUnits,3);}
            Assert.AreEqual(ProjectionCursorStatus.Complete,cursor.Status,cursor.Reason);
            var result=cursor.Result;cursor.Dispose();pin.Dispose();return result;
        }
        [Test]
        public void ExactClosedScalarsPreserveTypesBitsVectorWidthsAndTextCaps()
        {
            object[] values={null,true,5L,BitConverter.Int64BitsToDouble(long.MinValue),BitConverter.Int64BitsToDouble(0x7ff8000000000011L),"5",new Vec2i(int.MinValue,int.MaxValue),new Vec3(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),BitConverter.Int32BitsToSingle(0x7fc00011),float.PositiveInfinity)};
            foreach(object value in values){var scalar=S(value);Assert.True(scalar.Equals(S(scalar.ToNormalized())));}
            Assert.False(S(5L).Equals(S(5.0)));Assert.False(S(0.0).Equals(S(BitConverter.Int64BitsToDouble(long.MinValue))));
            Assert.Throws<ArgumentException>(()=>S(5));Assert.Throws<ArgumentException>(()=>S(new GdDict()));Assert.Throws<ArgumentException>(()=>S(new GdArray()));
            Assert.Throws<ArgumentException>(()=>S(new string('x',1025)));
        }
        [Test]
        public void NodePagesDefensivelyOwnEntriesAndKeepInsertionOrderAndTypedKeys()
        {
            using var registry=new ParticipantProjectionRegistry();var handle=Node(registry);
            var entries=new[]{new ProjectionEntry(S(1L),new ProjectionValue(S(0.0))),new ProjectionEntry(S("1"),new ProjectionValue(S(BitConverter.Int64BitsToDouble(long.MinValue))))};
            var version=Version(handle,entries);entries[0]=E("replaced",99L);
            Assert.AreEqual(ProjectionScalarKind.Int64,version.GetEntry(0).Key.Kind);Assert.AreEqual("1",version.GetEntry(1).Key.Text);
            Assert.AreEqual(0UL,version.GetEntry(0).Value.Scalar.Bits0);Assert.AreEqual(0x8000000000000000UL,version.GetEntry(1).Value.Scalar.Bits0);
            // Dictionary key equality is type-sensitive, unlike V.VariantEquals numeric value equality.
            var legacyKeys=new GdDict{{1L,1L},{1.0,2L}};
            var typed=Version(handle,new ProjectionEntry(S(1L),new ProjectionValue(S(1L))),new ProjectionEntry(S(1.0),new ProjectionValue(S(2L))));
            Assert.AreEqual(2,legacyKeys.Count);Assert.AreEqual(legacyKeys.Count,typed.EntryCount);
            Assert.AreEqual(ProjectionScalarKind.Int64,typed.GetEntry(0).Key.Kind);Assert.AreEqual(ProjectionScalarKind.Float64,typed.GetEntry(1).Key.Kind);
            Assert.Throws<ArgumentException>(()=>handle.TryCreateVersion(new[]{new ProjectionEntry(S(1L),new ProjectionValue(S(1L))),new ProjectionEntry(S(1L),new ProjectionValue(S(2L)))},out _,out _));
        }
        [Test]
        public void SmallValueUpdateSharesUnchangedImmutablePagesAndPreservesOrder()
        {
            using var registry=new ParticipantProjectionRegistry();var handle=Node(registry);var entries=new ProjectionEntry[100];for(int i=0;i<entries.Length;i++)entries[i]=E("k"+i,(long)i);
            var before=Version(handle,entries);Assert.True(handle.TryReplaceValue(before,40,new ProjectionValue(S(-1L)),out var after,out _));
            Assert.AreSame(before.GetPage(0),after.GetPage(0));Assert.AreNotSame(before.GetPage(1),after.GetPage(1));Assert.AreSame(before.GetPage(2),after.GetPage(2));
            Assert.AreEqual(40L,before.GetEntry(40).Value.Scalar.ToNormalized());Assert.AreEqual(-1L,after.GetEntry(40).Value.Scalar.ToNormalized());Assert.True(before.GetEntry(40).Key.Equals(after.GetEntry(40).Key));
        }
        [Test]
        public void RadixTableSeparatesAll64BitsAndNeverChangesHistoricalRoots()
        {
            var a=new ProjectionNodeId(55,1);var b=new ProjectionNodeId(55,1UL<<63);var c=new ProjectionNodeId(55,ulong.MaxValue);
            var va=new ProjectionNodeVersion(a,1,ProjectionNodeKind.Array,Array.Empty<ProjectionEntry>(),0);
            var vb=new ProjectionNodeVersion(b,1,ProjectionNodeKind.Array,Array.Empty<ProjectionEntry>(),0);
            var vc=new ProjectionNodeVersion(c,1,ProjectionNodeKind.Array,Array.Empty<ProjectionEntry>(),0);
            var first=new ProjectionVersionTable(55).Set(a,va);var next=first.Set(b,vb).Set(c,vc);
            Assert.AreEqual(13,ProjectionVersionTable.Height);Assert.AreEqual(32,ProjectionVersionTable.Fanout);Assert.AreSame(va,first.Get(a));Assert.IsNull(first.Get(b));Assert.AreSame(vb,next.Get(b));Assert.AreSame(vc,next.Get(c));Assert.AreEqual(3,next.Count);
            var removed=next.Set(b,null);Assert.IsNull(removed.Get(b));Assert.AreSame(vb,next.Get(b));Assert.AreEqual(2,removed.Count);
        }
        [Test]
        public void HistoricalPinsResolveOldChildVersionsWithoutAncestorPropagation()
        {
            using var registry=new ParticipantProjectionRegistry();var parent=Node(registry);var child=Node(registry);
            var parentVersion=Version(parent,Child("left",child),Child("right",child));Publish(registry,Roots(parent),parentVersion,Version(child,E("number",1L)));
            Assert.True(registry.TryPin(out var oldPin,out _));
            Assert.True(registry.TryPrepare(new[]{Version(child,E("number",2L))},null,Roots(parent,2),out var prep,out _));
            while(prep.Status==ProjectionCursorStatus.Pending)prep.Advance(1);
            Assert.AreSame(parentVersion,prep.Candidate.Get(parent.Id));Assert.True(registry.TryPublish(prep,out _));
            Assert.AreEqual(ProjectionPinStatus.Active,oldPin.Status);
            Assert.True(oldPin.TryCreateCursor(out var cursor,out _));while(cursor.Status==ProjectionCursorStatus.Pending)cursor.Advance(1);
            using var old=cursor.Result;using var current=Capture(registry);
            Assert.AreEqual(2,old.NodeCount);Assert.AreEqual(child.Id,old.Entry(0,0).Value.Child);Assert.AreEqual(child.Id,old.Entry(0,1).Value.Child);
            Assert.AreEqual(1UL,old.Node(0).Version);Assert.AreEqual(1L,old.Entry(1,0).Value.Scalar.ToNormalized());Assert.AreEqual(2L,current.Entry(1,0).Value.Scalar.ToNormalized());
        }
        [Test]
        public void MissingChildAndCycleRefuseCandidateWithoutRewritingCurrentCut()
        {
            using var registry=new ParticipantProjectionRegistry();var a=Node(registry);var b=Node(registry);
            Publish(registry,Roots(a),Version(a,E("n",1L)));ulong revision=registry.Revision;
            Assert.True(registry.TryPrepare(new[]{Version(a,Child("b",b))},null,Roots(a),out var missing,out _));while(missing.Status==ProjectionCursorStatus.Pending)missing.Advance(2);
            Assert.AreEqual(ProjectionCursorStatus.Refused,missing.Status);Assert.AreEqual("projection_missing_child",missing.Reason);Assert.AreEqual(revision,registry.Revision);
            Assert.True(registry.TryPrepare(new[]{Version(a,Child("b",b)),Version(b,Child("a",a))},null,Roots(a),out var cycle,out _));while(cycle.Status==ProjectionCursorStatus.Pending)cycle.Advance(1);
            Assert.AreEqual("projection_cycle",cycle.Reason);Assert.AreEqual(revision,registry.Revision);
            using var cut=Capture(registry);Assert.AreEqual(1L,cut.Entry(0,0).Value.Scalar.ToNormalized());
        }
        [Test]
        public void DiamondAliasesCannotHideOverDepthDescendants()
        {
            using var registry=new ParticipantProjectionRegistry();var root=Node(registry);
            var subtree=new ProjectionNodeHandle[81];var prefix=new ProjectionNodeHandle[61];
            for(int i=0;i<subtree.Length;i++)subtree[i]=Node(registry);for(int i=0;i<prefix.Length;i++)prefix[i]=Node(registry);
            var versions=new List<ProjectionNodeVersion>();
            for(int i=0;i<subtree.Length;i++)versions.Add(i+1<subtree.Length?Version(subtree[i],Child("n",subtree[i+1])):Version(subtree[i]));
            for(int i=0;i<prefix.Length;i++)versions.Add(Version(prefix[i],Child("n",i+1<prefix.Length?prefix[i+1]:subtree[0])));
            versions.Add(Version(root,Child("short",subtree[0]),Child("long",prefix[0])));
            for(int start=0;start<versions.Count;start+=32)Publish(registry,Empty(),versions.GetRange(start,Math.Min(32,versions.Count-start)).ToArray());
            Assert.True(registry.TryPrepare(Array.Empty<ProjectionNodeVersion>(),null,Roots(root),out var cursor,out _));while(cursor.Status==ProjectionCursorStatus.Pending)cursor.Advance(4);
            Assert.AreEqual(ProjectionCursorStatus.Refused,cursor.Status);Assert.AreEqual("projection_graph_depth",cursor.Reason);
        }
        [Test]
        public void CursorBudgetsThreadsAndUnresolvedPreparationHaveExplicitBoundaries()
        {
            using var registry=new ParticipantProjectionRegistry();var node=Node(registry);var entries=new ProjectionEntry[65];for(int i=0;i<entries.Length;i++)entries[i]=E("k"+i,(long)i);
            Assert.True(registry.TryPrepare(new[]{Version(node,entries)},null,Roots(node),out var prep,out _));Assert.False(registry.TryPin(out _,out _));
            Assert.Throws<ArgumentOutOfRangeException>(()=>prep.Advance(0));Assert.Throws<ArgumentOutOfRangeException>(()=>prep.Advance(65));
            var error=Task.Run(()=>{try{prep.Advance(1);return null;}catch(Exception ex){return ex;}}).GetAwaiter().GetResult();Assert.IsInstanceOf<InvalidOperationException>(error);
            while(prep.Status==ProjectionCursorStatus.Pending){prep.Advance(1);Assert.LessOrEqual(prep.LastWorkUnits,1);}Assert.True(registry.TryPublish(prep,out _));
            Assert.True(registry.TryPin(out var pin,out _));Assert.True(pin.TryCreateCursor(out var capture,out _));
            error=Task.Run(()=>{try{capture.Advance(1);return null;}catch(Exception ex){return ex;}}).GetAwaiter().GetResult();Assert.IsInstanceOf<InvalidOperationException>(error);
            while(capture.Status==ProjectionCursorStatus.Pending){capture.Advance(1);Assert.LessOrEqual(capture.LastWorkUnits,1);}using var result=capture.Result;Assert.AreEqual(64L,result.Entry(0,64).Value.Scalar.ToNormalized());
        }
        [Test]
        public void CompletedOutputOwnsCutAfterPinAndCursorReleaseUntilExplicitRelease()
        {
            using var registry=new ParticipantProjectionRegistry();var root=Node(registry);Publish(registry,Roots(root),Version(root,E("n",1L)));
            long live=registry.RetainedUnits;var result=Capture(registry);Assert.Greater(registry.RetainedUnits,live);
            Publish(registry,Roots(root,2),Version(root,E("n",2L)));Assert.AreEqual(1L,result.Entry(0,0).Value.Scalar.ToNormalized());
            result.Dispose();result.Dispose();Assert.AreEqual(ProjectionOutputStatus.Released,result.Status);Assert.AreEqual(live,registry.RetainedUnits);Assert.Throws<InvalidOperationException>(()=>result.Entry(0,0));
        }
        [Test]
        public void CancelledCaptureCannotCompleteAndReleaseDropsAllOwnedHistory()
        {
            using var registry=new ParticipantProjectionRegistry();var root=Node(registry);Publish(registry,Roots(root),Version(root,E("n",1L)));long live=registry.RetainedUnits;
            Assert.True(registry.TryPin(out var pin,out _));Assert.True(pin.TryCreateCursor(out var cursor,out _));cursor.Advance(1);pin.Dispose();pin.Dispose();
            Assert.AreEqual(ProjectionCursorStatus.Cancelled,cursor.Advance(64));Assert.IsNull(cursor.Result);Assert.AreEqual(live,registry.RetainedUnits);
            cursor.Dispose();cursor.Dispose();Assert.AreEqual(ProjectionCursorStatus.Released,cursor.Status);
        }
        [Test]
        public void ReadinessEpochRevokesPendingAndCompletedOutputsButOrdinaryRevisionDoesNot()
        {
            using var registry=new ParticipantProjectionRegistry();var root=Node(registry);Publish(registry,Roots(root),Version(root,E("n",1L)));
            var result=Capture(registry);Assert.True(registry.TryPin(out var pin,out _));ulong epoch=registry.ReadinessEpoch;
            Publish(registry,Roots(root,2),Version(root,E("n",2L)));Assert.AreEqual(epoch,registry.ReadinessEpoch);Assert.AreEqual(ProjectionPinStatus.Active,pin.Status);Assert.AreEqual(ProjectionOutputStatus.CompletePartialDiagnostic,result.Status);
            registry.Invalidate("unsupported_writer");Assert.AreEqual(ProjectionPinStatus.Cancelled,pin.Status);Assert.AreEqual(ProjectionOutputStatus.Revoked,result.Status);Assert.Greater(registry.ReadinessEpoch,epoch);Assert.False(registry.TryPin(out _,out _));
            Assert.False(result.TryBuildWholeWorldSave(out var refusal));Assert.AreEqual("participants_not_fully_enrolled",refusal);
        }
        [Test]
        public void RetentionPressureRevokesOldPinAndStillAllowsNewCoherentUpdate()
        {
            using var registry=new ParticipantProjectionRegistry(new ProjectionLimits(retainedUnits:55));var root=Node(registry);Publish(registry,Roots(root),Version(root,E("n",1L)));
            Assert.True(registry.TryPin(out var pin,out _));Publish(registry,Roots(root,2),Version(root,E("n",2L)));
            Assert.AreEqual(ProjectionPinStatus.Cancelled,pin.Status);Assert.AreEqual("projection_retention_pressure",pin.Reason);Assert.AreEqual(ProjectionReadiness.ReadyPartialDiagnostic,registry.Readiness);
            using var cut=Capture(registry);Assert.AreEqual(2L,cut.Entry(0,0).Value.Scalar.ToNormalized());
        }
        [Test]
        public void CompletedResultsAreExplicitlyRevocableUnderRetentionPressure()
        {
            using var registry=new ParticipantProjectionRegistry(new ProjectionLimits(retainedUnits:60));var root=Node(registry);Publish(registry,Roots(root),Version(root,E("n",1L)));
            var old=Capture(registry);Publish(registry,Roots(root,2),Version(root,E("n",2L)));
            Assert.AreEqual(ProjectionOutputStatus.Revoked,old.Status);Assert.Throws<InvalidOperationException>(()=>old.Entry(0,0));Assert.AreEqual(ProjectionReadiness.ReadyPartialDiagnostic,registry.Readiness);old.Dispose();
        }
        [Test]
        public void PinOutputAndLeafCapsRefuseWithoutPartialPayload()
        {
            using var registry=new ParticipantProjectionRegistry(new ProjectionLimits(entries:1,pins:1,outputs:1));var root=Node(registry);
            Assert.Throws<ArgumentException>(()=>Version(root,E("one",1L),E("two",2L)));Publish(registry,Roots(root),Version(root,E("one",1L)));
            Assert.True(registry.TryPin(out var pin,out _));Assert.False(registry.TryPin(out _,out var reason));Assert.AreEqual("projection_pin_capacity",reason);
            Assert.True(pin.TryCreateCursor(out var cursor,out _));while(cursor.Status==ProjectionCursorStatus.Pending)cursor.Advance(2);var result=cursor.Result;
            Assert.True(registry.TryPin(out var second,out _));Assert.False(second.TryCreateCursor(out _,out reason));Assert.AreEqual("projection_output_capacity",reason);
            result.Dispose();Assert.True(second.TryCreateCursor(out var next,out _));next.Dispose();Assert.IsNull(next.Result);
        }
        [Test]
        public void IssuedIdsAreNeverReusedAcrossRetirementAndExhaustionInvalidatesEligibility()
        {
            using var registry=new ParticipantProjectionRegistry(new ProjectionLimits(nodes:1,maximumNodeId:2));var first=Node(registry);Assert.True(first.TryRetire(out _));var second=Node(registry);Assert.Greater(second.Id.Value,first.Id.Value);Assert.True(second.TryRetire(out _));
            Assert.False(registry.TryCreateNode(ProjectionNodeKind.Array,out _,out var reason));Assert.AreEqual("projection_node_id_exhausted",reason);Assert.AreEqual(ProjectionReadiness.Exhausted,registry.Readiness);
            Assert.False(first.TryCreateVersion(Array.Empty<ProjectionEntry>(),out _,out _));
        }
        [Test]
        public void VersionAndEpochOverflowFailClosedWithoutNodePublication()
        {
            using var registry=new ParticipantProjectionRegistry(new ProjectionLimits(maximumEpoch:2));var root=Node(registry);Publish(registry,Roots(root),Version(root,E("n",1L)));
            registry.Invalidate("one");Assert.AreEqual(2UL,registry.ReadinessEpoch);registry.Invalidate("two");Assert.AreEqual(ProjectionReadiness.Exhausted,registry.Readiness);
            using var other=new ParticipantProjectionRegistry();var node=Node(other);typeof(ProjectionNodeHandle).GetField("_version",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(node,ulong.MaxValue);
            Assert.False(node.TryCreateVersion(Array.Empty<ProjectionEntry>(),out var refused,out _));Assert.IsNull(refused);Assert.AreEqual(ProjectionReadiness.Exhausted,other.Readiness);
        }
        [Test]
        public void ExplicitPruningRetainsHistoricalPinsAndReinsertionKeepsLifetimeIdentity()
        {
            using var registry=new ParticipantProjectionRegistry();var old=Node(registry);var replacement=Node(registry);var oldVersion=Version(old,E("old",1L));
            Publish(registry,Roots(old),oldVersion);Assert.True(registry.TryPin(out var pin,out _));
            Assert.True(registry.TryPrepare(new[]{Version(replacement,E("new",2L))},new[]{old.Id},Roots(replacement),out var remove,out _));while(remove.Status==ProjectionCursorStatus.Pending)remove.Advance(3);Assert.True(registry.TryPublish(remove,out _));
            Assert.True(pin.TryCreateCursor(out var cursor,out _));while(cursor.Status==ProjectionCursorStatus.Pending)cursor.Advance(2);using var historic=cursor.Result;Assert.AreEqual(1L,historic.Entry(0,0).Value.Scalar.ToNormalized());
            Assert.False(registry.TryPrepare(new[]{oldVersion},null,Roots(old),out _,out var refusal));Assert.AreEqual("stale_projection_node_lifetime",refusal);
            Publish(registry,Roots(old),Version(old,E("old",3L)));using var fresh=Capture(registry);Assert.AreEqual(old.Id,fresh.Root(0).Node);Assert.AreEqual(2UL,fresh.Node(0).Version);
        }
        [Test]
        public void RetiredPendingNodeCancelsPreparedOwnershipAndCannotResurrect()
        {
            using var registry=new ParticipantProjectionRegistry();var root=Node(registry);var version=Version(root,E("n",1L));
            Assert.True(registry.TryPrepare(new[]{version},null,Roots(root),out var pending,out _));Assert.Greater(registry.RetainedUnits,0);
            Assert.True(root.TryRetire(out _));Assert.AreEqual(ProjectionCursorStatus.Cancelled,pending.Status);Assert.AreEqual(0,registry.RetainedUnits);Assert.False(registry.TryPublish(pending,out _));
            Assert.False(registry.TryPrepare(new[]{version},null,Roots(root),out _,out _));
        }
        [Test]
        public void CapturePagePressureCancelsWithoutPartialResultAndReleasesCopyClaim()
        {
            using var registry=new ParticipantProjectionRegistry(new ProjectionLimits(retainedUnits:45));var root=Node(registry);Publish(registry,Roots(root),Version(root,E("n",1L)));long live=registry.RetainedUnits;
            Assert.True(registry.TryPin(out var pin,out _));Assert.True(pin.TryCreateCursor(out var cursor,out _));cursor.Advance(64);
            Assert.AreEqual(ProjectionCursorStatus.Cancelled,cursor.Status);Assert.AreEqual("projection_capture_retention_pressure",cursor.Reason);Assert.IsNull(cursor.Result);Assert.AreEqual(live,registry.RetainedUnits);
            Assert.AreEqual(ProjectionReadiness.ReadyPartialDiagnostic,registry.Readiness);
        }
        [Test]
        public void FabricatedUnissuedVersionCannotStrandRealHandleOrEnterRegistry()
        {
            using var registry=new ParticipantProjectionRegistry();var root=Node(registry);var legitimate=Version(root,E("n",1L));
            var fabricated=new ProjectionNodeVersion(root.Id,100,ProjectionNodeKind.Dictionary,new[]{E("n",99L)},4096);
            Assert.False(registry.TryPrepare(new[]{fabricated},null,Roots(root),out _,out var refusal));Assert.AreEqual("invalid_projection_node_issuer",refusal);
            Publish(registry,Roots(root),legitimate);using var result=Capture(registry);Assert.AreEqual(1UL,result.Node(0).Version);Assert.AreEqual(1L,result.Entry(0,0).Value.Scalar.ToNormalized());
        }
        [Test]
        public void PropagatedIssuerCloneAtIssuedNumberCannotReplaceExactIssuedObject()
        {
            using var registry=new ParticipantProjectionRegistry();var root=Node(registry);var original=Version(root,E("n",1L));Publish(registry,Roots(root),original);
            Assert.True(root.TryReplaceValue(original,0,new ProjectionValue(S(2L)),out var issued,out _));
            // The helper preserves issuer identity, but this second object was never issued by the handle.
            var forged=original.ReplaceValue(issued.Version,0,new ProjectionValue(S(99L)));
            Assert.False(registry.TryPrepare(new[]{forged},null,Roots(root,2),out _,out var refusal));Assert.AreEqual("invalid_projection_node_issuer",refusal);
            Publish(registry,Roots(root,2),issued);using var result=Capture(registry);Assert.AreEqual(2L,result.Entry(0,0).Value.Scalar.ToNormalized());
        }
        [Test]
        public void NewSpeculativeIssuanceInvalidatesOlderPreparedPlanWithoutTouchingCurrent()
        {
            using var registry=new ParticipantProjectionRegistry();var root=Node(registry);var original=Version(root,E("n",1L));Publish(registry,Roots(root),original);
            var older=Version(root,E("n",2L));Assert.True(registry.TryPrepare(new[]{older},null,Roots(root,2),out var pending,out _));while(pending.Status==ProjectionCursorStatus.Pending)pending.Advance(2);
            var latest=Version(root,E("n",3L));Assert.False(registry.TryPublish(pending,out var refusal));Assert.AreEqual("stale_projection_issuance",refusal);
            using var unchanged=Capture(registry);Assert.AreEqual(1L,unchanged.Entry(0,0).Value.Scalar.ToNormalized());
            Publish(registry,Roots(root,3),latest);using var current=Capture(registry);Assert.AreEqual(3L,current.Entry(0,0).Value.Scalar.ToNormalized());
        }
        [Test]
        public void CrossRegistryReferencesAndWholeWorldClaimsAlwaysRefuse()
        {
            using var first=new ParticipantProjectionRegistry();using var second=new ParticipantProjectionRegistry();var root=Node(first);var foreign=Node(second);
            Assert.Throws<ArgumentException>(()=>Version(root,Child("foreign",foreign)));Assert.False(first.TryBuildWholeWorldSave(out var refusal));Assert.AreEqual("participants_not_fully_enrolled",refusal);
            Publish(first,Roots(root),Version(root));using var result=Capture(first);Assert.False(result.TryBuildWholeWorldSave(out refusal));Assert.AreEqual("participants_not_fully_enrolled",refusal);
        }
    }
}
