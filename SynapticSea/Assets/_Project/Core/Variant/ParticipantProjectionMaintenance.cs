using System;
using System.Collections.Generic;
namespace SynapticSea.Core.Variant
{
    internal sealed partial class ParticipantProjectionCohort
    {
        readonly object _maintenanceIssuer = new object();
        internal bool OwnsMaintenanceIssuer(object issuer) => ReferenceEquals(issuer, _maintenanceIssuer);
        // Closed candidate graph preparation only; no live participant write and no readiness publication.
        internal MaintainedCandidate PrepareMaintainedCandidate(PreparedVariantBacking.ProjectionReplacementOrigin[] supplied)
        {
            if (supplied == null || supplied.Length == 0 || supplied.Length > 3) throw new ArgumentException("maintenance_origin_capacity");
            var origins = (PreparedVariantBacking.ProjectionReplacementOrigin[])supplied.Clone();
            var ownerIndices = new int[origins.Length]; var rootStates = new List<EnrolledProjectionNode>();
            var rootNodes = new List<PreparedVariantBacking.ProjectionReplacementOrigin.CandidateNode>();
            ulong[] stamps; ulong revision;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!CurrentUnderGate()) throw new InvalidOperationException("maintenance_cohort_not_ready");
                if (_cohortRevision > ulong.MaxValue - 2) throw new InvalidOperationException("maintenance_revision_exhausted");
                stamps = (ulong[])_stamps.Clone(); revision = _cohortRevision;
                var uniqueOwners = new HashSet<int>(); var uniqueRoots = new HashSet<ulong>();
                for (int i = 0; i < origins.Length; i++)
                {
                    int ownerIndex = -1;
                    for (int j = 0; j < 3; j++) if (origins[i] != null && origins[i].MatchesUnderGate(_owners[j])) ownerIndex = j;
                    if (ownerIndex < 0 || !uniqueOwners.Add(ownerIndex)) throw new ArgumentException("foreign_or_duplicate_maintenance_origin");
                    ownerIndices[i] = ownerIndex;
                    for (int r = 0; r < origins[i].RootCount; r++)
                    {
                        var candidate = origins[i].CaptureCandidateRoot(r); EnrolledProjectionNode existing = null;
                        foreach (var entry in _bindings)
                            if (entry.Value.TryGetTarget(out var state) && candidate.MatchesBinding(state)) { existing = state; break; }
                        if (existing == null || existing.Policy || existing.OwnerIndex != ownerIndex ||
                            existing.Epoch > ulong.MaxValue - 2 || !uniqueRoots.Add(existing.Handle.Id.Value))
                            throw new ArgumentException("unadmitted_or_policy_maintenance_root");
                        rootNodes.Add(candidate); rootStates.Add(existing);
                    }
                }
            }
            var nodes = new List<PreparedVariantBacking.ProjectionReplacementOrigin.CandidateNode>();
            var bindings = new List<EnrolledProjectionNode>(); var entries = new List<PreparedVariantBacking.ProjectionReplacementOrigin.CandidateEntry[]>();
            var rootCount = rootNodes.Count;
            for (int i = 0; i < rootCount; i++)
            {
                nodes.Add(rootNodes[i]); int count = rootNodes[i].CaptureEntryCount();
                if (count > _registry.Limits.MaximumEntriesPerNode) throw new ArgumentException("maintenance_node_entries");
                var state = rootNodes[i].PreparePrivateBinding(this, _maintenanceIssuer, rootStates[i].Handle, rootStates[i].OwnerIndex, count);
                state.Epoch = rootStates[i].Epoch + 1; bindings.Add(state); entries.Add(null);
            }
            var freshHandles = new List<ProjectionNodeHandle>();
            var reused = new List<bool>(); for (int i = 0; i < nodes.Count; i++) reused.Add(false);
            try
            {
                // Read only private candidate entries; never enumerate live before backing outside the gate.
                for (int index = 0; index < nodes.Count; index++)
                {
                    if (reused[index]) { entries[index] = Array.Empty<PreparedVariantBacking.ProjectionReplacementOrigin.CandidateEntry>(); continue; }
                    int count = nodes[index].CaptureEntryCount();
                    if (count > _registry.Limits.MaximumEntriesPerNode) throw new ArgumentException("maintenance_node_entries");
                    var row = new PreparedVariantBacking.ProjectionReplacementOrigin.CandidateEntry[count];
                    for (int slot = 0; slot < count; slot++)
                    {
                        row[slot] = nodes[index].CaptureEntry(slot);
                        if (!row[slot].IsChild) continue;
                        int found = Find(nodes, row[slot].Child);
                        if (found < 0)
                        {
                            if (nodes.Count == _registry.Limits.MaximumNodes) throw new ArgumentException("maintenance_node_capacity");
                            EnrolledProjectionNode known = null;
                            lock (CommonParticipantGate.SyncRoot)
                            {
                                if (!CurrentUnderGate()) throw new InvalidOperationException("stale_reused_publication_child");
                                foreach (var entry in _bindings)
                                    if (entry.Value.TryGetTarget(out var state) && state.OwnerIndex == bindings[index].OwnerIndex &&
                                        row[slot].Child.MatchesBinding(state)) { known = state; break; }
                            }
                            nodes.Add(row[slot].Child);
                            if (known != null) { bindings.Add(known); entries.Add(null); reused.Add(true); }
                            else
                            {
                                if (!_registry.TryCreateNode(row[slot].Child.Kind, out var handle, out var reason)) throw new InvalidOperationException(reason);
                                freshHandles.Add(handle);
                                int childCount = row[slot].Child.CaptureEntryCount();
                                if (childCount > _registry.Limits.MaximumEntriesPerNode) throw new ArgumentException("maintenance_node_entries");
                                bindings.Add(row[slot].Child.PreparePrivateBinding(this, _maintenanceIssuer, handle, bindings[index].OwnerIndex, childCount)); entries.Add(null); reused.Add(false);
                            }
                        }
                        nodes[index].BindPrivateChild(this, _maintenanceIssuer, bindings[index], slot, row[slot].Child);
                    }
                    entries[index] = row;
                }
                var versions = new List<ProjectionNodeVersion>(nodes.Count);
                for (int index = 0; index < nodes.Count; index++)
                {
                    if (reused[index]) continue;
                    var row = entries[index]; var closed = new ProjectionEntry[row.Length];
                    for (int slot = 0; slot < row.Length; slot++)
                        closed[slot] = new ProjectionEntry(row[slot].Key, row[slot].IsChild ?
                            new ProjectionValue(bindings[Find(nodes, row[slot].Child)].Handle.Id) : new ProjectionValue(row[slot].Scalar));
                    if (!bindings[index].Handle.TryPrepareInitialVersion(closed, out var version)) throw new InvalidOperationException("maintenance_issuance_refused");
                    bindings[index].Current = version; versions.Add(version);
                }
                var result = new MaintainedCandidate(this, origins, ownerIndices, stamps, revision, rootStates.ToArray(), bindings.ToArray(), versions.ToArray(), freshHandles.ToArray(), _maintenanceIssuer);
                lock (CommonParticipantGate.SyncRoot) if (!result.MatchesUnderGate()) throw new InvalidOperationException("stale_maintenance_candidate");
                freshHandles.Clear(); return result;
            }
            catch { foreach (var handle in freshHandles) handle.TryRetire(out _); throw; }
        }
        internal MaintainedGraphAdmissionCursor PrepareCandidateAdmission(MaintainedCandidate candidate)
            => MaintainedGraphAdmissionCursor.Create(this, candidate);
        internal void CaptureMaintainedAdmissionSource(MaintainedCandidate candidate,
            out ParticipantProjectionRegistry.MaintainedAdmissionSource source, out ProjectionRootDescriptor descriptor, out ProjectionLimits limits)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (candidate == null || !candidate.IsForCohort(this) || !candidate.MatchesUnderGate()) throw new InvalidOperationException("stale_or_foreign_maintenance_candidate");
                source = _registry.CaptureMaintainedSourceUnderGate(this); descriptor = Descriptor(_cohortRevision + 1); limits = _registry.Limits;
            }
        }
        static int Find(List<PreparedVariantBacking.ProjectionReplacementOrigin.CandidateNode> nodes, PreparedVariantBacking.ProjectionReplacementOrigin.CandidateNode needle)
        { for (int i = 0; i < nodes.Count; i++) if (nodes[i].IsSameNode(needle)) return i; return -1; }
        internal sealed class MaintainedCandidate : IDisposable
        {
            readonly ParticipantProjectionCohort _cohort;
            readonly PreparedVariantBacking.ProjectionReplacementOrigin[] _origins;
            readonly int[] _ownerIndices;
            readonly ulong[] _stamps;
            readonly ulong _revision;
            readonly EnrolledProjectionNode[] _beforeRoots, _afterNodes;
            readonly ProjectionNodeVersion[] _versions;
            ProjectionNodeHandle[] _fresh;
            internal MaintainedCandidate(ParticipantProjectionCohort cohort, PreparedVariantBacking.ProjectionReplacementOrigin[] origins,
                int[] ownerIndices, ulong[] stamps, ulong revision, EnrolledProjectionNode[] beforeRoots,
                EnrolledProjectionNode[] afterNodes, ProjectionNodeVersion[] versions, ProjectionNodeHandle[] fresh, object issuer)
            { if (cohort == null || !ReferenceEquals(issuer, cohort._maintenanceIssuer)) throw new ArgumentException("unissued_maintenance_candidate"); _cohort = cohort; _origins = origins; _ownerIndices = ownerIndices; _stamps = stamps; _revision = revision; _beforeRoots = beforeRoots; _afterNodes = afterNodes; _versions = versions; _fresh = fresh; }
            internal EnrolledProjectionNode[] CapturePublicationNodes(ParticipantProjectionCohort cohort, object issuer)
            { if (!ReferenceEquals(_cohort, cohort) || !cohort.OwnsMaintenanceIssuer(issuer)) throw new ArgumentException("foreign_maintenance_issuer"); return (EnrolledProjectionNode[])_afterNodes.Clone(); }
            internal EnrolledProjectionNode[] CapturePublicationRoots(ParticipantProjectionCohort cohort, object issuer)
            { if (!ReferenceEquals(_cohort, cohort) || !cohort.OwnsMaintenanceIssuer(issuer)) throw new ArgumentException("foreign_maintenance_issuer"); return (EnrolledProjectionNode[])_beforeRoots.Clone(); }
            internal void TransferFreshOwnership(ParticipantProjectionCohort cohort, object issuer)
            { CommonParticipantGate.RequireHeld(); if (!ReferenceEquals(_cohort, cohort) || !cohort.OwnsMaintenanceIssuer(issuer)) throw new ArgumentException("foreign_maintenance_issuer"); _fresh = null; }
            internal bool IsForCohort(ParticipantProjectionCohort cohort) => ReferenceEquals(_cohort, cohort);
            internal int VersionCount => _versions.Length;
            internal ProjectionNodeVersion Version(int index) => _versions[index];
            internal bool MatchesUnderGate()
            {
                CommonParticipantGate.RequireHeld();
                if (_fresh == null || !_cohort.CurrentUnderGate() || _cohort._cohortRevision != _revision) return false;
                for (int i = 0; i < 3; i++) if (!_cohort._owners[i].MatchesUnderGate(_stamps[i])) return false;
                for (int i = 0; i < _origins.Length; i++) if (!_origins[i].MatchesUnderGate(_cohort._owners[_ownerIndices[i]])) return false;
                foreach (var root in _beforeRoots)
                    if (!_cohort._bindings.TryGetValue(root.Handle.Id.Value, out var weak) || !weak.TryGetTarget(out var current) || !ReferenceEquals(root, current)) return false;
                return true;
            }
            public void Dispose()
            {
                ProjectionNodeHandle[] fresh;
                lock (CommonParticipantGate.SyncRoot) { fresh = _fresh; _fresh = null; }
                if (fresh != null)
                {
                    // Only still-private fresh descendants are cleared on cancellation/rollback.
                    // Successful publication transfers _fresh ownership and never enters this branch.
                    for (int i = _beforeRoots.Length; i < _afterNodes.Length; i++)
                    {
                        bool isFresh = false;
                        for (int h = 0; h < fresh.Length; h++) if (ReferenceEquals(fresh[h], _afterNodes[i].Handle)) { isFresh = true; break; }
                        if (!isFresh) continue;
                        if (!_afterNodes[i].TryCaptureCleanupNode(_cohort, _cohort._maintenanceIssuer, out object node)) continue;
                        lock (CommonParticipantGate.SyncRoot)
                        {
                            if (node is GdDict d && ReferenceEquals(d.ProjectionNode, _afterNodes[i])) d.ProjectionNode = null;
                            else if (node is GdArray a && ReferenceEquals(a.ProjectionNode, _afterNodes[i])) a.ProjectionNode = null;
                        }
                    }
                    foreach (var handle in fresh) handle.TryRetire(out _);
                }
            }
        }
    }
}
