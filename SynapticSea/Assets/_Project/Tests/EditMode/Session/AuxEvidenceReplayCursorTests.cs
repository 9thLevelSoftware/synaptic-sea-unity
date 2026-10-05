using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public sealed class AuxEvidenceReplayCursorTests
    {
        static readonly string Origin = new string('a',64);
        static UntrustedAuxReplaySeed Seed(long steps=0) => new UntrustedAuxReplaySeed("run","actor","service",Origin,12,0,0,steps);
        static UntrustedAuxEvidenceStep S(long sequence,double progress,double eligible,double stamina=100)
        {
            double ratio=Math.Max(0,Math.Min(1,stamina/100)),speed=.35+.65*ratio,remaining=12-progress;
            double elapsed=Math.Min(.125,Math.Min(remaining/speed,stamina/8)),advance=Math.Min(remaining,elapsed*speed);
            return new UntrustedAuxEvidenceStep(sequence,.125,stamina,100,1,ratio,speed,remaining,elapsed,advance,Math.Max(0,stamina-8*elapsed),Math.Min(12,progress+advance),eligible+elapsed);
        }
        static GdDict StepDict(UntrustedAuxEvidenceStep s) => new GdDict {
            {"sequence",s.Sequence},{"delta_seconds",s.Delta},{"stamina_before",s.StaminaBefore},{"max_stamina",s.MaxStamina},
            {"wound_work_multiplier",s.Wound},{"ratio",s.Ratio},{"speed",s.Speed},{"remaining_before",s.Remaining},
            {"elapsed_seconds",s.Elapsed},{"delta_progress",s.DeltaProgress},{"stamina_after",s.StaminaAfter},
            {"progress_after",s.ProgressAfter},{"eligible_after",s.EligibleAfter} };
        static GdDict ChunkDict(UntrustedAuxEvidenceChunk c) => new GdDict {
            {"chunk_version",1L},{"ordinal",c.Ordinal},{"previous_chunk_digest",c.PreviousDigest},{"initial_step_sequence",c.InitialSequence},
            {"progress_before",c.ProgressBefore},{"eligible_before",c.EligibleBefore},{"accepted_steps_before",c.AcceptedBefore},
            {"steps",new GdArray(Enumerable.Range(0,c.Count).Select(i=>(object)StepDict(c.At(i))))},
            {"progress_after",c.ProgressAfter},{"eligible_after",c.EligibleAfter},{"accepted_steps_after",c.AcceptedAfter} };

        static UntrustedAuxEvidenceChunk C(int ordinal,string previous,UntrustedAuxEvidenceStep step,double progress,double eligible,long accepted)
        {
            var raw=new UntrustedAuxEvidenceChunk(ordinal,previous,Origin,step.Sequence,progress,eligible,accepted,step.ProgressAfter,step.EligibleAfter,accepted+1,new[]{step});
            return new UntrustedAuxEvidenceChunk(ordinal,previous,PaidHashContext.BitsV2.Hash(ChunkDict(raw)),step.Sequence,progress,eligible,accepted,step.ProgressAfter,step.EligibleAfter,accepted+1,new[]{step});
        }
        static AuxEvidenceReplayCursor Begin(params UntrustedAuxEvidenceChunk[] chunks)
        { Assert.IsTrue(AuxEvidenceReplayCursor.Begin(Seed(),chunks,out var cursor,out string reason),reason); return cursor; }
        static AuxReplayStatus DrainDigest(AuxEvidenceReplayCursor c,bool tiny=true)
        {
            int count=0;
            while(!c.DigestAwaitingFinalization)
            {
                var status=c.Step(0,tiny?1:1024,tiny?1:4096,tiny?1:4096,false);
                Assert.Less(++count,100000);
                if(status!=AuxReplayStatus.Pending) return status;
            }
            long output=c.DigestOutputBytes,hash=c.DigestHashBytes;
            var final=c.Step(0,0,0,128,true);
            Assert.AreEqual(output,c.DigestOutputBytes); Assert.AreEqual(hash,c.DigestHashBytes);
            return final;
        }
        [Test] public void SingleChunkYieldsThenMatchesUntrustedDigestAndRepeatsWithoutWork()
        {
            var a=S(1,0,0); var c=Begin(C(0,Origin,a,0,0,0));
            Assert.AreEqual(AuxReplayStatus.Pending,c.Step(0,0,0,0,false)); Assert.AreEqual(0,c.ReplayedStepCount);
            Assert.AreEqual(AuxReplayStatus.Pending,c.Step(1,0,0,0,false)); Assert.AreEqual(1,c.ReplayedStepCount);
            Assert.AreEqual(AuxReplayStatus.UntrustedResult,DrainDigest(c));
            Assert.IsTrue(c.ArithmeticResult.ArithmeticMatched); Assert.AreEqual(1,c.ArithmeticResult.MatchedChunkDigestCount); Assert.AreEqual(1,c.ArithmeticResult.TotalChunkCount);
            long bytes=c.DigestHashBytes; c.Step(256,1024,4096,4096,true); Assert.AreEqual(bytes,c.DigestHashBytes); Assert.AreEqual(1,c.ReplayedStepCount);
        }
        [Test] public void TwoChunksPermitRecoveryAndNoNextArithmeticOnDigestFinish()
        {
            var a=S(1,0,0,25); var first=C(0,Origin,a,0,0,0); var b=S(2,a.ProgressAfter,a.EligibleAfter,100); var second=C(1,first.Digest,b,a.ProgressAfter,a.EligibleAfter,1);
            var c=Begin(first,second); c.Step(1,0,0,0,false);
            Assert.AreEqual(AuxReplayStatus.Pending,DrainDigest(c,false)); Assert.AreEqual(1,c.ReplayedStepCount);
            Assert.AreEqual(AuxReplayStatus.Pending,c.Step(1,0,0,0,false)); Assert.AreEqual(2,c.ReplayedStepCount);
            Assert.AreEqual(AuxReplayStatus.UntrustedResult,DrainDigest(c,false)); Assert.AreEqual(2,c.ArithmeticResult.MatchedChunkDigestCount);
        }
        [Test] public void ActualSuppliedDigestMismatchRefuses()
        {
            var a=S(1,0,0); var good=C(0,Origin,a,0,0,0);
            var bad=new UntrustedAuxEvidenceChunk(0,Origin,Origin,1,0,0,0,a.ProgressAfter,a.EligibleAfter,1,new[]{a}); var c=Begin(bad); c.Step(1,0,0,0,false);
            Assert.AreEqual(AuxReplayStatus.Rejected,DrainDigest(c,false)); Assert.AreEqual("chunk_digest_mismatch",c.Reason); Assert.IsNull(c.ArithmeticResult);
        }
        [Test] public void CancelDuringDigestKeepsPrivateArithmeticAndStopsHashing()
        {
            var c=Begin(C(0,Origin,S(1,0,0),0,0,0)); c.Step(1,0,0,0,false); c.Step(0,1,1,1,false); long bytes=c.DigestOutputBytes;
            c.Cancel(); c.Cancel(); Assert.AreEqual(AuxReplayStatus.Rejected,c.Step(1,1024,4096,4096,true)); Assert.AreEqual(bytes,c.DigestOutputBytes); Assert.AreEqual(1,c.ReplayedStepCount); Assert.IsNull(c.ArithmeticResult);
        }
        [Test] public void EmptyProofMatchesZeroChunksWithoutOriginClaim()
        { var c=Begin(); Assert.AreEqual(AuxReplayStatus.UntrustedResult,c.Step(0,0,0,0,false)); Assert.AreEqual(0,c.ArithmeticResult.MatchedChunkDigestCount); Assert.AreEqual(0,c.ArithmeticResult.TotalChunkCount); }
        [Test] public void ArithmeticBitFlipRefusesBeforeHashAndAllBudgetsValidate()
        {
            var a=S(1,0,0); var bad=new UntrustedAuxEvidenceStep(1,a.Delta,a.StaminaBefore,a.MaxStamina,a.Wound,a.Ratio,a.Speed,a.Remaining,a.Elapsed,a.DeltaProgress,a.StaminaAfter,BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(a.ProgressAfter)+1),a.EligibleAfter);
            var c=Begin(C(0,Origin,bad,0,0,0)); Assert.AreEqual(AuxReplayStatus.Rejected,c.Step(1,0,0,0,false)); Assert.AreEqual("arithmetic_mismatch",c.Reason); Assert.AreEqual(0,c.DigestOutputBytes);
            c=Begin(C(0,Origin,a,0,0,0)); Assert.AreEqual(AuxReplayStatus.Rejected,c.Step(0,-1,0,0,false)); Assert.AreEqual("invalid_budget",c.Reason);
        }
        [Test] public void ImmutableEvidenceArraysCannotChangePendingHash()
        {
            var a=S(1,0,0); var raw=new[]{a}; var good=C(0,Origin,a,0,0,0);
            var chunk=new UntrustedAuxEvidenceChunk(0,Origin,good.Digest,1,0,0,0,a.ProgressAfter,a.EligibleAfter,1,raw);
            var array=new[]{chunk}; var c=Begin(array); raw[0]=default; array[0]=null;
            c.Step(1,0,0,0,false); Assert.AreEqual(AuxReplayStatus.UntrustedResult,DrainDigest(c));
        }
        [Test] public void SignedZeroHeaderRemainsExact()
        {
            var a=S(1,0,0); var chunk=new UntrustedAuxEvidenceChunk(0,Origin,Origin,1,BitConverter.Int64BitsToDouble(long.MinValue),0,0,a.ProgressAfter,a.EligibleAfter,1,new[]{a});
            var c=Begin(chunk); Assert.AreEqual(AuxReplayStatus.Rejected,c.Step(1,0,0,0,false)); Assert.AreEqual("chunk_continuity",c.Reason);
        }
        [Test] public void ConjunctiveCodecCapacityStillRefuses()
        {
            var steps=Enumerable.Range(0,256).Select(i=>S(i+1,0,0)).ToArray();
            var chunk=new UntrustedAuxEvidenceChunk(0,Origin,Origin,1,0,0,0,.125,.125,256,steps);
            Assert.IsFalse(AuxEvidenceReplayCursor.Begin(Seed(),Enumerable.Repeat(chunk,16).ToArray(),out _,out string reason)); Assert.AreEqual("codec_capacity_bound",reason);
        }
        static UntrustedAuxEvidenceChunk Exact(int ordinal,string previous,UntrustedAuxEvidenceStep[] steps,double before=0,double eligible=0,long count=0)
        {
            var last=steps[steps.Length-1];
            var raw=new UntrustedAuxEvidenceChunk(ordinal,previous,Origin,count+1,before,eligible,count,last.ProgressAfter,last.EligibleAfter,count+steps.Length,steps);
            return new UntrustedAuxEvidenceChunk(ordinal,previous,PaidHashContext.BitsV2.Hash(ChunkDict(raw)),count+1,before,eligible,count,last.ProgressAfter,last.EligibleAfter,count+steps.Length,steps);
        }
        [Test] public void MultipleStepsResumeOnceWhileDigestRemainsPending()
        {
            var a=S(1,0,0); var b=S(2,a.ProgressAfter,a.EligibleAfter,100); var c=Begin(Exact(0,Origin,new[]{a,b}));
            Assert.AreEqual(AuxReplayStatus.Pending,c.Step(1,0,0,0,false)); Assert.AreEqual(1,c.ReplayedStepCount);
            Assert.AreEqual(AuxReplayStatus.Pending,c.Step(1,0,0,0,false)); Assert.AreEqual(2,c.ReplayedStepCount);
            c.Step(256,1,1,1,false); Assert.AreEqual(2,c.ReplayedStepCount);
            Assert.AreEqual(AuxReplayStatus.UntrustedResult,DrainDigest(c)); Assert.AreEqual(2,c.ReplayedStepCount);
        }
        [TestCase("A",64)][TestCase("a",63)][TestCase("g",64)]
        public void MalformedSeedAndChunkDigestRefuse(string character,int length)
        {
            string bad=new string(character[0],length); var seed=new UntrustedAuxReplaySeed("run","actor","service",bad,12,0,0,0);
            Assert.IsFalse(AuxEvidenceReplayCursor.Begin(seed,Array.Empty<UntrustedAuxEvidenceChunk>(),out _,out string reason)); Assert.AreEqual("invalid_seed",reason);
            var a=S(1,0,0); var chunk=new UntrustedAuxEvidenceChunk(0,Origin,bad,1,0,0,0,a.ProgressAfter,a.EligibleAfter,1,new[]{a});
            Assert.IsFalse(AuxEvidenceReplayCursor.Begin(Seed(),new[]{chunk},out _,out reason)); Assert.AreEqual("chunk_bound",reason);
        }
        [TestCase(257,false)][TestCase(256,true)]
        public void OversizedAndWhitespaceIdentifiersRefuse(int length,bool whitespace)
        {
            var seed=new UntrustedAuxReplaySeed(new string(whitespace?' ':'x',length),"actor","service",Origin,12,0,0,0);
            Assert.IsFalse(AuxEvidenceReplayCursor.Begin(seed,Array.Empty<UntrustedAuxEvidenceChunk>(),out _,out string reason)); Assert.AreEqual("invalid_seed",reason);
        }
        [Test] public void ActualReorderedStepsRefuseBeforeHash()
        {
            var a=S(1,0,0); var b=S(2,a.ProgressAfter,a.EligibleAfter); var c=Begin(Exact(0,Origin,new[]{b,a}));
            Assert.AreEqual(AuxReplayStatus.Rejected,c.Step(1,0,0,0,false)); Assert.AreEqual("step_sequence",c.Reason); Assert.AreEqual(0,c.DigestOutputBytes);
        }
        [TestCase(true)][TestCase(false)]
        public void SecondChunkBadLinkOrHeaderRefuses(bool badLink)
        {
            var a=S(1,0,0); var first=C(0,Origin,a,0,0,0); var b=S(2,a.ProgressAfter,a.EligibleAfter);
            var second=Exact(1,badLink?Origin:first.Digest,new[]{b},badLink?a.ProgressAfter:0,a.EligibleAfter,1);
            var c=Begin(first,second); c.Step(1,0,0,0,false); Assert.AreEqual(AuxReplayStatus.Pending,DrainDigest(c,false));
            Assert.AreEqual(AuxReplayStatus.Rejected,c.Step(1,0,0,0,false)); Assert.AreEqual("chunk_continuity",c.Reason); Assert.AreEqual(1,c.ReplayedStepCount);
        }
        [TestCase(double.NaN)][TestCase(double.PositiveInfinity)]
        public void NonfiniteStepInputRefuses(double value)
        {
            var a=S(1,0,0); var bad=new UntrustedAuxEvidenceStep(1,value,a.StaminaBefore,a.MaxStamina,a.Wound,a.Ratio,a.Speed,a.Remaining,a.Elapsed,a.DeltaProgress,a.StaminaAfter,a.ProgressAfter,a.EligibleAfter);
            // Cannot generate a valid digest for nonfinite data; shape digest is sufficient for prehash refusal.
            var chunk=new UntrustedAuxEvidenceChunk(0,Origin,Origin,1,0,0,0,a.ProgressAfter,a.EligibleAfter,1,new[]{bad});
            var c=Begin(chunk); Assert.AreEqual(AuxReplayStatus.Rejected,c.Step(1,0,0,0,false)); Assert.AreEqual("invalid_step_input",c.Reason); Assert.AreEqual(0,c.DigestOutputBytes);
        }
        [TestCase(-1,0,0,0)][TestCase(257,0,0,0)][TestCase(0,-1,0,0)][TestCase(0,1025,0,0)]
        [TestCase(0,0,-1,0)][TestCase(0,0,4097,0)][TestCase(0,0,0,-1)][TestCase(0,0,0,4097)]
        public void EveryInvalidBudgetDimensionRefusesWithoutWork(int steps,int tokens,int output,int hash)
        {
            var c=Begin(C(0,Origin,S(1,0,0),0,0,0)); Assert.AreEqual(AuxReplayStatus.Rejected,c.Step(steps,tokens,output,hash,false));
            Assert.AreEqual("invalid_budget",c.Reason); Assert.AreEqual(0,c.ReplayedStepCount); Assert.AreEqual(0,c.DigestOutputBytes); Assert.AreEqual(0,c.DigestHashBytes);
        }
        [Test] public void SequenceOverflowAndHeaderReorderRefuse()
        {
            var a=S(long.MaxValue,0,0); var chunk=new UntrustedAuxEvidenceChunk(0,Origin,Origin,long.MaxValue,0,0,long.MaxValue,a.ProgressAfter,a.EligibleAfter,long.MaxValue,new[]{a});
            Assert.IsTrue(AuxEvidenceReplayCursor.Begin(Seed(long.MaxValue),new[]{chunk},out var c,out _)); Assert.AreEqual(AuxReplayStatus.Rejected,c.Step(1,0,0,0,false)); Assert.AreEqual("sequence_overflow",c.Reason);
            c=Begin(C(1,Origin,S(1,0,0),0,0,0)); Assert.AreEqual(AuxReplayStatus.Rejected,c.Step(1,0,0,0,false)); Assert.AreEqual("chunk_continuity",c.Reason);
        }
    }
}
