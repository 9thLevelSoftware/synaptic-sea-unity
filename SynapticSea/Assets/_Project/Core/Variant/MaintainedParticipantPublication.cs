using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
namespace SynapticSea.Core.Variant
{
    internal sealed partial class ParticipantProjectionCohort
    {
        PreparedMaintainedPublication _activeMaintenance;
        internal bool CanPreserveScalarTrainingRowsUnderGate(GdArray root)
        {
            CommonParticipantGate.RequireHeld(); var binding = root.ProjectionNode;
            if (!CurrentUnderGate() || binding == null || binding.OwnerIndex != 2 || binding.Policy || !binding.IsExactNode(root)) return false;
            for (int i = 0; i < _roots.Length; i++) if (_roots[i].Name == "training.log") return _roots[i].Node.Equals(binding.Handle.Id);
            return false;
        }
        internal bool CanRetireDeadBindingUnderGate(ulong id, WeakReference<EnrolledProjectionNode> binding, object issuer)
        { CommonParticipantGate.RequireHeld(); return OwnsMaintenanceIssuer(issuer) && _bindings.TryGetValue(id, out var exact) && ReferenceEquals(exact, binding) && !binding.TryGetTarget(out _); }
        internal bool OwnsMaintainedBackingUnderGate(PreparedVariantBacking backing, ParticipantPublicationAttempt attempt)
        { CommonParticipantGate.RequireHeld(); return _activeMaintenance != null && _activeMaintenance.OwnsBackingUnderGate(backing, attempt); }
        internal PreparedMaintainedPublication PrepareMaintainedPublication(InventoryState.PreparedReplacement inventory,
            PlayerProgressionState.PreparedReplacement progression, TrainingEventBus.PreparedReplacement training)
        {
            var origins = new List<PreparedVariantBacking.ProjectionReplacementOrigin>(3);
            var changed = new bool[3]; ulong[] beforeStamps; ulong revision;
            Dictionary<ulong,WeakReference<EnrolledProjectionNode>> beforeBindings; ProjectionScalarBinding[] beforeScalars;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!CurrentUnderGate() || _activeMaintenance != null || _cohortRevision > ulong.MaxValue - 2)
                    throw new InvalidOperationException("maintenance_cohort_not_ready");
                if (inventory != null) { if (!inventory.IsForModel(_inventory) || !inventory.MatchesUnderGate()) throw new ArgumentException("foreign_or_stale_inventory_command"); origins.Add(inventory.CaptureProjectionOrigin()); changed[0] = true; }
                if (progression != null) { if (!progression.IsForModel(_progression) || !progression.MatchesUnderGate()) throw new ArgumentException("foreign_or_stale_progression_command"); origins.Add(progression.CaptureProjectionOrigin()); changed[1] = true; }
                if (training != null) { if (!training.IsForModel(_training) || !training.MatchesUnderGate()) throw new ArgumentException("foreign_or_stale_training_command"); origins.Add(training.CaptureProjectionOrigin()); changed[2] = true; }
                if (origins.Count == 0) throw new ArgumentException("empty_maintenance_command");
                beforeStamps = _stamps; revision = _cohortRevision; beforeBindings = _bindings; beforeScalars = _modelScalars;
            }
            MaintainedCandidate candidate = null; MaintainedGraphAdmissionCursor admission = null;
            ParticipantProjectionRegistry.MaintainedLifetimeCursor lifetimes = null;
            try
            {
                candidate = PrepareMaintainedCandidate(origins.ToArray()); admission = PrepareCandidateAdmission(candidate);
                // Disclosed bounded-graph preparation outside publication, not a frame-time guarantee.
                while (admission.Status == ProjectionCursorStatus.Pending) admission.Advance(64);
                if (admission.Status != ProjectionCursorStatus.Complete) throw new InvalidOperationException(admission.Reason);
                lifetimes = PrepareMaintainedLifetimes(candidate, admission);
                while (lifetimes.Status == ProjectionCursorStatus.Pending) lifetimes.Advance(64);
                if (lifetimes.Status != ProjectionCursorStatus.Complete) throw new InvalidOperationException(lifetimes.Reason);
                var nodes = candidate.CapturePublicationNodes(this, _maintenanceIssuer);
                var oldRoots = candidate.CapturePublicationRoots(this, _maintenanceIssuer);
                var nextBindings = new Dictionary<ulong,WeakReference<EnrolledProjectionNode>>(beforeBindings);
                // Dead detached aliases are retired only when absent from the new current graph.
                // Historical cuts own immutable versions, not live handles; numeric IDs are never reused.
                foreach (var entry in beforeBindings)
                    if (lifetimes.RetireDeadBinding(this, entry.Key, entry.Value, _maintenanceIssuer)) nextBindings.Remove(entry.Key);
                var rootObjects = new object[oldRoots.Length];
                for (int i = 0; i < nodes.Length; i++)
                {
                    object node = nodes[i].CaptureLiveNode(this, _maintenanceIssuer);
                    nextBindings[nodes[i].Handle.Id.Value] = new WeakReference<EnrolledProjectionNode>(nodes[i]);
                    if (i < oldRoots.Length) rootObjects[i] = node;
                    else
                    {
                        // Exclusively private imported descendants, never visible in a current root yet.
                        lock (CommonParticipantGate.SyncRoot)
                        {
                            if (!candidate.MatchesUnderGate()) throw new InvalidOperationException("stale_private_publication_node");
                            if (node is GdDict d) { if (!ReferenceEquals(d.RawStorage, nodes[i].Backing) || (d.ProjectionNode != null && !ReferenceEquals(d.ProjectionNode, nodes[i]))) throw new InvalidOperationException("nonprivate_publication_child"); if (d.ProjectionNode == null) d.ProjectionNode = nodes[i]; }
                            else { var a = (GdArray)node; if (!ReferenceEquals(a.RawStorage, nodes[i].Backing) || (a.ProjectionNode != null && !ReferenceEquals(a.ProjectionNode, nodes[i]))) throw new InvalidOperationException("nonprivate_publication_child"); if (a.ProjectionNode == null) a.ProjectionNode = nodes[i]; }
                        }
                    }
                }
                if (nextBindings.Count > _registry.Limits.MaximumNodes) throw new InvalidOperationException("maintenance_binding_capacity");
                var nextScalars = (ProjectionScalarBinding[])beforeScalars.Clone();
                if (training != null)
                {
                    if (nextScalars.Length != 5 || nextScalars[3].Name != "training.dropped" || nextScalars[4].Name != "training.xp_total") throw new InvalidOperationException("maintenance_header_shape");
                    nextScalars[3] = new ProjectionScalarBinding("training.dropped", ProjectionScalar.FromNormalized(training.ProjectedDropped));
                    nextScalars[4] = new ProjectionScalarBinding("training.xp_total", ProjectionScalar.FromNormalized(training.ProjectedXp));
                }
                var nextStamps = (ulong[])beforeStamps.Clone(); var rollbackStamps = (ulong[])beforeStamps.Clone();
                for (int i = 0; i < 3; i++) if (changed[i]) { nextStamps[i] = checked(beforeStamps[i] + 1); rollbackStamps[i] = checked(beforeStamps[i] + 2); }
                var after = new ProjectionRootDescriptor("fresh-participant-cohort", _lease.Snapshot.ContentSha256 + ":" + _classId, revision + 1, _roots, nextScalars);
                var rollback = new ProjectionRootDescriptor("fresh-participant-cohort", _lease.Snapshot.ContentSha256 + ":" + _classId, revision + 2, _roots, beforeScalars);
                var registryPlan = lifetimes.PreparePublication(this, after, rollback, nextBindings.Count + 16L, beforeBindings.Count + 16L, _maintenanceIssuer);
                return PreparedMaintainedPublication.Create(this, inventory, progression, training, origins.ToArray(), candidate, admission, lifetimes,
                    registryPlan, beforeBindings, nextBindings, beforeScalars, nextScalars, beforeStamps, nextStamps, rollbackStamps,
                    oldRoots, nodes, rootObjects, revision, _maintenanceIssuer);
            }
            catch { lifetimes?.Dispose(); admission?.Dispose(); candidate?.Dispose(); throw; }
        }
        internal sealed class PreparedMaintainedPublication : IDisposable
        {
            readonly ParticipantProjectionCohort _cohort;
            readonly InventoryState.PreparedReplacement _inventory;
            readonly PlayerProgressionState.PreparedReplacement _progression;
            readonly TrainingEventBus.PreparedReplacement _training;
            readonly PreparedVariantBacking.ProjectionReplacementOrigin[] _origins;
            readonly MaintainedCandidate _candidate;
            readonly MaintainedGraphAdmissionCursor _admission;
            readonly ParticipantProjectionRegistry.MaintainedLifetimeCursor _lifetimes;
            readonly ParticipantProjectionRegistry.MaintainedRegistryPublication _registryPlan;
            readonly Dictionary<ulong,WeakReference<EnrolledProjectionNode>> _beforeBindings, _afterBindings;
            readonly ProjectionScalarBinding[] _beforeScalars, _afterScalars;
            readonly ulong[] _beforeStamps, _afterStamps, _rollbackStamps;
            readonly EnrolledProjectionNode[] _oldRoots, _nodes;
            readonly object[] _rootObjects;
            readonly ulong _revision;
            ParticipantPublicationAttempt _attempt; int _state;
            private PreparedMaintainedPublication(ParticipantProjectionCohort cohort, InventoryState.PreparedReplacement inventory,
                PlayerProgressionState.PreparedReplacement progression, TrainingEventBus.PreparedReplacement training,
                PreparedVariantBacking.ProjectionReplacementOrigin[] origins, MaintainedCandidate candidate, MaintainedGraphAdmissionCursor admission,
                ParticipantProjectionRegistry.MaintainedLifetimeCursor lifetimes, ParticipantProjectionRegistry.MaintainedRegistryPublication registryPlan,
                Dictionary<ulong,WeakReference<EnrolledProjectionNode>> beforeBindings, Dictionary<ulong,WeakReference<EnrolledProjectionNode>> afterBindings,
                ProjectionScalarBinding[] beforeScalars, ProjectionScalarBinding[] afterScalars, ulong[] beforeStamps, ulong[] afterStamps,
                ulong[] rollbackStamps, EnrolledProjectionNode[] oldRoots, EnrolledProjectionNode[] nodes, object[] rootObjects, ulong revision)
            { _cohort = cohort; _inventory = inventory; _progression = progression; _training = training; _origins = origins; _candidate = candidate; _admission = admission; _lifetimes = lifetimes; _registryPlan = registryPlan; _beforeBindings = beforeBindings; _afterBindings = afterBindings; _beforeScalars = beforeScalars; _afterScalars = afterScalars; _beforeStamps = beforeStamps; _afterStamps = afterStamps; _rollbackStamps = rollbackStamps; _oldRoots = oldRoots; _nodes = nodes; _rootObjects = rootObjects; _revision = revision; }
            internal static PreparedMaintainedPublication Create(ParticipantProjectionCohort cohort, InventoryState.PreparedReplacement inventory,
                PlayerProgressionState.PreparedReplacement progression, TrainingEventBus.PreparedReplacement training,
                PreparedVariantBacking.ProjectionReplacementOrigin[] origins, MaintainedCandidate candidate, MaintainedGraphAdmissionCursor admission,
                ParticipantProjectionRegistry.MaintainedLifetimeCursor lifetimes, ParticipantProjectionRegistry.MaintainedRegistryPublication registryPlan,
                Dictionary<ulong,WeakReference<EnrolledProjectionNode>> beforeBindings, Dictionary<ulong,WeakReference<EnrolledProjectionNode>> afterBindings,
                ProjectionScalarBinding[] beforeScalars, ProjectionScalarBinding[] afterScalars, ulong[] beforeStamps, ulong[] afterStamps,
                ulong[] rollbackStamps, EnrolledProjectionNode[] oldRoots, EnrolledProjectionNode[] nodes, object[] rootObjects, ulong revision, object issuer)
            {
                if (cohort == null || !cohort.OwnsMaintenanceIssuer(issuer)) throw new ArgumentException("foreign_maintenance_issuer");
                return new PreparedMaintainedPublication(cohort, inventory, progression, training, origins, candidate, admission, lifetimes, registryPlan,
                    beforeBindings, afterBindings, beforeScalars, afterScalars, beforeStamps, afterStamps, rollbackStamps, oldRoots, nodes, rootObjects, revision);
            }
            internal bool MatchesUnderGate(ParticipantPublicationAttempt attempt)
            {
                CommonParticipantGate.RequireHeld();
                return _state == 0 && _cohort._activeMaintenance == null && _cohort.CurrentUnderGate() &&
                    ReferenceEquals(_cohort._bindings, _beforeBindings) && ReferenceEquals(_cohort._modelScalars, _beforeScalars) &&
                    ReferenceEquals(_cohort._stamps, _beforeStamps) && _cohort._cohortRevision == _revision &&
                    (_inventory == null || _inventory.MatchesUnderGate()) && (_progression == null || _progression.MatchesUnderGate()) &&
                    (_training == null || _training.MatchesUnderGate()) && _registryPlan.MatchesUnderGate(attempt);
            }
            internal bool OwnsBackingUnderGate(PreparedVariantBacking backing, ParticipantPublicationAttempt attempt)
            {
                if (attempt == null || !attempt.IsOpenUnderGate || !ReferenceEquals(_attempt, attempt) || !ReferenceEquals(_cohort._activeMaintenance, this)) return false;
                for (int i = 0; i < _origins.Length; i++) if (_origins[i].IsExactBacking(backing)) return true;
                return false;
            }
            internal void InstallUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (!MatchesUnderGate(attempt)) throw new InvalidOperationException("stale_maintained_publication");
                _attempt = attempt; _cohort._activeMaintenance = this;
                _inventory?.InstallUnderGate(attempt); _progression?.InstallUnderGate(attempt); _training?.InstallUnderGate(attempt);
                // Fixed root count (at most seven), never descendants or a graph walk.
                for (int i = 0; i < _oldRoots.Length; i++) SetBinding(_rootObjects[i], _nodes[i]);
                _cohort._bindings = _afterBindings; _cohort._modelScalars = _afterScalars; _cohort._stamps = _afterStamps; _cohort._cohortRevision = _revision + 1;
                _registryPlan.InstallAfterParticipantGuardsUnderGate(attempt); _state = 1;
            }
            bool InstalledUnderGate(ParticipantPublicationAttempt attempt) => _state == 1 && ReferenceEquals(_attempt, attempt) &&
                ReferenceEquals(_cohort._activeMaintenance, this) && _registryPlan.InstalledUnderGate(attempt);
            internal void CompleteUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (!InstalledUnderGate(attempt)) throw new InvalidOperationException("wrong_maintained_completion_attempt");
                _registryPlan.CompleteUnderGate(attempt); _candidate.TransferFreshOwnership(_cohort, _cohort._maintenanceIssuer);
                _cohort._activeMaintenance = null; _state = 3;
            }
            internal void RollbackUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (!InstalledUnderGate(attempt)) throw new InvalidOperationException("wrong_maintained_rollback_attempt");
                _training?.RollbackUnderGate(attempt); _progression?.RollbackUnderGate(attempt); _inventory?.RollbackUnderGate(attempt);
                for (int i = 0; i < _oldRoots.Length; i++) { _oldRoots[i].Epoch = _nodes[i].Epoch + 1; SetBinding(_rootObjects[i], _oldRoots[i]); }
                _cohort._bindings = _beforeBindings; _cohort._modelScalars = _beforeScalars; _cohort._stamps = _rollbackStamps; _cohort._cohortRevision = _revision + 2;
                _registryPlan.RollbackUnderGate(attempt); _cohort._activeMaintenance = null; _state = 2;
            }
            internal void NotifyAfterPublication()
            { if (_state != 3) throw new InvalidOperationException("uncompleted_maintenance_notification"); _inventory?.NotifyAfterPublication(); }
            static void SetBinding(object node, EnrolledProjectionNode binding)
            { if (node is GdDict d) d.ProjectionNode = binding; else ((GdArray)node).ProjectionNode = binding; }
            public void Dispose()
            {
                lock (CommonParticipantGate.SyncRoot)
                {
                    if (_state == 1) throw new InvalidOperationException("unresolved_maintenance_attempt");
                    if (_state == 0) { _registryPlan.CancelUnderGate(); _state = 2; }
                }
                _lifetimes.Dispose(); _admission.Dispose(); _candidate.Dispose();
            }
        }
    }
}
