using System;
using System.Collections.Generic;
using System.Threading;
namespace SynapticSea.Core.Variant
{
    internal sealed partial class ParticipantProjectionRegistry
    {
        internal void AdvanceMaintenanceLifetimeEpochUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            CheckMaintenanceLifetimeCanAdvanceUnderGate();
            _maintenanceLifetimeEpoch++;
        }
        internal void CheckMaintenanceLifetimeCanAdvanceUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            if (_maintenanceLifetimeEpoch == ulong.MaxValue) throw new InvalidOperationException("maintenance_lifetime_epoch_exhausted");
        }
        internal MaintainedAdmissionSource CaptureMaintainedSourceUnderGate(ParticipantProjectionCohort cohort)
            => MaintainedAdmissionSource.Create(this, cohort);
        internal sealed class MaintainedAdmissionSource
        {
            readonly ParticipantProjectionRegistry _registry;
            readonly ParticipantProjectionCohort _cohort;
            readonly ulong _revision, _epoch, _lifetimeEpoch;
            internal readonly ProjectionVersionTable BeforeTable;
            internal readonly ProjectionGraphPlan BeforePlan;
            private MaintainedAdmissionSource(ParticipantProjectionRegistry registry, ParticipantProjectionCohort cohort)
            { _registry = registry; _cohort = cohort; BeforeTable = registry._table; BeforePlan = registry._plan; _revision = registry._revision; _epoch = registry._readinessEpoch; _lifetimeEpoch = registry._maintenanceLifetimeEpoch; }
            internal static MaintainedAdmissionSource Create(ParticipantProjectionRegistry registry, ParticipantProjectionCohort cohort)
            {
                CommonParticipantGate.RequireHeld();
                if (registry == null || cohort == null || !ReferenceEquals(registry._ownerCohort, cohort) || !cohort.Ready || registry._preparation != null || registry._ownerPreparation != null || registry._maintenancePublication != null)
                    throw new InvalidOperationException("unadmitted_maintenance_source");
                return new MaintainedAdmissionSource(registry, cohort);
            }
            internal bool IsCurrentUnderGate()
            {
                CommonParticipantGate.RequireHeld();
                return ReferenceEquals(_registry._ownerCohort, _cohort) && _cohort.Ready &&
                    ReferenceEquals(_registry._table, BeforeTable) && ReferenceEquals(_registry._plan, BeforePlan) &&
                    _registry._revision == _revision && _registry._readinessEpoch == _epoch && _registry._maintenanceLifetimeEpoch == _lifetimeEpoch;
            }
            internal bool ValidateIssuedUnderGate(ProjectionNodeVersion node)
            {
                CommonParticipantGate.RequireHeld();
                return IsCurrentUnderGate() && _registry.ExactIssuedUnderGate(node) &&
                    node.Version > _registry._lifetimes[node.Id.Value].LastAdmitted;
            }
        }
    }
    // Pure preparation data: a complete table/graph proof, never a publication or reward authority.
    internal sealed class MaintainedGraphAdmissionCursor : IDisposable
    {
        readonly ParticipantProjectionCohort.MaintainedCandidate _candidate;
        readonly ParticipantProjectionRegistry.MaintainedAdmissionSource _source;
        readonly ProjectionRootDescriptor _descriptor;
        readonly ProjectionLimits _limits;
        readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        readonly Dictionary<ProjectionNodeId, int> _heights;
        readonly HashSet<ProjectionNodeId> _active = new HashSet<ProjectionNodeId>(129);
        readonly Stack<Frame> _stack = new Stack<Frame>(129);
        ProjectionVersionTable _table;
        ProjectionGraphPlan _plan = new ProjectionGraphPlan();
        int _phase, _version, _root, _prune;
        internal ProjectionCursorStatus Status { get; private set; } = ProjectionCursorStatus.Pending;
        internal string Reason { get; private set; } = "";
        internal int LastWorkUnits { get; private set; }
        internal ProjectionVersionTable PreparedTable { get { if (Status != ProjectionCursorStatus.Complete) throw new InvalidOperationException("maintenance_admission_incomplete"); return _table; } }
        internal ProjectionGraphPlan PreparedPlan { get { if (Status != ProjectionCursorStatus.Complete) throw new InvalidOperationException("maintenance_admission_incomplete"); return _plan; } }
        private MaintainedGraphAdmissionCursor(ParticipantProjectionCohort.MaintainedCandidate candidate,
            ParticipantProjectionRegistry.MaintainedAdmissionSource source, ProjectionRootDescriptor descriptor, ProjectionLimits limits)
        {
            _candidate = candidate; _source = source; _descriptor = descriptor; _limits = limits;
            _table = source.BeforeTable;
            int upper = Math.Min(limits.MaximumNodes, checked(source.BeforeTable.Count + candidate.VersionCount));
            _heights = new Dictionary<ProjectionNodeId, int>(upper);
        }
        internal static MaintainedGraphAdmissionCursor Create(ParticipantProjectionCohort cohort, ParticipantProjectionCohort.MaintainedCandidate candidate)
        {
            if (cohort == null) throw new ArgumentNullException(nameof(cohort));
            cohort.CaptureMaintainedAdmissionSource(candidate, out var source, out var descriptor, out var limits);
            return new MaintainedGraphAdmissionCursor(candidate, source, descriptor, limits);
        }
        internal bool IsForCandidate(ParticipantProjectionCohort.MaintainedCandidate candidate)
            => ReferenceEquals(_candidate, candidate);
        internal bool MatchesUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            return Status == ProjectionCursorStatus.Complete && _source.IsCurrentUnderGate() && _candidate.MatchesUnderGate();
        }
        internal ProjectionCursorStatus Advance(int units)
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("projection_cursor_thread");
            if (units < 1 || units > 64) throw new ArgumentOutOfRangeException(nameof(units));
            LastWorkUnits = 0;
            if (Status != ProjectionCursorStatus.Pending) return Status;
            lock (CommonParticipantGate.SyncRoot)
                if (!_source.IsCurrentUnderGate() || !_candidate.MatchesUnderGate()) { Refuse("stale_maintenance_admission"); return Status; }
            while (Status == ProjectionCursorStatus.Pending && LastWorkUnits < units)
            {
                LastWorkUnits++;
                if (_phase == 0)
                {
                    if (_version == _candidate.VersionCount) { _phase = 1; continue; }
                    var next = _candidate.Version(_version++);
                    lock (CommonParticipantGate.SyncRoot)
                        if (!_source.ValidateIssuedUnderGate(next)) { Refuse("invalid_maintenance_issuance"); continue; }
                    _table = _table.Set(next.Id, next); // fixed thirteen-page path, preparation only
                    continue;
                }
                if (_phase == 1)
                {
                    if (_stack.Count == 0)
                    {
                        if (_root == _descriptor.RootCount) { _phase = 2; continue; }
                        Enter(_descriptor.Root(_root++).Node, 0); continue;
                    }
                    var frame = _stack.Peek();
                    if (frame.Next == frame.Node.EntryCount)
                    {
                        _stack.Pop(); _active.Remove(frame.Node.Id); _heights.Add(frame.Node.Id, frame.Height);
                        if (_stack.Count > 0) _stack.Peek().Height = Math.Max(_stack.Peek().Height, frame.Height + 1);
                        continue;
                    }
                    var entry = frame.Node.GetEntry(frame.Next++);
                    if (entry.Value.IsChild)
                    {
                        if (_heights.TryGetValue(entry.Value.Child, out int height))
                        { if (frame.Depth + 1 + height > 128) Refuse("projection_graph_depth"); else frame.Height = Math.Max(frame.Height, height + 1); }
                        else Enter(entry.Value.Child, frame.Depth + 1);
                    }
                    continue;
                }
                if (_prune < _source.BeforePlan.NodeCount)
                {
                    var id = _source.BeforePlan.Node(_prune++);
                    if (!_heights.ContainsKey(id)) _table = _table.Set(id, null);
                    continue;
                }
                if (_table.Count != _heights.Count || _table.Count > _limits.MaximumNodes || ParticipantProjectionRegistry.OwnedTableUnits(_table) > _limits.MaximumRetainedUnits)
                { Refuse("maintenance_admission_capacity"); continue; }
                lock (CommonParticipantGate.SyncRoot)
                    if (!_source.IsCurrentUnderGate() || !_candidate.MatchesUnderGate()) { Refuse("stale_maintenance_admission"); continue; }
                Status = ProjectionCursorStatus.Complete;
            }
            return Status;
        }
        void Enter(ProjectionNodeId id, int depth)
        {
            if (_active.Contains(id)) { Refuse("projection_cycle"); return; }
            if (_heights.TryGetValue(id, out int height)) { if (depth + height > 128) Refuse("projection_graph_depth"); return; }
            if (depth > 128 || _plan.NodeCount == _limits.MaximumNodes) { Refuse("projection_graph_capacity"); return; }
            var node = _table.Get(id); if (node == null) { Refuse("projection_missing_child"); return; }
            _active.Add(id); _plan = _plan.Append(id, node.PageCount + 1); _stack.Push(new Frame(node, depth));
        }
        void Refuse(string reason) { Reason = reason; Status = ProjectionCursorStatus.Refused; _table = null; _plan = null; _stack.Clear(); _active.Clear(); _heights.Clear(); }
        public void Dispose() { if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("projection_cursor_thread"); Status = ProjectionCursorStatus.Released; _table = null; _plan = null; _stack.Clear(); _active.Clear(); _heights.Clear(); }
        sealed class Frame
        {
            internal readonly ProjectionNodeVersion Node;
            internal readonly int Depth;
            internal int Next, Height;
            internal Frame(ProjectionNodeVersion node, int depth) { Node = node; Depth = depth; }
        }
    }
}
