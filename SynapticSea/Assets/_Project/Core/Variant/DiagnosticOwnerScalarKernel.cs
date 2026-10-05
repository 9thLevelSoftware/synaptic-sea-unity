using System;
using System.Collections.Generic;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// Unwired, synthetic origin only. Tokens below authenticate this private shadow instance,
    /// not resource catalogs, live factories, real policy, a world cohort or storage admission.
    /// No actual model or publicly exposed GdDict writer is enrolled by this class.
    /// </summary>
    internal sealed class DiagnosticOwnerScalarKernel
    {
        static ulong _lastIdentity;
        readonly TrackedParticipantOwner _owner;
        readonly GdDict _dict;
        readonly GdArray _array;
        readonly bool _attached;
        readonly object _origin = new object(), _cohort = new object(), _policy = new object(), _resource = new object();
        readonly ProjectionNodeIssuer _issuer = new ProjectionNodeIssuer();
        readonly ProjectionNodeId _id;
        ProjectionVersionTable _table;
        ProjectionNodeVersion _node;
        ProjectionRootDescriptor _descriptor;
        WeakReference<ProjectionNodeVersion> _latest;
        ulong _issued, _admitted, _nodeEpoch, _revision = 1, _readinessEpoch = 1;
        bool _ready = true;
        ParticipantPublicationAttempt _pending;

        DiagnosticOwnerScalarKernel(ProjectionNodeKind kind, ProjectionEntry[] entries, bool attached)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (entries.Length > 4096) throw new ArgumentException("synthetic_genesis_entry_capacity");
            _attached = attached; _owner = new TrackedParticipantOwner();
            if (kind == ProjectionNodeKind.Dictionary) _dict = _owner.NewDict(attached);
            else if (kind == ProjectionNodeKind.Array) _array = _owner.NewArray(attached);
            else throw new ArgumentException("diagnostic_node_kind");
            // Initial imports are disclosed preparation, not a bounded final publication section.
            // Synthetic genesis refuses children: no supplied NodeId constitutes authentic enrollment.
            var owned = (ProjectionEntry[])entries.Clone();
            foreach (var entry in owned)
            {
                if (entry.Value.IsChild) throw new ArgumentException("synthetic_genesis_requires_scalar");
                if (_dict != null) _dict.RawStorage.Set(entry.Key.ToNormalized(), entry.Value.Scalar.ToNormalized());
                else _array.RawStorage.Add(entry.Value.Scalar.ToNormalized());
            }
            lock (CommonParticipantGate.SyncRoot)
            {
                if (_lastIdentity == ulong.MaxValue) throw new InvalidOperationException("diagnostic_identity_exhausted");
                _id = new ProjectionNodeId(++_lastIdentity, 1);
            }
            _node = new ProjectionNodeVersion(_id, 1, kind, owned, 4096, _issuer);
            _issued = _admitted = 1; _latest = new WeakReference<ProjectionNodeVersion>(_node);
            _table = new ProjectionVersionTable(_id.Registry).Set(_id, _node);
            _descriptor = Descriptor(_owner.Stamp);
        }
        internal static DiagnosticOwnerScalarKernel CreateSynthetic(ProjectionNodeKind kind, ProjectionEntry[] entries, bool attached = true)
            => new DiagnosticOwnerScalarKernel(kind, entries, attached);
        ProjectionRootDescriptor Descriptor(ulong stamp)
            => new ProjectionRootDescriptor("synthetic-shadow-cohort", "synthetic-policy-NOT-authenticated", stamp,
                _attached ? new[] { new ProjectionRootBinding("shadow", _id) } : Array.Empty<ProjectionRootBinding>());
        object BackingUnderGate => _dict != null ? (object)_dict.RawStorage : _array.RawStorage;
        internal ulong OwnerStamp => _owner.Stamp;
        internal ulong NodeEpoch { get { lock (CommonParticipantGate.SyncRoot) return _nodeEpoch; } }
        internal ulong LastAdmitted { get { lock (CommonParticipantGate.SyncRoot) return _admitted; } }
        internal bool Ready { get { lock (CommonParticipantGate.SyncRoot) return _ready && _pending == null && _owner.MatchesUnderGate(_descriptor.OwnerStamp); } }
        internal object ReadLive(int index)
        {
            lock (CommonParticipantGate.SyncRoot)
                return _dict != null ? _dict.RawStorage.Values[index] : _array.RawStorage[index];
        }
        internal DiagnosticScalarCut Capture()
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!_ready || _pending != null || !_owner.MatchesUnderGate(_descriptor.OwnerStamp))
                    throw new InvalidOperationException("diagnostic_projection_not_ready");
                return new DiagnosticScalarCut(_table, _id, _descriptor, _revision, _readinessEpoch);
            }
        }
        // Closed synthetic revocation represents a dependency change. It is not a real resource lease.
        internal void RevokeSyntheticOrigin()
        {
            lock (CommonParticipantGate.SyncRoot) InvalidateUnderGate();
        }
        void InvalidateUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            _ready = false;
            if (_readinessEpoch != ulong.MaxValue) _readinessEpoch++;
        }
        internal void WriteUnsupported(int index, object normalizedValue)
        {
            normalizedValue = TrackedParticipantOwner.NormalizeProtected(normalizedValue);
            lock (CommonParticipantGate.SyncRoot)
            {
                // This diagnostic writer never pauses or drops an accepted write to retain a cut.
                InvalidateUnderGate();
                if (_dict != null) _dict[_dict.RawStorage.Keys[index]] = normalizedValue;
                else _array[index] = normalizedValue;
                if (_nodeEpoch != ulong.MaxValue) _nodeEpoch++;
            }
        }
        internal PreparedDiagnosticScalarDelta Prepare(int index, ProjectionScalar after)
        {
            ProjectionNodeVersion before;
            ProjectionVersionTable table;
            ProjectionRootDescriptor descriptor;
            object beforeBacking;
            GdDict.Storage candidateDict = null;
            List<object> candidateArray = null;
            ulong ownerStamp, nodeEpoch, revision, epoch, issued;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!_ready || _pending != null) throw new InvalidOperationException("diagnostic_projection_not_ready");
                before = _node; table = _table; descriptor = _descriptor; beforeBacking = BackingUnderGate;
                ownerStamp = _owner.Stamp; nodeEpoch = _nodeEpoch; revision = _revision; epoch = _readinessEpoch; issued = _issued;
                if (index < 0 || index >= before.EntryCount || before.GetEntry(index).Value.IsChild)
                    throw new ArgumentException("scalar_delta_requires_scalar_slot");
                if (issued == ulong.MaxValue || nodeEpoch > ulong.MaxValue - 2 || revision > ulong.MaxValue - 2 ||
                    (_attached && ownerStamp > ulong.MaxValue - 2))
                { InvalidateUnderGate(); throw new InvalidOperationException("diagnostic_counter_capacity"); }
                // Closed snapshot copy while locked. Never enumerate live storage outside the gate.
                if (_dict != null) candidateDict = _dict.RawStorage.Clone();
                else candidateArray = new List<object>(_array.RawStorage);
            }
            // Exclusively owned candidate; immutable baseline; no live getters or callbacks.
            if (candidateDict != null) candidateDict.Values[index] = after.ToNormalized();
            else candidateArray[index] = after.ToNormalized();
            ValidateCandidate(before, candidateDict, candidateArray, index);
            var next = before.ReplaceValue(issued + 1, index, new ProjectionValue(after));
            var nextTable = table.Set(_id, next);
            var afterDescriptor = Descriptor(_attached ? ownerStamp + 1 : ownerStamp);
            var rollbackDescriptor = Descriptor(_attached ? ownerStamp + 2 : ownerStamp);
            // Immutable minting above is separate from closed exact object registration below.
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!BaseMatches(beforeBacking, before, table, descriptor, ownerStamp, nodeEpoch, revision, epoch) || _issued != issued)
                    throw new InvalidOperationException("stale_scalar_preparation");
                _issued = next.Version; _latest.SetTarget(next);
                return new PreparedDiagnosticScalarDelta(this, beforeBacking, candidateDict, candidateArray,
                    before, next, table, nextTable, descriptor, afterDescriptor, rollbackDescriptor,
                    ownerStamp, nodeEpoch, revision, epoch, _origin, _cohort, _policy, _resource);
            }
        }
        static void ValidateCandidate(ProjectionNodeVersion before, GdDict.Storage dict, List<object> array, int changed)
        {
            int count = dict != null ? dict.Values.Count : array.Count;
            if (count != before.EntryCount || (count + 31) / 32 != before.PageCount)
                throw new ArgumentException("scalar_delta_shape_changed");
            for (int i = 0; i < count; i++)
            {
                var entry = before.GetEntry(i);
                if (dict != null && !ProjectionScalar.FromNormalized(dict.Keys[i]).Equals(entry.Key))
                    throw new ArgumentException("scalar_delta_key_changed");
                object value = dict != null ? dict.Values[i] : array[i];
                // The synthetic origin has no child enrollment; future real origin needs exact child-ID mapping.
                if (entry.Value.IsChild || value is GdDict || value is GdArray)
                    throw new ArgumentException("scalar_delta_child_changed");
                var scalar = ProjectionScalar.FromNormalized(value);
                if (i != changed && !scalar.Equals(entry.Value.Scalar))
                    throw new ArgumentException("scalar_delta_other_value_changed");
            }
        }
        bool BaseMatches(object backing, ProjectionNodeVersion node, ProjectionVersionTable table, ProjectionRootDescriptor descriptor,
            ulong owner, ulong local, ulong revision, ulong epoch)
            => _ready && ReferenceEquals(BackingUnderGate, backing) && ReferenceEquals(_node, node) &&
                ReferenceEquals(_table, table) && ReferenceEquals(_descriptor, descriptor) &&
                _owner.MatchesUnderGate(owner) && _nodeEpoch == local && _revision == revision && _readinessEpoch == epoch;

        internal sealed class PreparedDiagnosticScalarDelta
        {
            readonly DiagnosticOwnerScalarKernel _target;
            readonly object _beforeBacking, _origin, _cohort, _policy, _resource;
            readonly GdDict.Storage _dict;
            readonly List<object> _array;
            readonly ProjectionNodeVersion _before, _after;
            readonly ProjectionVersionTable _beforeTable, _afterTable;
            readonly ProjectionRootDescriptor _beforeDescriptor, _afterDescriptor, _rollbackDescriptor;
            readonly ulong _owner, _local, _revision, _epoch;
            ParticipantPublicationAttempt _attempt;
            int _state;
            internal PreparedDiagnosticScalarDelta(DiagnosticOwnerScalarKernel target, object beforeBacking,
                GdDict.Storage dict, List<object> array, ProjectionNodeVersion before, ProjectionNodeVersion after,
                ProjectionVersionTable beforeTable, ProjectionVersionTable afterTable, ProjectionRootDescriptor beforeDescriptor,
                ProjectionRootDescriptor afterDescriptor, ProjectionRootDescriptor rollbackDescriptor,
                ulong owner, ulong local, ulong revision, ulong epoch, object origin, object cohort, object policy, object resource)
            {
                _target = target; _beforeBacking = beforeBacking; _dict = dict; _array = array;
                _before = before; _after = after; _beforeTable = beforeTable; _afterTable = afterTable;
                _beforeDescriptor = beforeDescriptor; _afterDescriptor = afterDescriptor; _rollbackDescriptor = rollbackDescriptor;
                _owner = owner; _local = local; _revision = revision; _epoch = epoch;
                _origin = origin; _cohort = cohort; _policy = policy; _resource = resource;
            }
            internal bool MatchesUnderGate(ParticipantPublicationAttempt attempt)
            {
                CommonParticipantGate.RequireHeld();
                return _state == 0 && attempt != null && attempt.IsOpenUnderGate &&
                    (_target._pending == null || ReferenceEquals(_target._pending, attempt)) &&
                    ReferenceEquals(_origin, _target._origin) && ReferenceEquals(_cohort, _target._cohort) &&
                    ReferenceEquals(_policy, _target._policy) && ReferenceEquals(_resource, _target._resource) &&
                    _target.BaseMatches(_beforeBacking, _before, _beforeTable, _beforeDescriptor, _owner, _local, _revision, _epoch) &&
                    _target._latest.TryGetTarget(out var exact) && ReferenceEquals(exact, _after) &&
                    _after.HasIssuer(_target._issuer) && _after.Version == _target._issued && _after.Version > _target._admitted;
            }
            internal void InstallUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (!MatchesUnderGate(attempt)) throw new InvalidOperationException("stale_scalar_certificate");
                // All copies/descriptors/page paths/counter checks were prepared above.
                _target._pending = attempt;
                if (_target._dict != null) _target._dict.RawStorage = _dict; else _target._array.RawStorage = _array;
                if (_target._attached) _target._owner.AdvancePreparedUnderGate(_owner + 1);
                _target._nodeEpoch = _local + 1; _target._revision = _revision + 1;
                _target._node = _after; _target._table = _afterTable; _target._descriptor = _afterDescriptor;
                _target._admitted = _after.Version; _attempt = attempt; _state = 1;
            }
            internal void CompleteUnderGate(ParticipantPublicationAttempt attempt)
            {
                RequireAttemptedState(attempt);
                _target._pending = null; _state = 3;
            }
            void RequireAttemptedState(ParticipantPublicationAttempt attempt)
            {
                CommonParticipantGate.RequireHeld();
                if (_state != 1 || attempt == null || !attempt.IsOpenUnderGate || !ReferenceEquals(_attempt, attempt) ||
                    !ReferenceEquals(_target._pending, attempt) || !_target._ready || _target._readinessEpoch != _epoch ||
                    !ReferenceEquals(_target.BackingUnderGate, _dict != null ? (object)_dict : _array) ||
                    !ReferenceEquals(_target._table, _afterTable) || !ReferenceEquals(_target._node, _after) ||
                    _target._nodeEpoch != _local + 1 || _target._revision != _revision + 1 ||
                    !_target._owner.MatchesUnderGate(_target._attached ? _owner + 1 : _owner))
                    throw new InvalidOperationException("wrong_or_delayed_scalar_attempt");
            }
            internal void RollbackUnderGate(ParticipantPublicationAttempt attempt)
            {
                RequireAttemptedState(attempt);
                if (_target._dict != null) _target._dict.RawStorage = (GdDict.Storage)_beforeBacking;
                else _target._array.RawStorage = (List<object>)_beforeBacking;
                if (_target._attached) _target._owner.AdvancePreparedUnderGate(_owner + 2);
                _target._nodeEpoch = _local + 2; _target._revision = _revision + 2;
                _target._node = _before; _target._table = _beforeTable; _target._descriptor = _rollbackDescriptor;
                // LastAdmitted and issuance do not decrease, even though historical data is restored.
                _target._pending = null; _state = 2;
            }
        }
    }
    internal sealed class DiagnosticScalarCut
    {
        readonly ProjectionVersionTable _table;
        readonly ProjectionNodeId _node;
        internal readonly ProjectionRootDescriptor Descriptor;
        internal readonly ulong Revision, ReadinessEpoch;
        internal DiagnosticScalarCut(ProjectionVersionTable table, ProjectionNodeId node, ProjectionRootDescriptor descriptor, ulong revision, ulong epoch)
        { _table = table; _node = node; Descriptor = descriptor; Revision = revision; ReadinessEpoch = epoch; }
        internal ProjectionScalar Value(int index) => _table.Get(_node).GetEntry(index).Value.Scalar;
    }
}
