using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Structural DAG plan only. Nodes are not admitted owners and may not be used as before authority.
    internal sealed class ProofPackageGraphPlan
    {
        readonly GdDict _package;readonly string[] _order;
        internal int OriginCount=>_order.Length;
        ProofPackageGraphPlan(GdDict package,string[] order){_package=package.DeepCopy();_order=(string[])order.Clone();}
        internal string OriginDigestAt(int index)=>_order[index];
        internal GdDict CopyCapsuleAt(int index)=>((GdDict)((GdDict)_package.Get("origins")).Get(_order[index])).DeepCopy();
        internal GdDict CopyPackage()=>_package.DeepCopy();
        internal static bool TryPrepare(OwnedProofPackageInput input,ProofResourceBinding binding,out ProofPackageGraphPlan plan,out string reason)
        {
            plan=null;reason="missing_owned_proof_input";if(input==null||binding==null)return false;
            var package=input.CopyPackage();if(!binding.MatchesPackage(package,out reason))return false;
            reason="invalid_proof_origin_graph";
            try
            {
                var origins=(GdDict)package.Get("origins");var proofs=(GdDict)package.Get("proofs");var current=(GdDict)package.Get("current_owner");
                if(origins.Count>64||!AuxiliaryProofOwnerProfile.ExactOwner(current)||!AuxiliaryProofOwnerProfile.IsBinding(current)||
                    PaidHashContext.BitsV2.Hash(current)!=package.Get("current_owner_digest") as string)return false;
                string run=package.Get("run_id") as string,actor=package.Get("actor_id") as string;
                if(run==null||actor==null||run.Length>256||actor.Length>256||string.IsNullOrWhiteSpace(run)||string.IsNullOrWhiteSpace(actor))return false;
                var ordered=new List<Tuple<string,long,long>>();var dependencies=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);var usedProofs=new HashSet<string>(StringComparer.Ordinal);
                foreach(var entry in origins)
                {
                    if(!AuxiliaryProofOwnerProfile.Digest(entry.Key)||!(entry.Value is GdDict capsule)||!AuxiliaryProofOwnerProfile.ExactCapsule(capsule)||
                        !(capsule.Get("capsule_version") is long v)||v!=1||capsule.Get("owner_profile") as string!=AuxiliaryProofOwnerProfile.Profile||capsule.Get("run_id") as string!=run||capsule.Get("actor_id") as string!=actor||
                        capsule.Get("resource_digest") as string!=binding.ResourceCapsuleDigest||!(capsule.Get("owner") is GdDict owner)||
                        !(capsule.Get("owner_revision") is long revision)||!(capsule.Get("command_sequence") is long sequence)||revision<0||sequence<0||
                        !(owner.Get("revision") is long ownRevision)||ownRevision!=revision||!(owner.Get("command_sequence") is long ownSequence)||ownSequence!=sequence||
                        PaidHashContext.BitsV2.Hash(capsule)!=(string)entry.Key)return false;
                    if(!References(owner,proofs,run,actor,binding,usedProofs,out var refs)||!(capsule.Get("origin_refs") is GdArray declared)||declared.Count!=refs.Count)return false;
                    string previous=null;foreach(var item in declared){if(!(item is string digest)||!refs.Contains(digest)||previous!=null&&string.CompareOrdinal(previous,digest)>=0)return false;previous=digest;}
                    ordered.Add(Tuple.Create((string)entry.Key,revision,sequence));dependencies.Add((string)entry.Key,refs);
                }
                if(!References(current,proofs,run,actor,binding,usedProofs,out var currentRefs)||usedProofs.Count!=proofs.Count)return false;
                var metadata=ordered.ToDictionary(t=>t.Item1,t=>t,StringComparer.Ordinal);
                foreach(var row in ordered)foreach(string reference in dependencies[row.Item1])if(!metadata.TryGetValue(reference,out var target)||target.Item2>=row.Item2||target.Item3>=row.Item3)return false;
                if(!(current.Get("revision") is long currentRevision)||!(current.Get("command_sequence") is long currentSequence))return false;
                foreach(string reference in currentRefs)if(!metadata.TryGetValue(reference,out var target)||target.Item2>=currentRevision||target.Item3>=currentSequence)return false;
                var reachable=new HashSet<string>(StringComparer.Ordinal);var pending=new Stack<string>(currentRefs);
                while(pending.Count>0){string next=pending.Pop();if(reachable.Add(next))foreach(string dependency in dependencies[next])pending.Push(dependency);}
                if(reachable.Count!=origins.Count)return false;
                if(!binding.Lease.IsCurrent){reason="resource_epoch_changed";return false;}
                plan=new ProofPackageGraphPlan(package,ordered.OrderBy(t=>t.Item2).ThenBy(t=>t.Item3).ThenBy(t=>t.Item1,StringComparer.Ordinal).Select(t=>t.Item1).ToArray());reason="structurally_owned_unadmitted";return true;
            }
            catch(ArgumentException){return false;}catch(OverflowException){return false;}catch(InvalidCastException){return false;}
        }
        static bool References(GdDict owner,GdDict proofs,string run,string actor,ProofResourceBinding binding,HashSet<string> used,out HashSet<string> refs)
        {
            refs=new HashSet<string>(StringComparer.Ordinal);
            var map=owner.Get("auxiliary_proofs") as GdDict;
            if(map==null){if(!(owner.Get("schema_version") is long legacy)||legacy!=6||!(owner.Get("feature_schema") is long f)||f!=5)return false;return true;}
            if(!AuxiliaryProofOwnerProfile.IsBinding(owner))return false;
            foreach(var entry in map)
            {
                if(!(entry.Key is string receiptId)||!AuxiliaryProofOwnerProfile.Digest(entry.Value)||!(proofs.Get(entry.Value) is GdDict proof)||!AuxiliaryProofOwnerProfile.ProofKeys(proof)||
                    proof.Get("run_id") as string!=run||proof.Get("actor_id") as string!=actor||proof.Get("resource_capsule_digest") as string!=binding.ResourceCapsuleDigest||proof.Get("snapshot_content_sha256") as string!=binding.SnapshotContentSha256||
                    AuxiliaryProofOwnerProfile.ProofDigest(proof)!=(string)entry.Value)return false;
                bool complete=proof.Get("state") as string=="completed";string reciprocal=complete?"completion_receipt_id":"checkpoint_receipt_id";
                if(proof.Get(reciprocal) as string!=receiptId)return false;used.Add((string)entry.Value);
                foreach(string field in complete?new[]{"start_origin_capsule_digest","terminal_before_capsule_digest"}:new[]{"start_origin_capsule_digest","checkpoint_before_capsule_digest"})
                {if(!AuxiliaryProofOwnerProfile.Digest(proof.Get(field)))return false;refs.Add((string)proof.Get(field));}
            }
            if(owner.Get("receipts") is GdDict receipts)foreach(var entry in receipts)if(entry.Value is GdDict receipt&&receipt.Get("command") is GdDict command&&command.Get("operation") as string=="aux_proof_take_v1")
            {if(!AuxiliaryProofOwnerProfile.Digest(command.Get("take_before_capsule_digest")))return false;refs.Add((string)command.Get("take_before_capsule_digest"));}
            return true;
        }
    }
}
