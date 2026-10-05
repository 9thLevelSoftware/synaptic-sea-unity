using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Variant;
using SynapticSea.Core.Services;
namespace SynapticSea.Core.Session
{
    // Economic compatibility of already fully admitted snapshots, never output/live-source authority.
    internal static class ProofGenerationComparison
    {
        static readonly object IssuerSeal=new object();
        internal sealed class Compatibility
        {
            readonly string[] _omitted;
            readonly ResourceAuthorityLease _parentLease,_childLease;
            internal bool ResourcesCurrent=>_parentLease.IsCurrent&&_childLease.IsCurrent;
            internal readonly string RequiredBeforeOwnerDigest,ParentOwnerDigest,ChildOwnerDigest;
            internal bool RequiresOutputSourceBinding=>_omitted.Length!=0;
            internal int OmittedCheckpointCount=>_omitted.Length;
            internal string OmittedCheckpointAt(int index)=>_omitted[index];
            internal Compatibility(object seal,string parent,string child,string before,List<string> omitted,ResourceAuthorityLease parentLease,ResourceAuthorityLease childLease)
            {if(!ReferenceEquals(seal,IssuerSeal))throw new InvalidOperationException("generation_comparison_issuer_mismatch");ParentOwnerDigest=parent;ChildOwnerDigest=child;RequiredBeforeOwnerDigest=before;_parentLease=parentLease;_childLease=childLease;_omitted=omitted.ToArray();}
        }
        internal static bool TryCompare(CheckpointProofAdmission.Result parent,CheckpointProofAdmission.Result child,out Compatibility compatibility,out string reason)
        {
            compatibility=null;reason="current_admitted_generation_pair_required";
            if(parent==null||child==null||!parent.Lease.IsCurrent||!child.Lease.IsCurrent)return false;
            var a=parent.CopyPackage();var b=child.CopyPackage();
            if(!Equal(a,b,"owner_profile","hash_algorithm","run_id","actor_id","current_resource_digest","snapshot_content_sha256")){reason="proof_generation_binding_mismatch";return false;}
            var old=(GdDict)a.Get("current_owner");var next=(GdDict)b.Get("current_owner");
            if(next.GetInt("revision")<old.GetInt("revision")||next.GetInt("command_sequence")<old.GetInt("command_sequence")){reason="proof_generation_counters_regressed";return false;}
            var oldReceipts=old.GetDictOrEmpty("receipts");var nextReceipts=next.GetDictOrEmpty("receipts");var omitted=new List<string>();string requiredBefore="";
            foreach(var entry in oldReceipts)
            {
                string id=(string)entry.Key;
                if(nextReceipts.Has(id)){if(!PaidHashContext.BitsV2.Equal(entry.Value,nextReceipts.Get(id))){reason="paid_receipt_changed";return false;}continue;}
                if(!(entry.Value is GdDict receipt)||receipt.GetDictOrEmpty("result").Get("operation") as string!="aux_proof_checkpoint_v1") {reason="stable_paid_receipt_missing";return false;}
                if(!CheckpointBefore(b,next,out var before,out string digest)||before.GetDictOrEmpty("receipts").Has(id)){reason="adopted_checkpoint_receipt_missing";return false;}
                if(requiredBefore!=""&&requiredBefore!=digest){reason="checkpoint_branch_source_mismatch";return false;}
                requiredBefore=digest;omitted.Add(id);
            }
            var childByLineage=new Dictionary<string,GdDict>(StringComparer.Ordinal);
            foreach(var entry in ((GdDict)b.Get("proofs")))
            {
                var proof=(GdDict)entry.Value;string lineage=(string)proof.Get("work_lineage_id");
                if(!childByLineage.TryGetValue(lineage,out var best)||proof.GetInt("accepted_steps")>best.GetInt("accepted_steps")||proof.GetInt("accepted_steps")==best.GetInt("accepted_steps")&&proof.Get("state") as string=="completed")childByLineage[lineage]=proof;
            }
            foreach(var entry in ((GdDict)a.Get("proofs")))
            {
                var prior=(GdDict)entry.Value;string lineage=(string)prior.Get("work_lineage_id");
                if(!childByLineage.TryGetValue(lineage,out var full)||full.GetInt("accepted_steps")<prior.GetInt("accepted_steps")||full.GetInt("cut_work_version")<prior.GetInt("cut_work_version")||
                    !Equal(prior,full,"run_id","actor_id","service_id","work_lineage_id","origin_receipt_id","origin_receipt_digest","start_origin_capsule_digest","start_origin_owner_digest","resource_capsule_digest","snapshot_content_sha256","descriptor_digest","owner_hash_algorithm")) {reason="proof_lineage_regressed";return false;}
                if(prior.Get("state") as string=="completed"&&full.Get("state") as string!="completed"){reason="proof_terminal_regressed";return false;}
                var prefix=(GdArray)prior.Get("chunks");var chunks=(GdArray)full.Get("chunks");if(prefix.Count>chunks.Count){reason="proof_prefix_regressed";return false;}
                for(int i=0;i<prefix.Count;i++)if(!PaidHashContext.BitsV2.Equal(prefix[i],chunks[i])){reason="proof_permanent_boundary_changed";return false;}
            }
            if(!parent.Lease.IsCurrent||!child.Lease.IsCurrent){reason="resource_epoch_changed";return false;}
            compatibility=new Compatibility(IssuerSeal,(string)a.Get("current_owner_digest"),(string)b.Get("current_owner_digest"),requiredBefore,omitted,parent.Lease,child.Lease);reason="proof_generation_compatible";return true;
        }
        static bool Equal(GdDict a,GdDict b,params string[] fields)=>fields.All(k=>PaidHashContext.BitsV2.Equal(a.Get(k),b.Get(k)));
        static bool CheckpointBefore(GdDict package,GdDict owner,out GdDict before,out string digest)
        {
            before=null;digest="";var latest=owner.GetDictOrEmpty("receipts").Values.OfType<GdDict>().OrderByDescending(r=>r.GetInt("revision")).FirstOrDefault();
            if(latest==null||latest.GetDictOrEmpty("result").Get("operation") as string!="aux_proof_checkpoint_v1")return false;
            string receiptId=latest.Get("commit_id") as string;
            if(!(owner.GetDictOrEmpty("auxiliary_proofs").Get(receiptId) is string hash)||!(package.GetDictOrEmpty("proofs").Get(hash) is GdDict proof)||!(package.GetDictOrEmpty("origins").Get(proof.Get("checkpoint_before_capsule_digest")) is GdDict capsule))return false;
            before=(GdDict)capsule.Get("owner");digest=(string)proof.Get("checkpoint_before_owner_digest");return true;
        }
    }
}
