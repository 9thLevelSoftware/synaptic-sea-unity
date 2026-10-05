using System;
using SynapticSea.Core.Systems;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        ContinuousActualVitalsWriter _continuousActualVitalsWriter;
        // Called only by root-owned authentic bootstrap after producer closure; binding alone is NOT activation.
        // Private to this partial class: no caller-created DTO, public setter or arbitrary callback installer.
        void BindContinuousVitalsProducer(ActualVitalsProjectionBridge bridge)
        {
            if (!ContinuousDiagnosticBootstrapOpen) throw new InvalidOperationException("continuous_bootstrap_closed");
            if (_continuousActualVitalsWriter != null) throw new InvalidOperationException("continuous_vitals_already_bound");
            _continuousActualVitalsWriter = new ContinuousActualVitalsWriter(this, bridge);
        }
        // Return value means HANDLED, not committed. Refusal must never fall back to a legacy untracked write.
        bool TryRunContinuousVitalsTick(double delta, DiagnosticVitalsTickInput input, out bool committed, out string reason)
        {
            committed = false; reason = "continuous_vitals_unbound";
            if (_continuousActualVitalsWriter == null) return false;
            committed = _continuousActualVitalsWriter.TryTick(delta, input, out reason); return true;
        }
        bool TryRunContinuousVitalsDelta(double health, double stamina, double hunger, double thirst,
            out bool committed, out string reason)
        {
            committed = false; reason = "continuous_vitals_unbound";
            if (_continuousActualVitalsWriter == null) return false;
            committed = _continuousActualVitalsWriter.TryDelta(health, stamina, hunger, thirst, out reason); return true;
        }
        IDamageVitalsTarget ContinuousDamageVitalsTarget => _continuousActualVitalsWriter == null
            ? (IDamageVitalsTarget)VitalsState : _continuousActualVitalsWriter;
        ulong ContinuousAuxiliaryDamageEpoch => _continuousActualVitalsWriter?.DamageEpoch ?? 0;
        // Complete active cohort, closed Tick writers, paid completion and authenticated world-cut join
        // are deliberately supplied by separately reviewed integration. These hooks never declare readiness.
    }
}
