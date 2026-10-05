using System;
using System.Threading;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        ContinuousOrdinaryWorkBinding _continuousOrdinaryContextBinding;
        ContinuousOrdinaryStartSource _continuousWorkFrameSource;
        // Private issuer from this authentic actual cohort, not a constructor seed/claimed stamp.
        // Ordinary participant semantic confirmation is synchronous/offgate; no alias ABA claim.
        internal sealed class ContinuousOrdinaryWorkBinding
        {
            readonly RunSession _session;readonly int _thread;
            internal ContinuousOrdinaryWorkBinding(RunSession session,object issuer)
            {
                if(session==null||!ReferenceEquals(issuer,session._continuousWorkContextIssuer)||session._continuousWorldCohort==null)
                    throw new InvalidOperationException("ordinary_work_binding_unissued");
                _session=session;_thread=Thread.CurrentThread.ManagedThreadId;
            }
            internal bool MatchesBirthUnderGate(InventoryState inventory,ResourceAuthorityLease resources)
            {
                CommonParticipantGate.RequireHeld();
                return Thread.CurrentThread.ManagedThreadId==_thread&&_session.ContinuousAuxiliaryRuntimeActive&&
                    ReferenceEquals(_session.InventoryState,inventory)&&ReferenceEquals(_session._continuousWorldCohort.ResourceLease,resources)&&resources.IsCurrent;
            }
            internal bool MatchesFrameUnderGate(InventoryState inventory,ResourceAuthorityLease resources)
                =>MatchesBirthUnderGate(inventory,resources)&&_session._inTick&&_session._continuousWorkFrameSource!=null&&_session._continuousWorkFrameSource.MatchesUnderGate(_session);
        }
    }
}
