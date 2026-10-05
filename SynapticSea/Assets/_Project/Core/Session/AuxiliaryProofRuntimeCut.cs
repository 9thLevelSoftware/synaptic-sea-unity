using System;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    public sealed partial class AuxiliaryWorkRuntime
    {
        // Transport evidence only: the session must separately bind this pin to its world-cut ticket.
        internal EvidenceExportPin PinInstalledEvidenceCutUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            if(_evidenceJournal==null||_count!=0||_pendingEvidence!=null)throw new InvalidOperationException("evidence_cut_not_installed");
            if(_admittedHistory==null||!_admittedHistory.ResourcesCurrent)throw new InvalidOperationException("admitted_history_required");
            return _evidenceJournal.PinForExportUnderGate(this,View(_state));
        }
        internal sealed class EvidenceExportPin
        {
            readonly AuxiliarySealedEvidenceChunk[] _prefix;
            readonly object _producerIdentity;
            internal readonly int ChunkCount;
            internal readonly ulong StructuralVersion;
            internal readonly AuxiliaryWorkSnapshot Work;
            internal readonly AdmittedAuxiliaryHistory SourceHistory;
            internal readonly string RunId,ActorId,ServiceId,Algorithm;
            internal EvidenceExportPin(AuxiliaryWorkRuntime producer,object issuer,AuxiliarySealedEvidenceChunk[] prefix,int count,ulong version,AuxiliaryWorkSnapshot work)
            {if(producer==null||!producer.IsEvidenceIssuer(issuer))throw new ArgumentException("invalid_evidence_issuer");_producerIdentity=issuer;RunId=producer.RunId;ActorId=producer.ActorId;ServiceId=producer.ServiceId;Algorithm=producer.HashAlgorithm;SourceHistory=producer._admittedHistory;_prefix=prefix;ChunkCount=count;StructuralVersion=version;Work=work;}
            internal AuxiliarySealedEvidenceChunk ChunkAt(int index)
            {if(index<0||index>=ChunkCount)throw new ArgumentOutOfRangeException(nameof(index));return _prefix[index];}
            internal bool IsProducer(AuxiliaryWorkRuntime producer)=>producer!=null&&producer.IsEvidenceIssuer(_producerIdentity);
        }
    }
    internal sealed partial class AuxiliaryEvidenceJournal
    {
        internal AuxiliaryWorkRuntime.EvidenceExportPin PinForExportUnderGate(AuxiliaryWorkRuntime producer,AuxiliaryWorkSnapshot work)
        {
            CommonParticipantGate.RequireHeld();
            if(!ReferenceEquals(producer,_producer)||_historyCount!=_sealedCount||_lastCustodySequence!=work.EligibleSteps||!Bits(_custodyProgress,work.ProgressSeconds)||!Bits(_custodyEligible,work.EligibleSeconds))throw new InvalidOperationException("incomplete_evidence_custody");
            // Existing prefix slots are append-only; retaining the array and fixed count is O(1).
            return new AuxiliaryWorkRuntime.EvidenceExportPin(producer,_epoch,_history,_historyCount,StructuralVersion,work);
        }
    }
}
