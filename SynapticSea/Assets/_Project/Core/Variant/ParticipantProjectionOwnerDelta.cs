using System;
using SynapticSea.Core.Services;

namespace SynapticSea.Core.Variant
{
    internal sealed partial class ProjectionNodeHandle
    {
        // Page minting is outside the registration fence. Only exact object registration advances issuance.
        internal bool TryPrepareScalarVersion(ProjectionNodeVersion before, int index, ProjectionValue value, out ProjectionNodeVersion result, out string reason)
        {
            result = null; ulong baseVersion;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (_retired || _registry.IsUnavailableUnderGate || !_registry.HandleCurrentUnderGate(Id, _issuer)) { reason = "projection_node_unavailable"; return false; }
                if (before == null || !before.Id.Equals(Id) || before.Kind != _kind || !before.HasIssuer(_issuer) || before.Version > _version || index < 0 || index >= before.EntryCount || value.IsChild || before.GetEntry(index).Value.IsChild)
                { reason = "invalid_owner_scalar_slot"; return false; }
                if (_version == ulong.MaxValue) { reason = "projection_version_exhausted"; return false; }
                baseVersion = _version;
            }
            var candidate = before.ReplaceValue(baseVersion + 1, index, value);
            lock (CommonParticipantGate.SyncRoot)
            {
                if (_retired || _registry.IsUnavailableUnderGate || !_registry.HandleCurrentUnderGate(Id, _issuer) || _version != baseVersion) { reason = "stale_projection_issuance"; return false; }
                _registry.CheckMaintenanceLifetimeCanAdvanceUnderGate();
                _version = candidate.Version; _registry.RecordIssuedUnderGate(_issuer, candidate); result = candidate; reason = ""; return true;
            }
        }
        internal bool TryPrepareInitialVersion(ProjectionEntry[] ownedEntries, out ProjectionNodeVersion result)
        {
            result = null; ulong expected;
            lock (CommonParticipantGate.SyncRoot)
            { if (_retired || _registry.IsUnavailableUnderGate || !_registry.HandleCurrentUnderGate(Id, _issuer) || _version == ulong.MaxValue) return false; expected = _version; }
            var candidate = new ProjectionNodeVersion(Id, expected + 1, _kind, ownedEntries, _registry.Limits.MaximumEntriesPerNode, _issuer);
            lock (CommonParticipantGate.SyncRoot)
            {
                if (_retired || _registry.IsUnavailableUnderGate || !_registry.HandleCurrentUnderGate(Id, _issuer) || _version != expected) return false;
                _registry.CheckMaintenanceLifetimeCanAdvanceUnderGate();
                _version = candidate.Version; _registry.RecordIssuedUnderGate(_issuer, candidate); result = candidate; return true;
            }
        }
    }
    internal sealed partial class ParticipantProjectionRegistry
    {
        ResourceAuthorityLease _ownerLease;
        ParticipantProjectionCohort _ownerCohort;
        bool _ownerLeaseRevoked;
        OwnerScalarRegistryPlan _ownerPreparation;
        long _ownerPreparationUnits;
        internal void BindOwnerLeaseUnderGate(ParticipantProjectionCohort cohort, ResourceAuthorityLease lease)
        {
            CommonParticipantGate.RequireHeld();
            if (_ownerLease != null || _ownerCohort != null || cohort == null || lease == null || !lease.IsCurrent || !cohort.CanBindRegistryUnderGate(this, lease)) throw new InvalidOperationException("invalid_owned_projection_lease");
            _ownerLease = lease; _ownerCohort = cohort;
        }
        bool OwnerLeaseCurrentUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            if (_ownerLease == null) return true;
            if (_ownerLeaseRevoked) return false;
            if (_ownerLease.IsCurrent) return true;
            _ownerLeaseRevoked = true;
            InvalidateUnderGate(ProjectionReadiness.RebuildRequired, "owned_projection_resource_revoked"); return false;
        }
        void ReleaseOwnerPreparationUnderGate()
        { if (_ownerPreparation != null) { _retainedUnits -= _ownerPreparationUnits; _ownerPreparationUnits = 0; _ownerPreparation = null; } }
        internal bool TryPrepareOwnerScalar(ParticipantProjectionCohort cohort, ProjectionNodeVersion before, ProjectionNodeVersion issued,
            ProjectionRootDescriptor after, ProjectionRootDescriptor rollback, out OwnerScalarRegistryPlan plan)
        {
            plan = null; ProjectionVersionTable table; ProjectionGraphPlan graph; ProjectionRootDescriptor descriptor; ulong revision, epoch;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (before == null || issued == null || cohort == null || !ReferenceEquals(_ownerCohort, cohort) || !OwnerLeaseCurrentUnderGate() || _readiness != ProjectionReadiness.ReadyPartialDiagnostic || _descriptor.OwnerStamp > ulong.MaxValue - 2 || _preparation != null || _ownerPreparation != null || _maintenancePublication != null || _revision > ulong.MaxValue - 2 || !ReferenceEquals(_table.Get(before.Id), before)) return false;
                if (!ExactIssuedUnderGate(issued) || issued.Version <= _lifetimes[issued.Id.Value].LastAdmitted) return false;
                table = _table; graph = _plan; descriptor = _descriptor; revision = _revision; epoch = _readinessEpoch;
            }
            // Verify actual immutable topology, not a caller-provided validity boolean.
            if (!SameScalarTopology(before, issued) || !SameOwnedDescriptor(descriptor, after, descriptor.OwnerStamp + 1) || !SameOwnedDescriptor(descriptor, rollback, descriptor.OwnerStamp + 2)) return false;
            var candidate = table.Set(issued.Id, issued);
            long units = OwnedTableUnits(candidate);
            var prepared = new OwnerScalarRegistryPlan(this, cohort, before, issued, table, candidate, graph, descriptor, after, rollback, revision, epoch);
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!ReferenceEquals(_ownerCohort, cohort) || !OwnerLeaseCurrentUnderGate() || _ownerPreparation != null || _preparation != null || _maintenancePublication != null || !ReferenceEquals(_table, table) || _revision != revision || _readinessEpoch != epoch || !ExactIssuedUnderGate(issued)) return false;
                while (_retainedUnits > Limits.MaximumRetainedUnits - units)
                {
                    if (_pins.Count > 0) _pins[0].CancelUnderGate("owner_scalar_retention_pressure");
                    else if (_outputs.Count > 0) _outputs[0].RevokeUnderGate("owner_scalar_retention_pressure");
                    else { InvalidateUnderGate(ProjectionReadiness.Capacity, "owner_scalar_retention_capacity"); return false; }
                }
                _ownerPreparation = prepared; _ownerPreparationUnits = units; _retainedUnits += units; plan = prepared; return true;
            }
        }
        internal bool OwnerDescriptorCurrentUnderGate(ParticipantProjectionCohort cohort, ulong revision)
        { CommonParticipantGate.RequireHeld(); return ReferenceEquals(_ownerCohort, cohort) && _descriptor != null && _descriptor.OwnerStamp == revision; }
        static bool SameOwnedDescriptor(ProjectionRootDescriptor before, ProjectionRootDescriptor after, ulong stamp)
        {
            if (after == null || after.OwnerStamp != stamp || after.RootCount != before.RootCount || after.ScalarCount != before.ScalarCount ||
                !string.Equals(after.CohortIdentity, before.CohortIdentity, StringComparison.Ordinal) || !string.Equals(after.PolicyIdentity, before.PolicyIdentity, StringComparison.Ordinal)) return false;
            for (int i = 0; i < before.RootCount; i++)
            { var a = before.Root(i); var b = after.Root(i); if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) || !a.Node.Equals(b.Node)) return false; }
            for (int i = 0; i < before.ScalarCount; i++)
            { var a = before.Scalar(i); var b = after.Scalar(i); if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) || !a.Value.Equals(b.Value)) return false; }
            return true;
        }
        bool ExactIssuedUnderGate(ProjectionNodeVersion node)
            => node != null && _lifetimes.TryGetValue(node.Id.Value, out var life) && node.HasIssuer(life.Issuer) &&
                node.Kind == life.Kind && node.Version == life.LastIssued && life.Issued.TryGetTarget(out var exact) && ReferenceEquals(node, exact);
        static bool SameScalarTopology(ProjectionNodeVersion before, ProjectionNodeVersion after)
        {
            if (before.Kind != after.Kind || !before.Id.Equals(after.Id) || before.EntryCount != after.EntryCount || before.PageCount != after.PageCount) return false;
            int changed = 0;
            for (int i = 0; i < before.EntryCount; i++)
            {
                var a = before.GetEntry(i); var b = after.GetEntry(i);
                if (!a.Key.Equals(b.Key) || a.Value.IsChild != b.Value.IsChild) return false;
                if (a.Value.IsChild) { if (!a.Value.Child.Equals(b.Value.Child)) return false; }
                else if (!a.Value.Scalar.Equals(b.Value.Scalar) && ++changed > 1) return false;
            }
            return true; // zero differences is an accepted same-value scalar write
        }
        internal bool OwnerPlanMatchesUnderGate(OwnerScalarRegistryPlan plan)
            => ReferenceEquals(_ownerCohort, plan.Cohort) && OwnerLeaseCurrentUnderGate() && ReferenceEquals(_ownerPreparation, plan) && _readiness == ProjectionReadiness.ReadyPartialDiagnostic &&
                _revision == plan.Revision && _readinessEpoch == plan.Epoch && ReferenceEquals(_table, plan.BeforeTable) &&
                ReferenceEquals(_plan, plan.Graph) && ReferenceEquals(_descriptor, plan.BeforeDescriptor) &&
                ReferenceEquals(_table.Get(plan.Before.Id), plan.Before) && ExactIssuedUnderGate(plan.After);
        internal void InstallOwnerPlanUnderGate(OwnerScalarRegistryPlan plan)
        {
            // Called only after closed final guards under one continuously held attempt. No allocation/callback.
            long oldUnits = _currentUnits;
            _currentUnits = _ownerPreparationUnits; _ownerPreparationUnits = oldUnits;
            var life = _lifetimes[plan.After.Id.Value];
            _lifetimes[plan.After.Id.Value] = new Lifetime(life.Kind, plan.After.Version, life.LastIssued, life.Issuer, life.Issued);
            _table = plan.AfterTable; _descriptor = plan.AfterDescriptor; _revision = plan.Revision + 1;
        }
        internal void RollbackOwnerPlanUnderGate(OwnerScalarRegistryPlan plan)
        {
            long afterUnits = _currentUnits; _currentUnits = _ownerPreparationUnits; _ownerPreparationUnits = afterUnits;
            _table = plan.BeforeTable; _descriptor = plan.RollbackDescriptor; _revision = plan.Revision + 2;
            // Lifetime admission and issuance never rewind; restoration is same-attempt only.
        }
        internal bool InstalledOwnerPlanUnderGate(OwnerScalarRegistryPlan plan)
            => ReferenceEquals(_ownerCohort, plan.Cohort) && ReferenceEquals(_ownerPreparation, plan) && _readiness == ProjectionReadiness.ReadyPartialDiagnostic &&
                _readinessEpoch == plan.Epoch && _revision == plan.Revision + 1 && ReferenceEquals(_table, plan.AfterTable);
        internal void CancelOwnerPlanUnderGate(OwnerScalarRegistryPlan plan)
        { if (ReferenceEquals(_ownerPreparation, plan)) ReleaseOwnerPreparationUnderGate(); }
    }
    internal sealed class OwnerScalarRegistryPlan
    {
        readonly ParticipantProjectionRegistry _registry;
        internal readonly ParticipantProjectionCohort Cohort;
        internal readonly ProjectionNodeVersion Before, After;
        internal readonly ProjectionVersionTable BeforeTable, AfterTable;
        internal readonly ProjectionGraphPlan Graph;
        internal readonly ProjectionRootDescriptor BeforeDescriptor, AfterDescriptor, RollbackDescriptor;
        internal readonly ulong Revision, Epoch;
        ParticipantPublicationAttempt _attempt;
        int _state;
        internal OwnerScalarRegistryPlan(ParticipantProjectionRegistry registry, ParticipantProjectionCohort cohort,
            ProjectionNodeVersion before, ProjectionNodeVersion after, ProjectionVersionTable beforeTable, ProjectionVersionTable afterTable,
            ProjectionGraphPlan graph, ProjectionRootDescriptor beforeDescriptor, ProjectionRootDescriptor afterDescriptor,
            ProjectionRootDescriptor rollback, ulong revision, ulong epoch)
        { _registry = registry; Cohort = cohort; Before = before; After = after; BeforeTable = beforeTable; AfterTable = afterTable; Graph = graph; BeforeDescriptor = beforeDescriptor; AfterDescriptor = afterDescriptor; RollbackDescriptor = rollback; Revision = revision; Epoch = epoch; }
        internal bool MatchesUnderGate(ParticipantPublicationAttempt attempt)
        { CommonParticipantGate.RequireHeld(); return _state == 0 && attempt != null && attempt.IsOpenUnderGate && _registry.OwnerPlanMatchesUnderGate(this); }
        internal void InstallUnderGate(ParticipantPublicationAttempt attempt)
        {
            if (!MatchesUnderGate(attempt)) throw new InvalidOperationException("stale_owned_scalar_plan");
            _registry.InstallOwnerPlanUnderGate(this); _attempt = attempt; _state = 1;
        }
        internal bool CanRollbackUnderGate(ParticipantPublicationAttempt attempt)
        { CommonParticipantGate.RequireHeld(); return _state == 1 && attempt != null && attempt.IsOpenUnderGate && ReferenceEquals(_attempt, attempt) && _registry.InstalledOwnerPlanUnderGate(this); }
        internal void CompleteUnderGate(ParticipantPublicationAttempt attempt)
        { RequireInstalled(attempt); _registry.CancelOwnerPlanUnderGate(this); _state = 3; }
        internal void RollbackUnderGate(ParticipantPublicationAttempt attempt)
        { RequireInstalled(attempt); _registry.RollbackOwnerPlanUnderGate(this); _registry.CancelOwnerPlanUnderGate(this); _state = 2; }
        void RequireInstalled(ParticipantPublicationAttempt attempt)
        {
            CommonParticipantGate.RequireHeld();
            if (_state != 1 || attempt == null || !attempt.IsOpenUnderGate || !ReferenceEquals(attempt, _attempt) || !_registry.InstalledOwnerPlanUnderGate(this)) throw new InvalidOperationException("wrong_owned_scalar_attempt");
        }
        internal void CancelUnderGate()
        { CommonParticipantGate.RequireHeld(); if (_state == 0) { _registry.CancelOwnerPlanUnderGate(this); _state = 2; } }
    }
}
