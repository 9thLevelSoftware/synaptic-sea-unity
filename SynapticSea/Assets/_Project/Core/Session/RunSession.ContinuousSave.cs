using System;
using System.Threading.Tasks;
using System.Threading;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        sealed class ContinuousSaveRequest
        {
            internal readonly string Slot,Kind,Run;
            internal string AbortedReason;
            internal Task ActiveTask => (Task)Commit ?? (Task)Preparation ?? Parent;
            internal Task<ParentResult> Parent;
            internal SaveLoadService.ContinuousCommitParent CommitParent;
            internal Task<OutputResult> Preparation;
            internal Task<GdDict> Commit;
            internal OwnedContinuousSaveOutput Output;
            internal ContinuousSaveRequest(string run,string slot,string kind){Run=run;Slot=slot;Kind=kind;}
        }
        sealed class ParentResult {internal SaveLoadService.ContinuousCommitParent Parent;internal string Reason;}
        sealed class OutputResult {internal OwnedContinuousSaveOutput Output;internal string Reason;}
        static int _continuousSaveReservation;
        ContinuousSaveRequest _continuousSaveRequest;
        bool _continuousSavePumpActive;
        bool RequestContinuousSaveToSlot(string slot,string kind,string displayName)
        {
            if(!ContinuousAuxiliaryRuntimeActive||!PlayableStarted||SliceComplete||ComponentTerminalPending||SaveLoadService==null)return false;
            if(_continuousSaveRequest!=null){LastSaveResult=new GdDict{{"ok",false},{"reason","continuous_save_busy"}};return false;}
            if(kind!=SaveLoadService.ComponentSlotKind(slot))return false;
            if(Interlocked.CompareExchange(ref _continuousSaveReservation,1,0)!=0){LastSaveResult=new GdDict{{"ok",false},{"reason","continuous_save_worker_reserved"}};return false;}
            // Intent only. Observer callbacks never capture a partially published transitive world.
            _continuousSaveRequest=new ContinuousSaveRequest(RunIdInternal,slot,kind);
            LastSaveResult=new GdDict{{"ok",false},{"pending",true},{"reason","continuous_save_queued"}};
            return true;
        }

        internal void AbandonContinuousSaveRequest()
        {
            var request=_continuousSaveRequest;
            if(request==null)return;
            request.AbortedReason="continuous_save_lifecycle_changed";
            _continuousSaveRequest=null;
            var active=request.ActiveTask;
            if(active==null||active.IsCompleted)Interlocked.Exchange(ref _continuousSaveReservation,0);
            else active.ContinueWith(_=>Interlocked.Exchange(ref _continuousSaveReservation,0),TaskScheduler.Default);
        }
        // Called by the root after complete outer Tick, never from native observer notification.
        void ProcessContinuousSaveAtSafeEndTick()
        {
            if(_continuousSavePumpActive||_continuousSaveRequest==null)return;
            _continuousSavePumpActive=true;
            try
            {
                var request=_continuousSaveRequest;
                void Release(){_continuousSaveRequest=null;Interlocked.Exchange(ref _continuousSaveReservation,0);}
                void Fail(string reason){request.AbortedReason=reason;LastSaveResult=new GdDict{{"ok",false},{"pending",false},{"reason",reason}};var active=request.ActiveTask;if(active==null||active.IsCompleted)Release();}
                if(request.AbortedReason!=null){if(request.ActiveTask==null||request.ActiveTask.IsCompleted)Release();return;}
                if(request.Commit!=null)
                {
                    if(!request.Commit.IsCompleted)return;
                    if(request.Commit.IsFaulted||request.Commit.IsCanceled){Fail("continuous_commit_worker_failed");return;}
                    LastSaveResult=request.Commit.Result.DeepCopy();Release();
                    // Acknowledgment is bookkeeping only: never apply historical vitals/pose/progress or refund stamina.
                    return;
                }
                if(!ContinuousAuxiliaryRuntimeActive||!PlayableStarted||SliceComplete||ComponentTerminalPending||request.Run!=RunIdInternal)
                {Fail("continuous_save_lifecycle_changed");return;}
                if(request.Preparation!=null)
                {
                    if(!request.Preparation.IsCompleted)return;
                    if(request.Preparation.IsFaulted||request.Preparation.IsCanceled){Fail("continuous_output_worker_failed");return;}
                    var result=request.Preparation.Result;
                    if(result.Output==null){Fail(result.Reason);return;}
                    request.Output=result.Output;
                    var coordinator=request.CommitParent.Coordinator;var output=result.Output;
                    request.Commit=Task.Run(()=>coordinator.CommitContinuousCut(output));
                    LastSaveResult=new GdDict{{"ok",false},{"pending",true},{"reason","continuous_save_committing"}};
                    return;
                }
                if(request.Parent==null)
                {
                    if(!SaveLoadService.TryPrepareContinuousParentRead(request.Run,request.Slot,out var plan,out string reason)){Fail(reason);return;}
                    // All arbitrary clock/engine metadata was sampled before any snapshot/proof pin exists.
                    request.Parent=Task.Run(()=>{plan.TryRead(out var parent,out string why);return new ParentResult{Parent=parent,Reason=why};});
                    return;
                }
                if(!request.Parent.IsCompleted)return;
                if(request.Parent.IsFaulted||request.Parent.IsCanceled){Fail("continuous_parent_worker_failed");return;}
                var prepared=request.Parent.Result;
                if(prepared.Parent==null){Fail(prepared.Reason);return;}
                request.CommitParent=prepared.Parent;
                if(!TryReadContinuousSafeEndTick(out var ticket))return;
                if(!OwnedContinuousCapture.TryCapture(this,ticket,request.CommitParent,request.Slot,request.Kind,out var cut,out string failure)){Fail(failure);return;}
                request.Preparation=Task.Run(()=>{OwnedContinuousSaveOutput.TryPrepare(cut,out var output,out string why);return new OutputResult{Output=output,Reason=why};});
                LastSaveResult=new GdDict{{"ok",false},{"pending",true},{"reason","continuous_save_preparing"}};
            }
            finally{_continuousSavePumpActive=false;}
        }
    }
}
