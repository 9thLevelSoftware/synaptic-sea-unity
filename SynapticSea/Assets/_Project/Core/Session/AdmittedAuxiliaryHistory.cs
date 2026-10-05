using System;
using System.Linq;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Source admission only. Actual participant membership and session publication remain
    // separate private cohort checks. Preparation is synchronous worker/bootstrap work.
    internal sealed class AdmittedAuxiliaryHistory
    {
        readonly UntrustedAuxEvidenceChunk[] _chunks;
        readonly GdDict _originalOwner;
        readonly WorkerAdmissionEpoch _sourceEpoch;
        internal readonly ResourceAuthorityLease Lease;
        internal readonly UntrustedAuxReplaySeed OriginalSeed;
        internal readonly string OwnerDigest,StartOwnerDigest,WorkLineageId,LatestDigest,SourceSessionId;
        internal readonly long StartOwnerRevision,AcceptedSteps,CutWorkVersion;
        internal readonly double Progress,Eligible;
        internal int ChunkCount=>_chunks.Length;
        internal UntrustedAuxEvidenceChunk ChunkAt(int index)=>_chunks[index];
        internal GdDict CopyOriginalOwner()=>_originalOwner.DeepCopy();
        internal bool ResourcesCurrent=>Lease.IsCurrent&&(_sourceEpoch==null||_sourceEpoch.Active);
        AdmittedAuxiliaryHistory(ResourceAuthorityLease lease,GdDict owner,GdDict original,UntrustedAuxReplaySeed seed,
            UntrustedAuxEvidenceChunk[] chunks,string lineage,string latest,double progress,double eligible,long count,string session,WorkerAdmissionEpoch epoch=null)
        {
            Lease=lease;_sourceEpoch=epoch;_originalOwner=original.DeepCopy();OriginalSeed=seed;_chunks=(UntrustedAuxEvidenceChunk[])chunks.Clone();
            OwnerDigest=PaidHashContext.BitsV2.Hash(owner);StartOwnerDigest=PaidHashContext.BitsV2.Hash(original);StartOwnerRevision=original.GetInt("revision");
            WorkLineageId=lineage;LatestDigest=latest;Progress=progress;Eligible=eligible;AcceptedSteps=count;CutWorkVersion=count;SourceSessionId=session;
        }
        internal static bool TryIssueFresh(AdmittedWorkerOrigin admitted,out AdmittedAuxiliaryHistory history,out string reason)
        {
            history=null;reason="fresh_granted_origin_required";
            if(admitted==null||!admitted.Granted||!admitted.Epoch.Active||!admitted.Lease.IsCurrent||admitted.Bundle.SchemaVersion!=6||admitted.HashAlgorithm!=PaidHashContext.BitsV2.Algorithm)return false;
            var owner=admitted.Bundle.GetSummary();
            if(PaidHashContext.BitsV2.Hash(owner)!=admitted.OwnerDigest)return false;
            // A historical handle or grant bit alone is not sufficient: perform fresh stock admission.
            try { using(new PinnedAdmissionResourceScope(admitted.Lease)) { if(!DomainBundle.TryCreate(owner,out _,out reason))return false; } }
            catch(UnsupportedWorkerPortException){reason="unsupported_fresh_origin_port";return false;}
            catch(InvalidOperationException){reason="fresh_origin_resource_refused";return false;}
            var state=AuxiliaryServiceState.State(owner);var job=state.GetDictOrEmpty("job");string service=job.Get("service_id") as string;
            if(state.Get("run_id") as string!=admitted.Epoch.RunId||state.Get("actor_id") as string!=admitted.Epoch.ActorId||service==null||job.Get("status") as string!="running"||job.GetBool("resume_required")||job.GetFloat("progress_seconds")!=0||job.GetFloat("eligible_seconds")!=0||!AuxiliaryServiceState.Latest(owner,service,"aux_progress").IsEmpty)return false;
            string id=AuxiliaryServiceState.Origin(owner,service);var start=owner.GetDictOrEmpty("receipts").GetDictOrEmpty(id);if(start.IsEmpty)return false;
            var latest=AuxiliaryServiceState.Latest(owner,service);if(latest.GetDictOrEmpty("result").Get("operation") as string!="aux_start")return false;
            double duration=state.GetDictOrEmpty("descriptors").GetDictOrEmpty(service).GetFloat("required_seconds");if(duration!=8&&duration!=12)return false;
            string digest=PaidHashContext.BitsV2.Hash(start);var seed=new UntrustedAuxReplaySeed(admitted.Epoch.RunId,admitted.Epoch.ActorId,service,digest,duration,0,0,0);
            if(!admitted.Lease.IsCurrent||!admitted.Epoch.Active)return false;
            history=new AdmittedAuxiliaryHistory(admitted.Lease,owner,owner,seed,Array.Empty<UntrustedAuxEvidenceChunk>(),"auxiliary-work:"+digest,digest,0,0,0,admitted.Epoch.SessionId,admitted.Epoch);reason="admitted_fresh_auxiliary_source";return true;
        }
        internal static bool TryIssueResume(CheckpointProofAdmission.Result admitted,out AdmittedAuxiliaryHistory history,out string reason)
        {
            history=null;reason="admitted_unfinished_checkpoint_required";if(admitted==null||!admitted.Lease.IsCurrent)return false;
            var package=admitted.CopyPackage();var owner=admitted.CopyOwner();var latest=owner.GetDictOrEmpty("receipts").Values.OfType<GdDict>().OrderByDescending(r=>r.GetInt("revision")).FirstOrDefault();
            if(latest==null||latest.GetDictOrEmpty("result").Get("operation") as string!="aux_proof_checkpoint_v1")return false;
            string receiptId=latest.Get("commit_id") as string;if(!(owner.GetDictOrEmpty("auxiliary_proofs").Get(receiptId) is string hash)||!(package.GetDictOrEmpty("proofs").Get(hash) is GdDict proof)||proof.Get("state") as string=="completed")return false;
            var original=(GdDict)package.GetDictOrEmpty("origins").GetDictOrEmpty((string)proof.Get("start_origin_capsule_digest")).Get("owner");
            string service=(string)proof.Get("service_id");double duration=AuxiliaryServiceState.State(original).GetDictOrEmpty("descriptors").GetDictOrEmpty(service).GetFloat("required_seconds");
            var seed=new UntrustedAuxReplaySeed((string)proof.Get("run_id"),(string)proof.Get("actor_id"),service,(string)proof.Get("origin_receipt_digest"),duration,0,0,0);
            var encoded=(GdArray)proof.Get("chunks");var chunks=new UntrustedAuxEvidenceChunk[encoded.Count];
            for(int c=0;c<chunks.Length;c++)
            {
                var d=(GdDict)encoded[c];var rows=(GdArray)d.Get("steps");var steps=new UntrustedAuxEvidenceStep[rows.Count];
                for(int i=0;i<steps.Length;i++)
                {
                    var s=(GdDict)rows[i];steps[i]=new UntrustedAuxEvidenceStep((long)s.Get("sequence"),(double)s.Get("delta_seconds"),(double)s.Get("stamina_before"),(double)s.Get("max_stamina"),(double)s.Get("wound_work_multiplier"),(double)s.Get("ratio"),(double)s.Get("speed"),(double)s.Get("remaining_before"),(double)s.Get("elapsed_seconds"),(double)s.Get("delta_progress"),(double)s.Get("stamina_after"),(double)s.Get("progress_after"),(double)s.Get("eligible_after"));
                }
                chunks[c]=new UntrustedAuxEvidenceChunk((long)d.Get("ordinal"),(string)d.Get("previous_chunk_digest"),(string)d.Get("digest"),(long)d.Get("initial_step_sequence"),(double)d.Get("progress_before"),(double)d.Get("eligible_before"),(long)d.Get("accepted_steps_before"),(double)d.Get("progress_after"),(double)d.Get("eligible_after"),(long)d.Get("accepted_steps_after"),steps);
            }
            var job=(GdDict)proof.Get("job_after");if(!admitted.Lease.IsCurrent)return false;
            history=new AdmittedAuxiliaryHistory(admitted.Lease,owner,original,seed,chunks,(string)proof.Get("work_lineage_id"),(string)proof.Get("latest_chunk_digest"),(double)job.Get("progress_seconds"),(double)job.Get("eligible_seconds"),(long)proof.Get("accepted_steps"),null);reason="admitted_restored_auxiliary_source";return true;
        }
    }
}
