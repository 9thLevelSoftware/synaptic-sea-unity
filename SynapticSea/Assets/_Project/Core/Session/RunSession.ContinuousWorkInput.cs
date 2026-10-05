using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        string _continuousPauseReason="explicit_resume_required";
        string _continuousPendingService,_continuousPendingOperation;
        object _continuousInputGeneration=new object(),_continuousPendingGeneration;
        bool _continuousResumeIntent;
        ulong _continuousResumeDamageEpoch;
        // Input/presentation source only. Authentic start/take publication consumes the pending intent separately.
        bool ContinuousAuxiliaryWorkRunning => ContinuousAuxiliaryRuntimeActive && _continuousAuxiliaryRuntime!=null;
        GdDict RequestContinuousAuxiliaryIntent(string operation,string service)
        {
            if(_continuousPendingOperation!=null)return PaidFailure("continuous_command_pending");
            if(operation=="start" && _continuousAuxiliaryRuntime!=null)
            {
                if(_continuousAuxiliaryRuntime.ServiceId!=service)return PaidFailure("auxiliary_busy");
                BeginContinuousWorkHoldIntent();return new GdDict{{"ok",true},{"queued",true},{"committed",false}};
            }
            _continuousPendingOperation=operation;_continuousPendingService=service;_continuousPendingGeneration=_continuousInputGeneration;
            return new GdDict{{"ok",true},{"queued",true},{"committed",false}};
        }
        bool BeginContinuousWorkHoldIntent()
        {
            if(_continuousAuxiliaryRuntime==null)return false;
            _workHoldInput=true;
            if(!_auxConsent){_continuousResumeIntent=true;_continuousResumeDamageEpoch=ContinuousAuxiliaryDamageEpoch;}
            return true;
        }
        void PauseContinuousAuxiliaryIntent(string reason)
        {
            if(_continuousAuxiliaryRuntime==null)CancelContinuousQueuedStart();
            _auxConsent=false;_continuousConsentCaptured=false;_continuousResumeIntent=false;
            _workHoldInput=false;_continuousPauseReason=reason??"paused";
            _continuousInputGeneration=new object();_continuousPendingOperation=null;_continuousPendingService=null;_continuousPendingGeneration=null;
            // No domain transaction, stamina assignment, evidence deletion or historical restore.
        }
        void ConsumeContinuousResumeIntent()
        {
            if(!_continuousResumeIntent)return;_continuousResumeIntent=false;
            if(_continuousResumeDamageEpoch!=ContinuousAuxiliaryDamageEpoch)
            {PauseContinuousAuxiliaryIntent("damage");return;}
            _auxConsent=true;CaptureContinuousAuxiliaryConsent();
            // Actual physical predicates are checked immediately by TryPrepareContinuousWorkFrame.
        }
        // Public UI view only. Paid admission/save uses privately published canonical owner, never this overlay.
        GdDict GetContinuousAuxiliaryPresentation()
        {
            var view=_componentDomain.GetParticipantProjection("auxiliary_services");var runtime=_continuousAuxiliaryRuntime;
            if(runtime!=null)
            {
                var state=runtime.Snapshot();var job=view.GetDictOrEmpty("job");
                job["service_id"]=runtime.ServiceId;job["progress_seconds"]=state.ProgressSeconds;
                job["eligible_seconds"]=state.EligibleSeconds;job["status"]=_auxConsent?"running":"paused";
                job["reason"]=_auxConsent?"":_continuousPauseReason;view["job"]=job;
            }
            return view;
        }
        void RefreshContinuousAuxiliaryHud()
        {
            var runtime=_continuousAuxiliaryRuntime;if(runtime==null){Events.RaiseWorkActionHudState(new GdDict{{"action_id",""},{"target_id",""},{"progress",0.0},{"status","idle"},{"block_reason",""},{"noise",0.0}});return;}
            var state=runtime.Snapshot();
            Events.RaiseWorkActionHudState(new GdDict{{"action_id","auxiliary_service"},{"target_id",runtime.ServiceId},
                {"verb","Service"},{"progress",state.ProgressSeconds/runtime.RequiredSeconds},
                {"status",_auxConsent?"active":"paused"},{"block_reason",_auxConsent?"":_continuousPauseReason},{"noise",0.0}});
        }
    }
}
