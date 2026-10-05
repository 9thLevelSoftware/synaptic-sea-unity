using System;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    public sealed partial class AuxiliaryWorkRuntime
    {
        AdmittedAuxiliaryHistory _admittedHistory;
        internal int AdmittedPrefixChunkCount=>_admittedHistory?.ChunkCount??0;
        internal UntrustedAuxEvidenceChunk AdmittedPrefixChunkAt(int index)=>_admittedHistory.ChunkAt(index);
        internal string OriginalOriginDigest=>_admittedHistory?.OriginalSeed.OriginDigest;
        internal string AdmittedPrefixDigest=>_admittedHistory?.LatestDigest;
        internal string WorkLineageId=>_admittedHistory?.WorkLineageId;
        // Load/bootstrap-only full history reconstruction, never per-frame and never a stamina replay.
        // The privately issued capability already contains full original zero seed and admitted prefix.
        internal static AuxiliaryWorkRuntime CreateFromAdmittedRunSessionHistory(string owner,string algorithm,
            AdmittedAuxiliaryHistory history,int capacity,AuxiliaryEvidenceLimits limits,
            ActualVitalsProjectionBridge vitals,DiagnosticAuxiliaryPairContext context)
        {
            if(history==null||!history.ResourcesCurrent||history.OriginalSeed.Progress!=0||history.OriginalSeed.Eligible!=0||
                history.OriginalSeed.AcceptedSteps!=0||history.AcceptedSteps>int.MaxValue||history.ChunkCount>limits.MaximumChunks)
                throw new ArgumentException("invalid_admitted_auxiliary_history");
            var encoded=new UntrustedAuxEvidenceChunk[history.ChunkCount];
            for(int i=0;i<encoded.Length;i++)encoded[i]=history.ChunkAt(i);
            if(!AuxReplayCodecBudget.TryMeasure(history.OriginalSeed,encoded,out var budget)||!AuxReplayCodecBudget.Fits(budget,limits.MaximumChunks,limits.MaximumSteps))
                throw new ArgumentException("admitted_history_capacity");
            var runtime=CreateRunSessionVitalsPaired(owner,history.StartOwnerRevision,algorithm,history.OriginalSeed,capacity,limits,vitals,context);
            var rebuilt=new AuxiliarySealedEvidenceChunk[encoded.Length];
            for(int c=0;c<encoded.Length;c++)
            {
                var chunk=encoded[c];var steps=new AuxiliaryAcceptedStep[chunk.Count];double progress=chunk.ProgressBefore,eligible=chunk.EligibleBefore;
                for(int i=0;i<steps.Length;i++)
                {
                    var s=chunk.At(i);
                    steps[i]=new AuxiliaryAcceptedStep(s.Sequence,s.Delta,s.MaxStamina,s.Wound,progress,s.ProgressAfter,
                        eligible,s.EligibleAfter,s.DeltaProgress,s.Elapsed,s.Speed,s.StaminaBefore,s.StaminaAfter);
                    progress=s.ProgressAfter;eligible=s.EligibleAfter;
                }
                rebuilt[c]=new AuxiliarySealedEvidenceChunk(runtime,runtime._evidenceEpoch,c,steps,steps.Length);
            }
            var actual=vitals.Read();
            if(!history.ResourcesCurrent)throw new InvalidOperationException("admitted_history_stale");
            // Unexposed fresh runtime only. Current restored actual stamina is authoritative;
            // accepted historical stamina deltas are retained as proof, never applied again.
            runtime._state=new State(history.Progress,history.Eligible,actual.Values.Stamina,history.AcceptedSteps);
            runtime._runSessionPair.Budget=new RunSessionBudget(budget);
            runtime._evidenceJournal.ImportAdmittedBirth(rebuilt,history);
            runtime._admittedHistory=history;return runtime;
        }
    }
    internal sealed partial class AuxiliaryEvidenceJournal
    {
        internal void ImportAdmittedBirth(AuxiliarySealedEvidenceChunk[] prefix,AdmittedAuxiliaryHistory history)
        {
            if(history==null||!history.ResourcesCurrent||prefix.Length!=history.ChunkCount||prefix.Length>_history.Length||
                _historyCount!=0||_acceptedCount!=0||_sealedCount!=0||_nextQueued!=0)
                throw new InvalidOperationException("history_import_requires_unexposed_birth");
            for(int i=0;i<prefix.Length;i++)_history[i]=prefix[i];
            _historyCount=prefix.Length;_sealedCount=prefix.Length;_nextQueued=prefix.Length;
            _acceptedCount=checked((int)history.AcceptedSteps);_lastCustodySequence=history.AcceptedSteps;
            _custodyProgress=history.Progress;_custodyEligible=history.Eligible;
            // Already admitted immutable history is not a fresh transport queue or fabricated ACK.
            // No durable deletion authority is issued, and original digest records remain retained.
        }
    }
}
