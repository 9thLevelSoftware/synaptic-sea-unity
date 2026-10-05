using System;
namespace SynapticSea.Core.Systems
{
    // Diagnostic DTO accounting only; no admission or owner format authority.
    internal readonly struct AuxReplayBudgetReport
    {
        internal readonly long SourceNodes, WireNodes, Utf8Bytes, Chunks, Steps, InspectedStepRecords;
        internal readonly int WireDepth;
        internal readonly bool ConservativeBytes;
        internal AuxReplayBudgetReport(long source, long wire, long bytes, long chunks, long steps, long inspected, int depth, bool conservative=false)
        { SourceNodes=source; WireNodes=wire; Utf8Bytes=bytes; Chunks=chunks; Steps=steps; InspectedStepRecords=inspected; WireDepth=depth; ConservativeBytes=conservative; }
    }
    internal static class AuxReplayCodecBudget
    {
        internal const long MaximumSourceNodes=100000, MaximumWireNodes=549999, MaximumUtf8Bytes=4L*1024*1024;
        internal const int MaximumWireDepth=386;
        // Tagged codec-v2 scalar JSON: text11+escaped UTF8; real27; integer14+decimal digits.
        static long Pair(string key, long valueBytes) => checked(15+key.Length+valueBytes);
        static int DecimalLength(long value)
        {
            ulong magnitude=value<0 ? unchecked((ulong)(-(value+1)))+1UL : (ulong)value;
            int n=value<0 ? 2 : 1; while(magnitude>=10) { magnitude/=10; n++; } return n;
        }
        static long Integer(long value) => 14+DecimalLength(value);
        static bool Text(string value, out long bytes)
        {
            bytes=0;
            if(value==null || value.Length>256 || string.IsNullOrWhiteSpace(value)) return false;
            for(int i=0;i<value.Length;i++)
            {
                char c=value[i];
                if(c=='"'||c=='\\'||c=='\b'||c=='\t'||c=='\n'||c=='\f'||c=='\r') bytes+=2;
                else if(c<32) bytes+=6;
                else if(c<128) bytes++;
                else if(c<2048) bytes+=2;
                else if(char.IsHighSurrogate(c)) { if(++i>=value.Length || !char.IsLowSurrogate(value[i])) return false; bytes+=4; }
                else if(char.IsLowSurrogate(c)) return false;
                else bytes+=3;
            }
            bytes+=11; return true;
        }
        internal static bool TrySeed(UntrustedAuxReplaySeed seed, out AuxReplayBudgetReport report)
        {
            report=default;
            if(seed==null || !Text(seed.Run,out long run) || !Text(seed.Actor,out long actor) || !Text(seed.Service,out long service)) return false;
            long seedBytes=16+Pair("run",run)+Pair("actor",actor)+Pair("service",service)+Pair("origin_digest",75)+
                Pair("duration",27)+Pair("progress",27)+Pair("eligible",27)+Pair("accepted_steps",Integer(seed.AcceptedSteps));
            // Raw envelope47 + root dictionary56 + chunks array12. Empty wire depth8.
            report=new AuxReplayBudgetReport(21,77,checked(47+56+seedBytes+12),0,0,0,8); return Fits(report);
        }
        internal static bool Fits(AuxReplayBudgetReport r, int maxChunks=256, int maxSteps=65536)
            => r.SourceNodes>=0 && r.WireNodes>=0 && r.Utf8Bytes>=0 && r.Chunks>=0 && r.Steps>=0 && r.WireDepth>=0 &&
                r.SourceNodes<=MaximumSourceNodes && r.WireNodes<=MaximumWireNodes && r.Utf8Bytes<=MaximumUtf8Bytes && r.WireDepth<=MaximumWireDepth &&
                r.Chunks<=256 && r.Chunks<=maxChunks && r.Steps<=65536 && r.Steps<=maxSteps;
        static long HeaderBytes(long ordinal, long initial, long before, long after)
            => 16+Pair("chunk_version",15)+Pair("ordinal",Integer(ordinal))+Pair("initial_step_sequence",Integer(initial))+
                Pair("accepted_steps_before",Integer(before))+Pair("accepted_steps_after",Integer(after))+
                Pair("previous_chunk_digest",75)+Pair("digest",75)+Pair("progress_before",27)+Pair("eligible_before",27)+
                Pair("progress_after",27)+Pair("eligible_after",27)+Pair("steps",12);
        internal static long StepBytes(long sequence)
            => 16+Pair("sequence",Integer(sequence))+Pair("delta_seconds",27)+Pair("stamina_before",27)+Pair("max_stamina",27)+
                Pair("wound_work_multiplier",27)+Pair("ratio",27)+Pair("speed",27)+Pair("remaining_before",27)+
                Pair("elapsed_seconds",27)+Pair("delta_progress",27)+Pair("stamina_after",27)+Pair("progress_after",27)+Pair("eligible_after",27);
        internal static bool TryAppendHeader(AuxReplayBudgetReport r,long ordinal,long initial,long before,long after,out AuxReplayBudgetReport next)
        {
            next=default;
            try { next=new AuxReplayBudgetReport(checked(r.SourceNodes+25),checked(r.WireNodes+87),checked(r.Utf8Bytes+HeaderBytes(ordinal,initial,before,after)+(r.Chunks==0?0:1)),checked(r.Chunks+1),r.Steps,r.InspectedStepRecords,Math.Max(r.WireDepth,10),r.ConservativeBytes); return Fits(next); }
            catch(OverflowException) { return false; }
        }
        // Reserve a future active chunk's final counter without rescanning/correcting its prefix.
        internal static bool TryAppendReservedHeader(AuxReplayBudgetReport r,long ordinal,long initial,long before,out AuxReplayBudgetReport next)
        {
            if(!TryAppendHeader(r,ordinal,initial,before,long.MinValue,out next)) return false;
            next=new AuxReplayBudgetReport(next.SourceNodes,next.WireNodes,next.Utf8Bytes,next.Chunks,next.Steps,next.InspectedStepRecords,next.WireDepth,true); return true;
        }
        internal static bool TryAppendStep(AuxReplayBudgetReport r,long sequence,int precedingStepsInChunk,out AuxReplayBudgetReport next)
        {
            next=default; if(precedingStepsInChunk<0 || precedingStepsInChunk>=256 || r.Chunks<1) return false;
            try { next=new AuxReplayBudgetReport(checked(r.SourceNodes+27),checked(r.WireNodes+94),checked(r.Utf8Bytes+StepBytes(sequence)+(precedingStepsInChunk==0?0:1)),r.Chunks,checked(r.Steps+1),checked(r.InspectedStepRecords+1),Math.Max(r.WireDepth,15),r.ConservativeBytes); return Fits(next); }
            catch(OverflowException) { return false; }
        }
        // Preparation-only bounded scan. Runtime append/report uses the helpers above, never this scan.
        internal static bool TryMeasure(UntrustedAuxReplaySeed seed,UntrustedAuxEvidenceChunk[] chunks,out AuxReplayBudgetReport report)
        {
            report=default; if(!TrySeed(seed,out var current)||chunks==null||chunks.Length>256) return false;
            foreach(var c in chunks)
            {
                if(c==null||!TryAppendHeader(current,c.Ordinal,c.InitialSequence,c.AcceptedBefore,c.AcceptedAfter,out current)) return false;
                for(int i=0;i<c.Count;i++) if(!TryAppendStep(current,c.At(i).Sequence,i,out current)) return false;
            }
            report=current; return true;
        }
    }
}
