using System;
using System.Collections.Generic;
using System.Threading;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    // Diagnostic-only closed data types. No live model, resource accessor, schema or storage authority.
    public enum SaveCutScalarKind { Null, Boolean, Int64, Float32, Float64, Text }
    public sealed class SaveCutScalar
    {
        public SaveCutScalarKind Kind { get; }
        public long Bits { get; }
        public string Text { get; }
        SaveCutScalar(SaveCutScalarKind kind, long bits, string text) { Kind = kind; Bits = bits; Text = text; }
        public static SaveCutScalar Null() => new SaveCutScalar(SaveCutScalarKind.Null, 0, null);
        public static SaveCutScalar Boolean(bool value) => new SaveCutScalar(SaveCutScalarKind.Boolean, value ? 1 : 0, null);
        public static SaveCutScalar Int64(long value) => new SaveCutScalar(SaveCutScalarKind.Int64, value, null);
        public static SaveCutScalar Float64(double value) => new SaveCutScalar(SaveCutScalarKind.Float64, BitConverter.DoubleToInt64Bits(value), null);
        public static SaveCutScalar Float32(float value) => new SaveCutScalar(SaveCutScalarKind.Float32, BitConverter.ToInt32(BitConverter.GetBytes(value), 0), null);
        public static SaveCutScalar String(string value)
        {
            if (value == null || value.Length > SaveCutChunk.MaximumTextLength) throw new ArgumentException("oversized_text_unit");
            return new SaveCutScalar(SaveCutScalarKind.Text, 0, value); // strings are immutable, bounded and never cloned on a cut
        }
    }
    public sealed class SaveCutChunk
    {
        public const int MaximumScalars = 32, MaximumChildren = 4, MaximumTextLength = 128;
        public const int MaximumTreeNodes = 32, MaximumDepth = 4;
        readonly SaveCutScalar[] _values;
        readonly SaveCutChunk[] _children;
        public string Label { get; }
        public int ValueCount => _values.Length;
        public int ChildCount => _children.Length;
        public int TreeNodes { get; }
        public int Depth { get; }
        public SaveCutScalar ValueAt(int index) => _values[index];
        public SaveCutChunk ChildAt(int index) => _children[index];
        // Build before entering the registry gate. Bounded arrays only; no caller accessor/delegate executes under a gate.
        public SaveCutChunk(string label, SaveCutScalar[] values, SaveCutChunk[] children = null)
        {
            if (label == null || label.Length > MaximumTextLength || values == null || values.Length > MaximumScalars ||
                children != null && children.Length > MaximumChildren) throw new ArgumentException("oversized_chunk_unit");
            int nodes = 1, depth = 1;
            foreach (var value in values) if (value == null) throw new ArgumentException("null_scalar");
            if (children != null) foreach (var child in children)
            {
                if (child == null) throw new ArgumentException("null_child");
                nodes += child.TreeNodes; depth = Math.Max(depth, child.Depth + 1);
            }
            if (nodes > MaximumTreeNodes || depth > MaximumDepth) throw new ArgumentException("oversized_chunk_tree");
            Label = label; _values = (SaveCutScalar[])values.Clone();
            _children = children == null ? Array.Empty<SaveCutChunk>() : (SaveCutChunk[])children.Clone();
            TreeNodes = nodes; Depth = depth;
        }
        internal SaveCutChunk CopyLeaf() => new SaveCutChunk(Label, _values);
    }
    public sealed class SaveCutRootUpdate
    {
        public string SourceId { get; }
        public SaveCutChunk Chunk { get; }
        public SaveCutRootUpdate(string sourceId, SaveCutChunk chunk)
        {
            if (sourceId == null || sourceId.Length > 128 || string.IsNullOrWhiteSpace(sourceId) || chunk == null) throw new ArgumentException("invalid_root");
            SourceId = sourceId; Chunk = chunk;
        }
    }
    public sealed class SaveCutRootVersion
    {
        public string SourceId { get; }
        public long Version { get; }
        public SaveCutChunk Chunk { get; }
        internal SaveCutRootVersion(string source, long version, SaveCutChunk chunk) { SourceId = source; Version = version; Chunk = chunk; }
    }
    public sealed class SaveCutPin
    {
        SaveCutRootVersion[] _roots;
        internal readonly SaveCutRegistry Registry;
        internal bool Released;
        public string SessionId { get; }
        public string RunId { get; }
        public string ResourceLeaseId { get; }
        public long WorldRevision { get; }
        public long CaptureSequence { get; }
        public int RootCount { get { lock (CommonParticipantGate.SyncRoot) return _roots.Length; } }
        public SaveCutRootVersion RootAt(int index)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (Released) throw new InvalidOperationException("pin_released");
                return _roots[index];
            }
        }
        internal void DropRoots() => _roots = Array.Empty<SaveCutRootVersion>();
        internal SaveCutPin(SaveCutRegistry registry, string session, string run, string lease, long revision, long sequence, SaveCutRootVersion[] roots)
        { Registry = registry; SessionId = session; RunId = run; ResourceLeaseId = lease; WorldRevision = revision; CaptureSequence = sequence; _roots = roots; }
        public bool IsReleased { get { lock (CommonParticipantGate.SyncRoot) return Released; } }
        public void Release() => Registry.Release(this);
    }
    /// <summary>Closed diagnostic mutation sequencer. A cut certifies only enrolled synthetic roots, never a production save.</summary>
    public sealed class SaveCutRegistry
    {
        public const int MaximumRoots = 16, MaximumPins = 4, MaximumRetainedNodes = 512;
        public const string TopologySourceId = "topology";
        readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        readonly string _session, _run, _lease;
        SaveCutRootVersion[] _roots = Array.Empty<SaveCutRootVersion>();
        readonly List<SaveCutPin> _pins = new List<SaveCutPin>();
        long _revision, _sequence;
        public SaveCutRegistry(string sessionId, string runId, string resourceLeaseId)
        {
            foreach (string id in new[] { sessionId, runId, resourceLeaseId })
                if (id == null || id.Length > 128 || string.IsNullOrWhiteSpace(id)) throw new ArgumentException("invalid_cut_identity");
            _session = sessionId; _run = runId; _lease = resourceLeaseId;
        }
        bool CorrectThread => Thread.CurrentThread.ManagedThreadId == _threadId;
        public long WorldRevision { get { lock (CommonParticipantGate.SyncRoot) return _revision; } }
        public int ActivePins { get { lock (CommonParticipantGate.SyncRoot) return _pins.Count; } }
        public bool TryReplaceRoots(long expectedRevision, SaveCutRootUpdate[] updates, out string reason)
        {
            reason = "";
            if (!CorrectThread) { reason = "wrong_mutation_thread"; return false; }
            if (updates == null || updates.Length == 0 || updates.Length > MaximumRoots) { reason = "invalid_transaction"; return false; }
            // Copy the bounded request before locking; immutable update/chunk objects contain no arbitrary accessors.
            var request = (SaveCutRootUpdate[])updates.Clone();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var update in request)
                if (update == null || !ids.Add(update.SourceId)) { reason = "invalid_transaction"; return false; }
            lock (CommonParticipantGate.SyncRoot)
            {
                if (expectedRevision != _revision) { reason = "revision_conflict"; return false; }
                if (_revision == long.MaxValue) { reason = "revision_overflow"; return false; }
                var next = new List<SaveCutRootVersion>(_roots);
                foreach (var update in request)
                {
                    int index = next.FindIndex(root => root.SourceId == update.SourceId);
                    if (index >= 0)
                    {
                        if (next[index].Version == long.MaxValue) { reason = "version_overflow"; return false; }
                        next[index] = new SaveCutRootVersion(update.SourceId, next[index].Version + 1, update.Chunk);
                    }
                    else next.Add(new SaveCutRootVersion(update.SourceId, 1, update.Chunk));
                }
                if (next.Count > MaximumRoots) { reason = "root_capacity"; return false; }
                var candidate = next.ToArray();
                if (RetainedNodes(candidate) > MaximumRetainedNodes) { reason = "retention_capacity"; return false; }
                _roots = candidate; _revision++; return true;
            }
        }
        int RetainedNodes(SaveCutRootVersion[] candidate)
        {
            // Conservative occurrence count: bounded and safe even when immutable subtrees are shared.
            int nodes = 0;
            foreach (var root in candidate) nodes += root.Chunk.TreeNodes;
            foreach (var pin in _pins) for (int i = 0; i < pin.RootCount; i++) nodes += pin.RootAt(i).Chunk.TreeNodes;
            return nodes;
        }
        public bool TryPinCut(out SaveCutPin pin, out string reason)
        {
            pin = null; reason = "";
            if (!CorrectThread) { reason = "wrong_mutation_thread"; return false; }
            lock (CommonParticipantGate.SyncRoot)
            {
                bool topology = false; foreach (var root in _roots) if (root.SourceId == TopologySourceId) topology = true;
                if (!topology) { reason = "topology_not_enrolled"; return false; }
                if (_pins.Count == MaximumPins) { reason = "pin_capacity"; return false; }
                if (_sequence == long.MaxValue) { reason = "capture_sequence_overflow"; return false; }
                int candidateNodes = 0; foreach (var root in _roots) candidateNodes += root.Chunk.TreeNodes;
                if (RetainedNodes(_roots) + candidateNodes > MaximumRetainedNodes) { reason = "retention_capacity"; return false; }
                pin = new SaveCutPin(this, _session, _run, _lease, _revision, ++_sequence, (SaveCutRootVersion[])_roots.Clone());
                _pins.Add(pin); return true;
            }
        }
        internal void Release(SaveCutPin pin)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!ReferenceEquals(pin.Registry, this) || pin.Released) return;
                _pins.Remove(pin); pin.Released = true; pin.DropRoots();
            }
        }
    }
    public sealed class SaveCutCopiedUnit
    {
        public string SourceId { get; }
        public long SourceVersion { get; }
        public int TreeOrdinal { get; }
        public int ChildCount { get; }
        public SaveCutChunk Leaf { get; }
        internal SaveCutCopiedUnit(string source, long version, int ordinal, int childCount, SaveCutChunk leaf)
        { SourceId = source; SourceVersion = version; TreeOrdinal = ordinal; ChildCount = childCount; Leaf = leaf; }
    }
    public sealed class SaveCutDetachedPayload
    {
        readonly SaveCutCopiedUnit[] _units;
        public string SessionId { get; }
        public string RunId { get; }
        public string ResourceLeaseId { get; }
        public long WorldRevision { get; }
        public long CaptureSequence { get; }
        public int UnitCount => _units.Length;
        public SaveCutCopiedUnit UnitAt(int index) => _units[index];
        internal SaveCutDetachedPayload(SaveCutPin pin, SaveCutCopiedUnit[] units)
        { SessionId = pin.SessionId; RunId = pin.RunId; ResourceLeaseId = pin.ResourceLeaseId; WorldRevision = pin.WorldRevision; CaptureSequence = pin.CaptureSequence; _units = units; }
    }
    public sealed class SaveCutCaptureCursor
    {
        readonly SaveCutPin _pin;
        readonly List<SaveCutCopiedUnit> _copied = new List<SaveCutCopiedUnit>();
        readonly Stack<SaveCutChunk> _pending = new Stack<SaveCutChunk>();
        int _root, _ordinal;
        bool _cancelled, _complete;
        public bool Complete => _complete;
        public int CopiedUnits => _copied.Count;
        public SaveCutDetachedPayload Payload { get; private set; }
        public SaveCutCaptureCursor(SaveCutPin pin) { _pin = pin ?? throw new ArgumentNullException(nameof(pin)); }
        // Cursor is caller-owned and single-threaded. Each unit is at most 32 scalar values, no model or serializer invocation.
        public bool Advance(int maximumUnits, out string reason)
        {
            reason = "";
            if (maximumUnits < 1 || maximumUnits > 32) { reason = "invalid_budget"; return false; }
            if (_complete) return true;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (_cancelled || _pin.Released) { reason = "capture_cancelled"; return false; }
                for (int units = 0; units < maximumUnits; units++)
                {
                    if (_pending.Count == 0)
                    {
                        if (_root == _pin.RootCount) break;
                        _pending.Push(_pin.RootAt(_root).Chunk); _ordinal = 0;
                    }
                    var node = _pending.Pop(); var root = _pin.RootAt(_root);
                    _copied.Add(new SaveCutCopiedUnit(root.SourceId, root.Version, _ordinal++, node.ChildCount, node.CopyLeaf()));
                    for (int i = node.ChildCount - 1; i >= 0; i--) _pending.Push(node.ChildAt(i));
                    if (_pending.Count == 0) _root++;
                }
                if (_root == _pin.RootCount && _pending.Count == 0)
                {
                    // A capped final reference array, no full document concatenation or serialization.
                    Payload = new SaveCutDetachedPayload(_pin, _copied.ToArray()); _complete = true; _pin.Release();
                }
                return true;
            }
        }
        public void Cancel()
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (_complete || _cancelled) return;
                _cancelled = true; _pending.Clear(); _copied.Clear(); _pin.Release();
            }
        }
    }
}
