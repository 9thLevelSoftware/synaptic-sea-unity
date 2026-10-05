using System;
using System.Threading;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    internal sealed class ContinuousActualOxygenWriter
    {
        readonly RunSession _session;
        readonly ActualOxygenProjectionBridge _bridge;
        readonly OxygenState _actual;
        readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        internal ContinuousActualOxygenWriter(RunSession session, ActualOxygenProjectionBridge bridge)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge)); _actual = session.OxygenState;
            lock (CommonParticipantGate.SyncRoot)
                if (_actual == null || !bridge.IsBoundActualSessionUnderGate(session)) throw new ArgumentException("oxygen_writer_binding");
        }
        internal bool CaptureAvailable {get{lock(CommonParticipantGate.SyncRoot)return _bridge.CaptureAvailableUnderGate();}}
        internal bool TryTick(double delta, ContinuousOxygenTickInput input, out string reason)
        {
            if (_thread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("oxygen_writer_thread");
            var before = _bridge.ReadSimulation();var proposal=ContinuousOxygenEvaluator.EvaluateTick(before.Values,delta,input);
            ActualOxygenProjectionBridge.PreparedChange projected=null;
            bool available;lock(CommonParticipantGate.SyncRoot)available=_bridge.CaptureAvailableUnderGate();
            if(available){var captured=_bridge.Read();_bridge.PrepareNext(captured,proposal.After,out projected,out reason);}
            using(projected)
            {
                if(projected!=null)lock(CommonParticipantGate.SyncRoot)
                {
                    if(!SourceMatchesUnderGate(before)||!projected.MatchesUnderGate()){reason="oxygen_writer_stale";return false;}
                    if(projected.TryInstallActualRawAndProjectionUnderGate(out reason))return true;
                }
            }
            if(!_bridge.PrepareSimulationOnly(before,proposal,out var native,out reason))return false;
            using(native)lock(CommonParticipantGate.SyncRoot)
            {
                if(!SourceMatchesUnderGate(before)||!native.MatchesUnderGate()){reason="oxygen_writer_stale";return false;}
                return native.TryInstallNativeOnlyUnderGate(out reason);
            }
        }
        bool SourceMatchesUnderGate(ContinuousSimulationOxygenSnapshot before)
        {
            CommonParticipantGate.RequireHeld();
            return ReferenceEquals(_session.OxygenState,_actual)&&_bridge.IsBoundSimulationSessionUnderGate(_session)&&
                _bridge.IsBoundSimulationModelUnderGate(_actual)&&before.MatchesUnderGate(_bridge);
        }
    }
}
