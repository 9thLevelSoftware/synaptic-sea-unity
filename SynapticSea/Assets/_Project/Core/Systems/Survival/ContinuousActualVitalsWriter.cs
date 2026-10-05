using System;
using System.Threading;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    // Closed typed writer prerequisite, not a complete world/scene eligibility authority.
    internal sealed class ContinuousActualVitalsWriter : IDamageVitalsTarget
    {
        readonly RunSession _session;
        readonly VitalsState _actual;
        readonly ActualVitalsProjectionBridge _bridge;
        readonly object _notificationIssuer = new object();
        readonly int _ownerThread;
        ContinuousSimulationVitalsSnapshot _damageRead;
        ulong _damageEpoch;
        internal ContinuousActualVitalsWriter(RunSession session, ActualVitalsProjectionBridge bridge)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _actual = session.VitalsState;
            _ownerThread = Thread.CurrentThread.ManagedThreadId;
            lock (CommonParticipantGate.SyncRoot)
                if (_actual == null || (!_bridge.IsBoundActualSessionUnderGate(_session) || !_bridge.IsBoundActualModelUnderGate(_actual))) throw new ArgumentException("vitals_writer_binding");
        }
        internal ulong DamageEpoch { get { lock (CommonParticipantGate.SyncRoot) return _damageEpoch; } }
        internal bool IsNotificationIssuer(object issuer) => ReferenceEquals(issuer, _notificationIssuer);
        internal bool TryTick(double delta, DiagnosticVitalsTickInput input, out string reason)
        {
            RequireOwnerThread();
            var snapshot = _bridge.ReadSimulation();
            return TryPublish(snapshot, ContinuousVitalsEvaluator.Tick(snapshot.Values, delta, input), out reason);
        }
        internal bool TryDelta(double health, double stamina, double hunger, double thirst, out string reason)
        {
            RequireOwnerThread();
            var snapshot = _bridge.ReadSimulation();
            return TryPublish(snapshot, ContinuousVitalsEvaluator.Delta(snapshot.Values, health, stamina, hunger, thirst), out reason);
        }
        double IDamageVitalsTarget.Health
        {
            get { RequireOwnerThread(); _damageRead = _bridge.ReadSimulation(); return _damageRead.Values.Health; }
            set
            {
                RequireOwnerThread();
                var read = _damageRead; _damageRead = null;
                if (read == null) throw new InvalidOperationException("damage_requires_exact_prior_read");
                if (!TryPublish(read, ContinuousVitalsEvaluator.CombatHealth(read.Values, value), out var reason))
                    throw new InvalidOperationException(reason);
            }
        }
        void RequireOwnerThread()
        { if (Thread.CurrentThread.ManagedThreadId != _ownerThread) throw new InvalidOperationException("vitals_writer_thread"); }
        // Capture revocation must suppress fresh auxiliary debit, but never suppress actual survival writers.
        internal bool CaptureAvailable { get { lock (CommonParticipantGate.SyncRoot) return _bridge.CaptureAvailableUnderGate() && _damageEpoch != ulong.MaxValue; } }
        bool TryPublish(ContinuousSimulationVitalsSnapshot snapshot, ContinuousVitalsProposal proposal, out string reason)
        {
            reason = "stale_vitals_proposal";
            if (snapshot == null || proposal == null || !ReferenceEquals(snapshot.Values, proposal.Before)) return false;
            ulong epoch, nextEpoch; bool forceNative;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!ReferenceEquals(_session.VitalsState, _actual) || !_bridge.IsBoundSimulationSessionUnderGate(_session) || !_bridge.IsBoundSimulationModelUnderGate(_actual) || !snapshot.MatchesUnderGate(_bridge)) return false;
                epoch = _damageEpoch;
                // Opaque native generation continues rotating; the diagnostic damage counter never wraps.
                forceNative = proposal.DamageCount > 0 && epoch == ulong.MaxValue;
                nextEpoch = proposal.DamageCount > 0 && epoch != ulong.MaxValue ? epoch + 1 : epoch;
            }
            var notification = new ContinuousVitalsDamageNotification(this, _notificationIssuer, _actual, proposal);
            bool committed = false;
            ActualVitalsProjectionBridge.PreparedChange projected = null;
            if (!forceNative)
            {
                bool available;
                lock (CommonParticipantGate.SyncRoot) available = _bridge.CaptureAvailableUnderGate();
                if (available)
                {
                    var projectedRead = _bridge.Read();
                    _bridge.PrepareNext(projectedRead, proposal.After, out projected, out reason);
                }
            }
            using (projected)
            {
                if (projected != null) lock (CommonParticipantGate.SyncRoot)
                {
                    if (_damageEpoch != epoch || !ReferenceEquals(_session.VitalsState, _actual) ||
                        !_bridge.IsBoundSimulationSessionUnderGate(_session) || !_bridge.IsBoundSimulationModelUnderGate(_actual) || !snapshot.MatchesUnderGate(_bridge) || !projected.MatchesUnderGate()) return false;
                    if (proposal.After.Health <= 0) _session.RetireContinuousOutputUnderGate("death");
                    committed = projected.TryInstallActualRawAndProjectionUnderGate(out reason);
                    if (committed) { _damageEpoch = nextEpoch; notification.Committed = true; }
                }
            }
            if (!committed)
            {
                // Same fresh, privately issued native read; stale proposals cannot select this path.
                if (!_bridge.PrepareSimulationOnly(snapshot, proposal, out var native, out reason)) return false;
                using (native) lock (CommonParticipantGate.SyncRoot)
                {
                    if (_damageEpoch != epoch || !ReferenceEquals(_session.VitalsState, _actual) ||
                        !_bridge.IsBoundSimulationSessionUnderGate(_session) || !_bridge.IsBoundSimulationModelUnderGate(_actual) || !snapshot.MatchesUnderGate(_bridge) || !native.MatchesUnderGate()) return false;
                    if (proposal.After.Health <= 0) _session.RetireContinuousOutputUnderGate("death");
                    if (!native.TryInstallNativeOnlyUnderGate(out reason)) return false;
                    // Projection readiness was revoked before native writes. Closed assignment-only suffix.
                    _damageEpoch = nextEpoch; notification.Committed = true;
                }
            }
            try { _actual.NotifyContinuousDamage(notification); reason = ""; }
            catch (Exception ex) { reason = "committed_notification_failed:" + ex.GetType().Name; }
            return true;
        }

    }
    internal sealed class ContinuousVitalsDamageNotification
    {
        readonly VitalsState _actual;
        readonly ContinuousVitalsProposal _proposal;
        internal bool Committed;
        bool _consumed;
        internal ContinuousVitalsDamageNotification(ContinuousActualVitalsWriter owner, object issuer,
            VitalsState actual, ContinuousVitalsProposal proposal)
        {
            if (owner == null || !owner.IsNotificationIssuer(issuer)) throw new InvalidOperationException("foreign_damage_notification");
            _actual = actual; _proposal = proposal;
        }
        internal bool TryConsume(VitalsState actual, out ContinuousVitalsProposal proposal)
        {
            if (Monitor.IsEntered(CommonParticipantGate.SyncRoot)) throw new InvalidOperationException("notification_under_publication_gate");
            proposal = null;
            if (!Committed || _consumed || !ReferenceEquals(actual, _actual)) return false;
            _consumed = true; proposal = _proposal; return true;
        }
    }
}
