using System;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    internal sealed class ContinuousOxygenValues
    {
        readonly string[] _zones;
        internal readonly double Oxygen,MaxOxygen,DrainRate,RegenRate,RecoveryThreshold,SafeThreshold,EffectiveDrainRate,InventoryMultiplier,EquipmentMultiplier;
        internal readonly bool BreachOpen,BreachSealed,PassabilityBlocked,LastPlayerInBreachZone;
        ContinuousOxygenValues(OxygenState source)
        {
            Oxygen=source.Oxygen;MaxOxygen=source.MaxOxygen;DrainRate=source.DrainRate;RegenRate=source.RegenRate;
            RecoveryThreshold=source.RecoveryThreshold;SafeThreshold=source.SafeThreshold;EffectiveDrainRate=source.EffectiveDrainRate;
            BreachOpen=source.BreachOpen;BreachSealed=source.BreachSealed;PassabilityBlocked=source.PassabilityBlocked;LastPlayerInBreachZone=source.LastPlayerInBreachZone;
            source.ReadContinuousDrainCacheMultipliers(out InventoryMultiplier,out EquipmentMultiplier);
            if(!Finite(Oxygen)||!Finite(MaxOxygen)||!Finite(DrainRate)||!Finite(RegenRate)||!Finite(RecoveryThreshold)||!Finite(SafeThreshold)||
                !Finite(EffectiveDrainRate)||!Finite(InventoryMultiplier)||!Finite(EquipmentMultiplier))throw new ArgumentException("continuous_oxygen_nonfinite");
            if(source.BreachZoneIds==null||source.BreachZoneIds.Count>256)throw new ArgumentException("continuous_oxygen_zones_bound");
            _zones=new string[source.BreachZoneIds.Count];
            for(int i=0;i<_zones.Length;i++)
            {if(!(source.BreachZoneIds[i] is string zone)||zone.Length==0||zone.Length>256)throw new ArgumentException("continuous_oxygen_zone_shape");_zones[i]=zone;}
        }
        internal static ContinuousOxygenValues CaptureExact(OxygenState source)
        {if(source==null)throw new ArgumentNullException(nameof(source));return new ContinuousOxygenValues(source);}
        static bool Finite(double x)=>!double.IsNaN(x)&&!double.IsInfinity(x);
        static bool B(double a,double b)=>BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);
        internal int ZoneCount=>_zones.Length;
        internal string Zone(int index)=>_zones[index];
        internal bool MatchesRaw(OxygenState source)
        {
            if(source==null||source.BreachZoneIds==null||source.BreachZoneIds.Count!=_zones.Length)return false;
            source.ReadContinuousDrainCacheMultipliers(out double inventory,out double equipment);
            if(!B(Oxygen,source.Oxygen)||!B(MaxOxygen,source.MaxOxygen)||!B(DrainRate,source.DrainRate)||!B(RegenRate,source.RegenRate)||
                !B(RecoveryThreshold,source.RecoveryThreshold)||!B(SafeThreshold,source.SafeThreshold)||!B(EffectiveDrainRate,source.EffectiveDrainRate)||
                !B(InventoryMultiplier,inventory)||!B(EquipmentMultiplier,equipment)||BreachOpen!=source.BreachOpen||BreachSealed!=source.BreachSealed||
                PassabilityBlocked!=source.PassabilityBlocked||LastPlayerInBreachZone!=source.LastPlayerInBreachZone)return false;
            for(int i=0;i<_zones.Length;i++)if(!(source.BreachZoneIds[i] is string zone)||!StringComparer.Ordinal.Equals(_zones[i],zone))return false;
            return true;
        }
        internal bool SameTickConfiguration(ContinuousOxygenValues other)
        {
            if(other==null||!B(MaxOxygen,other.MaxOxygen)||!B(DrainRate,other.DrainRate)||!B(RegenRate,other.RegenRate)||
                !B(RecoveryThreshold,other.RecoveryThreshold)||!B(SafeThreshold,other.SafeThreshold)||BreachOpen!=other.BreachOpen||BreachSealed!=other.BreachSealed||
                !B(InventoryMultiplier,other.InventoryMultiplier)||!B(EquipmentMultiplier,other.EquipmentMultiplier)||ZoneCount!=other.ZoneCount)return false;
            for(int i=0;i<ZoneCount;i++)if(!StringComparer.Ordinal.Equals(Zone(i),other.Zone(i)))return false;
            return true;
        }
        // Non-current exact proposal, never another live stamina/oxygen authority.
        internal OxygenState ExactScratch()
        {
            var model=new OxygenState{Oxygen=Oxygen,MaxOxygen=MaxOxygen,DrainRate=DrainRate,RegenRate=RegenRate,RecoveryThreshold=RecoveryThreshold,
                SafeThreshold=SafeThreshold,EffectiveDrainRate=EffectiveDrainRate,BreachOpen=BreachOpen,BreachSealed=BreachSealed,
                PassabilityBlocked=PassabilityBlocked,LastPlayerInBreachZone=LastPlayerInBreachZone};
            for(int i=0;i<_zones.Length;i++)model.BreachZoneIds.Add(_zones[i]);
            model.ApplyInventorySummary(new GdDict{{"drain_multiplier",InventoryMultiplier}});
            model.ApplyEquipmentSummary(new GdDict{{"drain_multiplier",EquipmentMultiplier}});return model;
        }
    }
}
