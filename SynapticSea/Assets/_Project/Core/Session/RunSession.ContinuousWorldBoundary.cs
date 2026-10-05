using System;
using System.Threading;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        readonly object _continuousBoundaryIssuer = new object();
        int _continuousMutationDepth, _continuousMutationThread;
        ulong _continuousBoundaryEpoch;
        bool _continuousBatchFaulted, _continuousBoundaryExhausted;
        ContinuousSafeEndTick _continuousSafeEndTick;
        ContinuousWorldMutationBatch _continuousCurrentBatch;
        // Root-owned Tick/typed writer dispatch only. Not public and not callable by a save observer.
        ContinuousWorldMutationBatch BeginContinuousWorldMutationBatch()
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!_continuousDiagnosticRequested) throw new InvalidOperationException("continuous_profile_not_requested");
                int thread = Thread.CurrentThread.ManagedThreadId;
                if (_continuousMutationThread != 0 && _continuousMutationThread != thread) throw new InvalidOperationException("continuous_world_thread");
                if (_continuousMutationDepth == int.MaxValue)
                    throw new InvalidOperationException("continuous_world_reentrancy_overflow");
                var batch = new ContinuousWorldMutationBatch(this, _continuousBoundaryIssuer, thread, _continuousCurrentBatch);
                if (_continuousMutationDepth == 0) _continuousBatchFaulted = false;
                _continuousMutationThread = thread; _continuousMutationDepth++;
                // Exhaustion revokes capture eligibility, never survival simulation. No epoch wraps.
                if (_continuousBoundaryEpoch == ulong.MaxValue) _continuousBoundaryExhausted = true;
                else _continuousBoundaryEpoch++;
                _continuousSafeEndTick = null; _continuousCurrentBatch = batch; return batch;
            }
        }
        internal bool TryReadContinuousSafeEndTick(out ContinuousSafeEndTick ticket)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                ticket = _continuousSafeEndTick;
                return ticket != null && ticket.MatchesUnderGate(this);
            }
        }
        internal sealed class ContinuousSafeEndTick
        {
            readonly RunSession _session;
            readonly object _issuer, _scene;
            readonly string _run;
            readonly ulong _epoch;
            readonly long _worldBits, _playBits;
            internal bool IsCompleteWorldAuthority => false;
            internal ContinuousSafeEndTick(RunSession session, object issuer)
            {
                if (!ReferenceEquals(session._continuousBoundaryIssuer, issuer)) throw new InvalidOperationException("foreign_end_tick_issuer");
                _session = session; _issuer = issuer; _scene = session.Scene; _run = session.RunId;
                _epoch = session._continuousBoundaryEpoch;
                _worldBits = BitConverter.DoubleToInt64Bits(session.WorldTime); _playBits = BitConverter.DoubleToInt64Bits(session.RunPlayTimeSeconds);
            }
            internal bool MatchesUnderGate(RunSession session)
            {
                CommonParticipantGate.RequireHeld();
                return ReferenceEquals(session, _session) && ReferenceEquals(_issuer, session._continuousBoundaryIssuer) &&
                    ReferenceEquals(session._continuousSafeEndTick, this) && session._continuousMutationDepth == 0 && !session._continuousBatchFaulted && !session._continuousBoundaryExhausted &&
                    Thread.CurrentThread.ManagedThreadId == session._continuousMutationThread && session._continuousBoundaryEpoch == _epoch &&
                    ReferenceEquals(session.Scene, _scene) && session.RunId == _run && !session.ComponentGenerationRestoreInProgress &&
                    BitConverter.DoubleToInt64Bits(session.WorldTime) == _worldBits && BitConverter.DoubleToInt64Bits(session.RunPlayTimeSeconds) == _playBits;
            }
        }
        sealed class ContinuousWorldMutationBatch : IDisposable
        {
            readonly RunSession _session;
            readonly int _thread;
            readonly ContinuousWorldMutationBatch _parent;
            bool _closed;
            internal ContinuousWorldMutationBatch(RunSession session, object issuer, int thread, ContinuousWorldMutationBatch parent)
            {
                if (!ReferenceEquals(session._continuousBoundaryIssuer, issuer)) throw new InvalidOperationException("foreign_world_batch");
                _session = session; _thread = thread; _parent = parent;
            }
            // Root calls after all normal stages AND ordered postcommit notifications. Exception path uses Dispose.
            internal void Complete()
            {
                lock (CommonParticipantGate.SyncRoot)
                {
                    RequireOpen();
                    if (_session._continuousMutationDepth == 1 && !_session._continuousBatchFaulted && !_session._continuousBoundaryExhausted)
                    {
                        var ticket = new ContinuousSafeEndTick(_session, _session._continuousBoundaryIssuer);
                        _session._continuousMutationDepth = 0; _session._continuousSafeEndTick = ticket;
                    }
                    else _session._continuousMutationDepth--;
                    _session._continuousCurrentBatch = _parent; _closed = true;
                }
            }
            void RequireOpen()
            {
                if (_closed || !ReferenceEquals(_session._continuousCurrentBatch, this) || _thread != Thread.CurrentThread.ManagedThreadId || _session._continuousMutationDepth <= 0)
                    throw new InvalidOperationException("closed_or_foreign_world_batch");
            }
            public void Dispose()
            {
                lock (CommonParticipantGate.SyncRoot)
                {
                    if (_closed) return; RequireOpen();
                    _session._continuousBatchFaulted = true; _session._continuousSafeEndTick = null;
                    _session._continuousMutationDepth--; _session._continuousCurrentBatch = _parent; _closed = true;
                }
            }
        }
    }
}
