using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        // Root Tick substitutes only duplicate typed producer stages within the FULL ordinary TickOrder for
        // an authentic routing-active cohort. Requested flag alone never selects this branch.
        bool TryTickContinuousRestricted(double delta)
        {
            if(!ContinuousAuxiliaryRuntimeActive)return false;
            if(!_inTick||_continuousMutationDepth<=0)throw new InvalidOperationException("continuous_tick_outside_batch");
            PumpContinuousAuxiliaryStart();
            var location=AwayFromStart?SessionLocation.Away:SessionLocation.Home;
            foreach(string id in TickOrder.OrderFor(location))
            {
                switch(id)
                {
                    case TickOrder.Oxygen:
                        if(!TryPrepareContinuousOxygenSceneInput(out var oxygen,out var oxygenReason))throw new InvalidOperationException(oxygenReason);
                        using(var attempt=CommonParticipantGate.BeginAttempt())
                            if(!oxygen.MatchesUnderGate(this))throw new InvalidOperationException("stale_oxygen_scene_input");
                        TryRunContinuousOxygenTick(delta,oxygen.Input,out var oxygenCommitted,out oxygenReason);
                        if(!oxygenCommitted)throw new InvalidOperationException(oxygenReason);
                        break;
                    case TickOrder.Threat:TickContinuousThreats(delta);break;
                    case TickOrder.Wounds:TickContinuousWounds(delta);break;
                    case TickOrder.WorkAction:TryTickContinuousAuxiliaryWork(delta,out _,out _,out _);break;
                    case TickOrder.WorkActionHud:RefreshContinuousAuxiliaryHud();break;
                    default:TickOrder.Get(id).Run(this,location,delta);break;
                }
                StageRan?.Invoke(id,location);
            }
            return true;
        }
        void TickContinuousThreats(double delta)
        {
            if(ThreatManager==null)return;
            var cohort=_continuousWorldCohort;
            // Selected capture handles plain authored attacks. An unsupported legitimate effect
            // revokes capture/debits BEFORE the ordinary real attack, but does not suppress it.
            bool unsupported=EquipmentState!=null&&EquipmentState.GetEquipped("suit")=="hardsuit";
            foreach(var threat in ThreatManager.Threats)
                if(threat!=null&&(!string.IsNullOrEmpty(threat.StatusOnHit)||threat.StructureDamage>0))unsupported=true;
            if(unsupported)cohort.RevokeCapture("unsupported_threat_effect");
            Vec3 position=HasPlayer?PlayerPos:Vec3.Zero;
            ThreatManager.SetPlayerSignals((HasPlayer&&PlayerMoving) ? .3 : .05,PlayerRoomLit() ? .6 : .15,.8,
                HasPlayer&&PlayerCrouching,ResolvePlayerRoom(position));
            UpdateThreatEngagedLos();RefreshThreatNavCosts();
            var armor=PlayerArmorProfile();
            ThreatManager.TickThreats(delta,ContinuousDamageVitalsTarget,StatusEffectsState,armor,position);
            if(EquipmentState!=null&&EquipmentState.GetEquipped("suit")=="hardsuit"&&armor.GetFloat("durability")<40)
                EquipmentState.ArmorDurability["hardsuit"]=armor.Get("durability",40.0);
            SyncCurrentShipCombatSummary();
            // Tutorial/history mutations are outside this restricted capability. Real threat
            // signals, navigation, attacks, motion and combat summary are retained.
        }
        void TickContinuousWounds(double delta)
        {
            if(WoundState==null||WoundState.Wounds.IsEmpty)return;
            var operation=ContinuousWoundsNativeOperation.PrepareTickHeal(delta,WOUND_TREATED_HEAL_PER_SECOND,WOUND_BANDAGED_HEAL_PER_SECOND);
            if(ApplyContinuousWounds(operation,null,out long healed,out _)&&healed>0)Events.RaiseWoundsChanged(WoundState);
        }
        void ApplyContinuousCombatWound(double damage,GdDict ev)
        {
            if(WoundState==null||damage<=0)return;
            // Preserve ordinary roll order even when a small hit does not create a wound.
            string body=RollWoundBodyPart(),type=V.Str((ev??new GdDict()).Get("damage_type","")),source=V.Str((ev??new GdDict()).Get("source_id",""));
            var operation=ContinuousWoundsNativeOperation.PrepareDamage(damage,type,body,source);
            if(ApplyContinuousWounds(operation,new ContinuousWoundDamage(damage,type,body,source),out _,out string id)&&id.Length>0)
                Events.RaiseWoundsChanged(WoundState);
        }
        sealed class ContinuousWoundDamage
        {
            internal readonly double Amount;internal readonly string Type,Body,Source;
            internal ContinuousWoundDamage(double amount,string type,string body,string source){Amount=amount;Type=type;Body=body;Source=source;}
        }
        bool ApplyContinuousWounds(ContinuousWoundsNativeOperation operation,ContinuousWoundDamage damage,out long healed,out string id)
        {
            healed=0;id="";var cohort=_continuousWorldCohort;var bridge=cohort.WoundsBridge;
            var native=bridge.ReadNativeContinuation();ActualWoundsProjectionBridge.PreparedChange projected=null;
            ContinuousWoundsProposal proposal=null;string reason="";
            bool source;
            lock(CommonParticipantGate.SyncRoot)source=bridge.SourceCurrentUnderGate();
            if(source)
            {
                try
                {
                    var before=bridge.Read();
                    proposal=damage==null?ContinuousWoundsEvaluator.TickHeal(before.Values,operation.Delta,operation.TreatedPerSecond,operation.BandagedPerSecond)
                        :ContinuousWoundsEvaluator.Damage(before.Values,damage.Amount,damage.Type,damage.Body,damage.Source);
                    bridge.PrepareNext(before,proposal.After,out projected,out reason);
                }
                catch(ArgumentException){cohort.RevokeCapture("wounds_projection_capacity");}
            }
            using(projected)
            {
                if(projected!=null)
                {
                    using(var attempt=CommonParticipantGate.BeginAttempt())
                        if(ReferenceEquals(cohort,_continuousWorldCohort)&&projected.MatchesUnderGate()&&projected.TryInstallActualRawAndProjectionUnderGate(out reason))
                        {healed=proposal.Healed;id=proposal.WoundId;return true;}
                }
                if(!bridge.PrepareNativeContinuation(native,operation,out var ticket,out reason))throw new InvalidOperationException(reason);
                using(ticket)using(var attempt=CommonParticipantGate.BeginAttempt())
                {
                    if(!ReferenceEquals(cohort,_continuousWorldCohort)||!ticket.MatchesUnderGate())throw new InvalidOperationException("stale_wounds_native");
                    cohort.RevokeCapture("wounds_native_continuation");
                    if(!ticket.TryApplyActualUnderGate(out healed,out id,out reason))throw new InvalidOperationException(reason);
                    return true;
                }
            }
        }
    }
}
