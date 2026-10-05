namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        // Revokes output/new debit BEFORE a terminal/lifecycle mutation. Accepted lineage and spent
        // stamina remain untouched. This deliberately does not stop ordinary simulation routing.
        // Guard-time path: no cancellation, allocation, callbacks or scene reads.
        internal void RetireContinuousOutputUnderGate(string reason)
        {
            _continuousWorldCohort?.RevokeCapture(reason);
        }
        internal void RetireContinuousSourceAuthority(string reason)
        {
            // Lifecycle callers (EndRun/reset/dispose) execute outside the publication gate.
            // Retire output first; abandon retains a running worker's reservation until task exit.
            _continuousWorldCohort?.RevokeCapture(reason);
            AbandonContinuousSaveRequest();
            if(_continuousWorldCohort==null)return;
            CancelContinuousQueuedStart();
            _auxConsent=false;_continuousConsentCaptured=false;_continuousResumeIntent=false;
            _workHoldInput=false;_continuousPauseReason=reason;
            _continuousInputGeneration=new object();
            _continuousPendingOperation=null;_continuousPendingService=null;_continuousPendingGeneration=null;
        }
    }
}
