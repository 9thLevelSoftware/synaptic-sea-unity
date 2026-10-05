using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    // Unwired closed proposal evaluator. Scratch is never a current model or publication authority.
    internal static class ContinuousVitalsEvaluator
    {
        static readonly object Issuer = new object();
        internal static bool IsIssuer(object issuer) => ReferenceEquals(issuer, Issuer);
        internal static ContinuousVitalsProposal Tick(DiagnosticVitalsValues before, double delta,
            DiagnosticVitalsTickInput context)
        {
            if (before == null || context == null) throw new ArgumentNullException();
            return Evaluate(before, scratch => scratch.Tick(delta, context.CopyForModel()));
        }
        internal static ContinuousVitalsProposal Delta(DiagnosticVitalsValues before,
            double health, double stamina, double hunger, double thirst)
        {
            RequireFinite(health); RequireFinite(stamina); RequireFinite(hunger); RequireFinite(thirst);
            return Evaluate(before, scratch => scratch.ApplyDelta(new GdDict {
                { "health", health }, { "stamina", stamina }, { "hunger", hunger }, { "thirst", thirst } }));
        }
        internal static ContinuousVitalsProposal CombatHealth(DiagnosticVitalsValues before, double health)
        {
            RequireFinite(health);
            return Evaluate(before, scratch => ((IDamageVitalsTarget)scratch).Health = health);
        }
        static ContinuousVitalsProposal Evaluate(DiagnosticVitalsValues before, Action<VitalsState> evaluate)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            var scratch = before.ExactScratch();
            var sources = new string[8]; var amounts = new double[8]; int count = 0;
            scratch.HealthDamageObserved += (source, amount) => {
                if (count == sources.Length) throw new InvalidOperationException("damage_event_capacity");
                sources[count] = source; amounts[count++] = amount;
            };
            evaluate(scratch); // All callbacks, allocations and ordinary arithmetic happen off publication gate.
            return new ContinuousVitalsProposal(Issuer, before, DiagnosticVitalsValues.FromModel(scratch), sources, amounts, count);
        }
        static void RequireFinite(double value)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("nonfinite_vitals_delta"); }
    }
    internal sealed class ContinuousVitalsProposal
    {
        internal readonly DiagnosticVitalsValues Before, After;
        readonly string[] _sources; readonly double[] _amounts;
        internal readonly int DamageCount;
        internal ContinuousVitalsProposal(object issuer, DiagnosticVitalsValues before, DiagnosticVitalsValues after,
            string[] sources, double[] amounts, int count)
        { if (!ContinuousVitalsEvaluator.IsIssuer(issuer)) throw new InvalidOperationException("foreign_vitals_proposal"); Before = before; After = after; _sources = sources; _amounts = amounts; DamageCount = count; }
        internal string DamageSource(int index) { Check(index); return _sources[index]; }
        internal double DamageAmount(int index) { Check(index); return _amounts[index]; }
        void Check(int index) { if (index < 0 || index >= DamageCount) throw new ArgumentOutOfRangeException(nameof(index)); }
        // Ordered loss data only. Actual committed-owner notification issuance belongs to bridge integration;
        // this proposal deliberately exposes no callback delivery or model installer.
    }
}
