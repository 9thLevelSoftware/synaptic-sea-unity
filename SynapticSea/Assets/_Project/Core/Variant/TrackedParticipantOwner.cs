using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SynapticSea.Core.Variant
{
    // Unwired diagnostic fence. Resource/publisher integration must enroll in this same gate.
    internal static class CommonParticipantGate
    {
        internal static readonly object SyncRoot = new object();
        internal static ParticipantPublicationAttempt BeginAttempt() => new ParticipantPublicationAttempt();
        internal static void RequireHeld()
        {
            if (!Monitor.IsEntered(SyncRoot)) throw new InvalidOperationException("participant_gate_not_held");
        }
    }

    internal sealed partial class TrackedParticipantOwner
    {
        internal const int MaximumNodes = 4096;
        internal const int MaximumDepth = 128;
        readonly List<object> _roots = new List<object>();
        ulong _stamp;
        readonly object _replacementIssuer = new object();
        bool _poisoned;
        internal ulong Stamp { get { lock (CommonParticipantGate.SyncRoot) return _stamp; } }
        internal bool IsPoisoned { get { lock (CommonParticipantGate.SyncRoot) return _poisoned; } }
        internal GdDict NewDict(bool root = false)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                var value = new GdDict(this);
                if (root) RegisterRoot(value);
                return value;
            }
        }
        internal GdArray NewArray(bool root = false)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                var value = new GdArray(this);
                if (root) RegisterRoot(value);
                return value;
            }
        }
        internal GdDict ImportDict(GdDict source, bool root = false) => (GdDict)Import(source, root);
        internal GdArray ImportArray(GdArray source, bool root = false) => (GdArray)Import(source, root);
        object Import(object source, bool root)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                var state = new ImportState(this);
                object value = state.Copy(source, 0);
                if (root) RegisterRoot(value);
                return value;
            }
        }
        void RegisterRoot(object value)
        {
            CommonParticipantGate.RequireHeld();
            if (!Owned(value)) throw new InvalidOperationException("unprotected_root");
            if (_roots.Contains(value)) throw new InvalidOperationException("duplicate_root");
            // Creation before exposure still records root-set changes.
            CheckCanAdvanceUnderGate();
            InvalidateProjectionUnderGate();
            _roots.Add(value);
            TouchUnderGate();
        }
        internal void ReplaceRoot(object oldRoot, object newRoot)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!Owned(newRoot)) throw new InvalidOperationException("unprotected_or_cross_owner_root");
                int at = _roots.IndexOf(oldRoot);
                if (at < 0 || (!ReferenceEquals(oldRoot, newRoot) && _roots.Contains(newRoot)))
                    throw new InvalidOperationException("invalid_root_replacement");
                Validate(newRoot, null, null);
                foreach (object root in _roots)
                    if (!ReferenceEquals(root, oldRoot) && Reach(root,newRoot,new HashSet<object>(ReferenceComparer.Instance))) throw new InvalidOperationException("nested_replacement_root");
                CheckCanAdvanceUnderGate();
                InvalidateProjectionUnderGate();
                _roots[at] = newRoot;
                TouchUnderGate();
            }
        }
        bool Owned(object value) => value is GdDict d ? ReferenceEquals(d.Owner, this) : value is GdArray a && ReferenceEquals(a.Owner, this);
        internal void CheckCanAdvanceUnderGate(int count = 1)
        {
            CommonParticipantGate.RequireHeld();
            if (count < 1 || _poisoned || _stamp > ulong.MaxValue - (ulong)count)
            {
                _poisoned = true;
                throw new InvalidOperationException("participant_stamp_exhausted");
            }
        }
        internal void TouchUnderGate()
        {
            CheckCanAdvanceUnderGate();
            _stamp++;
        }
        internal bool IsReplacementIssuerUnderGate(object issuer)
        { CommonParticipantGate.RequireHeld(); return ReferenceEquals(issuer, _replacementIssuer); }
        internal bool MatchesUnderGate(ulong stamp)
        {
            CommonParticipantGate.RequireHeld();
            return !_poisoned && stamp == _stamp;
        }
        internal static object NormalizeProtected(object value, bool key = false)
        {
            bool scalar = value == null || value is string || value is bool || value is long || value is double || value is Vec2i || value is Vec3 || value is int || value is short || value is byte || value is sbyte || value is uint || value is ushort || value is ulong || value is float || value is decimal || value is char;
            if (!scalar && (key || (!(value is GdDict) && !(value is GdArray)))) throw new ArgumentException("unsupported_protected_variant");
            return V.Normalize(value);
        }
        internal bool PrepareMutationUnderGate(object node, object candidateStorage)
        {
            CommonParticipantGate.RequireHeld();
            // Validate even detached nodes: provenance/cycles cannot be introduced while detached.
            Validate(node, node, candidateStorage);
            foreach (object root in _roots) Validate(root, node, candidateStorage);
            bool reachable = IsReachable(node);
            if (reachable) CheckCanAdvanceUnderGate();
            BeforeUnsupportedNodeWriteUnderGate(node, reachable);
            return reachable;
        }
        internal void FinishMutationUnderGate(bool reachable)
        {
            if (reachable) TouchUnderGate();
        }
        bool IsReachable(object needle)
        {
            var visited = new HashSet<object>(ReferenceComparer.Instance);
            foreach (object root in _roots)
                if (Reach(root, needle, visited)) return true;
            return false;
        }
        static bool Reach(object value, object needle, HashSet<object> visited)
        {
            if (!(value is GdDict) && !(value is GdArray)) return false;
            if (ReferenceEquals(value, needle)) return true;
            if (!visited.Add(value)) return false;
            if (value is GdDict d)
            {
                foreach (object child in d.RawStorage.Values) if (Reach(child, needle, visited)) return true;
            }
            else foreach (object child in ((GdArray)value).RawStorage) if (Reach(child, needle, visited)) return true;
            return false;
        }
        void Validate(object root, object changed, object storage)
        {
            var done = new Dictionary<object,int>(ReferenceComparer.Instance);
            var active = new HashSet<object>(ReferenceComparer.Instance);
            Walk(root, changed, storage, done, active, 0);
        }
        int Walk(object value, object changed, object storage, Dictionary<object,int> done, HashSet<object> active, int depth)
        {
            if (!(value is GdDict) && !(value is GdArray)) return -1;
            if (depth > MaximumDepth) throw new InvalidOperationException("participant_graph_depth");
            if (!Owned(value)) throw new InvalidOperationException("unprotected_or_cross_owner_child");
            if (depth > 0 && _roots.Contains(value)) throw new InvalidOperationException("participant_nested_root");
            if (active.Contains(value)) throw new InvalidOperationException("participant_cycle");
            if (done.TryGetValue(value,out int cachedHeight))
            {
                if (depth + cachedHeight > MaximumDepth) throw new InvalidOperationException("participant_graph_depth");
                return cachedHeight;
            }
            if (done.Count + active.Count >= MaximumNodes) throw new InvalidOperationException("participant_graph_size");
            active.Add(value); int height = 0;
            if (value is GdDict d)
            {
                var data = ReferenceEquals(value, changed) ? (GdDict.Storage)storage : d.RawStorage;
                foreach (object key in data.Keys)
                    if (key is GdDict || key is GdArray) throw new InvalidOperationException("participant_container_key");
                foreach (object child in data.Values) height = Math.Max(height,Walk(child,changed,storage,done,active,depth+1)+1);
            }
            else
            {
                var data = ReferenceEquals(value, changed) ? (List<object>)storage : ((GdArray)value).RawStorage;
                foreach (object child in data) height = Math.Max(height,Walk(child,changed,storage,done,active,depth+1)+1);
            }
            active.Remove(value); done.Add(value,height);
            if (depth + height > MaximumDepth) throw new InvalidOperationException("participant_graph_depth");
            return height;
        }
        internal PreparedVariantBacking PrepareReplacements(object[] liveRoots, object[] detachedValues, ulong expectedStamp)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!MatchesUnderGate(expectedStamp)) throw new InvalidOperationException("stale_participant");
                if (liveRoots == null || detachedValues == null || liveRoots.Length == 0 || liveRoots.Length != detachedValues.Length)
                    throw new ArgumentException("invalid_replacement_set");
                CheckCanAdvanceUnderGate(2);
                var roots = (object[])liveRoots.Clone();
                var oldData = new object[roots.Length];
                var newData = new object[roots.Length];
                var unique = new HashSet<object>(ReferenceComparer.Instance);
                // Each root replacement gets a fresh private descendant graph, never reuses a live child.
                for (int i = 0; i < roots.Length; i++)
                {
                    object target = roots[i];
                    if (!_roots.Contains(target) || !unique.Add(target)) throw new ArgumentException("invalid_replacement_root");
                    object fresh = new ImportState(this).Copy(detachedValues[i], 0);
                    if (target is GdDict d && fresh is GdDict nextD)
                    {
                        oldData[i] = d.RawStorage; newData[i] = nextD.RawStorage;
                    }
                    else if (target is GdArray a && fresh is GdArray nextA)
                    {
                        oldData[i] = a.RawStorage;
                        if (ProjectionCohort != null && ProjectionCohort.CanPreserveScalarTrainingRowsUnderGate(a))
                            PreserveUniqueScalarRows(a.RawStorage, nextA.RawStorage);
                        newData[i] = nextA.RawStorage;
                    }
                    else throw new ArgumentException("replacement_kind_mismatch");
                }
                return new PreparedVariantBacking(this, roots, oldData, newData, expectedStamp, _replacementIssuer);
            }
        }
        // Narrow existing-history optimization. Preserve only uniquely occurring scalar-only rows
        // at the same position, so no DAG alias topology is collapsed or invented. All comparisons
        // use exact typed Variant bits. Nested/duplicate/changed rows remain privately imported.
        static void PreserveUniqueScalarRows(List<object> before, List<object> after)
        {
            var oldCounts = new Dictionary<object,int>(ReferenceComparer.Instance);
            var newCounts = new Dictionary<object,int>(ReferenceComparer.Instance);
            foreach (object value in before) if (value is GdDict) oldCounts[value] = oldCounts.TryGetValue(value, out var n) ? n + 1 : 1;
            foreach (object value in after) if (value is GdDict) newCounts[value] = newCounts.TryGetValue(value, out var n) ? n + 1 : 1;
            for (int i = 0; i < Math.Min(before.Count, after.Count); i++)
            {
                if (!(before[i] is GdDict oldRow) || !(after[i] is GdDict newRow) || oldCounts[oldRow] != 1 || newCounts[newRow] != 1 ||
                    oldRow.RawStorage.Keys.Count != newRow.RawStorage.Keys.Count) continue;
                bool same = true;
                for (int k = 0; k < oldRow.RawStorage.Keys.Count; k++)
                {
                    object oldValue = oldRow.RawStorage.Values[k], newValue = newRow.RawStorage.Values[k];
                    if (oldValue is GdDict || oldValue is GdArray || newValue is GdDict || newValue is GdArray ||
                        !ProjectionScalar.FromNormalized(oldRow.RawStorage.Keys[k]).Equals(ProjectionScalar.FromNormalized(newRow.RawStorage.Keys[k])) ||
                        !ProjectionScalar.FromNormalized(oldValue).Equals(ProjectionScalar.FromNormalized(newValue))) { same = false; break; }
                }
                if (same) after[i] = oldRow;
            }
        }
        internal void AdvancePreparedUnderGate(ulong next)
        {
            CommonParticipantGate.RequireHeld();
            _stamp = next;
        }
        sealed class ImportState
        {
            readonly TrackedParticipantOwner _owner;
            readonly Dictionary<object, object> _copies = new Dictionary<object, object>(ReferenceComparer.Instance);
            readonly HashSet<object> _active = new HashSet<object>(ReferenceComparer.Instance);
            readonly Dictionary<object,int> _heights = new Dictionary<object,int>(ReferenceComparer.Instance);
            internal ImportState(TrackedParticipantOwner owner) { _owner = owner; }
            internal object Copy(object value, int depth)
            {
                if (!(value is GdDict) && !(value is GdArray)) return NormalizeProtected(value);
                if (depth > MaximumDepth) throw new InvalidOperationException("participant_import_bounds");
                if (_active.Contains(value)) throw new InvalidOperationException("participant_cycle");
                if (_copies.TryGetValue(value, out object existing))
                { if (depth + _heights[value] > MaximumDepth) throw new InvalidOperationException("participant_import_bounds"); return existing; }
                if (_copies.Count >= MaximumNodes) throw new InvalidOperationException("participant_import_bounds");
                _active.Add(value);
                object copy; int height = 0;
                if (value is GdDict d)
                {
                    var result = new GdDict(_owner); copy = result; _copies[value] = copy;
                    foreach (var kv in d)
                    {
                        if (kv.Key is GdDict || kv.Key is GdArray) throw new InvalidOperationException("participant_container_key");
                        result.RawStorage.Set(NormalizeProtected(kv.Key,true), Copy(kv.Value, depth + 1));
                        if (kv.Value is GdDict || kv.Value is GdArray) height = Math.Max(height,_heights[kv.Value]+1);
                    }
                }
                else
                {
                    var result = new GdArray(_owner); copy = result; _copies[value] = copy;
                    foreach (object child in (GdArray)value)
                    { result.RawStorage.Add(Copy(child,depth+1)); if (child is GdDict || child is GdArray) height = Math.Max(height,_heights[child]+1); }
                }
                _active.Remove(value); _heights.Add(value,height);
                if (depth + height > MaximumDepth) throw new InvalidOperationException("participant_import_bounds");
                return copy;
            }
        }
    }
    internal sealed class PreparedVariantBacking
    {
        readonly TrackedParticipantOwner _owner;
        readonly object[] _roots, _old, _next;
        readonly ulong _expected;
        int _state;
        ParticipantPublicationAttempt _attempt;
        internal PreparedVariantBacking(TrackedParticipantOwner owner, object[] roots, object[] oldData, object[] nextData, ulong expected, object issuer = null)
        {
            CommonParticipantGate.RequireHeld();
            if (owner == null || !owner.IsReplacementIssuerUnderGate(issuer)) throw new ArgumentException("unissued_prepared_participant");
            _owner = owner; _roots = (object[])roots.Clone(); _old = (object[])oldData.Clone(); _next = (object[])nextData.Clone(); _expected = expected;
        }
        internal bool MatchesUnderGate() => _state == 0 && _owner.MatchesUnderGate(_expected);
        // Minted only by this exact opaque replacement; never exposes live/candidate mutable graphs.
        internal ProjectionReplacementOrigin CaptureProjectionOrigin()
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!ProjectionOriginCurrentUnderGate()) throw new InvalidOperationException("stale_projection_replacement_origin");
                return ProjectionReplacementOrigin.FromSource(this);
            }
        }
        bool ProjectionOriginCurrentUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            if (!MatchesUnderGate()) return false;
            for (int i = 0; i < _roots.Length; i++)
                if (!ReferenceEquals(Current(_roots[i]), _old[i])) return false;
            return true;
        }
        internal sealed class ProjectionReplacementOrigin
        {
            readonly PreparedVariantBacking _source;
            readonly TrackedParticipantOwner _owner;
            internal bool IsExactBacking(PreparedVariantBacking backing) => ReferenceEquals(_source, backing);
            internal ulong ExpectedStamp { get; }
            internal ulong InstalledStamp => ExpectedStamp + 1;
            internal ulong RollbackStamp => ExpectedStamp + 2;
            internal int RootCount { get; }
            private ProjectionReplacementOrigin(PreparedVariantBacking source, TrackedParticipantOwner owner, ulong expected, int count)
            { _source = source; _owner = owner; ExpectedStamp = expected; RootCount = count; }
            internal static ProjectionReplacementOrigin FromSource(PreparedVariantBacking source)
            {
                CommonParticipantGate.RequireHeld();
                if (source == null || !source.ProjectionOriginCurrentUnderGate()) throw new InvalidOperationException("stale_projection_replacement_origin");
                return new ProjectionReplacementOrigin(source, source._owner, source._expected, source._roots.Length);
            }
            internal bool MatchesUnderGate(TrackedParticipantOwner exactOwner)
            {
                CommonParticipantGate.RequireHeld();
                return ReferenceEquals(exactOwner, _owner) && _source.ProjectionOriginCurrentUnderGate() &&
                    _source._expected == ExpectedStamp && _source._roots.Length == RootCount;
            }
            // Preparation-only closed entry reader. No live/candidate mutable backing leaves this type.
            internal CandidateNode CaptureCandidateRoot(int index)
            {
                lock (CommonParticipantGate.SyncRoot)
                {
                    if (!MatchesUnderGate(_owner) || index < 0 || index >= RootCount) throw new InvalidOperationException("stale_projection_candidate_root");
                    return CandidateNode.FromRoot(this, index);
                }
            }
            internal sealed class CandidateNode
            {
                readonly ProjectionReplacementOrigin _origin;
                readonly object _identity, _backing;
                internal readonly bool IsRoot;
                internal readonly int RootIndex;
                internal ProjectionNodeKind Kind => _identity is GdDict ? ProjectionNodeKind.Dictionary : ProjectionNodeKind.Array;
                private CandidateNode(ProjectionReplacementOrigin origin, object identity, object backing, bool root, int rootIndex)
                { _origin = origin; _identity = identity; _backing = backing; IsRoot = root; RootIndex = rootIndex; }
                internal static CandidateNode FromRoot(ProjectionReplacementOrigin origin, int index)
                {
                    CommonParticipantGate.RequireHeld();
                    if (origin == null || !origin.MatchesUnderGate(origin._owner) || index < 0 || index >= origin.RootCount)
                        throw new InvalidOperationException("stale_projection_candidate_root");
                    return new CandidateNode(origin, origin._source._roots[index], origin._source._next[index], true, index);
                }
                internal EnrolledProjectionNode PreparePrivateBinding(ParticipantProjectionCohort cohort, object issuer, ProjectionNodeHandle handle, int ownerIndex, int count)
                {
                    lock (CommonParticipantGate.SyncRoot)
                    {
                        RequireCurrentUnderGate();
                        if (cohort == null || !cohort.OwnsMaintenanceIssuer(issuer)) throw new ArgumentException("foreign_candidate_binding_issuer");
                        if (count < 0 || count > 4096) throw new ArgumentException("candidate_node_capacity");
                        return new EnrolledProjectionNode(handle, _origin._owner, ownerIndex, false, new object[count], _identity, _backing);
                    }
                }
                internal void BindPrivateChild(ParticipantProjectionCohort cohort, object issuer, EnrolledProjectionNode binding, int index, CandidateNode child)
                {
                    lock (CommonParticipantGate.SyncRoot)
                    {
                        RequireCurrentUnderGate(); child.RequireCurrentUnderGate();
                        if (cohort == null || !cohort.OwnsMaintenanceIssuer(issuer)) throw new ArgumentException("foreign_candidate_binding_issuer");
                        if (!ReferenceEquals(_origin, child._origin)) throw new ArgumentException("foreign_candidate_child");
                        binding.InitializeChildSlot(index, child._identity);
                    }
                }
                internal bool MatchesBinding(EnrolledProjectionNode binding) => binding != null && binding.IsExactNode(_identity);
                internal bool IsSameNode(CandidateNode other) => other != null && ReferenceEquals(_origin, other._origin) && ReferenceEquals(_identity, other._identity);
                internal bool MatchesLiveIdentity(object node) => ReferenceEquals(_identity, node);
                internal int CaptureEntryCount()
                {
                    lock (CommonParticipantGate.SyncRoot)
                    {
                        RequireCurrentUnderGate();
                        return Kind == ProjectionNodeKind.Dictionary ? ((GdDict.Storage)_backing).Keys.Count : ((List<object>)_backing).Count;
                    }
                }
                internal CandidateEntry CaptureEntry(int index)
                {
                    lock (CommonParticipantGate.SyncRoot)
                    {
                        RequireCurrentUnderGate();
                        object value; ProjectionScalar key;
                        if (Kind == ProjectionNodeKind.Dictionary)
                        {
                            var map = (GdDict.Storage)_backing;
                            if (index < 0 || index >= map.Keys.Count) throw new ArgumentOutOfRangeException(nameof(index));
                            key = ProjectionScalar.FromNormalized(map.Keys[index]); value = map.Values[index];
                        }
                        else
                        {
                            var array = (List<object>)_backing;
                            if (index < 0 || index >= array.Count) throw new ArgumentOutOfRangeException(nameof(index));
                            key = default; value = array[index];
                        }
                        if (value is GdDict childMap) return new CandidateEntry(key, new CandidateNode(_origin, childMap, childMap.RawStorage, false, -1));
                        if (value is GdArray childArray) return new CandidateEntry(key, new CandidateNode(_origin, childArray, childArray.RawStorage, false, -1));
                        return new CandidateEntry(key, ProjectionScalar.FromNormalized(value));
                    }
                }
                void RequireCurrentUnderGate()
                {
                    if (!_origin.MatchesUnderGate(_origin._owner)) throw new InvalidOperationException("stale_projection_candidate_node");
                    // Descendants remain private until this exact source installs; installed sources fail above.
                    if (!IsRoot && !ReferenceEquals(Current(_identity), _backing)) throw new InvalidOperationException("changed_projection_candidate_backing");
                }
            }
            internal readonly struct CandidateEntry
            {
                internal readonly ProjectionScalar Key, Scalar;
                internal readonly CandidateNode Child;
                internal bool IsChild => Child != null;
                internal CandidateEntry(ProjectionScalar key, ProjectionScalar scalar) { Key = key; Scalar = scalar; Child = null; }
                internal CandidateEntry(ProjectionScalar key, CandidateNode child) { Key = key; Scalar = default; Child = child; }
            }
            internal bool RootMatchesUnderGate(int index, object exactRoot)
            {
                CommonParticipantGate.RequireHeld();
                return index >= 0 && index < RootCount && MatchesUnderGate(_owner) && ReferenceEquals(_source._roots[index], exactRoot);
            }
        }

        internal void InstallUnderGate(ParticipantPublicationAttempt attempt)
        {
            CommonParticipantGate.RequireHeld();
            if (attempt == null || !attempt.IsOpenUnderGate || !MatchesUnderGate()) throw new InvalidOperationException("stale_prepared_participant");
            bool maintained = _owner.ProjectionCohort != null && _owner.ProjectionCohort.OwnsMaintainedBackingUnderGate(this, attempt);
            if (!maintained) _owner.InvalidateProjectionUnderGate();
            _attempt = attempt;
            if (!maintained) for (int i = 0; i < _roots.Length; i++) _owner.BeforeUnsupportedNodeWriteUnderGate(_roots[i], true);
            for (int i = 0; i < _roots.Length; i++) Swap(_roots[i], _next[i]);
            _owner.AdvancePreparedUnderGate(_expected + 1); _state = 1;
        }
        internal void RollbackUnderGate(ParticipantPublicationAttempt attempt)
        {
            CommonParticipantGate.RequireHeld();
            if (attempt == null || !attempt.IsOpenUnderGate || !ReferenceEquals(_attempt, attempt) || _state != 1 || !_owner.MatchesUnderGate(_expected + 1)) throw new InvalidOperationException("stale_rollback");
            for (int i = 0; i < _roots.Length; i++)
                if (!ReferenceEquals(Current(_roots[i]), _next[i])) throw new InvalidOperationException("changed_rollback_backing");
            bool maintained = _owner.ProjectionCohort != null && _owner.ProjectionCohort.OwnsMaintainedBackingUnderGate(this, attempt);
            if (!maintained) for (int i = 0; i < _roots.Length; i++) _owner.BeforeUnsupportedNodeWriteUnderGate(_roots[i], true);
            for (int i = 0; i < _roots.Length; i++) Swap(_roots[i], _old[i]);
            // Tracking revisions never go backwards, even when owner publication aborts.
            _owner.AdvancePreparedUnderGate(_expected + 2); _state = 2;
        }
        static object Current(object root) => root is GdDict d ? (object)d.RawStorage : ((GdArray)root).RawStorage;
        static void Swap(object root, object data)
        {
            if (root is GdDict d) d.RawStorage = (GdDict.Storage)data;
            else ((GdArray)root).RawStorage = (List<object>)data;
        }
    }
    internal sealed class ParticipantPublicationAttempt : IDisposable
    {
        readonly int _threadId;
        bool _open;
        internal ParticipantPublicationAttempt()
        {
            Monitor.Enter(CommonParticipantGate.SyncRoot);
            _threadId = Thread.CurrentThread.ManagedThreadId; _open = true;
        }
        internal bool IsOpenUnderGate => _open && _threadId == Thread.CurrentThread.ManagedThreadId && Monitor.IsEntered(CommonParticipantGate.SyncRoot);
        public void Dispose()
        {
            if (!IsOpenUnderGate) throw new InvalidOperationException("invalid_publication_attempt_close");
            _open = false; Monitor.Exit(CommonParticipantGate.SyncRoot);
        }
    }
    internal sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
}
