using System;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Caller-published current resource authority only. Package bytes cannot create an admission reader.
    internal sealed class ProofResourceBinding
    {
        readonly GdDict _capsule;
        internal readonly ResourceAuthorityLease Lease;
        internal readonly string SnapshotContentSha256,ResourceCapsuleDigest;
        ProofResourceBinding(ResourceAuthorityLease lease,GdDict capsule)
        {Lease=lease;_capsule=capsule.DeepCopy();SnapshotContentSha256=lease.Snapshot.ContentSha256;ResourceCapsuleDigest=PaidHashContext.BitsV2.Hash(_capsule);}
        internal static bool TryCapture(ResourceAuthorityLease callerOwnedLease,out ProofResourceBinding binding,out string reason)
        {
            binding=null;reason="current_trusted_resource_required";
            if(callerOwnedLease==null||!callerOwnedLease.IsCurrent)return false;
            if(!callerOwnedLease.Snapshot.TryCreateProofResourceCapsule(out var capsule,out reason))return false;
            var prepared=new ProofResourceBinding(callerOwnedLease,capsule);
            if(!callerOwnedLease.IsCurrent){reason="resource_epoch_changed";return false;}
            binding=prepared;reason="trusted_resource_binding";return true;
        }
        internal GdDict CopyCapsule()=>_capsule.DeepCopy();
        internal bool MatchesPackage(GdDict package,out string reason)
        {
            reason="resource_epoch_changed";if(!Lease.IsCurrent)return false;
            reason="unsupported_resource_fingerprint";
            if(package==null||package.Get("snapshot_content_sha256") as string!=SnapshotContentSha256||package.Get("current_resource_digest") as string!=ResourceCapsuleDigest||
                !(package.Get("resources") is GdDict resources)||resources.Count!=1||!(resources.Get(ResourceCapsuleDigest) is GdDict capsule)||
                !AuxiliaryProofOwnerProfile.ExactResource(capsule)||!V.VariantEquals(_capsule,capsule))return false;
            if(!Lease.IsCurrent){reason="resource_epoch_changed";return false;}reason="matched_current_trusted_resource";return true;
        }
    }
}
