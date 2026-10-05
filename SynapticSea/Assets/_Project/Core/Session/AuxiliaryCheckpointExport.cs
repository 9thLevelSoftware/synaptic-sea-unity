using System;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Detached preparation; a Result is NOT an actual session/world output ticket.
    internal static class AuxiliaryCheckpointExport
    {
        internal static bool TryExport(AuxiliaryWorkRuntime.EvidenceExportPin cut,AdmittedAuxiliaryHistory history,DomainBundle canonicalBefore,CheckpointProofAdmission.Result retained,ProofResourceBinding binding,string commandId,out CheckpointProofAdmission.Result result,out string reason)
        {
            result=null;reason="invalid_actual_evidence_cut";
            if(cut==null||history==null||!ReferenceEquals(cut.SourceHistory,history)||canonicalBefore==null||binding==null||!history.ResourcesCurrent||!binding.Lease.IsCurrent||!ReferenceEquals(history.Lease.Snapshot,binding.Lease.Snapshot)||commandId==null||commandId.Length==0||commandId.Length>200)return false;
            var seed=history.OriginalSeed;if(cut.RunId!=seed.Run||cut.ActorId!=seed.Actor||cut.ServiceId!=seed.Service||cut.Algorithm!=PaidHashContext.BitsV2.Algorithm)return false;
            if(cut.Work.EligibleSteps<=0||cut.ChunkCount<=0||cut.ChunkCount<history.ChunkCount||cut.Work.EligibleSteps<history.AcceptedSteps)return false;
            var origin=history.CopyOriginalOwner();var before=canonicalBefore.GetSummary();
            if(before.GetInt("schema_version")==7&&(retained==null||PaidHashContext.BitsV2.Hash(retained.CopyOwner())!=PaidHashContext.BitsV2.Hash(before)))return false;
            if(before.GetInt("revision")==long.MaxValue||before.GetInt("command_sequence")==long.MaxValue)return false;
            string receiptId="auxiliary-proof-checkpoint:"+commandId;
            var paid=PaidCraftingState.State(origin);string id=seed.Service;double duration=seed.Duration;
            string originId=AuxiliaryServiceState.Origin(origin,id),startHash=seed.OriginDigest;
            var capsule=Capsule(origin,binding);string capsuleDigest=PaidHashContext.BitsV2.Hash(capsule);
            var origins=retained==null?new GdDict():retained.CopyPackage().GetDictOrEmpty("origins");
            var proofs=retained==null?new GdDict():retained.CopyPackage().GetDictOrEmpty("proofs");
            origins[capsuleDigest]=capsule;
            var beforeCapsule=Capsule(before,binding);
            var refs=new GdArray();foreach(var e in before.GetDictOrEmpty("auxiliary_proofs"))
            {var p=proofs.GetDictOrEmpty(e.Value);foreach(string k in new[]{"start_origin_capsule_digest",p.Get("state") as string=="completed"?"terminal_before_capsule_digest":"checkpoint_before_capsule_digest"}){string r=p.Get(k) as string;if(r!=null&&!refs.Contains(r))refs.Add(r);}}
            foreach(var entry in before.GetDictOrEmpty("receipts"))if(entry.Value is GdDict savedReceipt&&savedReceipt.Get("command") is GdDict take&&take.Get("operation") as string=="aux_proof_take_v1")
            {string r=take.Get("take_before_capsule_digest") as string;if(r!=null&&!refs.Contains(r))refs.Add(r);}
            beforeCapsule["origin_refs"]=new GdArray(refs.Cast<string>().OrderBy(x=>x,StringComparer.Ordinal).Cast<object>());string beforeCapsuleDigest=PaidHashContext.BitsV2.Hash(beforeCapsule);origins[beforeCapsuleDigest]=beforeCapsule;
            var chunks=new GdArray();string chunkDigest=startHash;
            for(int c=0;c<cut.ChunkCount;c++)
            {
                var raw=cut.ChunkAt(c);var steps=new GdArray();for(int i=0;i<raw.Count;i++)steps.Add(Step(raw.At(i),duration));
                var chunk=new GdDict{{"chunk_version",1L},{"ordinal",(long)c},{"previous_chunk_digest",chunkDigest},{"initial_step_sequence",raw.FirstSequence},{"progress_before",raw.ProgressBefore},{"eligible_before",raw.EligibleBefore},{"accepted_steps_before",raw.FirstSequence-1},{"steps",steps},{"progress_after",raw.ProgressAfter},{"eligible_after",raw.EligibleAfter},{"accepted_steps_after",raw.LastSequence}};
                chunkDigest=PaidHashContext.BitsV2.Hash(chunk);
                if(c<history.ChunkCount&&history.ChunkAt(c).Digest!=chunkDigest){reason="restored_permanent_prefix_mismatch";return false;}
                chunk["digest"]=chunkDigest;chunks.Add(chunk);
            }
            var job=AuxiliaryServiceState.State(before).GetDictOrEmpty("job").DeepCopy();job["progress_seconds"]=cut.Work.ProgressSeconds;job["eligible_seconds"]=cut.Work.EligibleSeconds;
            var proof=new GdDict{{"proof_version",1L},{"state",cut.Work.ProgressSeconds==duration?"ready_on_cut":"ongoing"},{"run_id",paid.Get("run_id")},{"actor_id",paid.Get("actor_id")},{"service_id",id},{"owner_hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"resource_capsule_digest",binding.ResourceCapsuleDigest},{"snapshot_content_sha256",binding.SnapshotContentSha256},{"descriptor_digest",PaidHashContext.BitsV2.Hash(AuxiliaryServiceState.State(origin).GetDictOrEmpty("descriptors").Get(id))},{"origin_receipt_id",originId},{"origin_receipt_digest",startHash},{"start_origin_capsule_digest",capsuleDigest},{"start_origin_owner_digest",PaidHashContext.BitsV2.Hash(origin)},{"chunks",chunks},{"latest_chunk_digest",chunkDigest},{"job_after",job},{"accepted_steps",cut.Work.EligibleSteps},{"work_lineage_id","auxiliary-work:"+startHash},{"cut_work_version",cut.Work.EligibleSteps},{"checkpoint_before_capsule_digest",beforeCapsuleDigest},{"checkpoint_before_owner_digest",PaidHashContext.BitsV2.Hash(before)},{"checkpoint_receipt_id",receiptId},{"checkpoint_receipt_digest",new string('0',64)}};
            string proofHash=AuxiliaryProofOwnerProfile.ProofDigest(proof);var command=new GdDict{{"command_id",commandId},{"operation","aux_proof_checkpoint_v1"},{"run_id",paid.Get("run_id")},{"actor_id",paid.Get("actor_id")},{"proof_digest",proofHash}};
            foreach(string key in new[]{"service_id","work_lineage_id","checkpoint_before_capsule_digest","checkpoint_before_owner_digest","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest","accepted_steps","cut_work_version"})command[key]=proof.Get(key);
            var effect=new GdDict{{"operation","aux_proof_checkpoint_v1"},{"service_id",id},{"work_lineage_id",proof.Get("work_lineage_id")},{"proof_digest",proofHash},{"accepted_steps",cut.Work.EligibleSteps},{"cut_work_version",cut.Work.EligibleSteps},{"before_owner_revision",before.Get("revision")},{"before_command_sequence",before.Get("command_sequence")},{"job_before",AuxiliaryServiceState.State(before).Get("job")},{"job_after",job}};
            foreach(string key in new[]{"checkpoint_before_capsule_digest","checkpoint_before_owner_digest","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest"})effect[key]=proof.Get(key);
            var receipt=new GdDict{{"schema_version",1L},{"transaction_id",receiptId},{"commit_id",receiptId},{"command_id",commandId},{"command",command},{"command_hash",PaidHashContext.BitsV2.Hash(command)},{"revision",before.GetInt("revision")+1},{"result",effect}};proof["checkpoint_receipt_digest"]=PaidHashContext.BitsV2.Hash(receipt);
            var current=before.DeepCopy();Upgrade(current);current["revision"]=before.GetInt("revision")+1;current["command_sequence"]=before.GetInt("command_sequence")+1;AuxiliaryServiceState.State(current)["job"]=job.DeepCopy();current.GetDictOrEmpty("receipts")[receiptId]=receipt;current.GetDictOrEmpty("auxiliary_proofs")[receiptId]=proofHash;
            var package=new GdDict{{"package_version",1L},{"owner_profile",AuxiliaryProofOwnerProfile.Profile},{"hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"run_id",paid.Get("run_id")},{"actor_id",paid.Get("actor_id")},{"current_owner",current},{"current_owner_digest",PaidHashContext.BitsV2.Hash(current)},{"current_resource_digest",binding.ResourceCapsuleDigest},{"snapshot_content_sha256",binding.SnapshotContentSha256},{"origins",origins},{"resources",new GdDict{{binding.ResourceCapsuleDigest,binding.CopyCapsule()}}},{"proofs",proofs}};
            proofs[proofHash]=proof;
            var outer=new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",ComponentDomainCodec.Encode(package,ComponentDomainCodec.BitExactSchema)}};
            if(!ProofPackageCodec.TryOwnEnvelope(outer,out var owned,out reason))return false;
            return CheckpointProofAdmission.TryAdmit(owned,binding,out result,out reason);
        }
        static void Upgrade(GdDict owner){owner["schema_version"]=7L;owner["feature_schema"]=5L;owner["auxiliary_proof_format"]=1L;owner["hash_algorithm"]=PaidHashContext.BitsV2.Algorithm;if(!owner.Has("auxiliary_proofs"))owner["auxiliary_proofs"]=new GdDict();}
        static GdDict Capsule(GdDict owner,ProofResourceBinding binding)=>new GdDict{{"capsule_version",1L},{"owner_profile",AuxiliaryProofOwnerProfile.Profile},{"run_id",PaidCraftingState.State(owner).Get("run_id")},{"actor_id",PaidCraftingState.State(owner).Get("actor_id")},{"owner_revision",owner.Get("revision")},{"command_sequence",owner.Get("command_sequence")},{"resource_digest",binding.ResourceCapsuleDigest},{"owner",owner.DeepCopy()},{"origin_refs",new GdArray()}};
        static GdDict Step(AuxiliaryAcceptedStep a,double duration)=>new GdDict{{"sequence",a.Sequence},{"delta_seconds",a.RequestedDelta},{"stamina_before",a.StaminaBefore},{"max_stamina",a.MaxStamina},{"wound_work_multiplier",a.WoundSpeed},{"ratio",Math.Max(0,Math.Min(1,a.StaminaBefore/Math.Max(1,a.MaxStamina)))},{"speed",a.Speed},{"remaining_before",duration-a.ProgressBefore},{"elapsed_seconds",a.ElapsedSeconds},{"delta_progress",a.DeltaSeconds},{"stamina_after",a.StaminaAfter},{"progress_after",a.ProgressAfter},{"eligible_after",a.EligibleAfter}};
    }
}
