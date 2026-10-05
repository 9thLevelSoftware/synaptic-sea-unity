using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public sealed class ProofPackageGraphPlanTests
    {
        static GdDict EmptyOwner()=>new GdDict{{"schema_version",7L},{"feature_schema",5L},{"hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"auxiliary_proof_format",1L},{"revision",1L},{"registry",new GdDict()},{"holders",new GdDict()},{"machinery",new GdDict()},{"receipts",new GdDict()},{"physical_slots",new GdDict()},{"component_work",new GdDict()},{"participating_state",new GdDict()},{"command_sequence",1L},{"registered_owners",new GdArray()},{"domain_mode","craft_only"},{"auxiliary_proofs",new GdDict()}};
        static GdDict Package(ProofResourceBinding b,GdDict owner)=>new GdDict{{"package_version",1L},{"owner_profile",AuxiliaryProofOwnerProfile.Profile},{"hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"run_id","run"},{"actor_id","actor"},{"current_owner",owner},{"current_owner_digest",PaidHashContext.BitsV2.Hash(owner)},{"current_resource_digest",b.ResourceCapsuleDigest},{"snapshot_content_sha256",b.SnapshotContentSha256},{"origins",new GdDict()},{"resources",new GdDict{{b.ResourceCapsuleDigest,b.CopyCapsule()}}},{"proofs",new GdDict()}};
        static OwnedProofPackageInput Own(GdDict package)
        {Assert.IsTrue(ProofPackageCodec.TryOwnEnvelope(new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",ComponentDomainCodec.Encode(package,ComponentDomainCodec.BitExactSchema)}},out var input,out _));return input;}
        static GdDict Proof(ProofResourceBinding b,string start,string before,string receipt)
        {
            string hex=new string('a',64);
            return new GdDict{{"proof_version",1L},{"state","ongoing"},{"run_id","run"},{"actor_id","actor"},{"service_id","utility"},{"owner_hash_algorithm",PaidHashContext.BitsV2.Algorithm},
                {"resource_capsule_digest",b.ResourceCapsuleDigest},{"snapshot_content_sha256",b.SnapshotContentSha256},{"descriptor_digest",hex},{"origin_receipt_id","start"},{"origin_receipt_digest",hex},
                {"start_origin_capsule_digest",start},{"start_origin_owner_digest",hex},{"chunks",new GdArray()},{"latest_chunk_digest",hex},{"job_after",new GdDict()},
                {"accepted_steps",0L},{"work_lineage_id","auxiliary-work:"+hex},{"cut_work_version",0L},{"checkpoint_before_capsule_digest",before},{"checkpoint_before_owner_digest",hex},{"checkpoint_receipt_id",receipt},{"checkpoint_receipt_digest",hex}};
        }
        static GdDict Capsule(ProofResourceBinding b,GdDict owner,GdArray refs)=>new GdDict{{"capsule_version",1L},{"owner_profile",AuxiliaryProofOwnerProfile.Profile},{"run_id","run"},{"actor_id","actor"},
            {"owner_revision",owner.Get("revision")},{"command_sequence",owner.Get("command_sequence")},{"resource_digest",b.ResourceCapsuleDigest},{"owner",owner},{"origin_refs",refs}};
        static GdDict Chain(ProofResourceBinding b,out string first,out string second,out string third,long secondRevision=2,long secondSequence=2,bool reverseRefs=false,bool omitFirst=false)
        {
            var roots=new GdDict();var proofs=new GdDict();
            var a=new GdDict{{"schema_version",6L},{"feature_schema",5L},{"hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"revision",1L},{"command_sequence",1L}};
            var ca=Capsule(b,a,new GdArray());first=PaidHashContext.BitsV2.Hash(ca);if(!omitFirst)roots[first]=ca;
            var pb=Proof(b,first,first,"checkpointB");string hb=AuxiliaryProofOwnerProfile.ProofDigest(pb);proofs[hb]=pb;
            var bo=EmptyOwner();bo["revision"]=secondRevision;bo["command_sequence"]=secondSequence;bo["auxiliary_proofs"]=new GdDict{{"checkpointB",hb}};
            var cb=Capsule(b,bo,GdArray.Of(first));second=PaidHashContext.BitsV2.Hash(cb);roots[second]=cb;
            var pc=Proof(b,first,second,"checkpointC");string hc=AuxiliaryProofOwnerProfile.ProofDigest(pc);proofs[hc]=pc;
            var co=EmptyOwner();co["revision"]=3L;co["command_sequence"]=3L;co["auxiliary_proofs"]=new GdDict{{"checkpointC",hc}};
            string[] refs={first,second};Array.Sort(refs,StringComparer.Ordinal);if(reverseRefs)Array.Reverse(refs);
            var cc=Capsule(b,co,new GdArray(refs));third=PaidHashContext.BitsV2.Hash(cc);roots[third]=cc;
            var current=EmptyOwner();current["revision"]=4L;current["command_sequence"]=4L;
            var pd=Proof(b,first,third,"checkpointD");string hd=AuxiliaryProofOwnerProfile.ProofDigest(pd);proofs[hd]=pd;current["auxiliary_proofs"]=new GdDict{{"checkpointD",hd}};
            var package=Package(b,current);package["origins"]=roots;package["proofs"]=proofs;return package;
        }
        [Test] public void ThreeReachableNodesSortTopologicallyAndRejectMissingCounterAndReferenceOrder()
        {
            var old=CoreServices.Resources;try
            {
                ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(new Dictionary<string,string>()));Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var lease,out _));Assert.IsTrue(ProofResourceBinding.TryCapture(lease,out var binding,out _));
                var package=Chain(binding,out string first,out string second,out string third);
                Assert.IsTrue(ProofPackageGraphPlan.TryPrepare(Own(package),binding,out var plan,out _));Assert.AreEqual(3,plan.OriginCount);
                Assert.AreEqual(first,plan.OriginDigestAt(0));Assert.AreEqual(second,plan.OriginDigestAt(1));Assert.AreEqual(third,plan.OriginDigestAt(2));
                Assert.IsFalse(DomainBundle.TryCreate((GdDict)package.Get("current_owner"),out _,out _));
                Assert.IsFalse(ProofPackageGraphPlan.TryPrepare(Own(Chain(binding,out _,out _,out _,omitFirst:true)),binding,out _,out _));
                Assert.IsFalse(ProofPackageGraphPlan.TryPrepare(Own(Chain(binding,out _,out _,out _,secondRevision:1)),binding,out _,out _));
                Assert.IsFalse(ProofPackageGraphPlan.TryPrepare(Own(Chain(binding,out _,out _,out _,secondSequence:1)),binding,out _,out _));
                Assert.IsFalse(ProofPackageGraphPlan.TryPrepare(Own(Chain(binding,out _,out _,out _,reverseRefs:true)),binding,out _,out _));
            }finally{CoreServices.Resources=old;}
        }
        [Test] public void StructurallyClosedPlanHasNoOwnerAuthorityAndKeepsPrivateCopies()
        {
            var old=CoreServices.Resources;try
            {
                ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(new Dictionary<string,string>()));Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var lease,out _));Assert.IsTrue(ProofResourceBinding.TryCapture(lease,out var binding,out _));
                var owner=EmptyOwner();var package=Package(binding,owner);Assert.IsTrue(ProofPackageGraphPlan.TryPrepare(Own(package),binding,out var plan,out string reason));Assert.AreEqual("structurally_owned_unadmitted",reason);Assert.AreEqual(0,plan.OriginCount);
                package["run_id"]="changed";var copy=plan.CopyPackage();copy["run_id"]="also_changed";Assert.AreEqual("run",plan.CopyPackage().Get("run_id"));Assert.IsFalse(DomainBundle.TryCreate(owner,out _,out _));
            }finally{CoreServices.Resources=old;}
        }
        [Test] public void UnreachableOriginsAndChangedOwnerDigestOrResourceEpochRefuse()
        {
            var old=CoreServices.Resources;try
            {
                ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(new Dictionary<string,string>()));Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var lease,out _));Assert.IsTrue(ProofResourceBinding.TryCapture(lease,out var binding,out _));
                var owner=EmptyOwner();var package=Package(binding,owner);owner["revision"]=2L;Assert.IsFalse(ProofPackageGraphPlan.TryPrepare(Own(package),binding,out _,out _));
                owner=EmptyOwner();package=Package(binding,owner);var historical=new GdDict{{"schema_version",6L},{"feature_schema",5L},{"hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"revision",0L},{"command_sequence",0L}};
                var capsule=new GdDict{{"capsule_version",1L},{"owner_profile",AuxiliaryProofOwnerProfile.Profile},{"run_id","run"},{"actor_id","actor"},{"owner_revision",0L},{"command_sequence",0L},{"resource_digest",binding.ResourceCapsuleDigest},{"owner",historical},{"origin_refs",new GdArray()}};
                ((GdDict)package.Get("origins"))[PaidHashContext.BitsV2.Hash(capsule)]=capsule;Assert.IsFalse(ProofPackageGraphPlan.TryPrepare(Own(package),binding,out _,out _));
                package=Package(binding,EmptyOwner());var input=Own(package);ResourceAuthorityPublication.Invalidate();Assert.IsFalse(ProofPackageGraphPlan.TryPrepare(input,binding,out _,out _));
            }finally{CoreServices.Resources=old;}
        }
    }
}
