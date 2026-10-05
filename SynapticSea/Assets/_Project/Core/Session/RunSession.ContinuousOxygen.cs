using System;
using SynapticSea.Core.Systems;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        ContinuousActualOxygenWriter _continuousActualOxygenWriter;
        // Root-owned complete-bootstrap dispatch only; not an activation or complete-world readiness claim.
        void BindContinuousOxygenProducer(ActualOxygenProjectionBridge bridge)
        {
            if (!ContinuousDiagnosticBootstrapOpen) throw new InvalidOperationException("continuous_bootstrap_closed");
            if (_continuousActualOxygenWriter != null) throw new InvalidOperationException("continuous_oxygen_already_bound");
            _continuousActualOxygenWriter = new ContinuousActualOxygenWriter(this, bridge);
        }
        // True means handled; failure must not invoke legacy Tick/direct suit debit as a fallback.
        bool TryRunContinuousOxygenTick(double delta, ContinuousOxygenTickInput input, out bool committed, out string reason)
        {
            committed = false; reason = "continuous_oxygen_unbound";
            if (_continuousActualOxygenWriter == null) return false;
            committed = _continuousActualOxygenWriter.TryTick(delta, input, out reason); return true;
        }
        // Cache/config/zone/breach mutations remain separate enrolled producers or denied commands.
        // Hazards routing must guard current inventory/equipment inputs and collect actual air-owner/LOS hooks first.
    }
}
