using System;
using System.Threading;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    // Unwired correctness scaffold. No live scene eligibility or worker validation claim.
    internal sealed class DiagnosticAuxiliaryPublisher
    {
        readonly TrackedParticipantOwner _owner = new TrackedParticipantOwner();
        readonly GdDict _canonical;
        readonly InventoryState _inventory;
        readonly PlayerProgressionState _progression;
        readonly TrainingEventBus _training;
        readonly int _mainThread;
        readonly object _certificateSeal = new object();
        internal DiagnosticAuxiliaryPublisher(GdDict admittedInitial, InventoryState inventory,
            PlayerProgressionState progression, TrainingEventBus training)
        {
            if (!DomainBundle.TryCreate(admittedInitial, out var admitted, out string reason))
                throw new ArgumentException(reason);
            _canonical = _owner.ImportDict(admitted.GetSummary(), root: true);
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _progression = progression ?? throw new ArgumentNullException(nameof(progression));
            _training = training ?? throw new ArgumentNullException(nameof(training));
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            // Legacy participants explicitly refuse reusable preparation.
            inventory.CaptureTrackedStamp(); progression.CaptureTrackedStamp(); training.CaptureTrackedStamp();
        }
        internal GdDict CaptureOwnerSnapshot()
        {
            lock (CommonParticipantGate.SyncRoot) return _canonical.DeepCopy();
        }
        // Whole coherent diagnostic snapshot; individual model getters are not advertised as a transaction.
        internal GdDict CaptureSnapshot()
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                RefuseCallbackInventory();
                return new GdDict { { "owner", _canonical.DeepCopy() }, { "inventory", _inventory.CaptureTrackedSummary(out _) },
                    { "progression", _progression.CaptureTrackedSummary(out _) }, { "training", _training.ToDict() } };
            }
        }
        void RefuseCallbackInventory()
        {
            if (_inventory.ComponentMass != null || _inventory.RejectAnonymousComponent != null)
                throw new InvalidOperationException("Diagnostic full inventory snapshots refuse callback-dependent policy.");
        }
        internal Prepared Prepare(GdDict candidate, GdDict effect, ResourceAuthorityLease lease)
        {
            if (lease == null || !lease.IsCurrent) throw new ArgumentException("Stale resource lease.");
            if (effect == null || !ItemInstanceState.IsSafeSnapshot(effect)) throw new ArgumentException("Invalid completion effect.");
            effect = effect.DeepCopy(); // no caller-owned effect graph survives into admission/install
            GdDict before, inventoryBefore, progressionBefore, trainingBefore;
            ulong ownerStamp, inventoryStamp, progressionStamp, trainingStamp;
            lock (CommonParticipantGate.SyncRoot)
            {
                RefuseCallbackInventory();
                before = _canonical.DeepCopy(); ownerStamp = _owner.Stamp;
                progressionBefore = _progression.CaptureTrackedSummary(out progressionStamp);
                trainingBefore = _training.ToDict();
                inventoryBefore = _inventory.CaptureTrackedSummary(out inventoryStamp);
                trainingStamp = _training.CaptureTrackedStamp();
            }
            var expectedParticipants = before.GetDictOrEmpty("participating_state");
            var hashContext = PaidHashContext.FromOwner(before);
            if (!hashContext.Equal(expectedParticipants.Get("inventory"), inventoryBefore) ||
                !hashContext.Equal(expectedParticipants.Get("progression"), progressionBefore) ||
                !hashContext.Equal(expectedParticipants.Get("training"), trainingBefore))
                throw new ArgumentException("Diagnostic participants do not match canonical owner.");
            // Admission uses the ambient sealed authority during preparation. Any replacement (including away/back)
            // invalidates this lease before install; unknown resource reads fail closed. This is synchronous
            // main-thread scaffold preparation, not an isolated worker validator or complete closure manifest.
            // Full existing admission preserves catalog-derived auxiliary reward policy; no live GrantXp/RemoveItem.
            if (effect == null || effect.GetString("operation") != "aux_complete" ||
                !DomainBundle.TryCreate(candidate, out var admitted, out string reason) ||
                !AuxiliaryServiceState.Conserved(before, admitted.GetSummary(), effect))
                throw new ArgumentException("Invalid auxiliary completion.");
            GdDict after = admitted.GetSummary();
            var participant = after.GetDictOrEmpty("participating_state");
            return new Prepared(this, _certificateSeal, lease,
                _owner.PrepareReplacements(new object[] { _canonical }, new object[] { after }, ownerStamp),
                _inventory.PrepareTrackedReplacement(participant.GetDictOrEmpty("inventory"), inventoryStamp),
                _progression.PrepareTrackedReplacement(participant.GetDictOrEmpty("progression"), progressionStamp),
                _training.PrepareTrackedReplacement(participant.GetDictOrEmpty("training"), trainingStamp));
        }
        internal enum PublishResult { Refused, Committed, AlreadyConsumed, CommittedPresentationFailed }
        internal PublishResult PublishAndNotify(Prepared certificate, bool diagnosticEligible, Action notification)
        {
            PublishResult result = Publish(certificate, diagnosticEligible);
            if (result != PublishResult.Committed) return result;
            try { notification?.Invoke(); return result; }
            catch { return PublishResult.CommittedPresentationFailed; }
        }
        internal enum DiagnosticFault { None, AfterInventory, AfterProgression, AfterTraining, AfterOwner }
        // Fresh eligibility must be supplied on the constructing thread immediately after caller hooks.
        // This diagnostic bool is NOT an authenticated live scene-read contract; live wiring remains prohibited.
        internal PublishResult Publish(Prepared certificate, bool diagnosticEligible, DiagnosticFault fault = DiagnosticFault.None)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread) return PublishResult.Refused;
            if (certificate == null || !ReferenceEquals(certificate.Owner, this)) return PublishResult.Refused;
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                if (!certificate.TryConsumeUnderGate()) return PublishResult.AlreadyConsumed;
                if (!diagnosticEligible || !certificate.Lease.IsCurrent || !certificate.Backing.MatchesUnderGate() ||
                    !certificate.Inventory.MatchesUnderGate() || !certificate.Progression.MatchesUnderGate() ||
                    !certificate.Training.MatchesUnderGate()) return PublishResult.Refused;
                // Prebuilt typed tokens only; no delegates, copying, hashing, logging or world/survival writes.
                bool inventory = false, progression = false, training = false, owner = false;
                try
                {
                    certificate.Inventory.InstallUnderGate(attempt); inventory = true;
                    if (fault == DiagnosticFault.AfterInventory) throw certificate.FaultException;
                    certificate.Progression.InstallUnderGate(attempt); progression = true;
                    if (fault == DiagnosticFault.AfterProgression) throw certificate.FaultException;
                    certificate.Training.InstallUnderGate(attempt); training = true;
                    if (fault == DiagnosticFault.AfterTraining) throw certificate.FaultException;
                    certificate.Backing.InstallUnderGate(attempt); owner = true;
                    if (fault == DiagnosticFault.AfterOwner) throw certificate.FaultException;
                    return PublishResult.Committed;
                }
                catch
                {
                    if (owner) certificate.Backing.RollbackUnderGate(attempt);
                    if (training) certificate.Training.RollbackUnderGate(attempt);
                    if (progression) certificate.Progression.RollbackUnderGate(attempt);
                    if (inventory) certificate.Inventory.RollbackUnderGate(attempt);
                    throw;
                }
            }
        }
        internal sealed class Prepared
        {
            internal readonly DiagnosticAuxiliaryPublisher Owner;
            internal readonly ResourceAuthorityLease Lease;
            internal readonly PreparedVariantBacking Backing;
            internal readonly InventoryState.PreparedReplacement Inventory;
            internal readonly PlayerProgressionState.PreparedReplacement Progression;
            internal readonly TrainingEventBus.PreparedReplacement Training;
            bool _consumed;
            internal bool TryConsumeUnderGate()
            {
                CommonParticipantGate.RequireHeld();
                if (_consumed) return false;
                _consumed = true; return true;
            }
            internal readonly Exception FaultException = new InvalidOperationException("Diagnostic publication fault.");
            internal Prepared(DiagnosticAuxiliaryPublisher owner, object seal, ResourceAuthorityLease lease,
                PreparedVariantBacking backing, InventoryState.PreparedReplacement inventory,
                PlayerProgressionState.PreparedReplacement progression, TrainingEventBus.PreparedReplacement training)
            { if (owner == null || !ReferenceEquals(seal, owner._certificateSeal)) throw new ArgumentException("Invalid certificate authority.");
                Owner = owner; Lease = lease; Backing = backing; Inventory = inventory; Progression = progression; Training = training; }
        }
    }
}
