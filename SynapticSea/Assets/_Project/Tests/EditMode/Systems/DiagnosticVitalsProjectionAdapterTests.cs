using System;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Systems
{
    public class DiagnosticVitalsProjectionAdapterTests
    {
        static void Commit(DiagnosticVitalsProjectionAdapter.PreparedMutation p)
        {using(p){Assert.IsTrue(p.CommitStandalone(out string reason),reason);}}
        static ProjectionCaptureResult Capture(ProjectionPin pin)
        {
            Assert.IsTrue(pin.TryCreateCursor(out var cursor,out string reason),reason);
            using(cursor){while(cursor.Status==ProjectionCursorStatus.Pending){cursor.Advance(1);Assert.LessOrEqual(cursor.LastWorkUnits,1);}Assert.AreEqual(ProjectionCursorStatus.Complete,cursor.Status,cursor.Reason);return cursor.Result;}
        }
        static void ExactSummary(GdDict expected,ProjectionCaptureResult result)
        {
            Assert.AreEqual(1,result.NodeCount);Assert.AreEqual(21,result.Node(0).EntryCount);
            int i=0;foreach(var row in expected){var entry=result.Entry(0,i++);Assert.AreEqual(row.Key,entry.Key.ToNormalized());Assert.IsTrue(ProjectionScalar.FromNormalized(row.Value).Equals(entry.Value.Scalar),row.Key.ToString());}
        }
        static void Current(DiagnosticVitalsProjectionAdapter adapter,VitalsState oracle)
        {Assert.IsTrue(adapter.TryPinPartialDiagnostic(out var pin,out string reason),reason);using(pin)using(var result=Capture(pin))ExactSummary(oracle.GetSummary(),result);}
        [Test] public void ActualTickHistoricalCutSurvivesMultipleLiveWrites()
        {
            var values=new DiagnosticVitalsValues(health:85,stamina:45,hunger:20,thirst:30);
            using var adapter=new DiagnosticVitalsProjectionAdapter(values);var oracle=values.ExactScratch();var before=oracle.GetSummary();
            Assert.IsTrue(adapter.TryPinPartialDiagnostic(out var pin,out _));
            var input=new DiagnosticVitalsTickInput(moving:false,fire:2,woundDrain:1,temperatureHunger:1.5);
            for(int i=0;i<5;i++){Assert.IsTrue(adapter.PrepareTick(.25,input,out var p,out string reason),reason);Commit(p);oracle.Tick(.25,input.CopyForModel());}
            using(pin)using(var historical=Capture(pin)){ExactSummary(before,historical);Assert.IsFalse(historical.TryBuildWholeWorldSave(out _));}
            Current(adapter,oracle);Assert.Less(adapter.Read().Values.Health,values.Health);Assert.Greater(adapter.Read().Stamp,1UL);
        }
        [Test] public void ExactScratchPreservesTinyDifferencesSignedZeroAndAllRates()
        {
            var values=new DiagnosticVitalsValues(health:99.9999,stamina:50.00001,hunger:BitConverter.Int64BitsToDouble(long.MinValue),healthDrain:.125,staminaDrain:2.125,hungerDrain:.625,thirstDrain:.925,staminaRecovery:5.125,healthRecovery:.125);
            using var adapter=new DiagnosticVitalsProjectionAdapter(values);var oracle=values.ExactScratch();Current(adapter,oracle);
            var input=new DiagnosticVitalsTickInput(moving:true,sanityRecovery:.5,temperatureThirst:1.25);
            Assert.IsTrue(adapter.PrepareTick(.001,input,out var p,out _));Commit(p);oracle.Tick(.001,input.CopyForModel());Current(adapter,oracle);
        }
        [Test] public void ConfigureSummaryAndDeltaUseRealModelRules()
        {
            var initial=new DiagnosticVitalsValues();using var adapter=new DiagnosticVitalsProjectionAdapter(initial);var oracle=initial.ExactScratch();
            var changed=new DiagnosticVitalsValues(health:80,maxHealth:90,stamina:70,maxStamina:95,hunger:30,maxHunger:80,thirst:40,maxThirst:85,healthDrain:.1,staminaDrain:1.25,hungerDrain:.75,thirstDrain:1.75,staminaRecovery:7,healthRecovery:2);
            Assert.IsTrue(adapter.PrepareConfigure(changed,out var p,out _));Commit(p);oracle.Configure(changed.Configuration());Current(adapter,oracle);
            var summary=new DiagnosticVitalsValues(health:80.0001,stamina:70.0001);Assert.IsTrue(adapter.PrepareSummary(summary,out p,out _));Commit(p);oracle.ApplySummary(summary.Configuration());Current(adapter,oracle);
            Assert.IsTrue(adapter.PrepareDelta(-1,-2,-3,-4,out p,out _));Commit(p);oracle.ApplyDelta(new GdDict{{"health",-1.0},{"stamina",-2.0},{"hunger",-3.0},{"thirst",-4.0}});Current(adapter,oracle);
        }
        [Test] public void DiagnosticDamageCallbacksAreOrderedAfterCompletePublication()
        {
            var values=new DiagnosticVitalsValues(health:90,hunger:10,healthDrain:1);using var adapter=new DiagnosticVitalsProjectionAdapter(values);
            var oracle=values.ExactScratch();var expected=new List<string>();oracle.HealthDamageObserved+=(source,amount)=>expected.Add(source+":"+BitConverter.DoubleToInt64Bits(amount));
            var actual=new List<string>();adapter.DamageObserved+=(source,amount)=>{Assert.IsFalse(System.Threading.Monitor.IsEntered(CommonParticipantGate.SyncRoot));Assert.Less(adapter.Read().Values.At(4),10);actual.Add(source+":"+BitConverter.DoubleToInt64Bits(amount));};
            var input=new DiagnosticVitalsTickInput(moving:false,radiation:1,atmosphere:1,fire:1,sanityDrain:1,encumbrance:1,woundDrain:1);
            oracle.Tick(.25,input.CopyForModel());Assert.IsTrue(adapter.PrepareTick(.25,input,out var p,out _));Commit(p);CollectionAssert.AreEqual(expected,actual);Current(adapter,oracle);
        }
        [Test] public void DamageABAAndStaleSnapshotCannotDebitRecoveredStamina()
        {
            using var adapter=new DiagnosticVitalsProjectionAdapter(new DiagnosticVitalsValues());var before=adapter.Read();
            Assert.IsTrue(adapter.PrepareDamageHealthAfter(90,out var p,out _));Commit(p);
            Assert.IsTrue(adapter.PrepareDamageHealthAfter(100,out p,out _));Commit(p);Assert.AreEqual(100,adapter.Read().Values.Health);Assert.Greater(adapter.Read().Stamp,before.Stamp);
            Assert.IsFalse(adapter.PrepareStaminaAfter(before,90,out _,out string reason));Assert.AreEqual("stale_or_invalid_stamina_debit",reason);
            var current=adapter.Read();Assert.IsTrue(adapter.PrepareStaminaAfter(current,90,out p,out _));Commit(p);Assert.AreEqual(90,adapter.Read().Values.Stamina);
            Assert.IsFalse(p.CommitStandalone(out _));
        }
        [Test] public void ForeignAdapterSnapshotWithEqualStampAndStaminaCannotAuthorizeDebit()
        {
            using var first=new DiagnosticVitalsProjectionAdapter(new DiagnosticVitalsValues(stamina:50,maxStamina:100,healthDrain:1));
            using var second=new DiagnosticVitalsProjectionAdapter(new DiagnosticVitalsValues(stamina:50,maxStamina:200,healthDrain:2));
            var foreign=first.Read();var own=second.Read();Assert.AreEqual(foreign.Stamp,own.Stamp);Assert.AreEqual(foreign.Values.Stamina,own.Values.Stamina);
            Assert.IsFalse(second.PrepareStaminaAfter(foreign,40,out _,out string reason));Assert.AreEqual("stale_or_invalid_stamina_debit",reason);
            Assert.AreEqual(50,second.Read().Values.Stamina);Assert.AreEqual(own.Stamp,second.Read().Stamp);
            Assert.IsTrue(second.PrepareStaminaAfter(own,40,out var p,out reason),reason);Commit(p);Assert.AreEqual(40,second.Read().Values.Stamina);
            Assert.IsFalse(first.PrepareStaminaAfter(null,40,out _,out _));
            Assert.IsEmpty(typeof(DiagnosticVitalsSnapshot).GetConstructors(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance));
            Assert.IsEmpty(typeof(DiagnosticVitalsSnapshot).GetConstructors(System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Where(c=>!c.IsPrivate).ToArray());
        }
        [Test] public void CancelledPreparationAndObserverFaultDoNotRollbackActualMutation()
        {
            using var adapter=new DiagnosticVitalsProjectionAdapter(new DiagnosticVitalsValues());var before=adapter.Read();
            Assert.IsTrue(adapter.PrepareDelta(-5,-10,0,0,out var p,out _));p.Dispose();Assert.AreEqual(before.Stamp,adapter.Read().Stamp);Assert.AreEqual(100,adapter.Read().Values.Health);
            adapter.DamageObserved+=(source,amount)=>throw new InvalidOperationException("observer");
            Assert.IsTrue(adapter.PrepareDelta(-5,-10,0,0,out p,out _));Commit(p);Assert.AreEqual(95,adapter.Read().Values.Health);Assert.AreEqual(90,adapter.Read().Values.Stamina);Assert.AreEqual("InvalidOperationException",adapter.LastObserverError);
        }
        [Test] public void DeferredObserverMayReenterOnlyAsANewCompletedMutation()
        {
            using var adapter=new DiagnosticVitalsProjectionAdapter(new DiagnosticVitalsValues());int events=0;
            adapter.DamageObserved+=(source,amount)=>{events++;Assert.AreEqual(95,adapter.Read().Values.Health);Assert.IsTrue(adapter.PrepareDelta(1,0,0,0,out var next,out _));Commit(next);};
            Assert.IsTrue(adapter.PrepareDelta(-5,0,0,0,out var p,out _));Commit(p);Assert.AreEqual(1,events);Assert.AreEqual(96,adapter.Read().Values.Health);
        }
        [Test] public void FixedProjectionPreparationCapacityFailureProducesNoLiveAdapter()
        {
            Assert.Throws<ArgumentException>(()=>new DiagnosticVitalsProjectionAdapter(new DiagnosticVitalsValues(),new ProjectionLimits(entries:20)));
            Assert.Throws<InvalidOperationException>(()=>new DiagnosticVitalsProjectionAdapter(new DiagnosticVitalsValues(),new ProjectionLimits(retainedUnits:1)));
        }
        [Test] public void NonfiniteInputsAndDisposeRefuseWithoutCaptureOrWrite()
        {
            Assert.Throws<ArgumentException>(()=>new DiagnosticVitalsValues(stamina:double.NaN));Assert.Throws<ArgumentException>(()=>new DiagnosticVitalsTickInput(fire:double.PositiveInfinity));
            var adapter=new DiagnosticVitalsProjectionAdapter(new DiagnosticVitalsValues());Assert.IsFalse(adapter.PrepareTick(double.NaN,new DiagnosticVitalsTickInput(),out _,out _));Assert.AreEqual(1UL,adapter.Read().Stamp);
            Assert.IsTrue(adapter.TryPinPartialDiagnostic(out var pin,out _));adapter.Dispose();Assert.IsFalse(pin.TryCreateCursor(out _,out _));pin.Dispose();
            Assert.IsFalse(adapter.PrepareDelta(-1,0,0,0,out _,out _));
        }
    }
}
