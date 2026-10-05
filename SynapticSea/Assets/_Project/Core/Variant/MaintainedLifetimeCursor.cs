using System;
using System.Collections.Generic;
using System.Threading;
namespace SynapticSea.Core.Variant
{
    internal sealed partial class ParticipantProjectionCohort
    {
        internal ParticipantProjectionRegistry.MaintainedLifetimeCursor PrepareMaintainedLifetimes(
            MaintainedCandidate candidate, MaintainedGraphAdmissionCursor admission)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (candidate == null || admission == null || !candidate.IsForCohort(this) ||
                    !candidate.MatchesUnderGate() || !admission.IsForCandidate(candidate) || !admission.MatchesUnderGate())
                    throw new InvalidOperationException("stale_maintenance_lifetime_source");
            }
            return ParticipantProjectionRegistry.MaintainedLifetimeCursor.Create(_registry, this, candidate, admission);
        }
    }
    internal sealed partial class ParticipantProjectionRegistry
    {
        // Private immutable candidate dictionary preparation. Neither issuance nor admission clocks
        // are changed here. Complete publication will exchange this dictionary pointer atomically.
        internal sealed class MaintainedLifetimeCursor : IDisposable
        {
            readonly ParticipantProjectionRegistry _registry;
            readonly MaintainedAdmissionSource _source;
            readonly ParticipantProjectionCohort.MaintainedCandidate _candidate;
            readonly MaintainedGraphAdmissionCursor _admission;
            readonly int _thread = Thread.CurrentThread.ManagedThreadId;
            Dictionary<ulong,Lifetime>.Enumerator _reader;
            Dictionary<ulong,Lifetime> _next;
            int _phase, _change;
            internal ProjectionCursorStatus Status { get; private set; } = ProjectionCursorStatus.Pending;
            internal string Reason { get; private set; } = "";
            internal int PreparedCount => Status == ProjectionCursorStatus.Complete ? _next.Count : throw new InvalidOperationException("maintenance_lifetime_incomplete");
            private MaintainedLifetimeCursor(ParticipantProjectionRegistry registry,
                MaintainedAdmissionSource source, ParticipantProjectionCohort.MaintainedCandidate candidate,
                MaintainedGraphAdmissionCursor admission, int capacity)
            {
                _registry = registry; _source = source; _candidate = candidate; _admission = admission;
                // Disclosed off-gate preparation allocation; all subsequent additions fit this capacity.
                _next = new Dictionary<ulong,Lifetime>(capacity);
                lock (CommonParticipantGate.SyncRoot)
                {
                    if (!MatchesSourceUnderGate()) { Refuse("stale_maintenance_lifetime_source"); return; }
                    _reader = registry._lifetimes.GetEnumerator();
                }
            }
            internal static MaintainedLifetimeCursor Create(ParticipantProjectionRegistry registry,
                ParticipantProjectionCohort cohort, ParticipantProjectionCohort.MaintainedCandidate candidate,
                MaintainedGraphAdmissionCursor admission)
            {
                MaintainedAdmissionSource source; int capacity;
                lock (CommonParticipantGate.SyncRoot)
                {
                    if (candidate == null || admission == null || !candidate.IsForCohort(cohort) ||
                        !candidate.MatchesUnderGate() || !admission.IsForCandidate(candidate) || !admission.MatchesUnderGate())
                        throw new InvalidOperationException("stale_maintenance_lifetime_source");
                    source = registry.CaptureMaintainedSourceUnderGate(cohort); capacity = registry._lifetimes.Count;
                }
                return new MaintainedLifetimeCursor(registry, source, candidate, admission, capacity);
            }
            bool MatchesSourceUnderGate() => _source.IsCurrentUnderGate() &&
                _candidate.MatchesUnderGate() && _admission.IsForCandidate(_candidate) && _admission.MatchesUnderGate();
            internal Dictionary<ulong,Lifetime> CapturePublicationLifetimes(ParticipantProjectionCohort cohort, object issuer)
            { if (cohort == null || !cohort.OwnsMaintenanceIssuer(issuer)) throw new ArgumentException("foreign_maintenance_issuer"); return _next; }
            internal bool MatchesUnderGate()
            {
                CommonParticipantGate.RequireHeld();
                return Status == ProjectionCursorStatus.Complete && MatchesSourceUnderGate();
            }
            internal bool RetireDeadBinding(ParticipantProjectionCohort cohort, ulong id,
                WeakReference<EnrolledProjectionNode> binding, object issuer)
            {
                if (cohort == null || !cohort.OwnsMaintenanceIssuer(issuer)) throw new ArgumentException("foreign_maintenance_issuer");
                lock (CommonParticipantGate.SyncRoot)
                {
                    if (!MatchesUnderGate() || !cohort.CanRetireDeadBindingUnderGate(id, binding, issuer) ||
                        _admission.PreparedTable.Get(new ProjectionNodeId(_registry._identity, id)) != null) return false;
                    return _next.Remove(id);
                }
            }
            internal MaintainedRegistryPublication PreparePublication(ParticipantProjectionCohort cohort,
                ProjectionRootDescriptor after, ProjectionRootDescriptor rollback, long auxiliaryUnits, long beforeAuxiliaryUnits, object issuer)
            {
                if (cohort == null || !cohort.OwnsMaintenanceIssuer(issuer)) throw new ArgumentException("foreign_maintenance_issuer");
                return MaintainedRegistryPublication.Create(_registry, cohort, this, _candidate, _admission,
                    after, rollback, auxiliaryUnits, beforeAuxiliaryUnits, issuer);
            }
            internal ProjectionCursorStatus Advance(int budget)
            {
                if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("projection_cursor_thread");
                if (budget < 1 || budget > 64) throw new ArgumentOutOfRangeException(nameof(budget));
                if (Status != ProjectionCursorStatus.Pending) return Status;
                for (int unit = 0; unit < budget && Status == ProjectionCursorStatus.Pending; unit++)
                {
                    if (_phase == 0)
                    {
                        KeyValuePair<ulong,Lifetime> entry = default; bool found;
                        lock (CommonParticipantGate.SyncRoot)
                        {
                            if (!MatchesSourceUnderGate()) return Refuse("stale_maintenance_lifetime_source");
                            found = _reader.MoveNext(); if (found) entry = _reader.Current;
                        }
                        if (found) _next.Add(entry.Key, entry.Value); else _phase = 1;
                    }
                    else if (_phase == 1)
                    {
                        if (_change == _candidate.VersionCount) { _phase = 2; continue; }
                        var version = _candidate.Version(_change++);
                        lock (CommonParticipantGate.SyncRoot)
                        {
                            if (!MatchesSourceUnderGate() || !_source.ValidateIssuedUnderGate(version))
                                return Refuse("stale_maintenance_lifetime_issuance");
                        }
                        if (!_next.TryGetValue(version.Id.Value, out var before)) return Refuse("missing_maintenance_lifetime");
                        _next[version.Id.Value] = new Lifetime(before.Kind, version.Version,
                            before.LastIssued, before.Issuer, before.Issued);
                    }
                    else
                    {
                        lock (CommonParticipantGate.SyncRoot)
                        {
                            if (!MatchesSourceUnderGate()) return Refuse("stale_maintenance_lifetime_source");
                            Status = ProjectionCursorStatus.Complete;
                        }
                    }
                }
                return Status;
            }
            ProjectionCursorStatus Refuse(string reason)
            { Reason = reason; Status = ProjectionCursorStatus.Refused; _next = null; _reader.Dispose(); return Status; }
            public void Dispose()
            { _reader.Dispose(); _next = null; if (Status != ProjectionCursorStatus.Refused) Status = ProjectionCursorStatus.Cancelled; }
        }
    }
}
