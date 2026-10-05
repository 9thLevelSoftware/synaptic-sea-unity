using System;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class DomainTransactionCoordinator
    {
        // Called by the selected paid restore only after prospective world/owner validation.
        // An immutable fully admitted owner handle is preserved; its raw schema7 summary is
        // never reinterpreted by the ordinary raw-summary constructor or temporarily admitted globally.
        internal static DomainTransactionCoordinator CreateFromAdmittedCheckpoint(CheckpointProofAdmission.Result admitted,
            Action<string> stageHook=null,Action<GdDict> notification=null,Action<GdDict> publishViews=null)
            =>new DomainTransactionCoordinator(admitted,stageHook,notification,publishViews);
        DomainTransactionCoordinator(CheckpointProofAdmission.Result admitted,Action<string> stageHook,
            Action<GdDict> notification,Action<GdDict> publishViews)
        {
            if(admitted==null||admitted.Lease==null||!admitted.Lease.IsCurrent)
                throw new InvalidOperationException("checkpoint_restore_admission_not_current");
            var owner=admitted.OwnedDomainForReplacement;
            if(owner==null||owner.SchemaVersion!=7)
                throw new InvalidOperationException("checkpoint_restore_owner_profile_required");
            _current=owner;
            _stageHook=stageHook;_notification=notification;_publishViews=publishViews;
        }
    }
}
