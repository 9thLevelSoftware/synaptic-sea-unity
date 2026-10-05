using System;
namespace SynapticSea.Core.Systems
{
    internal enum ContinuousWoundsNativeOperationKind { TickHeal, Damage }
    // Closed intent for NONCAPTURING continuation only. No historical Before, projection, save or paid authority.
    // Receiver must guard its privately issued same-model/lifecycle/generation and revoke capture before native writes.
    internal sealed class ContinuousWoundsNativeOperation
    {
        static readonly object Issuer=new object();
        readonly object _issuer;
        internal bool IsIssued=>ReferenceEquals(_issuer,Issuer);
        internal readonly ContinuousWoundsNativeOperationKind Kind;
        internal readonly double Delta,TreatedPerSecond,BandagedPerSecond,Damage;
        internal readonly string DamageType,BodyPart,SourceId;
        ContinuousWoundsNativeOperation(ContinuousWoundsNativeOperationKind kind,double delta,double treated,double bandaged,double damage,string type,string body,string source)
        {_issuer=Issuer;Kind=kind;Delta=delta;TreatedPerSecond=treated;BandagedPerSecond=bandaged;Damage=damage;DamageType=type;BodyPart=body;SourceId=source;}
        internal void ApplyToActual(WoundState actual,out long healed,out string woundId)
        {
            if(!IsIssued || actual==null)throw new InvalidOperationException("foreign_native_wounds_operation");
            healed=0;woundId="";
            // Receiver already revoked capture and rotated private generation BEFORE calling.
            // Native iteration is deliberately uncapped and does not claim atomic save/projection publication.
            if(Kind==ContinuousWoundsNativeOperationKind.TickHeal)
            {actual.Tick(Delta);healed=actual.Heal(Delta,TreatedPerSecond,BandagedPerSecond);}
            else if(Damage>0)
            {
                var suggestion=WoundState.SuggestFromDamage(Damage,DamageType,BodyPart);
                if(!suggestion.IsEmpty){suggestion["source_id"]=SourceId;woundId=actual.ApplyOrWorsenWound(suggestion);}
            }
        }
        static void Finite(double value){if(double.IsNaN(value)||double.IsInfinity(value))throw new ArgumentException("wounds_native_input_nonfinite");}
        internal static ContinuousWoundsNativeOperation PrepareTickHeal(double delta,double treatedPerSecond,double bandagedPerSecond)
        {Finite(delta);Finite(treatedPerSecond);Finite(bandagedPerSecond);return new ContinuousWoundsNativeOperation(ContinuousWoundsNativeOperationKind.TickHeal,delta,treatedPerSecond,bandagedPerSecond,0,"","","");}
        internal static ContinuousWoundsNativeOperation PrepareDamage(double damage,string damageType,string bodyPart,string sourceId)
        {
            Finite(damage);if(damageType==null||bodyPart==null||sourceId==null)throw new ArgumentException("wounds_native_event_shape");
            // Projection text bounds do not become survival constraints after capture revocation.
            return new ContinuousWoundsNativeOperation(ContinuousWoundsNativeOperationKind.Damage,0,0,0,damage,damageType,bodyPart,sourceId);
        }
    }
}
