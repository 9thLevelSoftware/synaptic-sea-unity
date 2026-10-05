using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    [NonParallelizable]
    public class AuxiliaryActualPairTests
    {
        IResourceReader _before;
        [SetUp] public void Setup() { _before = ResourceAuthorityPublication.Reader; Publish(); }
        [TearDown] public void Cleanup() => ResourceAuthorityPublication.ReplaceReader(_before);
        static void Publish() => ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(new Dictionary<string,string>()));
        sealed class Rig : IDisposable
        {
            internal readonly DiagnosticVitalsProjectionAdapter Vitals = new DiagnosticVitalsProjectionAdapter(new DiagnosticVitalsValues());
            internal readonly InventoryState Inventory = InventoryState.CreateTracked(new GdDict());
            internal readonly DiagnosticAuxiliaryPairContext Context;
            internal readonly ResourceAuthorityLease Lease;
            internal readonly AuxiliaryWorkRuntime Runtime;
            internal Rig(int slots = 64, int maximumSteps = 2000)
            {
                Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out Lease, out _));
                var seed = new UntrustedAuxReplaySeed("run", "actor", "utility", new string('a',64), 12,0,0,0);
                Context = new DiagnosticAuxiliaryPairContext(0, Frame(), Inventory, Lease);
                Runtime = AuxiliaryWorkRuntime.CreateActualVitalsPaired("home",0,PaidHashContext.BitsV2.Algorithm,
                    seed,slots,new AuxiliaryEvidenceLimits(32,maximumSteps,2),Vitals,Context);
            }
            internal AuxiliaryWorkFrame Frame(double delta = 1.0/60)
            { var values = Vitals.Read().Values; return new AuxiliaryWorkFrame(delta,values.Stamina,values.MaxStamina,1); }
            internal void Next(long ordinal) => Context.Replace(ordinal,Frame(),Inventory,Lease);
            internal AuxiliaryWorkRuntime.PreparedActualPair Prepare()
            { Assert.IsTrue(Runtime.TryPrepareActualPair(out var plan,out _,out string reason),reason); return plan; }
            internal bool Install(AuxiliaryWorkRuntime.PreparedActualPair plan)
            { using(var attempt=CommonParticipantGate.BeginAttempt()) return plan.TryInstallUnderGate(Runtime,attempt,out _); }
            internal void WorldTick(double delta = 1.0/60)
            { Assert.IsTrue(Vitals.PrepareTick(delta,new DiagnosticVitalsTickInput(moving:false),out var tick,out string reason),reason);using(tick)Assert.IsTrue(tick.CommitStandalone(out reason),reason); }
            public void Dispose() => Vitals.Dispose();
        }
        static void ExactProjection(DiagnosticVitalsProjectionAdapter vitals)
        {
            var expected = vitals.Read().Values.ExactScratch().GetSummary();
            Assert.IsTrue(vitals.TryPinPartialDiagnostic(out var pin,out string reason),reason);
            using(pin)
            {
                Assert.IsTrue(pin.TryCreateCursor(out var cursor,out reason),reason);
                using(cursor)
                {
                    while(cursor.Status==ProjectionCursorStatus.Pending)cursor.Advance(8);
                    Assert.AreEqual(ProjectionCursorStatus.Complete,cursor.Status,cursor.Reason);
                    using(var result=cursor.Result)
                    { Assert.AreEqual(21,result.Node(0).EntryCount);int i=0;foreach(var row in expected){var entry=result.Entry(0,i++);Assert.AreEqual(row.Key,entry.Key.ToNormalized());Assert.IsTrue(ProjectionScalar.FromNormalized(row.Value).Equals(entry.Value.Scalar));} }
                }
            }
        }
        [Test] public void ActualWorldRecoveryThenWorkPublishesOneCoherentPairAndExactProjection()
        {
            using var rig=new Rig();rig.WorldTick();rig.Next(0);var before=rig.Vitals.Read().Values;
            using var plan=rig.Prepare();Assert.IsTrue(rig.Install(plan));plan.NotifyAfterGate();
            var after=rig.Runtime.CaptureActualPair();Assert.AreEqual(plan.Result.StaminaAfter,after.Vitals.Values.Stamina);
            Assert.AreEqual(plan.Result.Snapshot.ProgressSeconds,after.Work.ProgressSeconds);
            for(int i=0;i<14;i++)if(i!=2)Assert.AreEqual(before.At(i),after.Vitals.Values.At(i),"unrelated actual field "+i);
            Assert.AreEqual(1,after.AcceptedSteps);Assert.AreEqual(1,after.Budget.Steps);ExactProjection(rig.Vitals);
            Assert.AreEqual("paired_step_only",rig.Runtime.Step(rig.Frame()).Reason);
            Assert.IsFalse(rig.Install(plan));
        }
        [TestCase("context")][TestCase("inventory")][TestCase("resource")][TestCase("cancelled_adapter")]
        public void GuardRefusalLeavesActualVitalsRuntimeEvidenceAndBudgetUnchanged(string stale)
        {
            using var rig=new Rig();using var plan=rig.Prepare();
            if(stale=="context")rig.Next(1);
            if(stale=="inventory")rig.Inventory.BonusCapacity=rig.Inventory.BonusCapacity;
            if(stale=="resource")Publish();
            if(stale=="cancelled_adapter")plan.Dispose();
            var before=rig.Runtime.CaptureActualPair();Assert.IsFalse(rig.Install(plan));var after=rig.Runtime.CaptureActualPair();
            Assert.AreEqual(before.Vitals.Stamp,after.Vitals.Stamp);for(int i=0;i<14;i++)Assert.AreEqual(before.Vitals.Values.At(i),after.Vitals.Values.At(i));
            Assert.AreEqual(before.Work.ProgressSeconds,after.Work.ProgressSeconds);Assert.AreEqual(before.DirtyRevision,after.DirtyRevision);
            Assert.AreEqual(before.AcceptedSteps,after.AcceptedSteps);Assert.AreEqual(before.Budget.SourceNodes,after.Budget.SourceNodes);
            Assert.AreEqual(0,rig.Runtime.CopyAcceptedSteps().Length);
        }
        [TestCase(1,2000,"step_log_full")][TestCase(64,1,"complete_history_capacity")]
        public void CapacityIsReservedBeforeActualDebit(int slots,int maximum,string refusal)
        {
            using var rig=new Rig(slots,maximum);using(var first=rig.Prepare())Assert.IsTrue(rig.Install(first));rig.Next(1);
            var before=rig.Runtime.CaptureActualPair();Assert.IsFalse(rig.Runtime.TryPrepareActualPair(out _,out _,out string reason));Assert.AreEqual(refusal,reason);
            var after=rig.Runtime.CaptureActualPair();Assert.AreEqual(before.Vitals.Stamp,after.Vitals.Stamp);Assert.AreEqual(before.Vitals.Values.Stamina,after.Vitals.Values.Stamina);
            Assert.AreEqual(before.AcceptedSteps,after.AcceptedSteps);Assert.AreEqual(before.Budget.Steps,after.Budget.Steps);
        }
        [Test] public void OneProducerKeeps720PlusRemainderStepsAcrossRealCustodyWithoutFalseLegacyCap()
        {
            using var rig=new Rig();int count=0,chunks=0;long ordinal=0;
            while(rig.Runtime.Snapshot().ProgressSeconds<12 && count<1400)
            {
                rig.WorldTick();rig.Next(ordinal++);
                using(var plan=rig.Prepare())Assert.IsTrue(rig.Install(plan));count++;
                if(rig.Runtime.RetainedStepCount==rig.Runtime.Capacity)Seal(rig,ref chunks);
            }
            Assert.AreEqual(12,rig.Runtime.Snapshot().ProgressSeconds);Assert.Greater(count,720);
            if(rig.Runtime.RetainedStepCount>0)Seal(rig,ref chunks);
            Assert.AreEqual(count,rig.Runtime.EvidenceJournal.AcceptedStepCount);
            var snapshot=rig.Runtime.CaptureActualPair();Assert.AreEqual(count,snapshot.Budget.Steps);Assert.AreEqual(chunks,snapshot.Budget.Chunks);
            int retained=0;long sequence=0;
            for(int c=0;c<chunks;c++){var chunk=rig.Runtime.EvidenceJournal.ReadRetainedChunk(c);for(int i=0;i<chunk.Count;i++){Assert.AreEqual(++sequence,chunk.At(i).Sequence);retained++;}}
            Assert.AreEqual(count,retained);ExactProjection(rig.Vitals);
        }
        static void Seal(Rig rig,ref int chunks)
        {
            Assert.IsTrue(rig.Runtime.TryPrepareEvidenceRotation(out var rotation,out string reason),reason);Assert.IsTrue(rig.Runtime.TryInstallEvidenceRotation(rotation));
            var chunk=rig.Runtime.PendingEvidence;
            Assert.AreEqual(AuxiliaryCustodyStatus.Accepted,rig.Runtime.EvidenceJournal.TryTakeCustody(chunk,out var ack));Assert.IsTrue(rig.Runtime.TryAcknowledgeEvidenceCustody(ack));
            Assert.IsTrue(rig.Runtime.EvidenceJournal.TryAcquireQueuedChunk(out var queued));Assert.AreSame(chunk,queued);chunks++;
        }
        [Test] public void ActualHealthDeathRefusesWorkWithoutSyntheticAliveOverride()
        {
            using var rig=new Rig();Assert.IsTrue(rig.Vitals.PrepareDamageHealthAfter(0,out var damage,out string reason),reason);
            using(damage)Assert.IsTrue(damage.CommitStandalone(out reason),reason);rig.Next(0);
            var before=rig.Runtime.CaptureActualPair();Assert.IsFalse(rig.Runtime.TryPrepareActualPair(out _,out _,out reason));
            Assert.AreEqual("dead_actual_vitals",reason);var after=rig.Runtime.CaptureActualPair();
            Assert.AreEqual(0,after.Vitals.Values.Health);Assert.AreEqual(before.Vitals.Stamp,after.Vitals.Stamp);
            Assert.AreEqual(before.Vitals.Values.Stamina,after.Vitals.Values.Stamina);Assert.AreEqual(0,after.AcceptedSteps);
            rig.Context.Replace(0,rig.Frame(double.NaN),rig.Inventory,rig.Lease);
            Assert.IsFalse(rig.Runtime.TryPrepareActualPair(out _,out var invalid,out reason));
            Assert.AreEqual(AuxiliaryWorkStepStatus.IgnoredDelta,invalid.Status);Assert.AreEqual("",reason);
            Assert.AreEqual(before.Vitals.Stamp,rig.Runtime.CaptureActualPair().Vitals.Stamp);
        }
        [Test] public void UnrelatedCallerCannotMintOrForgeJournalCountAssignment()
        {
            using var rig=new Rig();var before=rig.Runtime.CaptureActualPair();
            using(var attempt=CommonParticipantGate.BeginAttempt())
            {
                Assert.Throws<InvalidOperationException>(()=>rig.Runtime.EvidenceJournal.PrepareActualCountUnderGate(new object(),before.Budget,0,0));
                Assert.Throws<InvalidOperationException>(()=>new AuxiliaryEvidenceJournal.PreparedActualCount(rig.Runtime.EvidenceJournal,new object(),new object(),before.Budget,0,0));
            }
            Assert.AreEqual(0,rig.Runtime.EvidenceJournal.AcceptedStepCount);Assert.AreEqual(before.Vitals.Stamp,rig.Vitals.Read().Stamp);
            using var plan=rig.Prepare();Assert.IsTrue(rig.Install(plan));Assert.AreEqual(1,rig.Runtime.EvidenceJournal.AcceptedStepCount);
        }
        [Test] public void ForeignClosedAndWrongThreadPlanRefuseBeforeAnyActualWrite()
        {
            using var first=new Rig();using var other=new Rig();using var plan=first.Prepare();
            using(var attempt=CommonParticipantGate.BeginAttempt())Assert.IsFalse(plan.TryInstallUnderGate(other.Runtime,attempt,out _));
            var closed=CommonParticipantGate.BeginAttempt();closed.Dispose();using(var attempt=CommonParticipantGate.BeginAttempt())Assert.IsFalse(plan.TryInstallUnderGate(first.Runtime,closed,out _));
            Exception error=null;var worker=new Thread(()=>{try{first.Runtime.TryPrepareActualPair(out _,out _,out _);}catch(Exception e){error=e;}});worker.Start();worker.Join();Assert.IsInstanceOf<InvalidOperationException>(error);
            Assert.IsTrue(first.Install(plan));Assert.AreEqual(1,first.Runtime.CaptureActualPair().AcceptedSteps);
        }
    }
}
