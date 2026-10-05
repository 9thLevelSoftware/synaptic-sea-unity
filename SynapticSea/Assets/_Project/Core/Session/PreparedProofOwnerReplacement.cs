using System;
using System.Linq;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Closed terminal/take replacement; snapshot checkpoint output can never be adopted through this path.
    internal sealed class PreparedProofOwnerReplacement
    {
        readonly DomainBundle _expected,_replacement;
        readonly ResourceAuthorityLease _lease;
        readonly CheckpointProofAdmission.Result _source;
        internal CheckpointProofAdmission.Result SourcePackage=>_source;
        ParticipantPublicationAttempt _authorized;
        bool _used;
        PreparedProofOwnerReplacement(DomainBundle expected,CheckpointProofAdmission.Result result)
        {_expected=expected;_source=result;_replacement=result.OwnedDomainForReplacement;_lease=result.Lease;}
        internal static bool TryPrepare(CheckpointProofAdmission.Result result,DomainBundle expectedCurrent,out PreparedProofOwnerReplacement prepared,out string reason)
        {
            prepared=null;reason="closed_terminal_owner_required";if(result==null||expectedCurrent==null||!result.Lease.IsCurrent)return false;
            var owner=result.CopyOwner();var latest=owner.GetDictOrEmpty("receipts").Values.OfType<GdDict>().OrderByDescending(r=>r.GetInt("revision")).FirstOrDefault();
            if(latest==null)return false;var command=latest.GetDictOrEmpty("command");string operation=command.Get("operation") as string;
            string key=operation=="aux_proof_complete_v1"?"terminal_before_owner_digest":operation=="aux_proof_take_v1"?"take_before_owner_digest":null;
            if(key==null)return false;
            // All full graph copies/hashes happen during private preparation, never inside final publication.
            if(!ReferenceEquals(expectedCurrent.HashContext,PaidHashContext.BitsV2)||command.Get(key) as string!=expectedCurrent.HashContext.Hash(expectedCurrent.GetSummary())) {reason="proof_replacement_before_mismatch";return false;}
            if(!result.Lease.IsCurrent){reason="resource_epoch_changed";return false;}
            prepared=new PreparedProofOwnerReplacement(expectedCurrent,result);reason="proof_replacement_prepared";return true;
        }
        internal bool TryAuthorizeUnderGate(DomainBundle current,ParticipantPublicationAttempt attempt)
        {
            if(attempt==null||!attempt.IsOpenUnderGate||_used||!ReferenceEquals(current,_expected)||!_lease.IsCurrent)return false;
            _authorized=attempt;return true;
        }
        internal DomainBundle ConsumeUnderGate(DomainBundle current,ParticipantPublicationAttempt attempt)
        {
            if(attempt==null||!attempt.IsOpenUnderGate||!ReferenceEquals(_authorized,attempt)||_used||!ReferenceEquals(current,_expected)||!_lease.IsCurrent)throw new InvalidOperationException("proof_replacement_not_authorized");
            _used=true;return _replacement;
        }
    }
}
