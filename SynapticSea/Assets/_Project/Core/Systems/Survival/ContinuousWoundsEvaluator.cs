using System;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    internal sealed class ContinuousWoundsProposal
    {
        internal readonly ContinuousWoundsValues Before,After;
        internal readonly long Healed;
        internal readonly string WoundId;
        internal ContinuousWoundsProposal(object issuer,ContinuousWoundsValues before,ContinuousWoundsValues after,long healed,string woundId)
        {
            if(!ContinuousWoundsEvaluator.IsIssuer(issuer))throw new InvalidOperationException("foreign_wounds_proposal");
            Before=before;After=after;Healed=healed;WoundId=woundId;
        }
    }
    internal static class ContinuousWoundsEvaluator
    {
        static readonly object Issuer=new object();
        internal static bool IsIssuer(object candidate)=>ReferenceEquals(candidate,Issuer);
        static void Finite(double value){if(double.IsNaN(value)||double.IsInfinity(value))throw new ArgumentException("wounds_input_nonfinite");}
        internal static ContinuousWoundsProposal TickHeal(ContinuousWoundsValues before,double delta,double treatedPerSecond,double bandagedPerSecond)
        {
            if(before==null)throw new ArgumentNullException(nameof(before));Finite(delta);Finite(treatedPerSecond);Finite(bandagedPerSecond);
            var scratch=before.ExactScratch();scratch.Tick(delta);long healed=scratch.Heal(delta,treatedPerSecond,bandagedPerSecond);
            return new ContinuousWoundsProposal(Issuer,before,ContinuousWoundsValues.CaptureExact(scratch),healed,"");
        }
        // Body part and event metadata are resolved by the actual session observer BEFORE the final guard.
        internal static ContinuousWoundsProposal Damage(ContinuousWoundsValues before,double damage,string damageType,string bodyPart,string sourceId)
        {
            if(before==null)throw new ArgumentNullException(nameof(before));Finite(damage);
            if(damageType==null||bodyPart==null||sourceId==null||damageType.Length>256||bodyPart.Length>256||sourceId.Length>256)throw new ArgumentException("wounds_event_bounds");
            var scratch=before.ExactScratch();string id="";
            if(damage>0){var suggestion=WoundState.SuggestFromDamage(damage,damageType,bodyPart);if(!suggestion.IsEmpty){suggestion["source_id"]=sourceId;id=scratch.ApplyOrWorsenWound(suggestion);}}
            return new ContinuousWoundsProposal(Issuer,before,ContinuousWoundsValues.CaptureExact(scratch),0,id);
        }
    }
}
