using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public sealed class AuxReplayCodecBudgetTests
    {
        static readonly string Digest=new string('a',64);
        static GdDict SeedDict(UntrustedAuxReplaySeed s)=>new GdDict{{"run",s.Run},{"actor",s.Actor},{"service",s.Service},{"origin_digest",s.OriginDigest},{"duration",s.Duration},{"progress",s.Progress},{"eligible",s.Eligible},{"accepted_steps",s.AcceptedSteps}};
        static GdDict StepDict(UntrustedAuxEvidenceStep s)=>new GdDict{{"sequence",s.Sequence},{"delta_seconds",s.Delta},{"stamina_before",s.StaminaBefore},{"max_stamina",s.MaxStamina},{"wound_work_multiplier",s.Wound},{"ratio",s.Ratio},{"speed",s.Speed},{"remaining_before",s.Remaining},{"elapsed_seconds",s.Elapsed},{"delta_progress",s.DeltaProgress},{"stamina_after",s.StaminaAfter},{"progress_after",s.ProgressAfter},{"eligible_after",s.EligibleAfter}};
        static GdDict ChunkDict(UntrustedAuxEvidenceChunk c,bool digest)
        {
            var steps=new GdArray();for(int i=0;i<c.Count;i++)steps.Add(StepDict(c.At(i)));
            var d=new GdDict{{"chunk_version",1L},{"ordinal",c.Ordinal},{"previous_chunk_digest",c.PreviousDigest},{"initial_step_sequence",c.InitialSequence},{"progress_before",c.ProgressBefore},{"eligible_before",c.EligibleBefore},{"accepted_steps_before",c.AcceptedBefore},{"steps",steps},{"progress_after",c.ProgressAfter},{"eligible_after",c.EligibleAfter},{"accepted_steps_after",c.AcceptedAfter}};
            if(digest)d["digest"]=c.Digest;return d;
        }
        static long Walk(object value,int depth,out int maximum)
        {
            long n=1;maximum=depth;
            if(value is GdDict d)foreach(var pair in d){n+=Walk(pair.Key,depth+1,out int a);n+=Walk(pair.Value,depth+1,out int b);maximum=Math.Max(maximum,Math.Max(a,b));}
            else if(value is GdArray arr)foreach(var item in arr){n+=Walk(item,depth+1,out int m);maximum=Math.Max(maximum,m);}
            return n;
        }
        static void Golden(UntrustedAuxReplaySeed seed,UntrustedAuxEvidenceChunk[] chunks)
        {
            Assert.IsTrue(AuxReplayCodecBudget.TryMeasure(seed,chunks,out var r));
            var array=new GdArray();foreach(var c in chunks)array.Add(ChunkDict(c,true));
            var raw=new GdDict{{"seed",SeedDict(seed)},{"chunks",array}};
            Assert.AreEqual(Walk(raw,0,out _),r.SourceNodes);
            var encoded=ComponentDomainCodec.Encode(raw,ComponentDomainCodec.BitExactSchema);
            Assert.AreEqual(Walk(encoded,0,out int depth),r.WireNodes);Assert.AreEqual(depth,r.WireDepth);
            Assert.AreEqual(new UTF8Encoding(false,true).GetByteCount(SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(encoded)),r.Utf8Bytes);
            Assert.AreEqual(r.Steps,r.InspectedStepRecords);
        }
        [TestCase(0)][TestCase(1)][TestCase(256)] public void RealCodecGoldenDistinguishesHashWrapper(int count)
        {
            var seed=new UntrustedAuxReplaySeed("x\"\\\n\u0001é😀","actor","service",Digest,12,0,0,long.MinValue);
            var chunks=new List<UntrustedAuxEvidenceChunk>();
            if(count>0){var ss=new UntrustedAuxEvidenceStep[count];for(int i=0;i<count;i++)ss[i]=new UntrustedAuxEvidenceStep(long.MinValue+i,.01,100,100,1,1,1,12,.01,.01,99.92,.01,.01);chunks.Add(new UntrustedAuxEvidenceChunk(0,Digest,Digest,long.MinValue,0,0,long.MinValue,.01,.01,long.MaxValue,ss));}
            Golden(seed,chunks.ToArray());
            if(count>0){Assert.AreEqual(23+27*count,Walk(ChunkDict(chunks[0],false),0,out _));Assert.AreEqual(25+27*count,Walk(new GdDict{{"value",ChunkDict(chunks[0],false)}},0,out _));}
        }
        [TestCase(8)][TestCase(12)] public void RepeatedSixtyHzLonghaulKeepsExactTraceAndIncrementalCounts(int duration)
        {
            for(int job=0;job<2;job++)
            {
                var seed=new UntrustedAuxReplaySeed("run","actor","service",Digest,duration,0,0,0);
                var chunks=new List<UntrustedAuxEvidenceChunk>();var active=new List<UntrustedAuxEvidenceStep>();
                double progress=0,eligible=0,before=0,beforeEligible=0;long count=0,beforeCount=0;string previous=Digest;
                // Exercise the installed runtime kernel. Each retained256-record segment is exported
                // before immutable snapshot continuation; no dependency on the separately owned journal patch.
                var runtime=new AuxiliaryWorkRuntime("owner","run","actor","service",0,PaidHashContext.BitsV2.Algorithm,duration,0,0,100,0,256);
                Assert.IsTrue(AuxReplayCodecBudget.TrySeed(seed,out var incremental));
                while(progress<duration)
                {
                    if(active.Count==0)Assert.IsTrue(AuxReplayCodecBudget.TryAppendReservedHeader(incremental,chunks.Count,count+1,count,out incremental));
                    var result=runtime.Step(new AuxiliaryWorkFrame(1.0/60,100,100,1));
                    Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted,result.Status);var accepted=result.AcceptedStep;
                    var step=new UntrustedAuxEvidenceStep(accepted.Sequence,accepted.RequestedDelta,accepted.StaminaBefore,accepted.MaxStamina,accepted.WoundSpeed,
                        Math.Max(0,Math.Min(1,accepted.StaminaBefore/Math.Max(1,accepted.MaxStamina))),accepted.Speed,duration-accepted.ProgressBefore,
                        accepted.ElapsedSeconds,accepted.DeltaSeconds,accepted.StaminaAfter,accepted.ProgressAfter,accepted.EligibleAfter);
                    count=accepted.Sequence;
                    Assert.IsTrue(AuxReplayCodecBudget.TryAppendStep(incremental,count,active.Count,out incremental));active.Add(step);progress=step.ProgressAfter;eligible=step.EligibleAfter;
                    long inspected=incremental.InspectedStepRecords;for(int r=0;r<3;r++)Assert.AreEqual(inspected,incremental.InspectedStepRecords);
                    if(active.Count==256||progress==duration)
                    {
                        var raw=new UntrustedAuxEvidenceChunk(chunks.Count,previous,Digest,beforeCount+1,before,beforeEligible,beforeCount,progress,eligible,count,active.ToArray());
                        string hash=PaidHashContext.BitsV2.Hash(ChunkDict(raw,false));
                        chunks.Add(new UntrustedAuxEvidenceChunk(chunks.Count,previous,hash,beforeCount+1,before,beforeEligible,beforeCount,progress,eligible,count,active.ToArray()));
                        Assert.AreEqual(active.Count,runtime.CopyAcceptedSteps().Length);
                        var snapshot=runtime.Snapshot();Assert.AreEqual(count,snapshot.EligibleSteps);
                        previous=hash;before=progress;beforeEligible=eligible;beforeCount=count;active.Clear();
                        if(progress<duration)runtime=new AuxiliaryWorkRuntime("owner","run","actor","service",0,PaidHashContext.BitsV2.Algorithm,duration,
                            snapshot.ProgressSeconds,snapshot.EligibleSeconds,snapshot.LastAcceptedStamina,snapshot.EligibleSteps,256);
                    }
                    Assert.LessOrEqual(count,duration*60+1);
                }
                Assert.GreaterOrEqual(count,duration*60);Assert.AreEqual(count,incremental.InspectedStepRecords);Assert.IsTrue(incremental.ConservativeBytes);
                Assert.IsTrue(AuxReplayCodecBudget.TryMeasure(seed,chunks.ToArray(),out var exact));
                Assert.AreEqual(exact.SourceNodes,incremental.SourceNodes);Assert.AreEqual(exact.WireNodes,incremental.WireNodes);
                Assert.GreaterOrEqual(incremental.Utf8Bytes,exact.Utf8Bytes);Assert.AreEqual(exact.WireDepth,incremental.WireDepth);
                Golden(seed,chunks.ToArray());Assert.IsTrue(AuxEvidenceReplayCursor.Begin(seed,chunks.ToArray(),out var cursor,out _));
                AuxReplayStatus status=AuxReplayStatus.Pending;for(int turn=0;turn<100000&&status==AuxReplayStatus.Pending;turn++)status=cursor.Step(16,1024,4096,4096,true);
                Assert.AreEqual(AuxReplayStatus.UntrustedResult,status);Assert.AreEqual(count,cursor.ReplayedStepCount);Assert.AreEqual(duration,cursor.ArithmeticResult.Progress);
            }
        }
        [Test] public void StrictIdentifierAndEveryConjunctiveLimitRefuses()
        {
            foreach(string id in new[]{"\ud800","\udc00",new string('x',257)})Assert.IsFalse(AuxReplayCodecBudget.TrySeed(new UntrustedAuxReplaySeed(id,"actor","service",Digest,12,0,0,0),out _));
            Assert.IsTrue(AuxReplayCodecBudget.TrySeed(new UntrustedAuxReplaySeed(new string('x',256),"actor","service",Digest,12,0,0,0),out _));
            var cap=new AuxReplayBudgetReport(100000,549999,4L*1024*1024,256,65536,0,386);Assert.IsTrue(AuxReplayCodecBudget.Fits(cap));
            Assert.IsFalse(AuxReplayCodecBudget.Fits(new AuxReplayBudgetReport(100001,0,0,0,0,0,0)));
            Assert.IsFalse(AuxReplayCodecBudget.Fits(new AuxReplayBudgetReport(0,550000,0,0,0,0,0)));
            Assert.IsFalse(AuxReplayCodecBudget.Fits(new AuxReplayBudgetReport(0,0,4L*1024*1024+1,0,0,0,0)));
            Assert.IsFalse(AuxReplayCodecBudget.Fits(new AuxReplayBudgetReport(0,0,0,0,0,0,387)));
            Assert.IsFalse(AuxReplayCodecBudget.Fits(new AuxReplayBudgetReport(0,0,0,257,0,0,0)));
            Assert.IsFalse(AuxReplayCodecBudget.Fits(new AuxReplayBudgetReport(0,0,0,0,65537,0,0)));
            Assert.IsFalse(AuxReplayCodecBudget.Fits(new AuxReplayBudgetReport(0,0,0,2,1,0,0),1,1));
            Assert.IsFalse(AuxReplayCodecBudget.Fits(new AuxReplayBudgetReport(0,0,0,1,2,0,0),1,1));
            Assert.IsFalse(AuxReplayCodecBudget.TryAppendHeader(new AuxReplayBudgetReport(long.MaxValue,0,0,0,0,0,0),0,1,0,1,out _));
        }
    }
}
