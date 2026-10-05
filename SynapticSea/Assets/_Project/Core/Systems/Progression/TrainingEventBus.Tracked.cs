using System;
using SynapticSea.Core.Variant;
using SynapticSea.Core.Services;

namespace SynapticSea.Core.Systems
{
    public partial class TrainingEventBus
    {
        readonly TrackedParticipantOwner _trackedOwner;
        readonly object _maintainedReplacementIssuer = new object();
        public TrainingEventBus() { }
        TrainingEventBus(bool tracked)
        {
            _trackedOwner = new TrackedParticipantOwner();
            _actionsById = _trackedOwner.NewDict(root: true);
            _log = _trackedOwner.NewArray(root: true);
        }
        // Diagnostic only; never retrofits already exposed legacy graphs. Nonnull eligibility delegates
        // refuse reusable work. Ordinary tracked Emit/Replay use detached exact-policy models and prepared
        // atomic installs; no progression grant, loader or presentation callback runs under the final guard.
        internal static TrainingEventBus CreateTracked() => new TrainingEventBus(true);
        GdDict ProtectRecord(GdDict value) => _trackedOwner == null ? value : _trackedOwner.ImportDict(value);
        void SetDelegate<T>(ref T field, T value) where T : class
        {
            if (_trackedOwner == null) { field = value; return; }
            lock (CommonParticipantGate.SyncRoot)
            {
                _trackedOwner.CheckCanAdvanceUnderGate(); _trackedOwner.InvalidateProjectionUnderGate();
                field = value;
                _trackedOwner.TouchUnderGate();
            }
        }
        void RequireSealedPolicy()
        {
            if (_eventFilter != null || _skillGate != null)
                throw new InvalidOperationException("Diagnostic training refuses callback-dependent eligibility.");
        }
        internal ulong CaptureTrackedStamp()
        {
            if (_trackedOwner == null) throw new InvalidOperationException("Legacy training is not lease-capable.");
            lock (CommonParticipantGate.SyncRoot) { RequireSealedPolicy(); return _trackedOwner.Stamp; }
        }
        public bool Configure(GdDict actionsCatalog = null)
        {
            if (_trackedOwner == null) return ConfigureLegacy(actionsCatalog);
            if (actionsCatalog == null || actionsCatalog.IsEmpty)
                throw new InvalidOperationException("Tracked Configure requires an explicit detached catalog.");
            ulong stamp = CaptureTrackedStamp();
            var detached = new TrainingEventBus();
            bool configured = detached.ConfigureLegacy(actionsCatalog.DeepCopy());
            var backing = _trackedOwner.PrepareReplacements(new object[] { _actionsById },
                new object[] { detached._actionsById }, stamp);
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                if (!backing.MatchesUnderGate()) throw new InvalidOperationException("Stale training policy.");
                backing.InstallUnderGate(attempt);
            }
            return configured;
        }
        public GdDict Emit(string eventId, string targetId, PlayerProgressionState progression)
        {
            if (_trackedOwner == null) return EmitLegacy(eventId, targetId, progression);
            ReplayPrepared(eventId, targetId, progression, false, out GdDict record, out _);
            return record;
        }
        void ReplayPrepared(string eventId, string targetId, PlayerProgressionState progression,
            bool replay, out GdDict record, out long delivered)
        {
            record = null; delivered = 0;
            if (!ResourceAuthorityPublication.TryAcquire(out var lease, out string reason))
                throw new InvalidOperationException(reason);
            GdDict before, actions;
            ulong stamp, progressionStamp = 0;
            PlayerProgressionState detachedProgression;
            Action<GdDict> notification;
            lock (CommonParticipantGate.SyncRoot)
            {
                RequireSealedPolicy(); stamp = _trackedOwner.Stamp;
                before = ToDictLegacy(); actions = _actionsById.DeepCopy(); notification = _onEventResolved;
                detachedProgression = progression == null ? null : progression.CaptureTrackedReplay(out progressionStamp);
            }
            var detached = new TrainingEventBus();
            detached._actionsById.Merge(actions);
            if (!detached.ApplySummaryLegacy(before)) throw new ArgumentException("Invalid tracked training history.");
            GdDict emitted = null;
            if (replay)
            {
                long checkedTotal = 0;
                foreach (object item in detached._log)
                {
                    if (!(item is GdDict row) || row.GetBool("receipt_owned") || row.GetBool("gated")) continue;
                    long xp = V.I64(row.Get("base_xp", 0L));
                    if (row.GetString("skill_id").Length != 0 && xp > 0) checkedTotal = checked(checkedTotal + xp);
                }
                delivered = detached.ReplayIntoLegacy(detachedProgression);
            }
            else
            {
                // Checked scalar preflight on the detached operation before replaying any XP.
                if (detached._dropped == long.MaxValue) throw new OverflowException();
                long xp = V.I64(actions.GetDictOrEmpty(eventId).Get("base_xp", 0L));
                if (xp > 0 && detached._xpTotal > long.MaxValue - xp) throw new OverflowException();
                emitted = detached.EmitLegacy(eventId, targetId, detachedProgression);
            }
            var preparedTraining = PrepareTrackedReplacement(detached.ToDictLegacy(), stamp);
            var preparedProgression = progression == null ? null :
                progression.PrepareTrackedReplacement(detachedProgression.GetSummary(), progressionStamp);
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                if (!lease.IsCurrent || !preparedTraining.MatchesUnderGate() ||
                    (preparedProgression != null && !preparedProgression.MatchesUnderGate()))
                    throw new InvalidOperationException("Stale prepared training replay.");
                bool progressionInstalled = false;
                try
                {
                    if (preparedProgression != null) { preparedProgression.InstallUnderGate(attempt); progressionInstalled = true; }
                    preparedTraining.InstallUnderGate(attempt);
                    if (emitted != null) record = (GdDict)_log[_log.Count - 1];
                }
                catch
                {
                    if (progressionInstalled) preparedProgression.RollbackUnderGate(attempt);
                    throw;
                }
            }
            if (record != null) notification?.Invoke(record);
        }
        public void RecordApplied(GdDict eventRecord, string commitId)
        {
            if (_trackedOwner == null) { RecordAppliedLegacy(eventRecord, commitId); return; }
            GdDict before;
            ulong stamp;
            lock (CommonParticipantGate.SyncRoot) { stamp = _trackedOwner.Stamp; before = ToDictLegacy(); }
            var detached = new TrainingEventBus();
            if (!detached.ApplySummaryLegacy(before)) throw new ArgumentException("Invalid training history.");
            detached.RecordAppliedLegacy(eventRecord, commitId); // foreign input inspected/copied outside gate
            if (detached._log.Count == V.I64(before.Get("event_count")))
            {
                lock (CommonParticipantGate.SyncRoot)
                    if (!_trackedOwner.MatchesUnderGate(stamp)) throw new InvalidOperationException("Stale receipt read.");
                return;
            }
            var prepared = PrepareTrackedReplacement(detached.ToDictLegacy(), stamp);
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                if (!prepared.MatchesUnderGate()) throw new InvalidOperationException("Stale training receipt.");
                prepared.InstallUnderGate(attempt);
            }
        }
        public long ReplayInto(PlayerProgressionState progression)
        {
            if (_trackedOwner == null) return ReplayIntoLegacy(progression);
            ReplayPrepared(null, null, progression, true, out _, out long delivered);
            return delivered;
        }
        public void Reset()
        {
            if (_trackedOwner == null) { ResetLegacy(); return; }
            ulong stamp = CaptureTrackedStamp();
            var backing = _trackedOwner.PrepareReplacements(new object[] { _log }, new object[] { new GdArray() }, stamp);
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                if (!backing.MatchesUnderGate()) throw new InvalidOperationException("Stale training reset.");
                backing.InstallUnderGate(attempt);
                _dropped = 0; _xpTotal = 0;
            }
        }
        public GdDict ToDict()
        {
            if (_trackedOwner == null) return ToDictLegacy();
            lock (CommonParticipantGate.SyncRoot) return ToDictLegacy();
        }
        public bool ApplySummary(GdDict summary)
        {
            if (_trackedOwner == null) return ApplySummaryLegacy(summary);
            ulong stamp;
            lock (CommonParticipantGate.SyncRoot) stamp = _trackedOwner.Stamp;
            PreparedReplacement prepared;
            try { prepared = PrepareTrackedReplacement(summary, stamp); }
            catch (ArgumentException) { return false; }
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                if (!prepared.MatchesUnderGate()) return false;
                prepared.InstallUnderGate(attempt);
                return true;
            }
        }
        internal PreparedReplacement PrepareTrackedReplacement(GdDict summary, ulong expectedStamp)
        {
            if (_trackedOwner == null) throw new InvalidOperationException("Legacy training is not lease-capable.");
            GdDict before;
            lock (CommonParticipantGate.SyncRoot)
            {
                RequireSealedPolicy();
                if (!_trackedOwner.MatchesUnderGate(expectedStamp)) throw new InvalidOperationException("Stale training participant.");
                before = ToDictLegacy();
            }
            var validator = new TrainingEventBus();
            if (!validator.ApplySummary(before) || !validator.ApplySummary(summary))
                throw new ArgumentException("Invalid training replacement or retained receipt conflict.");
            GdDict admitted = validator.ToDict();
            var backing = _trackedOwner.PrepareReplacements(new object[] { _log },
                new object[] { admitted.Get("log") }, expectedStamp);
            return new PreparedReplacement(this, _maintainedReplacementIssuer, backing, V.I64(before.Get("dropped")), V.I64(before.Get("xp_total")),
                V.I64(admitted.Get("dropped")), V.I64(admitted.Get("xp_total")));
        }
        internal sealed class PreparedReplacement
        {
            readonly TrainingEventBus _target;
            readonly PreparedVariantBacking _backing;
            readonly long _oldDropped, _oldXp, _newDropped, _newXp;
            ParticipantPublicationAttempt _attempt;
            bool _installed;
            internal PreparedReplacement(TrainingEventBus target, object issuer, PreparedVariantBacking backing,
                long oldDropped, long oldXp, long newDropped, long newXp)
            {
                if (target == null || !ReferenceEquals(issuer, target._maintainedReplacementIssuer) || backing == null)
                    throw new ArgumentException("unissued_training_replacement");
                _target = target; _backing = backing; _oldDropped = oldDropped; _oldXp = oldXp;
                _newDropped = newDropped; _newXp = newXp;
            }
            internal bool IsForModel(TrainingEventBus target) => ReferenceEquals(_target, target);
            internal long ProjectedDropped => _newDropped;
            internal long ProjectedXp => _newXp;
            internal PreparedVariantBacking.ProjectionReplacementOrigin CaptureProjectionOrigin()
                => _backing.CaptureProjectionOrigin();
            internal bool MatchesUnderGate() => _backing.MatchesUnderGate() &&
                _target._eventFilter == null && _target._skillGate == null &&
                _target._dropped == _oldDropped && _target._xpTotal == _oldXp;
            internal void InstallUnderGate(ParticipantPublicationAttempt attempt)
            {
                _backing.InstallUnderGate(attempt);
                _target._dropped = _newDropped; _target._xpTotal = _newXp;
                _attempt = attempt; _installed = true;
            }
            internal void RollbackUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (!_installed || !ReferenceEquals(_attempt, attempt)) throw new InvalidOperationException("Wrong training attempt.");
                // The backing validates the still-open same-thread gate token before counter writes.
                _backing.RollbackUnderGate(attempt);
                _target._dropped = _oldDropped; _target._xpTotal = _oldXp; _installed = false;
            }
        }
    }
}
