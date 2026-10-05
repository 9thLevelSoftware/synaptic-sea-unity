using System;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public sealed class ProofPackageArithmeticTests
    {
        static readonly string Digest=new string('a',64);
        static GdDict Job(double progress=0,double eligible=0,string status="running")=>new GdDict{{"service_id","utility"},{"progress_seconds",progress},{"eligible_seconds",eligible},{"status",status},{"resume_required",false},{"reason",""}};
        static GdDict Fixture(out GdDict origin)
        {
            var descriptor=new GdDict{{"required_seconds",12.0}};
            var receipt=new GdDict{{"result",new GdDict{{"operation","aux_start"},{"service_id","utility"},{"job_after",Job()}}}};
            string receiptDigest=PaidHashContext.BitsV2.Hash(receipt);
            origin=new GdDict{{"participating_state",new GdDict{{"auxiliary_services",new GdDict{{"run_id","run"},{"actor_id","actor"},{"descriptors",new GdDict{{"utility",descriptor}}}}}}},{"receipts",new GdDict{{"start",receipt}}}};
            return new GdDict{{"proof_version",1L},{"state","ongoing"},{"run_id","run"},{"actor_id","actor"},{"service_id","utility"},{"owner_hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"resource_capsule_digest",Digest},{"snapshot_content_sha256",Digest},{"descriptor_digest",PaidHashContext.BitsV2.Hash(descriptor)},{"origin_receipt_id","start"},{"origin_receipt_digest",receiptDigest},{"start_origin_capsule_digest",Digest},{"start_origin_owner_digest",Digest},{"chunks",new GdArray()},{"latest_chunk_digest",receiptDigest},{"job_after",Job()},{"accepted_steps",0L},{"work_lineage_id","auxiliary-work:"+receiptDigest},{"cut_work_version",0L},{"checkpoint_before_capsule_digest",Digest},{"checkpoint_before_owner_digest",Digest},{"checkpoint_receipt_id","checkpoint"},{"checkpoint_receipt_digest",Digest}};
        }
        static GdDict RuntimeStep(AuxiliaryAcceptedStep a)=>new GdDict{{"sequence",a.Sequence},{"delta_seconds",a.RequestedDelta},{"stamina_before",a.StaminaBefore},{"max_stamina",a.MaxStamina},{"wound_work_multiplier",a.WoundSpeed},
            {"ratio",Math.Max(0,Math.Min(1,a.StaminaBefore/Math.Max(1,a.MaxStamina)))},{"speed",a.Speed},{"remaining_before",12-a.ProgressBefore},{"elapsed_seconds",a.ElapsedSeconds},{"delta_progress",a.DeltaSeconds},
            {"stamina_after",a.StaminaAfter},{"progress_after",a.ProgressAfter},{"eligible_after",a.EligibleAfter}};
        static GdDict Nonempty(out GdDict origin)
        {
            var proof=Fixture(out origin);var runtime=new AuxiliaryWorkRuntime("owner","run","actor","utility",0,PaidHashContext.BitsV2.Algorithm,12,0,0,100,0,256);var steps=new GdArray();
            for(int i=0;i<2;i++){var result=runtime.Step(new AuxiliaryWorkFrame(.125,100,100,1));Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted,result.Status);steps.Add(RuntimeStep(result.AcceptedStep));}
            var snapshot=runtime.Snapshot();var chunk=new GdDict{{"chunk_version",1L},{"ordinal",0L},{"previous_chunk_digest",proof.Get("origin_receipt_digest")},{"initial_step_sequence",1L},{"progress_before",0.0},{"eligible_before",0.0},{"accepted_steps_before",0L},
                {"steps",steps},{"progress_after",snapshot.ProgressSeconds},{"eligible_after",snapshot.EligibleSeconds},{"accepted_steps_after",snapshot.EligibleSteps}};
            string digest=PaidHashContext.BitsV2.Hash(chunk);chunk["digest"]=digest;proof["chunks"]=GdArray.Of(chunk);proof["latest_chunk_digest"]=digest;proof["job_after"]=Job(snapshot.ProgressSeconds,snapshot.EligibleSeconds);proof["accepted_steps"]=snapshot.EligibleSteps;proof["cut_work_version"]=snapshot.EligibleSteps;return proof;
        }
        static void RehashChunk(GdDict proof)
        {
            var chunk=(GdDict)((GdArray)proof.Get("chunks"))[0];var raw=chunk.DeepCopy();raw.Erase("digest");string digest=PaidHashContext.BitsV2.Hash(raw);chunk["digest"]=digest;proof["latest_chunk_digest"]=digest;
        }
        [Test] public void AuthenticNonemptyRuntimeTraceReplaysAndScalarDigestLinkHeaderTamperingRefuses()
        {
            var proof=Nonempty(out var origin);Assert.IsTrue(ProofPackageArithmetic.TryReplay(origin,proof,out var matched,out _));Assert.AreEqual(2,matched.AcceptedSteps);Assert.AreEqual(.25,matched.Progress);Assert.IsFalse(DomainBundle.TryCreate(origin,out _,out _));
            proof=Nonempty(out origin);var chunk=(GdDict)((GdArray)proof.Get("chunks"))[0];var step=(GdDict)((GdArray)chunk.Get("steps"))[0];double delta=(double)step.Get("delta_progress");step["delta_progress"]=BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(delta)+1);RehashChunk(proof);Assert.IsFalse(ProofPackageArithmetic.TryReplay(origin,proof,out _,out _));
            proof=Nonempty(out origin);chunk=(GdDict)((GdArray)proof.Get("chunks"))[0];chunk["digest"]=Digest;proof["latest_chunk_digest"]=Digest;Assert.IsFalse(ProofPackageArithmetic.TryReplay(origin,proof,out _,out _));
            proof=Nonempty(out origin);chunk=(GdDict)((GdArray)proof.Get("chunks"))[0];chunk["previous_chunk_digest"]=Digest;RehashChunk(proof);Assert.IsFalse(ProofPackageArithmetic.TryReplay(origin,proof,out _,out _));
            proof=Nonempty(out origin);chunk=(GdDict)((GdArray)proof.Get("chunks"))[0];chunk["initial_step_sequence"]=2L;RehashChunk(proof);Assert.IsFalse(ProofPackageArithmetic.TryReplay(origin,proof,out _,out _));
        }
        [Test] public void EmptyOngoingReplayIsExplicitlyUntrustedAndCannotAdmitFakeOrigin()
        {
            var proof=Fixture(out var origin);Assert.IsTrue(ProofPackageArithmetic.TryReplay(origin,proof,out var result,out string reason));Assert.AreEqual("untrusted_arithmetic_and_digests_match",reason);Assert.AreEqual(0,result.AcceptedSteps);
            Assert.IsFalse(DomainBundle.TryCreate(origin,out _,out _));
        }
        [Test] public void WorkVersionCountAndExactJobBitsMustAgree()
        {
            var proof=Fixture(out var origin);proof["cut_work_version"]=1L;Assert.IsFalse(ProofPackageArithmetic.TryReplay(origin,proof,out _,out _));
            proof=Fixture(out origin);((GdDict)proof.Get("job_after"))["progress_seconds"]=0L;Assert.IsFalse(ProofPackageArithmetic.TryReplay(origin,proof,out _,out _));
            proof=Fixture(out origin);proof["work_lineage_id"]="caller-origin";Assert.IsFalse(ProofPackageArithmetic.TryReplay(origin,proof,out _,out _));
        }
        [Test] public void ReciprocalReferencesExcludedFromHashStillRequireFullAdmission()
        {
            var proof=Fixture(out _);string before=AuxiliaryProofOwnerProfile.ProofDigest(proof);proof["checkpoint_receipt_digest"]=new string('b',64);Assert.AreEqual(before,AuxiliaryProofOwnerProfile.ProofDigest(proof));
            proof["accepted_steps"]=1L;Assert.AreNotEqual(before,AuxiliaryProofOwnerProfile.ProofDigest(proof));
        }
        [Test] public void ReadyStateRequiresExactDurationAndNoTerminalFields()
        {
            var proof=Fixture(out var origin);proof["state"]="ready_on_cut";Assert.IsFalse(ProofPackageArithmetic.TryReplay(origin,proof,out _,out _));
            proof=Fixture(out origin);proof["terminal_before_capsule_digest"]=Digest;Assert.IsFalse(ProofPackageArithmetic.TryReplay(origin,proof,out _,out _));
        }
    }
}
