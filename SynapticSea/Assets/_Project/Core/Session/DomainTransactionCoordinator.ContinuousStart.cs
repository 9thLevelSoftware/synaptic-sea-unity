using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class DomainTransactionCoordinator
    {
        internal long CurrentRevision=>_current.Revision;
        internal object CurrentSourceIdentity=>_current;
        internal long ContinuousRevisionUnderGate { get { CommonParticipantGate.RequireHeld(); return _current.Revision; } }
        // Owner-only start: stock preparation/conservation followed by fresh worker admission.
        // Neither live participant restoration nor ordinary publication callbacks are invoked.
        internal bool TryPrepareContinuousStartPublication(string transaction, AdmittedWorkerOrigin admitted,
            out ContinuousStartPublication publication, out string reason)
        {
            publication=null;reason="invalid_admitted_start";
            if(admitted==null||!admitted.Granted||!admitted.Epoch.Active||!admitted.Lease.IsCurrent||_busy||
                !_pending.TryGetValue(transaction,out var pending)||pending.ExpectedRevision!=_current.Revision||
                pending.Result.GetString("operation")!="aux_start"||pending.Command.GetString("operation")!="aux_start")return false;
            var candidate=admitted.Bundle.GetSummary();var before=_current.GetSummary();
            if(admitted.OwnerDigest!=_current.HashContext.Hash(pending.Candidate)||
                admitted.OwnerDigest!=_current.HashContext.Hash(candidate)||
                !AuxiliaryServiceState.Conserved(before,candidate,pending.Result))return false;
            foreach(string key in new[]{"inventory","progression","training"})
                if(!_current.HashContext.Equal(before.GetDictOrEmpty("participating_state").Get(key),candidate.GetDictOrEmpty("participating_state").Get(key)))
                {reason="start_changed_participant";return false;}
            publication=ContinuousStartPublication.Create(this,_current,pending,admitted,transaction);
            reason="";return true;
        }
        internal ContinuousStartPending CaptureContinuousStartPending(string transaction)
        {
            if(!_pending.TryGetValue(transaction,out var pending)||pending.Command.GetString("operation")!="aux_start")
                throw new InvalidOperationException("continuous_start_pending_missing");
            return ContinuousStartPending.Issue(this,transaction,pending);
        }
        internal sealed class ContinuousStartPending
        {
            readonly DomainTransactionCoordinator _owner;readonly string _transaction;readonly object _pending;
            ContinuousStartPending(DomainTransactionCoordinator owner,string transaction,object pending){_owner=owner;_transaction=transaction;_pending=pending;}
            internal static ContinuousStartPending Issue(DomainTransactionCoordinator owner,string transaction,object pending)
                =>new ContinuousStartPending(owner,transaction,pending);
            internal void CancelExact()
            {
                if(_owner._pending.TryGetValue(_transaction,out var current)&&ReferenceEquals(current,_pending))
                {_owner._pending.Remove(_transaction);_owner._pendingCommands.Remove(current.Command.GetString("command_id"));}
            }
        }
        internal sealed class ContinuousStartPublication
        {
            readonly DomainTransactionCoordinator _owner;
            readonly DomainBundle _before;readonly Pending _pending;
            readonly AdmittedWorkerOrigin _admitted;readonly string _transaction;
            bool _used;
            ContinuousStartPublication(DomainTransactionCoordinator owner,DomainBundle before,Pending pending,AdmittedWorkerOrigin admitted,string transaction)
            {_owner=owner;_before=before;_pending=pending;_admitted=admitted;_transaction=transaction;}
            internal static ContinuousStartPublication Create(DomainTransactionCoordinator owner,DomainBundle before,object pending,AdmittedWorkerOrigin admitted,string transaction)
                =>new ContinuousStartPublication(owner,before,(Pending)pending,admitted,transaction);
            internal bool MatchesUnderGate()
            {
                CommonParticipantGate.RequireHeld();
                return !_used&&!_owner._busy&&ReferenceEquals(_owner._current,_before)&&_admitted.Granted&&
                    _admitted.Epoch.Active&&_admitted.Lease.IsCurrent&&_owner._pending.TryGetValue(_transaction,out var current)&&ReferenceEquals(current,_pending);
            }
            internal bool TryInstallUnderGate(ParticipantPublicationAttempt attempt)
            {
                CommonParticipantGate.RequireHeld();
                if(attempt==null||!attempt.IsOpenUnderGate||!MatchesUnderGate())return false;
                _owner._current=_admitted.Bundle;_used=true;return true;
            }
            internal void RetirePendingAfterGate()
            {
                if(!_used)return;
                if(_owner._pending.TryGetValue(_transaction,out var current)&&ReferenceEquals(current,_pending))
                {_owner._pending.Remove(_transaction);_owner._pendingCommands.Remove(_pending.Command.GetString("command_id"));}
            }
        }
    }
}
