using System;

namespace SynapticSea.Core.Systems
{
    // UNWIRED arithmetic scaffold. No origin authority, admission, receipt or live model writes.
    internal sealed class UntrustedAuxReplaySeed
    {
        internal readonly string Run, Actor, Service, OriginDigest;
        internal readonly double Duration, Progress, Eligible;
        internal readonly long AcceptedSteps;
        internal UntrustedAuxReplaySeed(string run, string actor, string service, string originDigest,
            double duration, double progress, double eligible, long acceptedSteps)
        { Run = run; Actor = actor; Service = service; OriginDigest = originDigest; Duration = duration; Progress = progress; Eligible = eligible; AcceptedSteps = acceptedSteps; }
    }
    // Proof DTO; deliberately distinct from the runtime's accepted-step object.
    internal readonly struct UntrustedAuxEvidenceStep
    {
        internal readonly long Sequence;
        internal readonly double Delta, StaminaBefore, MaxStamina, Wound, Ratio, Speed, Remaining,
            Elapsed, DeltaProgress, StaminaAfter, ProgressAfter, EligibleAfter;
        internal UntrustedAuxEvidenceStep(long sequence, double delta, double stamina, double maximum, double wound,
            double ratio, double speed, double remaining, double elapsed, double advance, double after, double progress, double eligible)
        { Sequence = sequence; Delta = delta; StaminaBefore = stamina; MaxStamina = maximum; Wound = wound;
            Ratio = ratio; Speed = speed; Remaining = remaining; Elapsed = elapsed; DeltaProgress = advance;
            StaminaAfter = after; ProgressAfter = progress; EligibleAfter = eligible; }
    }
    internal sealed class UntrustedAuxEvidenceChunk
    {
        internal readonly long Ordinal, InitialSequence, AcceptedBefore, AcceptedAfter;
        internal readonly string PreviousDigest, Digest;
        internal readonly double ProgressBefore, EligibleBefore, ProgressAfter, EligibleAfter;
        readonly UntrustedAuxEvidenceStep[] _steps;
        internal int Count => _steps.Length;
        internal UntrustedAuxEvidenceStep At(int index) => _steps[index];
        internal UntrustedAuxEvidenceChunk(long ordinal, string previous, string digest, long initialSequence,
            double progressBefore, double eligibleBefore, long acceptedBefore, double progressAfter, double eligibleAfter,
            long acceptedAfter, UntrustedAuxEvidenceStep[] steps)
        {
            if (steps == null || steps.Length < 1 || steps.Length > 256) throw new ArgumentException("step_bound");
            Ordinal = ordinal; PreviousDigest = previous; Digest = digest; InitialSequence = initialSequence;
            ProgressBefore = progressBefore; EligibleBefore = eligibleBefore; AcceptedBefore = acceptedBefore;
            ProgressAfter = progressAfter; EligibleAfter = eligibleAfter; AcceptedAfter = acceptedAfter;
            _steps = (UntrustedAuxEvidenceStep[])steps.Clone();
        }
    }
    internal enum AuxReplayStatus { Pending, Rejected, UntrustedResult }
    internal sealed class UntrustedReplayResult
    {
        internal readonly double Progress, Eligible;
        internal readonly long AcceptedSteps;
        internal bool ArithmeticMatched => true;
        internal readonly int MatchedChunkDigestCount, TotalChunkCount;
        internal UntrustedReplayResult(double progress, double eligible, long steps, int matched, int total)
        { Progress = progress; Eligible = eligible; AcceptedSteps = steps; MatchedChunkDigestCount = matched; TotalChunkCount = total; }
    }
    internal sealed class AuxEvidenceReplayCursor
    {
        internal const int MaximumNodes = 100000, MaximumBytes = 4 * 1024 * 1024;
        internal AuxReplayBudgetReport Budget { get; private set; }
        readonly UntrustedAuxReplaySeed _seed;
        readonly UntrustedAuxEvidenceChunk[] _chunks;
        int _chunk, _step;
        double _progress, _eligible;
        long _accepted;
        string _previous;
        bool _terminal;
        UntrustedAuxChunkDigestCursor _digest;
        internal long ReplayedStepCount { get; private set; }
        internal long DigestOutputBytes { get; private set; }
        internal long DigestHashBytes { get; private set; }
        internal bool DigestAwaitingFinalization => _digest?.AwaitingFinalization == true;
        internal string Reason { get; private set; }
        internal UntrustedReplayResult ArithmeticResult { get; private set; }
        AuxEvidenceReplayCursor(UntrustedAuxReplaySeed seed, UntrustedAuxEvidenceChunk[] chunks)
        { _seed = seed; _chunks = chunks; _progress = seed.Progress; _eligible = seed.Eligible; _accepted = seed.AcceptedSteps; _previous = seed.OriginDigest; }
        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        static bool Bits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
        static bool Identifier(string s) => s != null && s.Length <= 256 && !string.IsNullOrWhiteSpace(s);
        static bool Digest(string s)
        { if (s == null || s.Length != 64) return false; foreach (char c in s) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false; return true; }
        internal static bool Begin(UntrustedAuxReplaySeed seed, UntrustedAuxEvidenceChunk[] chunks, out AuxEvidenceReplayCursor cursor, out string reason)
        {
            cursor = null; reason = "invalid_seed";
            if (seed == null || !Identifier(seed.Run) || !Identifier(seed.Actor) || !Identifier(seed.Service) || !Digest(seed.OriginDigest) ||
                !(seed.Duration == 8 || seed.Duration == 12) || !Finite(seed.Progress) || seed.Progress < 0 || seed.Progress > seed.Duration ||
                !Finite(seed.Eligible) || seed.Eligible < seed.Progress || seed.AcceptedSteps < 0) return false;
            reason = "chunk_bound";
            if (chunks == null || chunks.Length > 256) return false;
            long count = 0;
            foreach (var c in chunks)
            {
                if (c == null || !Digest(c.PreviousDigest) || !Digest(c.Digest)) return false;
                count += c.Count;
            }
            if (count > 65536 || !AuxReplayCodecBudget.TryMeasure(seed,chunks,out var budget)) { reason = "codec_capacity_bound"; return false; }
            // Copy only bounded references to immutable DTOs; source arrays never retained.
            cursor = new AuxEvidenceReplayCursor(seed, (UntrustedAuxEvidenceChunk[])chunks.Clone()) { Budget = budget }; reason = ""; return true;
        }
        AuxReplayStatus Reject(string reason) { _digest?.Dispose(); _digest = null; _terminal = true; Reason = reason; return AuxReplayStatus.Rejected; }
        internal void Cancel() { if (!_terminal) Reject("cancelled"); }
        internal AuxReplayStatus Step(int maxSteps, int maxDigestTokens, int maxDigestOutputBytes, int maxDigestHashBytes, bool allowDigestFinalize)
        {
            if (_terminal) return ArithmeticResult == null ? AuxReplayStatus.Rejected : AuxReplayStatus.UntrustedResult;
            if (maxSteps < 0 || maxSteps > 256 || maxDigestTokens < 0 || maxDigestTokens > 1024 ||
                maxDigestOutputBytes < 0 || maxDigestOutputBytes > 4096 || maxDigestHashBytes < 0 || maxDigestHashBytes > 4096) return Reject("invalid_budget");
            if (_digest != null)
            {
                long beforeOutput = _digest.OutputBytes, beforeHash = _digest.HashedBytes;
                var status = _digest.Step(maxDigestTokens, maxDigestOutputBytes, maxDigestHashBytes, allowDigestFinalize);
                DigestOutputBytes += _digest.OutputBytes - beforeOutput; DigestHashBytes += _digest.HashedBytes - beforeHash;
                if (status == DigestCursorStatus.Rejected) return Reject("chunk_digest:" + _digest.Reason);
                if (status == DigestCursorStatus.Pending) return AuxReplayStatus.Pending;
                string actual = _digest.Result.Digest;
                if (actual != _chunks[_chunk].Digest) return Reject("chunk_digest_mismatch");
                _previous = actual; _digest.Dispose(); _digest = null; _step = 0; _chunk++;
                // Never replay the next chunk in the call that finishes a digest.
                if (_chunk != _chunks.Length) return AuxReplayStatus.Pending;
                return Finish();
            }
            if (_chunk == _chunks.Length) return Finish();
            if (maxSteps == 0) return AuxReplayStatus.Pending;
            int consumed = 0;
            while (_chunk < _chunks.Length && consumed < maxSteps)
            {
                var c = _chunks[_chunk];
                if (_step == 0)
                {
                    if (_accepted == long.MaxValue) return Reject("sequence_overflow");
                    if (c.Ordinal != _chunk || c.PreviousDigest != _previous || c.InitialSequence != _accepted + 1 ||
                        c.AcceptedBefore != _accepted || !Bits(c.ProgressBefore, _progress) || !Bits(c.EligibleBefore, _eligible)) return Reject("chunk_continuity");
                }
                var s = c.At(_step);
                if (_accepted == long.MaxValue) return Reject("sequence_overflow");
                if (s.Sequence != _accepted + 1) return Reject("step_sequence");
                if (!Finite(s.Delta) || s.Delta <= 0 || !Finite(s.StaminaBefore) || s.StaminaBefore < 0 || !Finite(s.MaxStamina) || s.MaxStamina <= 0 ||
                    !Finite(s.Wound) || s.Wound <= 0 || s.Wound > 1 || _progress >= _seed.Duration) return Reject("invalid_step_input");
                double ratio = Math.Max(0, Math.Min(1, s.StaminaBefore / Math.Max(1, s.MaxStamina)));
                double speed = s.Wound * (.35 + .65 * ratio), remaining = _seed.Duration - _progress;
                double elapsed = Math.Min(s.Delta, Math.Min(remaining / speed, s.StaminaBefore / 8));
                double advance = Math.Min(remaining, elapsed * speed), after = Math.Max(0, s.StaminaBefore - 8 * elapsed);
                double progress = Math.Min(_seed.Duration, _progress + advance), eligible = _eligible + elapsed;
                if (!Finite(speed) || !Finite(elapsed) || elapsed <= 0 || !Finite(advance) || advance <= 0 || !Finite(progress) || !Finite(eligible) ||
                    !Bits(s.Ratio, ratio) || !Bits(s.Speed, speed) || !Bits(s.Remaining, remaining) || !Bits(s.Elapsed, elapsed) ||
                    !Bits(s.DeltaProgress, advance) || !Bits(s.StaminaAfter, after) || !Bits(s.ProgressAfter, progress) || !Bits(s.EligibleAfter, eligible)) return Reject("arithmetic_mismatch");
                _progress = progress; _eligible = eligible; _accepted = checked(_accepted + 1); _step++; consumed++; ReplayedStepCount++;
                if (_step == c.Count)
                {
                    if (!Bits(c.ProgressAfter, _progress) || !Bits(c.EligibleAfter, _eligible) || c.AcceptedAfter != _accepted) return Reject("chunk_mirror");
                    if (!UntrustedAuxChunkDigestCursor.Begin(c, out _digest, out string reason)) return Reject("chunk_digest:" + reason);
                    // Digest begins in a later call; arithmetic budget never hides hashing work.
                    return AuxReplayStatus.Pending;
                }
            }
            return AuxReplayStatus.Pending;
        }
        AuxReplayStatus Finish()
        {
            ArithmeticResult = new UntrustedReplayResult(_progress, _eligible, _accepted, _chunk, _chunks.Length);
            _terminal = true; return AuxReplayStatus.UntrustedResult;
        }
    }
}
