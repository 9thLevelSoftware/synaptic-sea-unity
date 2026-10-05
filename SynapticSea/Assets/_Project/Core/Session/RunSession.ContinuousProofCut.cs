using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        internal sealed class ContinuousRuntimeProofCut
        {
            internal readonly AuxiliaryWorkRuntime.EvidenceExportPin Evidence;
            internal readonly AdmittedAuxiliaryHistory History;
            internal readonly DomainBundle CanonicalBefore;
            internal readonly CheckpointProofAdmission.Result RetainedResult;
            internal readonly ContinuousOutputLifetime Lifetime;
            ContinuousRuntimeProofCut(AuxiliaryWorkRuntime.EvidenceExportPin evidence,AdmittedAuxiliaryHistory history,DomainBundle before,
                CheckpointProofAdmission.Result retained,ContinuousOutputLifetime lifetime)
            {Evidence=evidence;History=history;CanonicalBefore=before;RetainedResult=retained;Lifetime=lifetime;}
            internal static ContinuousRuntimeProofCut Issue(RunSession session,object issuer,AuxiliaryWorkRuntime.EvidenceExportPin evidence,DomainBundle before,ContinuousOutputLifetime lifetime)
            {
                if(!ReferenceEquals(issuer,session._continuousWorkContextIssuer)||!evidence.IsProducer(session._continuousAuxiliaryRuntime)||
                    !ReferenceEquals(evidence.SourceHistory,session._continuousWorkHistory))throw new InvalidOperationException("unissued_continuous_proof_cut");
                return new ContinuousRuntimeProofCut(evidence,session._continuousWorkHistory,before,session._continuousRetainedProofResult,lifetime);
            }
        }
        // Actual safe-end-Tick join. Rotation changes only permanent transport framing, not paid owner,
        // stamina, progress, participants or world clocks. Failed capture keeps every accepted record.
        internal bool TryCaptureContinuousRuntimeProofCut(ContinuousSafeEndTick ticket,out ContinuousRuntimeProofCut cut,out string reason)
        {
            cut=null;reason="continuous_unfinished_runtime_missing";
            var runtime=_continuousAuxiliaryRuntime;if(runtime==null||_continuousWorkHistory==null)return false;
            RequireContinuousClosedCut(ticket);
            var before=_componentDomain;var beforePointer=before.CurrentSourceIdentity;var lifetime=GetContinuousOutputLifetime(ticket);
            // Ordinary actual participants remain authority. A pointer match is not content equality:
            // background study/spoilage/loot can change them. Read callbacks/summary arithmetic only
            // outside the gate at this synchronous same-thread cut; refuse drift, never resync/overwrite.
            if(System.Threading.Monitor.IsEntered(CommonParticipantGate.SyncRoot))
            {reason="continuous_cut_inside_gate";return false;}
            var canonical=((DomainBundle)beforePointer).GetSummary();var hash=OwnerHashContext(canonical);
            var actual=ReadComponentParticipants(PaidState(canonical.DeepCopy()),hash);
            if(!PaidEqual(hash,canonical.Get("participating_state"),actual))
            {reason="continuous_cut_actual_participant_drift";return false;}
            RequireContinuousClosedCut(ticket);
            if(!ReferenceEquals(before,_componentDomain)||!ReferenceEquals(beforePointer,before.CurrentSourceIdentity))
            {reason="continuous_cut_owner_changed";return false;}
            if(!runtime.TryPrepareEvidenceRequestCut(runtime.EvidenceJournal.StructuralVersion,out var transport,out reason))return false;
            using(var attempt=CommonParticipantGate.BeginAttempt())
            {
                RequireContinuousClosedCut(ticket);
                if(!ReferenceEquals(runtime,_continuousAuxiliaryRuntime)||!ReferenceEquals(before,_componentDomain)||
                    !ReferenceEquals(beforePointer,before.CurrentSourceIdentity)||!_continuousWorkHistory.ResourcesCurrent||
                    !transport.MatchesUnderGate()){reason="stale_continuous_cut";return false;}
                if(!transport.TryInstallTransportUnderGate(out _)){reason="transport_cut_refused";return false;}
                var evidence=runtime.PinInstalledEvidenceCutUnderGate();
                cut=ContinuousRuntimeProofCut.Issue(this,_continuousWorkContextIssuer,evidence,(DomainBundle)beforePointer,lifetime);
                reason="continuous_permanent_evidence_cut";return true;
            }
        }
        // Called AFTER full native summaries, authored document and scene/pose getters. Getters
        // may reenter ordinary code: a pre-read proof pin is not a post-read source certificate.
        internal void RequireContinuousProofCutSource(ContinuousSafeEndTick ticket,ContinuousRuntimeProofCut cut)
        {
            if(System.Threading.Monitor.IsEntered(CommonParticipantGate.SyncRoot))
                throw new InvalidOperationException("continuous_postread_source_inside_gate");
            RequireContinuousClosedCut(ticket);
            var runtime=_continuousAuxiliaryRuntime;var coordinator=_componentDomain;
            if(cut==null||runtime==null||coordinator==null||!cut.Lifetime.IsCurrent||
                !ReferenceEquals(cut.Lifetime,_continuousWorldCohort._outputLifetime)||
                !ReferenceEquals(cut.CanonicalBefore,coordinator.CurrentSourceIdentity)||
                !cut.Evidence.IsProducer(runtime)||!ReferenceEquals(cut.History,_continuousWorkHistory)||
                !ReferenceEquals(cut.Evidence.SourceHistory,_continuousWorkHistory))
                throw new InvalidOperationException("continuous_postread_source_changed");
            var canonical=cut.CanonicalBefore.GetSummary();var hash=OwnerHashContext(canonical);
            var actual=ReadComponentParticipants(PaidState(canonical.DeepCopy()),hash);
            if(!PaidEqual(hash,canonical.Get("participating_state"),actual))
                throw new InvalidOperationException("continuous_postread_participant_drift");
            using(var attempt=CommonParticipantGate.BeginAttempt())
            {
                RequireContinuousClosedCut(ticket);
                if(!ReferenceEquals(runtime,_continuousAuxiliaryRuntime)||!ReferenceEquals(coordinator,_componentDomain)||
                    !ReferenceEquals(cut.CanonicalBefore,coordinator.CurrentSourceIdentity)||
                    !ReferenceEquals(cut.History,_continuousWorkHistory)||!cut.History.ResourcesCurrent||!cut.Lifetime.IsCurrent||
                    !ReferenceEquals(cut.Lifetime,_continuousWorldCohort._outputLifetime)||
                    !cut.Evidence.IsProducer(runtime)||runtime.EvidenceJournal.StructuralVersion!=cut.Evidence.StructuralVersion)
                    throw new InvalidOperationException("continuous_postread_source_changed");
            }
        }
        // No live overload accepts an arbitrary admitted Result. Worker preparation consumes only
        // the privately issued ContinuousRuntimeProofCut through OwnedContinuousCapture.
    }
}
