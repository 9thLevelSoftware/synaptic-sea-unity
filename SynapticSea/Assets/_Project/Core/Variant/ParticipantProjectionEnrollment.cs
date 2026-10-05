using System;
using System.Collections.Generic;
using System.Threading;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;

namespace SynapticSea.Core.Variant
{
    internal sealed class EnrolledProjectionNode
    {
        internal readonly ProjectionNodeHandle Handle;
        internal readonly TrackedParticipantOwner Owner;
        internal readonly int OwnerIndex;
        internal readonly bool Policy;
        readonly WeakReference<object> _liveNode;
        internal bool TryCaptureCleanupNode(ParticipantProjectionCohort cohort, object issuer, out object node)
        { if (cohort == null || !cohort.OwnsMaintenanceIssuer(issuer)) throw new ArgumentException("foreign_maintenance_issuer"); return _liveNode.TryGetTarget(out node); }
        internal object CaptureLiveNode(ParticipantProjectionCohort cohort, object issuer)
        { if (cohort == null || !cohort.OwnsMaintenanceIssuer(issuer) || !ReferenceEquals(Owner.ProjectionCohort, cohort) || !_liveNode.TryGetTarget(out var node)) throw new InvalidOperationException("foreign_or_dead_publication_node"); return node; }
        internal bool IsExactNode(object node) => _liveNode.TryGetTarget(out var exact) && ReferenceEquals(node, exact);
        readonly object[] _childReferences; // private origin-owned immutable slot mapping; never part of a cut
        internal int SlotCount => _childReferences.Length;
        internal object ChildAt(int index) => _childReferences[index];
        internal void InitializeChildSlot(int index, object child)
        { if (Current != null) throw new InvalidOperationException("already_issued_child_mapping"); _childReferences[index] = child; }
        internal ProjectionNodeVersion Current;
        internal object Backing;
        internal ulong Epoch;
        internal EnrolledProjectionNode(ProjectionNodeHandle handle, TrackedParticipantOwner owner, int ownerIndex, bool policy, object[] childReferences, object liveNode, object backing)
        { Handle = handle; Owner = owner; OwnerIndex = ownerIndex; Policy = policy; _childReferences = (object[])childReferences.Clone(); _liveNode = new WeakReference<object>(liveNode); Backing = backing; }
    }
    internal sealed partial class TrackedParticipantOwner
    {
        internal ParticipantProjectionCohort ProjectionCohort;
        // Only a factory's exclusively owned normalized forests may call this before model exposure.
        internal static TrackedParticipantOwner PreparePrivateForest(object[] inputs, out object[] roots)
        {
            if (inputs == null || inputs.Length > 16) throw new ArgumentException("private_root_capacity");
            var owner = new TrackedParticipantOwner(); var state = new ImportState(owner);
            roots = new object[inputs.Length];
            for (int i = 0; i < roots.Length; i++)
            {
                roots[i] = state.Copy(inputs[i], 0);
                if (!(roots[i] is GdDict) && !(roots[i] is GdArray)) throw new ArgumentException("private_root_kind");
                if (owner._roots.Contains(roots[i])) throw new ArgumentException("duplicate_private_root");
                owner._roots.Add(roots[i]);
            }
            // Validate the complete forest outside the common gate while it is still exclusively private.
            foreach (object root in roots) owner.Validate(root, null, null);
            owner._stamp = (ulong)roots.Length;
            return owner;
        }
        internal void InvalidateProjectionUnderGate()
        { CommonParticipantGate.RequireHeld(); ProjectionCohort?.InvalidateUnderGate("unsupported_participant_mutation"); }
        internal void BeforeUnsupportedNodeWriteUnderGate(object node, bool reachable)
        {
            if (reachable) InvalidateProjectionUnderGate();
            var binding = node is GdDict d ? d.ProjectionNode : ((GdArray)node).ProjectionNode;
            if (binding != null && binding.Epoch != ulong.MaxValue) binding.Epoch++;
        }
        internal bool TryProjectedDictionarySet(GdDict node, object key, object value)
        {
            if (ProjectionCohort == null || Monitor.IsEntered(CommonParticipantGate.SyncRoot)) return false;
            return ProjectionCohort.TryScalarWrite(node, key, -1, value);
        }
        internal bool TryProjectedArraySet(GdArray node, int index, object value)
        {
            if (ProjectionCohort == null || Monitor.IsEntered(CommonParticipantGate.SyncRoot)) return false;
            return ProjectionCohort.TryScalarWrite(node, null, index, value);
        }
    }
    internal sealed partial class ParticipantProjectionCohort : IDisposable
    {
        readonly ResourceAuthorityLease _lease;
        readonly ParticipantProjectionRegistry _registry;
        readonly TrackedParticipantOwner[] _owners;
                readonly ProjectionRootBinding[] _roots;
        ProjectionScalarBinding[] _modelScalars;
        readonly string _classId;
        Dictionary<ulong,WeakReference<EnrolledProjectionNode>> _bindings;
        ulong[] _stamps;
        ulong _cohortRevision = 1;
        bool _published, _disposed;
        readonly InventoryState _inventory;
        readonly PlayerProgressionState _progression;
        readonly TrainingEventBus _training;
        ParticipantProjectionCohort(ResourceAuthorityLease lease, ParticipantProjectionRegistry registry,
            InventoryState inventory, PlayerProgressionState progression, TrainingEventBus training,
            TrackedParticipantOwner[] owners, ProjectionRootBinding[] roots, ProjectionScalarBinding[] scalars, string classId, IEnumerable<EnrolledProjectionNode> bindings)
        {
            _lease = lease; _registry = registry; _inventory = inventory; _progression = progression; _training = training;
            _owners = owners; _roots = roots; _modelScalars = scalars; _classId = classId;
            _stamps = new[] { owners[0].Stamp, owners[1].Stamp, owners[2].Stamp };
            _bindings = new Dictionary<ulong,WeakReference<EnrolledProjectionNode>>();
            foreach (var binding in bindings) _bindings.Add(binding.Handle.Id.Value, new WeakReference<EnrolledProjectionNode>(binding));
        }
        internal InventoryState Inventory { get { if (!_published) throw new InvalidOperationException("unpublished_cohort"); return _inventory; } }
        internal PlayerProgressionState Progression { get { if (!_published) throw new InvalidOperationException("unpublished_cohort"); return _progression; } }
        internal TrainingEventBus Training { get { if (!_published) throw new InvalidOperationException("unpublished_cohort"); return _training; } }
        internal bool Ready { get { lock (CommonParticipantGate.SyncRoot) return CurrentUnderGate(); } }
        internal bool WholeWorldSave => false;
        internal ProjectionRootDescriptor Descriptor(ulong revision)
            => new ProjectionRootDescriptor("fresh-participant-cohort", _lease.Snapshot.ContentSha256 + ":" + _classId, revision, _roots, _modelScalars);
        internal bool CanBindRegistryUnderGate(ParticipantProjectionRegistry registry, ResourceAuthorityLease lease)
        { CommonParticipantGate.RequireHeld(); return !_published && !_disposed && ReferenceEquals(_registry, registry) && ReferenceEquals(_lease, lease); }
        internal bool PublishUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            if (_published || _disposed || !_lease.IsCurrent) return false;
            for (int i = 0; i < 3; i++) if (!_owners[i].MatchesUnderGate(_stamps[i]) || _owners[i].ProjectionCohort != null) return false;
            _registry.BindOwnerLeaseUnderGate(this, _lease);
            for (int i = 0; i < 3; i++) _owners[i].ProjectionCohort = this;
            _published = true; return true;
        }
        bool CurrentUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            if (!_published || _disposed || _activeMaintenance != null) return false;
            if (!_lease.IsCurrent) { InvalidateUnderGate("participant_resource_revoked"); return false; }
            for (int i = 0; i < 3; i++) if (!_owners[i].MatchesUnderGate(_stamps[i])) { InvalidateUnderGate("participant_stamp_changed"); return false; }
            return _registry.Readiness == ProjectionReadiness.ReadyPartialDiagnostic && _registry.OwnerDescriptorCurrentUnderGate(this, _cohortRevision);
        }
        internal void InvalidateUnderGate(string reason)
        { CommonParticipantGate.RequireHeld(); if (!_disposed && _registry.Readiness == ProjectionReadiness.ReadyPartialDiagnostic) _registry.InvalidateUnderGate(ProjectionReadiness.RebuildRequired, reason); }
        internal bool TryPin(out ProjectionPin pin, out string reason)
        {
            lock (CommonParticipantGate.SyncRoot)
            { pin = null; if (!CurrentUnderGate()) { reason = "participant_cohort_not_ready"; return false; } return _registry.TryPin(out pin, out reason); }
        }
        internal bool TryScalarWrite(object node, object key, int index, object value)
        {
            OwnerScalarRegistryPlan registryPlan;
            EnrolledProjectionNode binding;
            object beforeBacking;
            ulong stamp, local, cohort;
            ProjectionNodeVersion before;
            ProjectionScalar after;
            ulong[] nextStamps, rollbackStamps;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!CurrentUnderGate()) return false;
                binding = node is GdDict d ? d.ProjectionNode : ((GdArray)node).ProjectionNode;
                if (binding == null || binding.OwnerIndex < 0 || binding.OwnerIndex >= _owners.Length || !ReferenceEquals(binding.Owner, _owners[binding.OwnerIndex]) || !binding.IsExactNode(node) ||
                    !_bindings.TryGetValue(binding.Handle.Id.Value, out var known) || !known.TryGetTarget(out var exactBinding) || !ReferenceEquals(binding, exactBinding) || binding.Policy || binding.Epoch > ulong.MaxValue - 2 || _cohortRevision > ulong.MaxValue - 2) return false;
                beforeBacking = node is GdDict dict ? (object)dict.RawStorage : ((GdArray)node).RawStorage;
                if (!ReferenceEquals(binding.Backing, beforeBacking)) return false;
                if (node is GdDict map) { if (!map.RawStorage.Index.TryGetValue(key, out index)) return false; }
                if (index < 0 || index >= binding.Current.EntryCount || binding.Current.GetEntry(index).Value.IsChild) return false;
                try { after = ProjectionScalar.FromNormalized(value); } catch (ArgumentException) { return false; }
                stamp = _stamps[binding.OwnerIndex]; if (stamp > ulong.MaxValue - 2) return false;
                before = binding.Current; local = binding.Epoch; cohort = _cohortRevision;
                nextStamps = (ulong[])_stamps.Clone(); rollbackStamps = (ulong[])_stamps.Clone();
                nextStamps[binding.OwnerIndex] = stamp + 1; rollbackStamps[binding.OwnerIndex] = stamp + 2;
            }
            // Build exclusively owned storage from the immutable admitted node and fixed child-slot mapping.
            // No captured live backing is enumerated outside the gate.
            GdDict.Storage nextDict = null; List<object> nextArray = null;
            if (node is GdDict) nextDict = new GdDict.Storage(); else nextArray = new List<object>(before.EntryCount);
            for (int i = 0; i < before.EntryCount; i++)
            {
                var entry = before.GetEntry(i);
                object v = i == index ? value : entry.Value.IsChild ? binding.ChildAt(i) : entry.Value.Scalar.ToNormalized();
                if (entry.Value.IsChild)
                {
                    var child = v is GdDict childDict ? childDict.ProjectionNode : (v as GdArray)?.ProjectionNode;
                    if (child == null || !child.IsExactNode(v) || !child.Handle.Id.Equals(entry.Value.Child) || !ReferenceEquals(child.Owner, binding.Owner)) return false;
                }
                if (nextDict != null) nextDict.Set(entry.Key.ToNormalized(), v); else nextArray.Add(v);
            }
            if (!binding.Handle.TryPrepareScalarVersion(before, index, new ProjectionValue(after), out var issued, out _)) return false;
            var afterDescriptor = Descriptor(cohort + 1); var rollbackDescriptor = Descriptor(cohort + 2);
            if (!_registry.TryPrepareOwnerScalar(this, before, issued, afterDescriptor, rollbackDescriptor, out registryPlan)) return false;
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                if (!CurrentUnderGate() || !ReferenceEquals(registryPlan.Cohort, this) ||
                    _cohortRevision != cohort || !binding.Owner.MatchesUnderGate(stamp) || binding.Epoch != local ||
                    !ReferenceEquals(binding.Current, before) || !ReferenceEquals(binding.Backing, beforeBacking) ||
                    !ReferenceEquals(node is GdDict map ? (object)map.RawStorage : ((GdArray)node).RawStorage, beforeBacking) ||
                    !registryPlan.MatchesUnderGate(attempt))
                { registryPlan.CancelUnderGate(); return false; }
                // The registry's remaining argument checks run before any model write. Everything
                // after this point is a closed prebuilt pointer/clock install under the same gate.
                registryPlan.InstallUnderGate(attempt);
                try
                {
                    if (node is GdDict mapNext) mapNext.RawStorage = nextDict; else ((GdArray)node).RawStorage = nextArray;
                    binding.Owner.AdvancePreparedUnderGate(stamp + 1); binding.Epoch = local + 1;
                    binding.Current = issued; binding.Backing = nextDict != null ? (object)nextDict : nextArray;
                    _stamps = nextStamps; _cohortRevision = cohort + 1;
                    registryPlan.CompleteUnderGate(attempt); return true;
                }
                catch
                {
                    if (registryPlan.CanRollbackUnderGate(attempt))
                    {
                        if (node is GdDict oldMap) oldMap.RawStorage = (GdDict.Storage)beforeBacking; else ((GdArray)node).RawStorage = (List<object>)beforeBacking;
                        binding.Owner.AdvancePreparedUnderGate(stamp + 2); binding.Epoch = local + 2;
                        binding.Current = before; binding.Backing = beforeBacking;
                        _stamps = rollbackStamps; _cohortRevision = cohort + 2;
                        registryPlan.RollbackUnderGate(attempt);
                    }
                    else InvalidateUnderGate("failed_owned_scalar_attempt");
                    throw;
                }
            }
        }
        public void Dispose()
        { lock (CommonParticipantGate.SyncRoot) { if (_disposed) return; if (_activeMaintenance != null) throw new InvalidOperationException("unresolved_maintenance_attempt"); _disposed = true; _registry.Dispose(); for (int i = 0; i < _owners.Length; i++) if (ReferenceEquals(_owners[i].ProjectionCohort, this)) _owners[i].ProjectionCohort = null; } }
    }
}
