using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public class UntrustedAuxChunkDigestCursorTests
    {
        static UntrustedAuxEvidenceStep S(long sequence = 1, double progress = .125)
            => new UntrustedAuxEvidenceStep(sequence, .125, 100, 100, 1, 1, 1, 12, .125, .125, 99, progress, .125);
        static UntrustedAuxEvidenceChunk C(UntrustedAuxEvidenceStep[] steps, double before = 0)
            => new UntrustedAuxEvidenceChunk(long.MaxValue, new string('a', 64), new string('b', 64), long.MinValue, before, 0,
                long.MinValue, .125, .125, long.MaxValue, steps);
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
        static string Key(object key) => PaidSnapshotCodec.Stringify(ComponentDomainCodec.Encode(new GdDict{{"key",key}},ComponentDomainCodec.BitExactSchema),PaidSnapshotCodec.Policy.TypedOwner);
        static object Sorted(object value)
        {
            if(value is GdDict d) { var r=new GdDict(); foreach(var row in d.OrderBy(p=>Key(p.Key),StringComparer.Ordinal)) r[row.Key]=Sorted(row.Value); return r; }
            if(value is GdArray a) return new GdArray(a.Select(Sorted)); return value;
        }
        static UntrustedChunkDigestResult Drain(UntrustedAuxEvidenceChunk c,int tokens,int output,int hash)
        {
            Assert.IsTrue(UntrustedAuxChunkDigestCursor.Begin(c,out var cursor,out string reason),reason);
            using(cursor)
            {
                Assert.AreEqual(DigestCursorStatus.Pending,cursor.Step(0,0,0,false)); Assert.AreEqual(0,cursor.OutputBytes);
                int calls=0;
                while(!cursor.AwaitingFinalization)
                {
                    var status=cursor.Step(tokens,output,hash,false);
                    Assert.AreEqual(DigestCursorStatus.Pending,status,cursor.Reason);
                    Assert.Less(++calls,1000000);
                }
                long payload=cursor.OutputBytes;
                Assert.AreEqual(payload,cursor.HashedBytes);
                Assert.AreEqual(DigestCursorStatus.UntrustedResult,cursor.Step(0,0,128,true));
                Assert.AreEqual(payload,cursor.OutputBytes); Assert.AreEqual(payload,cursor.HashedBytes);
                Assert.AreEqual(DigestCursorStatus.UntrustedResult,cursor.Step(0,0,0,false));
                Assert.AreEqual(payload,cursor.OutputBytes); Assert.AreEqual(payload,cursor.HashedBytes);
                Assert.AreEqual(cursor.OutputBytes,cursor.HashedBytes); return cursor.Result;
            }
        }
        [TestCase(1,1,1)][TestCase(7,13,19)][TestCase(1024,4096,4096)]
        public void ByteAndDigestDifferentialMatchesActualProduction(int tokens,int output,int hash)
        {
            var c=C(new[]{S()}); var dict=ChunkDict(c);
            var envelope=ComponentDomainCodec.Encode(new GdDict{{"value",Sorted(dict)}},ComponentDomainCodec.BitExactSchema);
            string bytes=PaidSnapshotCodec.Stringify(envelope,PaidSnapshotCodec.Policy.TypedOwner);
            Assert.AreEqual(bytes,string.Concat(UntrustedAuxChunkDigestCursor.DiagnosticTokens(c)));
            StringAssert.StartsWith("{\"schema\":\"component_domain_codec_v2\",\"value\":[\"dictionary\",[[[\"text\",\"value\"],[\"dictionary\",[",bytes);
            StringAssert.EndsWith("]]]]]}",bytes);
            var result=Drain(c,tokens,output,hash); Assert.AreEqual(PaidHashContext.BitsV2.Hash(dict),result.Digest);
            Assert.AreEqual("63655aa39107dd2f9015d3ef19f037ac5b2557e8ad8943267ce8d48d9a75e236",result.Digest,"Pinned closed-chunk SHA golden");
            Assert.AreEqual(Encoding.UTF8.GetByteCount(bytes),result.Bytes); Assert.AreEqual(52,result.SourceNodes);
        }
        [Test] public void FixedOrdersMatchSerializedTypedKeyEnvelopes()
        {
            foreach(bool steps in new[]{false,true}) { var fields=UntrustedAuxChunkDigestCursor.FieldOrderForTests(steps); CollectionAssert.AreEqual(fields.OrderBy(Key,StringComparer.Ordinal).ToArray(),fields); }
        }
        [Test] public void MaximumChunkAndLargeInt64MatchProduction()
        { var c=C(Enumerable.Range(0,256).Select(i=>S(long.MaxValue-i)).ToArray()); var r=Drain(c,31,113,127); Assert.AreEqual(6937,r.SourceNodes); Assert.AreEqual(PaidHashContext.BitsV2.Hash(ChunkDict(c)),r.Digest); }
        [Test] public void SignedZeroAdjacentBitsAndReorderChangeDigest()
        {
            var a=S(long.MinValue);
            var b=S(long.MaxValue,BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(.125)+1));
            var p=Drain(C(new[]{a,b}),20,100,100).Digest;
            Assert.AreNotEqual(p,Drain(C(new[]{b,a}),20,100,100).Digest);
            Assert.AreNotEqual(p,Drain(C(new[]{a,b},BitConverter.Int64BitsToDouble(long.MinValue)),20,100,100).Digest);
        }
        [TestCase(double.NaN)][TestCase(double.PositiveInfinity)]
        public void NonfiniteAndMalformedDigestRefuse(double invalid)
        {
            Assert.IsTrue(UntrustedAuxChunkDigestCursor.Begin(C(new[]{S()},invalid),out var c,out _));
            using(c) { while(c.Step(1024,4096,4096,true)==DigestCursorStatus.Pending) {} Assert.IsNull(c.Result); Assert.AreEqual("nonfinite_real",c.Reason); }
            var wrong=new UntrustedAuxEvidenceChunk(0,new string('A',64),new string('b',64),1,0,0,0,0,0,0,new[]{S()});
            Assert.IsFalse(UntrustedAuxChunkDigestCursor.Begin(wrong,out _,out _));
        }
        [Test] public void PreviousDigestChangesActualHashAndSuppliedDigestDoesNotAuthorize()
        {
            var original=C(new[]{S()});
            var changed=new UntrustedAuxEvidenceChunk(original.Ordinal,new string('c',64),original.Digest,original.InitialSequence,
                original.ProgressBefore,original.EligibleBefore,original.AcceptedBefore,original.ProgressAfter,original.EligibleAfter,original.AcceptedAfter,new[]{S()});
            var result=Drain(changed,1,1,1);
            Assert.AreEqual(PaidHashContext.BitsV2.Hash(ChunkDict(changed)),result.Digest);
            Assert.AreNotEqual(Drain(original,1,1,1).Digest,result.Digest);
            Assert.AreNotEqual(changed.Digest,result.Digest,"Supplied digest mismatch remains caller evidence; cursor result is untrusted.");
        }
        [Test] public void ExtremeFiniteRealBitsRemainExact()
        {
            foreach(double value in new[]{double.Epsilon,double.MaxValue,-double.MaxValue,BitConverter.Int64BitsToDouble(long.MinValue)})
            { var chunk=C(new[]{S(long.MaxValue,value)}); Assert.AreEqual(PaidHashContext.BitsV2.Hash(ChunkDict(chunk)),Drain(chunk,3,11,17).Digest); }
        }
        [Test] public void CancellationAndInvalidBudgetCannotProduceDigest()
        {
            Assert.IsTrue(UntrustedAuxChunkDigestCursor.Begin(C(new[]{S()}),out var c,out _)); c.Step(1,1,1,false); c.Cancel(); c.Cancel();
            Assert.AreEqual(DigestCursorStatus.Rejected,c.Step(1024,4096,4096,true)); Assert.IsNull(c.Result); Assert.AreEqual("cancelled",c.Reason);
            Assert.IsTrue(UntrustedAuxChunkDigestCursor.Begin(C(new[]{S()}),out c,out _)); Assert.AreEqual(DigestCursorStatus.Rejected,c.Step(-1,0,0,false)); Assert.AreEqual("invalid_budget",c.Reason);
        }
    }
}
