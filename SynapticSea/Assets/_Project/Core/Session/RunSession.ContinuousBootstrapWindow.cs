namespace SynapticSea.Core.Session
{
    public partial class RunSession
    {
        readonly bool _continuousDiagnosticRequested;
        bool _continuousDiagnosticBootstrapClosed;
        // Construction phase only. This grants no model, resource, capture or activation authority.
        internal bool ContinuousDiagnosticBootstrapOpen => _continuousDiagnosticRequested && !_continuousDiagnosticBootstrapClosed;
        internal void CloseContinuousDiagnosticBootstrapForTick() => _continuousDiagnosticBootstrapClosed = true;
    }
}
