using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        // Selected diagnostic only: temperature/radiation/sanity/status/ship infrastructure remain sealed genesis.
        // Their current derived modifiers are honored; their legacy Tick writers are not dispatched.
        bool TryTickContinuousSurvival(double delta,out bool committed,out double movementMultiplier,out bool incapacitated,out string reason)
        {
            committed=false;movementMultiplier=1;incapacitated=false;reason="continuous_survival_unbound";
            if(_continuousActualVitalsWriter==null)return false;
            if(!_inTick || _continuousMutationDepth<=0){reason="survival_requires_active_tick";return true;}
            var scene=Scene;var position=PlayerPos;bool hasPlayer=HasPlayer;
            double temperature=BodyTemperatureState?.GetThirstMultiplier()??1;
            double radiation=RadiationState?.GetHealthDrainPerSecond()??0;
            double status=StatusEffectsState?.GetModifier("stamina_recovery")??1;
            double atmosphere=0;
            if(LifeSupportExpandedState!=null && PhysicalHomeAtmosphereApplies())
            {
                atmosphere=LifeSupportExpandedState.GetHealthDrainPerSecond();temperature*=LifeSupportExpandedState.GetThirstMultiplier();
                if(SuitFilteringShipAir)atmosphere=0;
            }
            double oxygenDrain=0;
            if(OxygenState!=null && IsFieldSuitPressureActive())
            {
                if(OxygenState.Oxygen<=.001)oxygenDrain=8;
                else if(OxygenState.Oxygen<=OxygenState.RecoveryThreshold+.001)oxygenDrain=2;
            }
            double hunger=1;if(HomeSpawnSafetyActive){hunger=0;temperature=0;}
            var teeth=HallucinationDirector?.GetDirectTeeth()??new GdDict{{"health_drain_per_second",0.0},{"stamina_recovery_mult",1.0}};
            double encumbrance=InventoryState==null?0:Encumbrance.HealthDrainPerSecond(InventoryState.GetLoadRatio());
            var input=new DiagnosticVitalsTickInput(moving:hasPlayer&&PlayerMoving,statusRecovery:status,
                sanityRecovery:V.F64(teeth["stamina_recovery_mult"]),radiation:radiation,atmosphere:atmosphere+oxygenDrain,
                fire:FIRE_HEALTH_DRAIN_PER_SECOND*PlayerFireIntensity(),sanityDrain:V.F64(teeth["health_drain_per_second"]),
                encumbrance:encumbrance,woundDrain:WoundState?.TotalBleedRate()??0,
                temperatureHunger:hunger,temperatureThirst:temperature,woundThirst:WoundState?.ThirstDrainMultiplier()??1);
            if(!_inTick || !ReferenceEquals(scene,Scene)||position!=PlayerPos||hasPlayer!=HasPlayer){reason="stale_survival_scene_sample";return true;}
            committed=_continuousActualVitalsWriter.TryTick(delta,input,out reason);
            if(committed)
            {
                movementMultiplier=VitalsState.GetMovementSpeedMultiplier()*(WoundState?.MovementSpeedMultiplier()??1);
                incapacitated=VitalsState.IsIncapacitated();
            }
            // Root applies movement presentation and terminal policy after commit, inside outer world batch.
            // No trailing Radiation/BodyTemperature Tick, no second Vitals Tick or historical state overwrite.
            return true;
        }
    }
}
