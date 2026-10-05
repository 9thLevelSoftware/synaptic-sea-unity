using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;

namespace SynapticSea.Core.Systems
{
    // Closed ASCII proof DTO encoder; not origin authority or candidate admission.
    internal sealed class UntrustedAuxChunkDigestCursor : IDisposable
    {
        static readonly string[] ChunkFields = { "accepted_steps_after", "accepted_steps_before", "chunk_version", "eligible_after", "eligible_before", "initial_step_sequence", "ordinal", "previous_chunk_digest", "progress_after", "progress_before", "steps" };
        static readonly string[] StepFields = { "delta_progress", "delta_seconds", "elapsed_seconds", "eligible_after", "max_stamina", "progress_after", "ratio", "remaining_before", "sequence", "speed", "stamina_after", "stamina_before", "wound_work_multiplier" };
        readonly IEnumerator<string> _tokens;
        readonly SHA256 _sha;
        readonly byte[] _buffer = new byte[256];
        string _token;
        int _offset, _bufferCount, _hashedOffset;
        bool _end, _terminal, _disposed;
        internal string Reason { get; private set; }
        internal long OutputBytes { get; private set; }
        internal long HashedBytes { get; private set; }
        internal int SourceNodes { get; }
        internal UntrustedChunkDigestResult Result { get; private set; }
        internal bool AwaitingFinalization => !_terminal && _end && _bufferCount == _hashedOffset;
        UntrustedAuxChunkDigestCursor(UntrustedAuxEvidenceChunk chunk)
        { _tokens = Tokens(chunk).GetEnumerator(); _sha = SHA256.Create(); SourceNodes = checked(25 + 27 * chunk.Count); }
        static bool Digest(string s)
        { if (s == null || s.Length != 64) return false; foreach (char c in s) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false; return true; }
        internal static bool Begin(UntrustedAuxEvidenceChunk chunk, out UntrustedAuxChunkDigestCursor cursor, out string reason)
        {
            cursor = null; reason = "invalid_chunk";
            if (chunk == null || chunk.Count < 1 || chunk.Count > 256 || !Digest(chunk.PreviousDigest)) return false;
            // At most256 fixed13-field steps; no graph traversal/copy/string allocation.
            cursor = new UntrustedAuxChunkDigestCursor(chunk); reason = ""; return true;
        }
        static string Integer(long n) => "[\"integer\",\"" + n.ToString(CultureInfo.InvariantCulture) + "\"]";
        static string Real(double n)
        {
            if (double.IsNaN(n) || double.IsInfinity(n)) throw new ArgumentException("nonfinite_real");
            return "[\"real\",\"" + unchecked((ulong)BitConverter.DoubleToInt64Bits(n)).ToString("x16", CultureInfo.InvariantCulture) + "\"]";
        }
        static string Text(string text) => "[\"text\",\"" + text + "\"]"; // Only fixed ASCII or validated64hex reaches this method.
        static IEnumerable<string> Tokens(UntrustedAuxEvidenceChunk c)
        {
            yield return "{\"schema\":\"component_domain_codec_v2\",\"value\":[\"dictionary\",[[[\"text\",\"value\"],[\"dictionary\",[";
            for (int f = 0; f < ChunkFields.Length; f++)
            {
                if (f != 0) yield return ",";
                yield return "["; yield return Text(ChunkFields[f]); yield return ",";
                switch (f)
                {
                    case 0: yield return Integer(c.AcceptedAfter); break;
                    case 1: yield return Integer(c.AcceptedBefore); break;
                    case 2: yield return Integer(1); break;
                    case 3: yield return Real(c.EligibleAfter); break;
                    case 4: yield return Real(c.EligibleBefore); break;
                    case 5: yield return Integer(c.InitialSequence); break;
                    case 6: yield return Integer(c.Ordinal); break;
                    case 7: yield return Text(c.PreviousDigest); break;
                    case 8: yield return Real(c.ProgressAfter); break;
                    case 9: yield return Real(c.ProgressBefore); break;
                    case 10:
                        yield return "[\"array\",[";
                        for (int i = 0; i < c.Count; i++)
                        {
                            if (i != 0) yield return ",";
                            yield return "[\"dictionary\",[";
                            var s = c.At(i);
                            for (int k = 0; k < StepFields.Length; k++)
                            {
                                if (k != 0) yield return ",";
                                yield return "["; yield return Text(StepFields[k]); yield return ",";
                                switch (k)
                                {
                                    case 0: yield return Real(s.DeltaProgress); break;
                                    case 1: yield return Real(s.Delta); break;
                                    case 2: yield return Real(s.Elapsed); break;
                                    case 3: yield return Real(s.EligibleAfter); break;
                                    case 4: yield return Real(s.MaxStamina); break;
                                    case 5: yield return Real(s.ProgressAfter); break;
                                    case 6: yield return Real(s.Ratio); break;
                                    case 7: yield return Real(s.Remaining); break;
                                    case 8: yield return Integer(s.Sequence); break;
                                    case 9: yield return Real(s.Speed); break;
                                    case 10: yield return Real(s.StaminaAfter); break;
                                    case 11: yield return Real(s.StaminaBefore); break;
                                    case 12: yield return Real(s.Wound); break;
                                }
                                yield return "]";
                            }
                            yield return "]]";
                        }
                        yield return "]]"; break;
                }
                yield return "]";
            }
            yield return "]]]]]}";
        }
        internal static string[] FieldOrderForTests(bool steps) => (string[])(steps ? StepFields : ChunkFields).Clone();
        internal static IEnumerable<string> DiagnosticTokens(UntrustedAuxEvidenceChunk chunk) => Tokens(chunk);
        internal DigestCursorStatus Step(int maxTokens, int maxOutputBytes, int maxHashBytes, bool allowFinalize)
        {
            if (_terminal) return Result == null ? DigestCursorStatus.Rejected : DigestCursorStatus.UntrustedResult;
            if (maxTokens < 0 || maxTokens > 1024 || maxOutputBytes < 0 || maxOutputBytes > 4096 || maxHashBytes < 0 || maxHashBytes > 4096)
                return Reject("invalid_budget");
            int tokens = 0, output = 0, hashed = 0;
            try
            {
                while (true)
                {
                    if (_bufferCount == _buffer.Length || _end)
                    {
                        int feed = Math.Min(_bufferCount - _hashedOffset, maxHashBytes - hashed);
                        if (feed > 0) { _sha.TransformBlock(_buffer, _hashedOffset, feed, _buffer, _hashedOffset); _hashedOffset += feed; hashed += feed; HashedBytes += feed; }
                        if (_hashedOffset < _bufferCount) return DigestCursorStatus.Pending;
                        _bufferCount = _hashedOffset = 0;
                        if (_end)
                        {
                            // Final SHA padding is <=2 compression blocks; requires explicit allowance.
                            if (!allowFinalize || maxHashBytes - hashed < 128) return DigestCursorStatus.Pending;
                            _sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            var bytes = _sha.Hash; char[] hex = new char[64]; const string digits = "0123456789abcdef";
                            for (int i = 0; i < 32; i++) { hex[i * 2] = digits[bytes[i] >> 4]; hex[i * 2 + 1] = digits[bytes[i] & 15]; }
                            Result = new UntrustedChunkDigestResult(new string(hex), OutputBytes, SourceNodes); _terminal = true; Dispose(); return DigestCursorStatus.UntrustedResult;
                        }
                    }
                    if (_token == null)
                    {
                        if (tokens == maxTokens) return DigestCursorStatus.Pending;
                        tokens++;
                        if (!_tokens.MoveNext()) { _end = true; continue; }
                        _token = _tokens.Current; _offset = 0;
                    }
                    if (output == maxOutputBytes) return DigestCursorStatus.Pending;
                    int n = Math.Min(_token.Length - _offset, Math.Min(maxOutputBytes - output, _buffer.Length - _bufferCount));
                    for (int i = 0; i < n; i++) { char c = _token[_offset++]; if (c > 127) return Reject("nonascii_token"); _buffer[_bufferCount++] = (byte)c; }
                    output += n; OutputBytes += n;
                    if (OutputBytes > 4 * 1024 * 1024) return Reject("byte_bound");
                    if (_offset == _token.Length) _token = null;
                }
            }
            catch (ArgumentException e) { return Reject(e.Message); }
        }
        DigestCursorStatus Reject(string reason) { Reason = reason; _terminal = true; Dispose(); return DigestCursorStatus.Rejected; }
        internal void Cancel() { if (!_terminal) Reject("cancelled"); }
        public void Dispose() { if (_disposed) return; _disposed = true; _tokens.Dispose(); _sha.Dispose(); Array.Clear(_buffer, 0, _buffer.Length); if (!_terminal) { _terminal = true; Reason = "disposed"; } }
    }
    internal enum DigestCursorStatus { Pending, Rejected, UntrustedResult }
    internal sealed class UntrustedChunkDigestResult
    {
        internal readonly string Digest;
        internal readonly long Bytes;
        internal readonly int SourceNodes;
        internal UntrustedChunkDigestResult(string digest, long bytes, int nodes) { Digest = digest; Bytes = bytes; SourceNodes = nodes; }
    }
}
