using System;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    // Fresh diagnostic-only actual model cohort. Legacy VitalsState remains unchanged.
    internal sealed class DiagnosticVitalsValues
    {
        readonly double[] _values;
        static readonly string[] Keys = { "health","max_health","stamina","max_stamina","hunger","max_hunger","thirst","max_thirst",
            "health_drain_rate","stamina_drain_rate","hunger_drain_rate","thirst_drain_rate","stamina_recovery_rate","health_recovery_rate" };
        internal DiagnosticVitalsValues(double health=100,double maxHealth=100,double stamina=100,double maxStamina=100,
            double hunger=100,double maxHunger=100,double thirst=100,double maxThirst=100,double healthDrain=0,
            double staminaDrain=2,double hungerDrain=.5,double thirstDrain=.8,double staminaRecovery=5,double healthRecovery=0)
        {
            _values=new[]{health,maxHealth,stamina,maxStamina,hunger,maxHunger,thirst,maxThirst,healthDrain,staminaDrain,hungerDrain,thirstDrain,staminaRecovery,healthRecovery};
            foreach(double v in _values) if(double.IsNaN(v)||double.IsInfinity(v)) throw new ArgumentException("nonfinite_vitals");
        }
        internal double At(int index)=>_values[index];
        internal double Health=>_values[0]; internal double Stamina=>_values[2]; internal double MaxStamina=>_values[3];
        internal static DiagnosticVitalsValues FromModel(VitalsState m)=>new DiagnosticVitalsValues(m.Health,m.MaxHealth,m.Stamina,m.MaxStamina,m.Hunger,m.MaxHunger,m.Thirst,m.MaxThirst,m.HealthDrainRate,m.StaminaDrainRate,m.HungerDrainRate,m.ThirstDrainRate,m.StaminaRecoveryRate,m.HealthRecoveryRate);
        internal VitalsState ExactScratch()
        {
            return new VitalsState { Health=_values[0],MaxHealth=_values[1],Stamina=_values[2],MaxStamina=_values[3],Hunger=_values[4],MaxHunger=_values[5],Thirst=_values[6],MaxThirst=_values[7],
                HealthDrainRate=_values[8],StaminaDrainRate=_values[9],HungerDrainRate=_values[10],ThirstDrainRate=_values[11],StaminaRecoveryRate=_values[12],HealthRecoveryRate=_values[13] };
        }
        internal GdDict Configuration()
        {var d=new GdDict();for(int i=0;i<14;i++)d[Keys[i]]=_values[i];return d;}
    }
    internal sealed class DiagnosticVitalsTickInput
    {
        readonly GdDict _input;
        internal DiagnosticVitalsTickInput(bool moving=true,double statusRecovery=1,double sanityRecovery=1,double radiation=0,double atmosphere=0,double fire=0,
            double sanityDrain=0,double encumbrance=0,double woundDrain=0,double temperatureHunger=1,double temperatureThirst=1,double woundThirst=1)
        {
            double[] values={statusRecovery,sanityRecovery,radiation,atmosphere,fire,sanityDrain,encumbrance,woundDrain,temperatureHunger,temperatureThirst,woundThirst};
            foreach(double v in values)if(double.IsNaN(v)||double.IsInfinity(v))throw new ArgumentException("nonfinite_tick_context");
            _input=new GdDict{{SimKeys.Moving,moving},{SimKeys.StatusStaminaRecoveryMult,statusRecovery},{SimKeys.SanityStaminaRecoveryMult,sanityRecovery},
                {SimKeys.RadiationHealthDrain,radiation},{SimKeys.AtmosphereHealthDrain,atmosphere},{SimKeys.FireHealthDrain,fire},{SimKeys.SanityHealthDrain,sanityDrain},
                {SimKeys.EncumbranceHealthDrain,encumbrance},{SimKeys.WoundHealthDrain,woundDrain},{SimKeys.TemperatureHungerMult,temperatureHunger},{SimKeys.TemperatureThirstMult,temperatureThirst},{SimKeys.WoundThirstMult,woundThirst}};
        }
        // Closed twelve scalar slots, no caller-owned dictionary alias.
        internal GdDict CopyForModel()=>_input.ShallowCopy();
    }
    internal sealed class DiagnosticVitalsSnapshot
    {
        readonly DiagnosticVitalsProjectionAdapter _issuer;
        internal readonly DiagnosticVitalsValues Values;
        internal readonly ulong Stamp;
        DiagnosticVitalsSnapshot(DiagnosticVitalsProjectionAdapter issuer,DiagnosticVitalsValues values,ulong stamp)
        {_issuer=issuer;Values=values;Stamp=stamp;}
        // Issuance accepts ONLY the actual owner, never caller-selected values/stamp/token.
        internal static DiagnosticVitalsSnapshot CaptureActual(DiagnosticVitalsProjectionAdapter owner)
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                owner.ReadActualUnderGate(out var values,out ulong stamp);
                return new DiagnosticVitalsSnapshot(owner,values,stamp);
            }
        }
        internal bool MatchesActualUnderGate(DiagnosticVitalsProjectionAdapter owner)
        {
            CommonParticipantGate.RequireHeld();if(!ReferenceEquals(owner,_issuer))return false;
            owner.ReadActualUnderGate(out var values,out ulong stamp);if(stamp!=Stamp)return false;
            for(int i=0;i<14;i++)if(BitConverter.DoubleToInt64Bits(values.At(i))!=BitConverter.DoubleToInt64Bits(Values.At(i)))return false;
            return true;
        }
    }
    internal sealed class DiagnosticVitalsProjectionAdapter : IDisposable
    {
        readonly ParticipantProjectionRegistry _registry;
        readonly ProjectionNodeHandle _node;
        VitalsState _model;
        DiagnosticVitalsValues _currentValues;
        ulong _stamp=1;
        bool _disposed;
        PreparedMutation _pending;
        internal event Action<string,double> DamageObserved;
        internal string LastObserverError{get;private set;}="";
        internal DiagnosticVitalsProjectionAdapter(DiagnosticVitalsValues initial,ProjectionLimits limits=null)
        {
            if(initial==null)throw new ArgumentNullException(nameof(initial));
            _model=initial.ExactScratch();_currentValues=initial;_registry=new ParticipantProjectionRegistry(limits);
            try
            {
                if(!_registry.TryCreateNode(ProjectionNodeKind.Dictionary,out _node,out string reason))throw new InvalidOperationException(reason);
                if(!PrepareProjection(_model,_stamp,true,out var plan,out reason))throw new InvalidOperationException(reason);
                using(plan){if(!_registry.TryPublish(plan,out reason))throw new InvalidOperationException(reason);}
            }
            catch{_registry.Dispose();throw;}
        }
        internal DiagnosticVitalsSnapshot Read()=>DiagnosticVitalsSnapshot.CaptureActual(this);
        internal void ReadActualUnderGate(out DiagnosticVitalsValues values,out ulong stamp)
        {CommonParticipantGate.RequireHeld();if(_disposed)throw new ObjectDisposedException(nameof(DiagnosticVitalsProjectionAdapter));values=_currentValues;stamp=_stamp;}
        bool PrepareProjection(VitalsState model,ulong stamp,bool rebuild,out ProjectionPreparationCursor plan,out string reason)
        {
            plan=null;var summary=model.GetSummary(); // Fixed21scalar actual-model oracle; no whole mutable world clone.
            if(summary.Count!=21){reason="vitals_projection_shape";return false;}
            var entries=new ProjectionEntry[21];int i=0;
            foreach(var row in summary)
            {
                if(row.Value is double v&&(double.IsNaN(v)||double.IsInfinity(v))){reason="nonfinite_vitals_projection";return false;}
                entries[i++]=new ProjectionEntry(ProjectionScalar.FromNormalized(row.Key),new ProjectionValue(ProjectionScalar.FromNormalized(row.Value)));
            }
            if(!_node.TryCreateVersion(entries,out var version,out reason))return false;
            var descriptor=new ProjectionRootDescriptor("actual-vitals-partial-diagnostic-v1","deferred-observers-v1",stamp,new[]{new ProjectionRootBinding("vitals_summary",_node.Id)});
            if(!_registry.TryPrepare(new[]{version},null,descriptor,out plan,out reason,rebuild))return false;
            // One dictionary node/21 scalar entries, fixed-bound preparation outside final writer gate.
            int slices=0;
            while(plan.Status==ProjectionCursorStatus.Pending){plan.Advance(8);if(++slices>32){plan.Dispose();plan=null;reason="vitals_preparation_bound";return false;}}
            if(plan.Status!=ProjectionCursorStatus.Complete){reason=plan.Reason;plan.Dispose();plan=null;return false;}return true;
        }
        enum Operation{Tick,Configure,Summary,Delta,StaminaAfter,DamageHealth}
        bool Prepare(Operation operation,DiagnosticVitalsValues values,DiagnosticVitalsTickInput context,double delta,double health,double stamina,double hunger,double thirst,
            out PreparedMutation prepared,out string reason,ulong? expectedStamp=null)
        {
            prepared=null;reason=""; DiagnosticVitalsSnapshot before;
            lock(CommonParticipantGate.SyncRoot)
            {
                if(_disposed){reason="vitals_disposed";return false;}
                if(expectedStamp.HasValue&&expectedStamp.Value!=_stamp){reason="stale_vitals_preparation";return false;}
                if(_pending!=null){reason="vitals_preparation_busy";return false;}
                if(_stamp==ulong.MaxValue){_registry.Invalidate("vitals_stamp_exhausted");reason="vitals_stamp_exhausted";return false;}
                before=Read();
            }
            var scratch=before.Values.ExactScratch();var losses=new LossBuffer();scratch.HealthDamageObserved+=losses.Record;
            try
            {
                switch(operation)
                {
                    case Operation.Tick:scratch.Tick(delta,context.CopyForModel());break;
                    case Operation.Configure:scratch.Configure(values.Configuration());break;
                    case Operation.Summary:scratch.ApplySummary(values.Configuration());break;
                    case Operation.Delta:scratch.ApplyDelta(new GdDict{{"health",health},{"stamina",stamina},{"hunger",hunger},{"thirst",thirst}});break;
                    case Operation.StaminaAfter:scratch.Stamina=stamina;break;
                    case Operation.DamageHealth:((IDamageVitalsTarget)scratch).Health=health;break;
                }
                scratch.HealthDamageObserved-=losses.Record;
                var nextValues=DiagnosticVitalsValues.FromModel(scratch); // Finite14stored values; derived guard in projection.
                if(!PrepareProjection(scratch,before.Stamp+1,false,out var plan,out reason))return false;
                var candidate=new PreparedMutation(this,before.Stamp,scratch,nextValues,plan,losses);
                lock(CommonParticipantGate.SyncRoot)
                {
                    if(_disposed||_stamp!=before.Stamp||_pending!=null){candidate.Dispose();reason="stale_vitals_preparation";return false;}
                    _pending=candidate;prepared=candidate;return true;
                }
            }
            catch(ArgumentException e){reason=e.Message;return false;}
            finally{scratch.HealthDamageObserved-=losses.Record;}
        }
        internal bool PrepareTick(double delta,DiagnosticVitalsTickInput input,out PreparedMutation prepared,out string reason)
        {if(input==null||!Finite(delta)){prepared=null;reason="invalid_tick";return false;}return Prepare(Operation.Tick,null,input,delta,0,0,0,0,out prepared,out reason);}
        internal bool PrepareConfigure(DiagnosticVitalsValues values,out PreparedMutation prepared,out string reason)
        {if(values==null){prepared=null;reason="invalid_configuration";return false;}return Prepare(Operation.Configure,values,null,0,0,0,0,0,out prepared,out reason);}
        internal bool PrepareSummary(DiagnosticVitalsValues values,out PreparedMutation prepared,out string reason)
        {if(values==null){prepared=null;reason="invalid_summary";return false;}return Prepare(Operation.Summary,values,null,0,0,0,0,0,out prepared,out reason);}
        internal bool PrepareDelta(double health,double stamina,double hunger,double thirst,out PreparedMutation prepared,out string reason)
        {if(!Finite(health)||!Finite(stamina)||!Finite(hunger)||!Finite(thirst)){prepared=null;reason="invalid_delta";return false;}return Prepare(Operation.Delta,null,null,0,health,stamina,hunger,thirst,out prepared,out reason);}
        internal bool PrepareStaminaAfter(DiagnosticVitalsSnapshot expected,double after,out PreparedMutation prepared,out string reason)
        {
            lock(CommonParticipantGate.SyncRoot)
            {if(_disposed||expected==null||!expected.MatchesActualUnderGate(this)||!Finite(after)||after<0||after>_model.Stamina){prepared=null;reason="stale_or_invalid_stamina_debit";return false;}}
            return Prepare(Operation.StaminaAfter,null,null,0,0,after,0,0,out prepared,out reason,expected.Stamp);
        }
        internal bool PrepareDamageHealthAfter(double health,out PreparedMutation prepared,out string reason)
        {if(!Finite(health)){prepared=null;reason="invalid_health";return false;}return Prepare(Operation.DamageHealth,null,null,0,health,0,0,0,out prepared,out reason);}
        static bool Finite(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v);
        static bool Bits(double a,double b)=>BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);
        internal bool TryPinPartialDiagnostic(out ProjectionPin pin,out string reason)
        {lock(CommonParticipantGate.SyncRoot){if(_disposed||_pending!=null){pin=null;reason="vitals_unavailable";return false;}return _registry.TryPin(out pin,out reason);}}
        internal sealed class PreparedMutation : IDisposable
        {
            readonly DiagnosticVitalsProjectionAdapter _owner;
            readonly ulong _before;
            readonly VitalsState _next;
            readonly DiagnosticVitalsValues _nextValues;
            readonly ProjectionPreparationCursor _plan;
            readonly LossBuffer _losses;
            bool _used,_installed,_notified;
            internal PreparedMutation(DiagnosticVitalsProjectionAdapter owner,ulong before,VitalsState next,DiagnosticVitalsValues nextValues,ProjectionPreparationCursor plan,LossBuffer losses)
            {_owner=owner;_before=before;_next=next;_nextValues=nextValues;_plan=plan;_losses=losses;}
            internal bool MatchesUnderGate()
            {CommonParticipantGate.RequireHeld();return !_used&&!_owner._disposed&&ReferenceEquals(_owner._pending,this)&&_owner._stamp==_before&&_plan.Status==ProjectionCursorStatus.Complete;}
            // Pair owner must prevalidate every other no-fail pointer assignment BEFORE this call.
            // Once true, only straight prevalidated pair assignments may follow under the same gate.
            internal bool TryInstallUnderGate(out string reason)
            {
                CommonParticipantGate.RequireHeld();if(!MatchesUnderGate()){reason="stale_vitals_install";return false;}
                if(!_owner._registry.TryPublish(_plan,out reason))return false;
                _owner._model=_next;_owner._currentValues=_nextValues;_owner._stamp=_before+1;_owner._pending=null;_used=_installed=true;return true;
            }
            internal bool CommitStandalone(out string reason)
            {lock(CommonParticipantGate.SyncRoot){if(!TryInstallUnderGate(out reason))return false;}NotifyAfterGate();return true;}
            internal void NotifyAfterGate()
            {
                if(System.Threading.Monitor.IsEntered(CommonParticipantGate.SyncRoot))throw new InvalidOperationException("vitals_observer_gate_held");
                if(!_installed||_notified)return;_notified=true;
                for(int i=0;i<_losses.Count;i++)
                {try{_owner.DamageObserved?.Invoke(_losses.Sources[i],_losses.Amounts[i]);}catch(Exception e){_owner.LastObserverError=e.GetType().Name;}}
            }
            public void Dispose()
            {lock(CommonParticipantGate.SyncRoot){if(!_used){_used=true;if(ReferenceEquals(_owner._pending,this))_owner._pending=null;}_plan.Dispose();}}
        }
        internal sealed class LossBuffer
        {
            internal readonly string[] Sources=new string[8];internal readonly double[] Amounts=new double[8];internal int Count;
            internal void Record(string source,double amount){if(Count==8)throw new ArgumentException("vitals_damage_event_capacity");Sources[Count]=source;Amounts[Count++]=amount;}
        }
        public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(_disposed)return;_disposed=true;_pending?.Dispose();_registry.Dispose();}}
    }
}
