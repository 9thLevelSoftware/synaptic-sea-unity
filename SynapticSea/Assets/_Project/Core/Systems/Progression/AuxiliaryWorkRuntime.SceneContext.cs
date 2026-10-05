using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    public sealed partial class AuxiliaryWorkRuntime
    {
        internal bool MatchesRunSessionContextUnderGate(RunSession session,DiagnosticAuxiliaryPairContext context)
        {
            CommonParticipantGate.RequireHeld();
            return _runSessionPair!=null && ReferenceEquals(_runSessionPair.Context,context) &&
                _runSessionPair.Vitals.IsBoundActualSessionUnderGate(session);
        }
    }
}
