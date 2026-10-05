using System;
namespace SynapticSea.Core.Session
{
    public sealed partial class DomainTransactionCoordinator
    {
        RunSession _continuousCommandOwner;
        internal void BindContinuousUnfinishedCommandFence(RunSession session)
        {
            if(session==null||!session.ContinuousCommandOwns(this))throw new InvalidOperationException("unbound_continuous_command_fence");
            _continuousCommandOwner=session;
        }
        bool ContinuousUnfinishedCommandFenceActive=>_continuousCommandOwner!=null&&_continuousCommandOwner.ContinuousCommandOwns(this);
    }
    public sealed partial class RunSession
    {
        internal bool ContinuousCommandOwns(DomainTransactionCoordinator owner)
            =>ReferenceEquals(owner,_componentDomain)&&ContinuousAuxiliaryRuntimeActive;
    }
}
