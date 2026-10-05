using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    internal sealed class ContinuousSimulationVitalsSnapshot
    {
        readonly ActualVitalsProjectionBridge _issuer;
        readonly object _generation;
        internal readonly DiagnosticVitalsValues Values;
        ContinuousSimulationVitalsSnapshot(ActualVitalsProjectionBridge issuer,DiagnosticVitalsValues values,object generation)
        {_issuer=issuer;Values=values;_generation=generation;}
        internal static ContinuousSimulationVitalsSnapshot CaptureFresh(ActualVitalsProjectionBridge issuer)
        {lock(CommonParticipantGate.SyncRoot){issuer.ReadSimulationUnderGate(out var values,out var generation);return new ContinuousSimulationVitalsSnapshot(issuer,values,generation);}}
        internal bool MatchesUnderGate(ActualVitalsProjectionBridge issuer)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(_issuer,issuer)&&issuer.SimulationMatchesUnderGate(Values,_generation);}
    }
}
