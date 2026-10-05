using System;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class ActualWoundsProjectionBridgeTests:InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp]public void Setup(){_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown]public void Teardown(){CoreServices.Engine=_engine;}
        static RunSession Boot(){var d=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(d);rig.Session=RunSession.Create(d);Assert.IsTrue(rig.Session.PlayableStarted,rig.Session.LastFailureReason);rig.Session.WoundState.ApplyWound(new GdDict{{"kind","laceration"},{"body_part","arm"},{"severity",.7}});return rig.Session;}
        static void Install(ActualWoundsProjectionBridge.PreparedChange p){using(p)lock(CommonParticipantGate.SyncRoot)Assert.IsTrue(p.TryInstallActualRawAndProjectionUnderGate(out var reason),reason);}
        static void Tick(ActualWoundsProjectionBridge b,double delta){var before=b.Read();var scratch=before.Values.ExactScratch();scratch.Tick(delta);Assert.IsTrue(b.PrepareNext(before,ContinuousWoundsValues.CaptureExact(scratch),out var p,out var reason),reason);Install(p);}
        [Test]public void ActualTickRetainsModelArrayRowAndMatchesNativeOracle(){var s=Boot();var actual=s.WoundState;var array=actual.Wounds;var row=array[0];var oracle=ContinuousWoundsValues.CaptureExact(actual).ExactScratch();using var b=new ActualWoundsProjectionBridge(s);Tick(b,.25);oracle.Tick(.25);Assert.AreSame(actual,s.WoundState);Assert.AreSame(array,actual.Wounds);Assert.AreSame(row,actual.Wounds[0]);Assert.IsTrue(ContinuousWoundsValues.CaptureExact(oracle).MatchesRaw(actual));}
        [Test]public void HistoricalCaptureSurvivesRegisteredTickAndRefusesRawDrift(){var s=Boot();using var b=new ActualWoundsProjectionBridge(s);Assert.IsTrue(GuardedWoundsCaptureCursor.TryBegin(b,out var c,out _));using(c){Tick(b,.25);int calls=0;while(c.Advance(1)==ContinuousTraversalStatus.Pending)Assert.Less(++calls,5000);Assert.IsTrue(c.TryReadWoundScalar("w1","age_seconds",out var age,out _));Assert.AreEqual(0.0,age);((GdDict)s.WoundState.Wounds[0])["age_seconds"]=.5;Assert.IsFalse(c.TryReadWoundScalar("w1","age_seconds",out _,out _));}}
        [Test]public void EqualRowReplacementRevokesSnapshot(){var s=Boot();using var b=new ActualWoundsProjectionBridge(s);var old=b.Read();s.WoundState.Wounds[0]=((GdDict)s.WoundState.Wounds[0]).DeepCopy();Assert.IsFalse(b.PrepareNext(old,old.Values,out _,out _));}
        [Test]public void ForeignSnapshotRefusesAndLatestPlanPreemptsOld(){var s=Boot();var other=Boot();using var b=new ActualWoundsProjectionBridge(s);using var foreign=new ActualWoundsProjectionBridge(other);var old=b.Read();Assert.IsFalse(b.PrepareNext(foreign.Read(),old.Values,out _,out _));Assert.IsTrue(b.PrepareNext(old,old.Values,out var first,out _));var scratch=old.Values.ExactScratch();scratch.Tick(.25);Assert.IsTrue(b.PrepareNext(old,ContinuousWoundsValues.CaptureExact(scratch),out var latest,out _));first.Dispose();lock(CommonParticipantGate.SyncRoot){Assert.IsFalse(first.MatchesUnderGate());Assert.IsTrue(latest.MatchesUnderGate());}Install(latest);}
        [Test]public void StaleAndRepeatedInstallRefuse(){var s=Boot();using var b=new ActualWoundsProjectionBridge(s);var old=b.Read();Tick(b,.25);Assert.IsFalse(b.PrepareNext(old,old.Values,out _,out _));var current=b.Read();Assert.IsTrue(b.PrepareNext(current,current.Values,out var p,out _));Install(p);lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(p.TryInstallActualRawAndProjectionUnderGate(out _));}
        [Test]public void AddRemoveReorderKeepsRetainedWrappersAndOwnsNewRows()
        {
            var s=Boot();var retained=(GdDict)s.WoundState.Wounds[0];using var b=new ActualWoundsProjectionBridge(s);
            var before=b.Read();var scratch=before.Values.ExactScratch();scratch.ApplyWound(new GdDict{{"kind","burn"},{"body_part","leg"},{"severity",.4}});
            var scratchNew=(GdDict)scratch.Wounds[1];scratch.Wounds=new GdArray(new object[]{scratchNew,scratch.Wounds[0]});
            Assert.IsTrue(b.PrepareNext(before,ContinuousWoundsValues.CaptureExact(scratch),out var add,out _));Install(add);
            Assert.AreSame(retained,s.WoundState.Wounds[1]);Assert.AreNotSame(scratchNew,s.WoundState.Wounds[0]);
            var newActual=s.WoundState.Wounds[0];before=b.Read();scratch=before.Values.ExactScratch();scratch.Wounds=new GdArray(new object[]{scratch.Wounds[0]});
            Assert.IsTrue(b.PrepareNext(before,ContinuousWoundsValues.CaptureExact(scratch),out var remove,out _));Install(remove);
            Assert.AreSame(newActual,s.WoundState.Wounds[0]);Assert.AreEqual(1,s.WoundState.Wounds.Count);
        }
        [Test]public void DuplicateIdentifiersRefuseBeforePreparation(){var s=Boot();s.WoundState.Wounds.Add(((GdDict)s.WoundState.Wounds[0]).DeepCopy());Assert.Throws<ArgumentException>(()=>ContinuousWoundsValues.CaptureExact(s.WoundState));}
        [Test]public void EqualWholeArrayReplacementRefuses(){var s=Boot();using var b=new ActualWoundsProjectionBridge(s);var old=b.Read();s.WoundState.Wounds=s.WoundState.Wounds.DeepCopy();Assert.IsFalse(b.PrepareNext(old,old.Values,out _,out _));}
        [Test]public void ZeroInvalidBudgetsAndCancelNeverRollbackNativeWorld()
        {
            var s=Boot();using var b=new ActualWoundsProjectionBridge(s);Assert.IsTrue(GuardedWoundsCaptureCursor.TryBegin(b,out var c,out _));
            using(c){Assert.AreEqual(ContinuousTraversalStatus.Pending,c.Advance(0));Assert.AreEqual(0,c.LastWorkUnits);Tick(b,.25);c.Cancel();Assert.AreEqual(ContinuousTraversalStatus.Cancelled,c.Status);Assert.AreEqual(.25,((GdDict)s.WoundState.Wounds[0]).Get("age_seconds"));}
            foreach(int budget in new[]{-1,65}){Assert.IsTrue(GuardedWoundsCaptureCursor.TryBegin(b,out var invalid,out _));using(invalid){Assert.AreEqual(ContinuousTraversalStatus.Refused,invalid.Advance(budget));Assert.AreEqual(0,invalid.LastWorkUnits);}}
        }
        [Test]public void ProtectedArrayOrRowRefusesWithoutChangingOwnerBackingOrStamp()
        {
            var s=Boot();var owner=new TrackedParticipantOwner();var protectedArray=owner.ImportArray(s.WoundState.Wounds,true);s.WoundState.Wounds=protectedArray;
            var backing=protectedArray.RawStorage;ulong stamp=owner.Stamp;
            Assert.Throws<ArgumentException>(()=>new ActualWoundsProjectionBridge(s));Assert.AreSame(backing,protectedArray.RawStorage);Assert.AreEqual(stamp,owner.Stamp);
            s=Boot();var rowOwner=new TrackedParticipantOwner();var protectedRow=rowOwner.ImportDict((GdDict)s.WoundState.Wounds[0],true);s.WoundState.Wounds[0]=protectedRow;
            var rowBacking=protectedRow.RawStorage;stamp=rowOwner.Stamp;
            Assert.Throws<ArgumentException>(()=>new ActualWoundsProjectionBridge(s));Assert.AreSame(rowBacking,protectedRow.RawStorage);Assert.AreEqual(stamp,rowOwner.Stamp);
        }
        [Test]public void ProtectedRowReplacementAfterPreparationRefusesWithoutBypassingOwner()
        {
            var s=Boot();using var b=new ActualWoundsProjectionBridge(s);var before=b.Read();Assert.IsTrue(b.PrepareNext(before,before.Values,out var p,out _));
            var owner=new TrackedParticipantOwner();var row=owner.ImportDict((GdDict)s.WoundState.Wounds[0],true);s.WoundState.Wounds[0]=row;var backing=row.RawStorage;ulong stamp=owner.Stamp;
            using(p)lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(p.TryInstallActualRawAndProjectionUnderGate(out _));Assert.AreSame(backing,row.RawStorage);Assert.AreEqual(stamp,owner.Stamp);
        }
    }
}
