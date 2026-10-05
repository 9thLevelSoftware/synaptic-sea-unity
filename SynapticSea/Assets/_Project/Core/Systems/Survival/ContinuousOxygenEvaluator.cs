using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    // Closed numeric inputs, not a certificate of scene/air-owner eligibility.
    internal sealed class ContinuousOxygenTickInput
    {
        internal readonly bool InBreach, Field, ApplySuitReserve;
        internal readonly double FieldMultiplier, FireDrain, Severity, ReserveSeconds, HomeHazardDial;
        internal ContinuousOxygenTickInput(bool inBreach, bool field, double fieldMultiplier, double fireDrain,
            bool applySuitReserve = false, double severity = 0, double reserveSeconds = 0, double homeHazardDial = 1)
        {
            foreach (double value in new[] { fieldMultiplier, fireDrain, severity, reserveSeconds, homeHazardDial })
                if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("oxygen_input_nonfinite");
            if (applySuitReserve && severity > 0 && reserveSeconds <= 0) throw new ArgumentException("oxygen_reserve_unavailable");
            InBreach = inBreach; Field = field; FieldMultiplier = fieldMultiplier; FireDrain = fireDrain;
            ApplySuitReserve = applySuitReserve; Severity = severity; ReserveSeconds = reserveSeconds; HomeHazardDial = homeHazardDial;
        }
    }
    internal sealed class ContinuousOxygenProposal
    {
        internal readonly ContinuousOxygenValues Before,After;
        internal ContinuousOxygenProposal(object issuer,ContinuousOxygenValues before,ContinuousOxygenValues after)
        {
            if(!ContinuousOxygenEvaluator.IsIssuer(issuer))throw new InvalidOperationException("foreign_oxygen_proposal");
            Before=before;After=after;
        }
    }
    internal static class ContinuousOxygenEvaluator
    {
        static readonly object Issuer=new object();
        internal static bool IsIssuer(object candidate)=>ReferenceEquals(candidate,Issuer);
        internal static ContinuousOxygenProposal EvaluateTick(ContinuousOxygenValues before,double delta,ContinuousOxygenTickInput input)
            =>new ContinuousOxygenProposal(Issuer,before,Tick(before,delta,input));
        internal static ContinuousOxygenValues Tick(ContinuousOxygenValues before, double delta, ContinuousOxygenTickInput input)
        {
            if (before == null || input == null) throw new ArgumentNullException();
            if (double.IsNaN(delta) || double.IsInfinity(delta)) throw new ArgumentException("oxygen_delta_nonfinite");
            var scratch = before.ExactScratch();
            double suitBefore = scratch.Oxygen;
            scratch.Tick(delta, new GdDict { { "player_in_breach_zone", input.InBreach }, { "field_atmosphere", input.Field },
                { "field_atmosphere_multiplier", input.FieldMultiplier }, { "fire_oxygen_drain", input.FireDrain } });
            // Exact existing ApplySuitAirReserve operation order; field branch never applies home reserve.
            if (!input.Field && input.ApplySuitReserve && input.Severity > 0 && delta > 0)
            {
                double perSecond = input.Severity * scratch.MaxOxygen / input.ReserveSeconds * Math.Max(.1, input.HomeHazardDial);
                double level = Math.Min(scratch.Oxygen, suitBefore);
                scratch.Oxygen = Math.Max(0, level - perSecond * delta);
            }
            return ContinuousOxygenValues.CaptureExact(scratch);
        }
    }
}
