using System;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Worker-private replay of detached evidence. Result remains untrusted until complete owner admission/conservation.
    internal static class ProofPackageArithmetic
    {
        static readonly string[] ChunkKeys={"chunk_version","ordinal","previous_chunk_digest","initial_step_sequence","progress_before","eligible_before","accepted_steps_before","steps","progress_after","eligible_after","accepted_steps_after","digest"};
        static readonly string[] StepKeys={"sequence","delta_seconds","stamina_before","max_stamina","wound_work_multiplier","ratio","speed","remaining_before","elapsed_seconds","delta_progress","stamina_after","progress_after","eligible_after"};
        static double Real(GdDict d,string key)
        {if(!(d.Get(key) is double value)||double.IsNaN(value)||double.IsInfinity(value))throw new ArgumentException("real_type");return value;}
        static long Integer(GdDict d,string key)
        {if(!(d.Get(key) is long value)||value<0)throw new ArgumentException("integer_type");return value;}
        static string Digest(GdDict d,string key)
        {if(!AuxiliaryProofOwnerProfile.Digest(d.Get(key)))throw new ArgumentException("digest_type");return (string)d.Get(key);}
        static bool SameBits(double a,double b)=>BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);
        internal static bool TryReplay(GdDict originOwner,GdDict proof,out UntrustedReplayResult result,out string reason)
        {
            result=null;reason="invalid_detached_proof_arithmetic";
            try
            {
                if(originOwner==null||!AuxiliaryProofOwnerProfile.ProofKeys(proof)||proof.Get("owner_hash_algorithm") as string!=PaidHashContext.BitsV2.Algorithm)return false;
                var state=AuxiliaryServiceState.State(originOwner);string service=proof.Get("service_id") as string,run=proof.Get("run_id") as string,actor=proof.Get("actor_id") as string;
                if(service==null||service.Length>256||run!=state.Get("run_id") as string||actor!=state.Get("actor_id") as string)return false;
                if(!(proof.Get("origin_receipt_id") is string receiptId)||receiptId.Length>256||!(originOwner.Get("receipts") is GdDict receipts)||!(receipts.Get(receiptId) is GdDict receipt)||
                    PaidHashContext.BitsV2.Hash(receipt)!=Digest(proof,"origin_receipt_digest"))return false;
                var effect=receipt.Get("result") as GdDict;var seedJob=effect?.Get("job_after") as GdDict;
                if(effect==null||effect.Get("operation") as string!="aux_start"||effect.Get("service_id") as string!=service||seedJob==null||!SameBits(Real(seedJob,"progress_seconds"),0.0)||!SameBits(Real(seedJob,"eligible_seconds"),0.0))return false;
                if(!(state.Get("descriptors") is GdDict descriptors)||!(descriptors.Get(service) is GdDict descriptor)||PaidHashContext.BitsV2.Hash(descriptor)!=Digest(proof,"descriptor_digest"))return false;
                double duration=Real(descriptor,"required_seconds");if(duration!=8&&duration!=12)return false;
                long count=Integer(proof,"accepted_steps");if(Integer(proof,"cut_work_version")!=count||proof.Get("work_lineage_id") as string!="auxiliary-work:"+(string)proof.Get("origin_receipt_digest"))return false;
                if(!(proof.Get("chunks") is GdArray encoded)||encoded.Count>256)return false;
                var chunks=new UntrustedAuxEvidenceChunk[encoded.Count];long totalSteps=0;
                for(int c=0;c<encoded.Count;c++)
                {
                    if(!(encoded[c] is GdDict d)||!AuxiliaryProofOwnerProfile.Exact(d,ChunkKeys)||Integer(d,"chunk_version")!=1||!(d.Get("steps") is GdArray rows)||rows.Count<1||rows.Count>256)return false;
                    totalSteps=checked(totalSteps+rows.Count);if(totalSteps>65536)return false;
                    var steps=new UntrustedAuxEvidenceStep[rows.Count];
                    for(int i=0;i<rows.Count;i++)
                    {
                        if(!(rows[i] is GdDict s)||!AuxiliaryProofOwnerProfile.Exact(s,StepKeys))return false;
                        steps[i]=new UntrustedAuxEvidenceStep(Integer(s,"sequence"),Real(s,"delta_seconds"),Real(s,"stamina_before"),Real(s,"max_stamina"),Real(s,"wound_work_multiplier"),Real(s,"ratio"),Real(s,"speed"),Real(s,"remaining_before"),Real(s,"elapsed_seconds"),Real(s,"delta_progress"),Real(s,"stamina_after"),Real(s,"progress_after"),Real(s,"eligible_after"));
                    }
                    chunks[c]=new UntrustedAuxEvidenceChunk(Integer(d,"ordinal"),Digest(d,"previous_chunk_digest"),Digest(d,"digest"),Integer(d,"initial_step_sequence"),Real(d,"progress_before"),Real(d,"eligible_before"),Integer(d,"accepted_steps_before"),Real(d,"progress_after"),Real(d,"eligible_after"),Integer(d,"accepted_steps_after"),steps);
                }
                if(totalSteps!=count)return false;
                var seed=new UntrustedAuxReplaySeed(run,actor,service,(string)proof.Get("origin_receipt_digest"),duration,0,0,0);
                if(!AuxEvidenceReplayCursor.Begin(seed,chunks,out var cursor,out reason))return false;
                AuxReplayStatus status=AuxReplayStatus.Pending;
                // Synchronous worker-only stock replay; no <=2ms claim. Each cursor call remains explicitly budgeted.
                for(int turn=0;turn<100000&&status==AuxReplayStatus.Pending;turn++)status=cursor.Step(256,1024,4096,4096,true);
                if(status!=AuxReplayStatus.UntrustedResult){reason=cursor.Reason??"proof_replay_incomplete";cursor.Cancel();return false;}
                var replay=cursor.ArithmeticResult;
                string latest=chunks.Length==0?(string)proof.Get("origin_receipt_digest"):chunks[chunks.Length-1].Digest;
                if(proof.Get("latest_chunk_digest") as string!=latest||!(proof.Get("job_after") is GdDict job)||!AuxiliaryProofOwnerProfile.Exact(job,"service_id","progress_seconds","eligible_seconds","status","resume_required","reason")||
                    job.Get("service_id") as string!=service||!SameBits(Real(job,"progress_seconds"),replay.Progress)||!SameBits(Real(job,"eligible_seconds"),replay.Eligible)||!(job.Get("resume_required") is bool resume)||resume||job.Get("reason") as string!=""||replay.AcceptedSteps!=count)return false;
                string proofState=proof.Get("state") as string;
                if(proofState=="ongoing"?(replay.Progress>=duration||job.Get("status") as string!="running"):
                    proofState=="ready_on_cut"?(replay.Progress!=duration||job.Get("status") as string!="running"):
                    proofState!="completed"||replay.Progress!=duration||job.Get("status") as string!="completed")return false;
                result=replay;reason="untrusted_arithmetic_and_digests_match";return true;
            }
            catch(ArgumentException){return false;}catch(OverflowException){return false;}catch(InvalidCastException){return false;}
        }
    }
}
