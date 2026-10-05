using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    internal sealed class ContinuousSimulationOxygenSnapshot
    {
        readonly ActualOxygenProjectionBridge _issuer;
        readonly object _generation;
        internal readonly ContinuousOxygenValues Values;
        ContinuousSimulationOxygenSnapshot(ActualOxygenProjectionBridge issuer,ContinuousOxygenValues values,object generation)
        {_issuer=issuer;Values=values;_generation=generation;}
        internal static ContinuousSimulationOxygenSnapshot CaptureFresh(ActualOxygenProjectionBridge issuer)
        {lock(CommonParticipantGate.SyncRoot){issuer.ReadSimulationUnderGate(out var values,out var generation);return new ContinuousSimulationOxygenSnapshot(issuer,values,generation);}}
        internal bool MatchesUnderGate(ActualOxygenProjectionBridge issuer)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(_issuer,issuer)&&issuer.SimulationMatchesUnderGate(Values,_generation);}
    }
}
