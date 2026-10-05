using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        // Actual scene reads, preparation only. Not a caller-supplied atmosphere eligibility label.
        // Root dispatch invokes after current caches are enrolled and before callback-free final install.
        bool TryPrepareContinuousOxygenSceneInput(out ContinuousOxygenSceneInput prepared,out string reason)
        {
            prepared=null;reason="continuous_oxygen_unbound";
            if(_continuousActualOxygenWriter==null || OxygenState==null)return false;
            if(!_inTick || _continuousMutationDepth<=0){reason="oxygen_requires_active_tick";return false;}
            var scene=Scene;var position=PlayerPos;var occupancy=CurrentOccupancy;bool hasPlayer=HasPlayer;
            ShipInstance airOwner=PhysicalAirOwner();
            FireSuppressionState fire=airOwner==HomeShip && airOwner!=null ? FireSuppressionState
                : PhysicalAirInfrastructureAbsent() ? ActiveFireState() : airOwner?.GetFire();
            double fireDrain=fire==null?0:FIRE_OXYGEN_DRAIN_PER_INTENSITY*fire.GetTotalIntensity();
            bool field=IsFieldSuitPressureActive();double multiplier=1;
            if(field)
            {
                IShipLoaderView loader=airOwner?.SceneRoot as IShipLoaderView ?? (PhysicalAirInfrastructureAbsent()?Loader:null);
                if(loader!=null && loader.IsValid && hasPlayer)multiplier=loader.GetAuthoredAtmosphereDrainMultiplierAt(ToLocal(loader,position));
            }
            bool inBreach=!field && PhysicalHomeAtmosphereApplies() && IsPlayerInBreachZone();
            double severity=field?0:HomeAtmosphereSeverity();
            var input=new ContinuousOxygenTickInput(inBreach,field,multiplier,fireDrain,!field,severity,
                Deps.HomeSuitAirReserveSeconds,HomeDial(SynapticSea.Core.Procgen.DifficultyProfile.DIAL_HAZARD));
            // Observer/raycast/loader side effects cannot silently certify their earlier scene sample.
            if(!_inTick||!ReferenceEquals(scene,Scene)||position!=PlayerPos||hasPlayer!=HasPlayer||!ReferenceEquals(occupancy,CurrentOccupancy))
            {reason="stale_oxygen_scene_sample";return false;}
            prepared=new ContinuousOxygenSceneInput(this,scene,position,occupancy,hasPlayer,input,_continuousBoundaryEpoch);
            reason="";return true;
        }
        sealed class ContinuousOxygenSceneInput
        {
            readonly RunSession _owner;readonly object _scene,_occupancy;readonly Vec3 _position;
            readonly bool _hasPlayer;readonly ulong _boundary;
            internal readonly ContinuousOxygenTickInput Input;
            internal ContinuousOxygenSceneInput(RunSession owner,object scene,Vec3 position,object occupancy,bool hasPlayer,ContinuousOxygenTickInput input,ulong boundary)
            {_owner=owner;_scene=scene;_position=position;_occupancy=occupancy;_hasPlayer=hasPlayer;Input=input;_boundary=boundary;}
            internal bool MatchesUnderGate(RunSession owner)
            {
                CommonParticipantGate.RequireHeld();
                return owner._inTick&&ReferenceEquals(owner,_owner)&&ReferenceEquals(_scene,owner.Scene)&&ReferenceEquals(_occupancy,owner.CurrentOccupancy)&&
                    _position==owner.PlayerPos&&_hasPlayer==owner.HasPlayer&&_boundary==owner._continuousBoundaryEpoch&&owner._continuousMutationDepth>0;
            }
        }
    }
}
