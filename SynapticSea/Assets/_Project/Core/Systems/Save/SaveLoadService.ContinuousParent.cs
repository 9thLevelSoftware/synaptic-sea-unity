using System;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    public partial class SaveLoadService
    {
        internal sealed class ContinuousCommitParent
        {
            readonly SaveLoadService _service;
            internal readonly string RunId,SlotId,ParentGeneration,ExpectedPointer,SavedAt,GodotVersion;
            internal readonly long PriorCaptureRevision,SavedAtEpoch;
            readonly GdDict _compatibility;
            internal readonly SaveCommitCoordinator Coordinator;
            internal readonly ProofResourceBinding Binding;
            internal readonly ContinuousSaveReadPolicy Policy;
            ContinuousCommitParent(ContinuousParentReadPlan plan,string parent,string pointer,long revision)
            {_service=plan.Service;RunId=plan.Run;SlotId=plan.Slot;ParentGeneration=parent;ExpectedPointer=pointer;PriorCaptureRevision=revision;
             SavedAtEpoch=plan.Epoch;SavedAt=plan.Date;GodotVersion=plan.Engine;_compatibility=plan.Compatibility.DeepCopy();Binding=plan.Binding;Policy=plan.Policy;Coordinator=plan.Coordinator;}
            internal GdDict CopyCompatibility()=>_compatibility.DeepCopy();
            internal bool Matches(SaveLoadService service,string run,string slot)
                => ReferenceEquals(_service,service)&&RunId==run&&SlotId==slot&&Policy.MatchesCurrentBinding(Binding);
            internal static ContinuousCommitParent Read(ContinuousParentReadPlan plan)
            {
                GdDict prior=plan.Coordinator.ReadSelected(plan.Slot);
                string parent=prior.GetBool("ok")&&prior.GetString("run_id")==plan.Run?prior.GetString("generation_id"):"";
                string pointer=parent.Length>0?prior.GetString("selected_pointer_sha256"):"";
                long revision=parent.Length>0?prior.GetDictOrEmpty("payloads").GetInt("domain_revision"):0;
                if(prior.GetString("reason")=="slot_deleted")
                {GdDict retained=plan.Coordinator.ReadCommitParent(plan.Run,plan.Slot);if(retained.GetBool("ok")){parent=retained.GetString("parent_generation_id");pointer=retained.GetString("expected_pointer_sha256");revision=retained.GetInt("domain_revision");}}
                return new ContinuousCommitParent(plan,parent,pointer,revision);
            }
        }
        internal sealed class ContinuousParentReadPlan
        {
            internal readonly SaveLoadService Service;
            internal readonly string Run,Slot,Date,Engine;
            internal readonly long Epoch;
            internal readonly GdDict Compatibility;
            internal readonly SaveCommitCoordinator Coordinator;
            internal readonly ProofResourceBinding Binding;
            internal readonly ContinuousSaveReadPolicy Policy;
            internal ContinuousParentReadPlan(SaveLoadService service,string run,string slot)
            {
                Service=service;Run=run;Slot=slot;Epoch=(long)service.Clock.UnixTime();Date=service.Clock.DateTimeString(true);Engine=EngineVersionString;
                Compatibility=service.ComponentCompatibility().DeepCopy();Coordinator=service.CreateContinuousWorkerCoordinator(run);Binding=service._continuousReaderBinding;Policy=service._continuousReaderPolicy;
            }
            // Storage/proof read only; metadata and coordinator were prepared on the caller thread.
            internal bool TryRead(out ContinuousCommitParent parent,out string reason)
            {
                parent=null;reason="continuous_parent_resource_changed";if(!Policy.MatchesCurrentBinding(Binding))return false;
                try{var prepared=ContinuousCommitParent.Read(this);if(!Policy.MatchesCurrentBinding(Binding))return false;parent=prepared;reason="";return true;}
                catch(Exception failure){reason="continuous_parent_read_failed:"+failure.Message;return false;}
            }
        }
        internal bool TryPrepareContinuousParentRead(string run,string slot,out ContinuousParentReadPlan plan,out string reason)
        {
            plan=null;reason="explicit_continuous_reader_required";
            if(_continuousReaderPolicy==null||!_continuousReaderPolicy.MatchesCurrentBinding(_continuousReaderBinding))return false;
            if(string.IsNullOrEmpty(run)||Array.IndexOf(ComponentSlotsIds,slot)<0){reason="invalid_continuous_save_slot";return false;}
            try{plan=new ContinuousParentReadPlan(this,run,slot);reason="";return true;}
            catch(Exception failure){reason="continuous_parent_prepare_failed:"+failure.Message;return false;}
        }
    }
}
