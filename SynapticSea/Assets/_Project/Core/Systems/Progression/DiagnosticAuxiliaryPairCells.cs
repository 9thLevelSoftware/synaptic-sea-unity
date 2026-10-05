using System;
using System.Threading;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    // Synthetic diagnostic cells. These labels do not authenticate any scene, damage or input predicate.
    internal sealed class DiagnosticAuxiliaryStaminaCell
    {
        readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        double _value;
        ulong _stamp;
        internal DiagnosticAuxiliaryStaminaCell(double value) { Validate(value); _value = value; }
        internal bool IsLiveSceneAuthority => false;
        internal void RequireThread()
        { if (_thread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("pair_wrong_thread"); }
        static void Validate(double value)
        { if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new ArgumentException("invalid_stamina"); }
        internal double Value { get { RequireThread(); lock (CommonParticipantGate.SyncRoot) return _value; } }
        internal ulong Stamp { get { RequireThread(); lock (CommonParticipantGate.SyncRoot) return _stamp; } }
        internal void Set(double value)
        {
            RequireThread(); Validate(value);
            lock (CommonParticipantGate.SyncRoot)
            { if (_stamp == ulong.MaxValue) throw new InvalidOperationException("stamina_stamp_overflow"); _value = value; _stamp++; }
        }
        internal bool MatchesUnderGate(ulong stamp, double value)
        { CommonParticipantGate.RequireHeld(); RequireThread(); return _stamp == stamp && BitConverter.DoubleToInt64Bits(_value) == BitConverter.DoubleToInt64Bits(value); }
        internal PreparedDebit PrepareUnderGate(double value)
        {
            CommonParticipantGate.RequireHeld(); RequireThread(); Validate(value);
            if (_stamp > ulong.MaxValue - 2) throw new InvalidOperationException("stamina_stamp_overflow");
            return new PreparedDebit(this, _value, value, _stamp);
        }
        internal sealed class PreparedDebit
        {
            readonly DiagnosticAuxiliaryStaminaCell _cell;
            readonly double _before, _after;
            readonly ulong _expected, _next, _rollback;
            ParticipantPublicationAttempt _attempt;
            int _state;
            internal PreparedDebit(DiagnosticAuxiliaryStaminaCell cell, double before, double after, ulong expected)
            { _cell = cell; _before = before; _after = after; _expected = expected; _next = checked(expected + 1); _rollback = checked(expected + 2); }
            internal bool MatchesUnderGate() => _state == 0 && _cell.MatchesUnderGate(_expected, _before);
            internal void InstallUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (attempt == null || !attempt.IsOpenUnderGate || !MatchesUnderGate()) throw new InvalidOperationException("stale_stamina_debit");
                _attempt = attempt; _cell._value = _after; _cell._stamp = _next; _state = 1; }
            internal bool CanRollbackUnderGate(ParticipantPublicationAttempt attempt)
            {
                CommonParticipantGate.RequireHeld();
                return _state == 1 && attempt != null && attempt.IsOpenUnderGate && ReferenceEquals(attempt, _attempt) &&
                    _cell.MatchesUnderGate(_next, _after);
            }
            internal void RollbackUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (!CanRollbackUnderGate(attempt)) throw new InvalidOperationException("invalid_pair_rollback");
                _cell._value = _before; _cell._stamp = _rollback; _state = 2;
            }
        }
    }
    internal sealed class DiagnosticAuxiliaryPairContext
    {
        readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        AuxiliaryWorkFrame _frame;
        long _ordinal;
        ulong _epoch;
        InventoryState _inventory;
        ResourceAuthorityLease _resources;
        readonly RunSession.ContinuousOrdinaryWorkBinding _ordinaryBinding;
        internal bool IsLiveSceneAuthority => false;
        internal DiagnosticAuxiliaryPairContext(long ordinal, AuxiliaryWorkFrame frame, InventoryState inventory, ResourceAuthorityLease resources)
        { Replace(ordinal, frame, inventory, resources); }
        internal DiagnosticAuxiliaryPairContext(long ordinal, AuxiliaryWorkFrame frame, InventoryState inventory, ResourceAuthorityLease resources, RunSession.ContinuousOrdinaryWorkBinding binding)
        { _ordinaryBinding=binding??throw new ArgumentNullException(nameof(binding)); Replace(ordinal,frame,inventory,resources); }
        internal void RequireThread()
        { if (_thread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("pair_wrong_thread"); }
        internal void Replace(long ordinal, AuxiliaryWorkFrame frame, InventoryState inventory, ResourceAuthorityLease resources)
        {
            RequireThread();
            if (ordinal < 0 || frame == null || inventory == null || resources == null) throw new ArgumentException("invalid_pair_context");
            // The frame is immutable, but take an owned scalar copy to make input custody explicit.
            var copy = new AuxiliaryWorkFrame(frame.Delta, frame.Stamina, frame.MaxStamina, frame.WoundSpeed,
                frame.GateReason, frame.Consent, frame.HoldRequired, frame.Held, frame.Moving, frame.Damaged);
            lock (CommonParticipantGate.SyncRoot)
            {
                if (_frame != null && ordinal < _ordinal) throw new InvalidOperationException("frame_ordinal_regression");
                if (_epoch == ulong.MaxValue) throw new InvalidOperationException("context_epoch_overflow");
                if(_ordinaryBinding==null)inventory.CaptureTrackedStamp(); // existing protected diagnostic path unchanged
                else if(!_ordinaryBinding.MatchesBirthUnderGate(inventory,resources))throw new InvalidOperationException("ordinary_context_binding_invalid");
                _frame = copy; _ordinal = ordinal; _inventory = inventory; _resources = resources; _epoch++;
            }
        }
        internal Capture ReadUnderGate()
        {
            CommonParticipantGate.RequireHeld(); RequireThread();
            return new Capture(_frame, _ordinal, _epoch, _inventory, _ordinaryBinding==null?_inventory.CaptureTrackedStamp():0, _resources);
        }
        internal bool MatchesUnderGate(Capture captured)
        {
            CommonParticipantGate.RequireHeld(); RequireThread();
            return _epoch == captured.Epoch && _ordinal == captured.Ordinal && ReferenceEquals(_frame, captured.Frame) &&
                ReferenceEquals(_inventory, captured.Inventory) && ReferenceEquals(_resources, captured.Resources) &&
                (_ordinaryBinding==null?_inventory.CaptureTrackedStamp()==captured.InventoryStamp:_ordinaryBinding.MatchesFrameUnderGate(_inventory,_resources)) && _resources.IsCurrent;
        }
        internal sealed class Capture
        {
            internal readonly AuxiliaryWorkFrame Frame;
            internal readonly long Ordinal;
            internal readonly ulong Epoch, InventoryStamp;
            internal readonly InventoryState Inventory;
            internal readonly ResourceAuthorityLease Resources;
            internal Capture(AuxiliaryWorkFrame frame, long ordinal, ulong epoch, InventoryState inventory, ulong stamp, ResourceAuthorityLease resources)
            { Frame = frame; Ordinal = ordinal; Epoch = epoch; Inventory = inventory; InventoryStamp = stamp; Resources = resources; }
        }
    }
}
