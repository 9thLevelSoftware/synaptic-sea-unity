using System;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    public sealed partial class SaveCommitCoordinator
    {
        OwnedContinuousSaveOutput _continuousOutputTicket;
        // The ordinary DTO entry remains incapable of minting output authority.
        internal GdDict CommitContinuousCut(OwnedContinuousSaveOutput output)
        {
            lock (_gate)
            {
                if (_mutating || _continuousOutputTicket != null) return Result(false,"reentrant",output?.RunId ?? "",output?.SlotId ?? "");
                if (output == null || _continuousReadPolicy == null ||
                    !ReferenceEquals(output.CurrentBinding,_continuousBinding) ||
                    !ReferenceEquals(output.ReaderPolicy,_continuousReadPolicy) ||
                    !output.IsCurrentForOutput(out _) || !output.TryCopyPayload(out GdDict payload,out _))
                    return Result(false,"continuous_output_authority_required",output?.RunId ?? "",output?.SlotId ?? "");
                _continuousOutputTicket = output;
                try { return Commit(payload,output.RunId,output.SlotId); }
                finally { _continuousOutputTicket = null; }
            }
        }
        void RequireContinuousGenerationAdvance(Candidate child,Candidate parent)
        {
            if (child.ContinuousAdmission == null || parent.ContinuousAdmission == null)
                throw new Refusal("continuous_profile_parent_mismatch");
            if (!ProofGenerationComparison.TryCompare(parent.ContinuousAdmission.PaidAdmission,
                    child.ContinuousAdmission.PaidAdmission,out var comparison,out string reason) || !comparison.ResourcesCurrent)
                throw new Refusal(reason);
            if (!comparison.RequiresOutputSourceBinding) return;
            // Validation/recovery checks structure; actual output additionally binds its original source.
            if (_continuousOutputTicket == null) return;
            var package=child.ContinuousAdmission.PaidAdmission.CopyPackage();
            GdDict exactBefore=null;
            foreach (object value in package.GetDictOrEmpty("origins").Values)
                if (value is GdDict capsule && capsule.Get("owner") is GdDict owner &&
                    PaidHashContext.BitsV2.Hash(owner)==comparison.RequiredBeforeOwnerDigest) {exactBefore=owner;break;}
            if (exactBefore==null || !_continuousOutputTicket.MatchesBeforeOwner(exactBefore))
                throw new Refusal("continuous_output_source_mismatch");
        }
    }
}
