using System;
using System.Collections.Generic;
namespace SynapticSea.Core.Variant
{
    internal sealed partial class ParticipantProjectionRegistry
    {
        MaintainedRegistryPublication _maintenancePublication;
        long _maintenanceReservedUnits;
        bool _maintenanceAuxiliaryCharged;
        internal sealed class MaintainedRegistryPublication
        {
            readonly ParticipantProjectionRegistry _registry;
            readonly ParticipantProjectionCohort _cohort;
            readonly MaintainedLifetimeCursor _lifetimes;
            readonly ParticipantProjectionCohort.MaintainedCandidate _candidate;
            readonly MaintainedGraphAdmissionCursor _admission;
            readonly ProjectionVersionTable _before, _after;
            readonly ProjectionGraphPlan _beforePlan, _afterPlan;
            readonly ProjectionRootDescriptor _beforeDescriptor, _afterDescriptor, _rollbackDescriptor;
            readonly Dictionary<ulong,Lifetime> _nextLifetimes;
            readonly ulong _revision, _epoch, _lifetimeEpoch;
            readonly long _nextUnits, _rollbackUnits;
            ParticipantPublicationAttempt _attempt;
            int _state;
            private MaintainedRegistryPublication(ParticipantProjectionRegistry registry, ParticipantProjectionCohort cohort,
                MaintainedLifetimeCursor lifetimes, ParticipantProjectionCohort.MaintainedCandidate candidate,
                MaintainedGraphAdmissionCursor admission, ProjectionRootDescriptor after, ProjectionRootDescriptor rollback,
                long units, long rollbackUnits, object issuer)
            {
                _registry = registry; _cohort = cohort; _lifetimes = lifetimes; _candidate = candidate; _admission = admission;
                _before = registry._table; _after = admission.PreparedTable;
                _beforePlan = registry._plan; _afterPlan = admission.PreparedPlan;
                _beforeDescriptor = registry._descriptor; _afterDescriptor = after; _rollbackDescriptor = rollback;
                _nextLifetimes = lifetimes.CapturePublicationLifetimes(cohort, issuer); _nextUnits = units; _rollbackUnits = rollbackUnits;
                _revision = registry._revision; _epoch = registry._readinessEpoch; _lifetimeEpoch = registry._maintenanceLifetimeEpoch;
            }
            internal static MaintainedRegistryPublication Create(ParticipantProjectionRegistry registry, ParticipantProjectionCohort cohort,
                MaintainedLifetimeCursor lifetimes, ParticipantProjectionCohort.MaintainedCandidate candidate,
                MaintainedGraphAdmissionCursor admission, ProjectionRootDescriptor after, ProjectionRootDescriptor rollback,
                long auxiliaryUnits, long beforeAuxiliaryUnits, object issuer)
            {
                if (cohort == null || !cohort.OwnsMaintenanceIssuer(issuer) || auxiliaryUnits < 0 || beforeAuxiliaryUnits < 0)
                    throw new ArgumentException("foreign_maintenance_issuer");
                long units = checked(OwnedTableUnits(admission.PreparedTable) + lifetimes.PreparedCount + auxiliaryUnits);
                lock (CommonParticipantGate.SyncRoot)
                {
                    if (!ReferenceEquals(registry._ownerCohort, cohort) || !cohort.Ready || !lifetimes.MatchesUnderGate() ||
                        !candidate.IsForCohort(cohort) || !admission.IsForCandidate(candidate) || registry._maintenancePublication != null ||
                        registry._preparation != null || registry._ownerPreparation != null || registry._revision > ulong.MaxValue - 2 ||
                        after.OwnerStamp != registry._descriptor.OwnerStamp + 1 || rollback.OwnerStamp != registry._descriptor.OwnerStamp + 2)
                        throw new InvalidOperationException("stale_or_pending_maintenance_publication");
                    long rollbackUnits = checked(OwnedTableUnits(registry._table) + lifetimes.PreparedCount + beforeAuxiliaryUnits);
                    long reservation = Math.Max(units, Math.Max(0L, rollbackUnits - registry._currentUnits));
                    // Reserve BOTH success and immediate rollback ownership. Do not revoke old cuts.
                    if (reservation > registry.Limits.MaximumRetainedUnits || registry._retainedUnits > registry.Limits.MaximumRetainedUnits - reservation)
                        throw new InvalidOperationException("maintenance_retention_pressure");
                    var result = new MaintainedRegistryPublication(registry, cohort, lifetimes, candidate, admission, after, rollback, units, rollbackUnits, issuer);
                    registry._maintenancePublication = result; registry._maintenanceReservedUnits = reservation; registry._retainedUnits += reservation;
                    return result;
                }
            }
            internal bool MatchesUnderGate(ParticipantPublicationAttempt attempt)
            {
                CommonParticipantGate.RequireHeld();
                return _state == 0 && attempt != null && attempt.IsOpenUnderGate && ReferenceEquals(_registry._maintenancePublication, this) &&
                    _lifetimes.MatchesUnderGate() && ReferenceEquals(_registry._table, _before) && ReferenceEquals(_registry._plan, _beforePlan) &&
                    ReferenceEquals(_registry._descriptor, _beforeDescriptor) && _registry._revision == _revision &&
                    _registry._readinessEpoch == _epoch && _registry._maintenanceLifetimeEpoch == _lifetimeEpoch;
            }
            internal void InstallAfterParticipantGuardsUnderGate(ParticipantPublicationAttempt attempt)
            {
                // Private-cohort issuer validates the entire closed certificate before ANY participant write.
                // After that fence these are assignments only; no re-enumeration or graph admission here.
                long oldUnits = _registry._currentUnits;
                _registry._currentUnits = _nextUnits; _registry._maintenanceReservedUnits = oldUnits + _registry._maintenanceReservedUnits - _nextUnits;
                _registry._maintenanceAuxiliaryCharged = true;
                _registry._table = _after; _registry._plan = _afterPlan; _registry._descriptor = _afterDescriptor;
                _registry._lifetimes = _nextLifetimes; _registry._activeHandles = _nextLifetimes.Count;
                _registry._revision = _revision + 1; _attempt = attempt; _state = 1;
            }
            internal bool InstalledUnderGate(ParticipantPublicationAttempt attempt)
            {
                CommonParticipantGate.RequireHeld();
                return _state == 1 && attempt != null && attempt.IsOpenUnderGate && ReferenceEquals(attempt, _attempt) &&
                    ReferenceEquals(_registry._maintenancePublication, this) && ReferenceEquals(_registry._table, _after) &&
                    _registry._revision == _revision + 1 && _registry._readinessEpoch == _epoch;
            }
            internal void RollbackUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (!InstalledUnderGate(attempt)) throw new InvalidOperationException("wrong_maintenance_rollback_attempt");
                long totalOwned = _registry._currentUnits + _registry._maintenanceReservedUnits; _registry._currentUnits = _rollbackUnits;
                _registry._maintenanceReservedUnits = totalOwned - _rollbackUnits;
                _registry._table = _before; _registry._plan = _beforePlan; _registry._descriptor = _rollbackDescriptor;
                _registry._revision = _revision + 2;
                // Keep next lifetime admissions: old payload restoration never rewinds numeric authority.
                ReleaseUnderGate(); _state = 2;
            }
            internal void CompleteUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (!InstalledUnderGate(attempt)) throw new InvalidOperationException("wrong_maintenance_completion_attempt");
                ReleaseUnderGate(); _state = 3;
            }
            internal void CancelUnderGate()
            { CommonParticipantGate.RequireHeld(); if (_state == 0) { ReleaseUnderGate(); _state = 2; } }
            void ReleaseUnderGate()
            {
                if (ReferenceEquals(_registry._maintenancePublication, this))
                { _registry._retainedUnits -= _registry._maintenanceReservedUnits; _registry._maintenanceReservedUnits = 0; _registry._maintenancePublication = null; }
            }
        }
        void ReleaseMaintenancePublicationUnderGate() => _maintenancePublication?.CancelUnderGate();
    }
}
