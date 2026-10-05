using System;
using System.Linq;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // New closed format. No legacy profile inference and no standalone admission authority.
    internal static class AuxiliaryProofOwnerProfile
    {
        internal const long OwnerSchema=7, FeatureSchema=5, ProofVersion=1;
        internal const string OuterSchema="component_paid_package_v1", Profile="auxiliary-proof-owner-v1";
        internal const string Compatibility="auxiliary-proof-owner-v1/current-resources";
        static readonly string[] PackageKeys={"package_version","owner_profile","hash_algorithm","run_id","actor_id","current_owner","current_owner_digest","current_resource_digest","snapshot_content_sha256","origins","resources","proofs"};
        static readonly string[] OwnerKeys={"schema_version","feature_schema","hash_algorithm","auxiliary_proof_format","revision","registry","holders","machinery","receipts","physical_slots","component_work","participating_state","command_sequence","registered_owners","domain_mode","auxiliary_proofs"};
        static readonly string[] CapsuleKeys={"capsule_version","owner_profile","run_id","actor_id","owner_revision","command_sequence","resource_digest","owner","origin_refs"};
        static readonly string[] ResourceKeys={"resource_version","path_policy","snapshot_content_sha256","entries","directories"};
        static readonly string[] ProofCommonKeys={"proof_version","state","run_id","actor_id","service_id","owner_hash_algorithm","resource_capsule_digest","snapshot_content_sha256","descriptor_digest","origin_receipt_id","origin_receipt_digest","start_origin_capsule_digest","start_origin_owner_digest","chunks","latest_chunk_digest","job_after","accepted_steps","work_lineage_id","cut_work_version"};
        static readonly string[] CheckpointProofKeys={"checkpoint_before_capsule_digest","checkpoint_before_owner_digest","checkpoint_receipt_id","checkpoint_receipt_digest"};
        static readonly string[] CompletedProofKeys={"terminal_before_capsule_digest","terminal_before_owner_digest","completion_receipt_id","completion_receipt_digest"};
        internal static bool Exact(GdDict value,params string[] keys) => value!=null && value.Count==keys.Length && value.Keys.All(k=>k is string text && keys.Contains(text,StringComparer.Ordinal));
        internal static bool ExactPackage(GdDict value)=>Exact(value,PackageKeys);
        internal static bool ExactOwner(GdDict value)=>Exact(value,OwnerKeys);
        internal static bool ExactCapsule(GdDict value)=>Exact(value,CapsuleKeys);
        internal static bool ExactResource(GdDict value)=>Exact(value,ResourceKeys);
        internal static bool Digest(object value)
        { if(!(value is string s)||s.Length!=64)return false;foreach(char c in s)if(!(c>='0'&&c<='9'||c>='a'&&c<='f'))return false;return true; }
        internal static bool IsBinding(GdDict owner)=>owner!=null&&owner.Get("schema_version") is long s&&s==OwnerSchema&&owner.Get("feature_schema") is long f&&f==FeatureSchema&&owner.Get("auxiliary_proof_format") is long p&&p==ProofVersion&&owner.Get("hash_algorithm") as string==PaidHashContext.BitsV2.Algorithm;
        internal static bool IsNewReceipt(string operation)=>operation=="aux_proof_checkpoint_v1"||operation=="aux_proof_complete_v1"||operation=="aux_proof_take_v1";
        internal static bool ProofKeys(GdDict proof)
        {
            if(proof==null||!(proof.Get("proof_version") is long v)||v!=1)return false;
            string state=proof.Get("state") as string;
            if(state=="ongoing"||state=="ready_on_cut")return Exact(proof,ProofCommonKeys.Concat(CheckpointProofKeys).ToArray());
            return state=="completed"&&Exact(proof,ProofCommonKeys.Concat(CompletedProofKeys).ToArray());
        }
        internal static string ProofDigest(GdDict proof)
        {
            if(!ProofKeys(proof))throw new ArgumentException("invalid_auxiliary_proof_keys");
            var projected=proof.DeepCopy();
            if(proof.Get("state") as string=="completed"){projected.Erase("completion_receipt_id");projected.Erase("completion_receipt_digest");}
            else{projected.Erase("checkpoint_receipt_id");projected.Erase("checkpoint_receipt_digest");}
            return PaidHashContext.BitsV2.Hash(projected);
        }
    }
}
