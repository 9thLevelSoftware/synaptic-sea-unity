using System;
using System.Collections;
using System.Collections.Generic;

namespace SynapticSea.Core.Variant
{
    /// <summary>Insertion-ordered Variant dictionary. Protection is diagnostic and opt-in from birth.</summary>
    public sealed class GdDict : IEnumerable<KeyValuePair<object, object>>
    {
        internal sealed class Storage
        {
            internal readonly List<object> Keys = new List<object>();
            internal readonly List<object> Values = new List<object>();
            internal readonly Dictionary<object, int> Index = new Dictionary<object, int>(VariantKeyComparer.Instance);
            internal Storage Clone()
            {
                var copy = new Storage();
                for (int i = 0; i < Keys.Count; i++) copy.Set(Keys[i], Values[i]);
                return copy;
            }
            internal void Set(object key, object value)
            {
                if (Index.TryGetValue(key, out int at)) { Values[at] = value; return; }
                Index[key] = Keys.Count; Keys.Add(key); Values.Add(value);
            }
            internal bool Erase(object key)
            {
                if (!Index.TryGetValue(key, out int at)) return false;
                Keys.RemoveAt(at); Values.RemoveAt(at); Index.Remove(key);
                for (int j = at; j < Keys.Count; j++) Index[Keys[j]] = j;
                return true;
            }
        }
        internal readonly TrackedParticipantOwner Owner;
        internal EnrolledProjectionNode ProjectionNode;
        internal Storage RawStorage = new Storage();
        readonly ProtectedView _keyView, _valueView;
        public GdDict() { }
        internal GdDict(TrackedParticipantOwner owner)
        { Owner = owner; _keyView = new ProtectedView(this, true); _valueView = new ProtectedView(this, false); }
        public int Count { get { if (Owner == null) return RawStorage.Keys.Count; lock (CommonParticipantGate.SyncRoot) return RawStorage.Keys.Count; } }
        public bool IsEmpty => Count == 0;
        public IReadOnlyList<object> Keys => Owner == null ? (IReadOnlyList<object>)RawStorage.Keys : _keyView;
        public IReadOnlyList<object> Values => Owner == null ? (IReadOnlyList<object>)RawStorage.Values : _valueView;
        public object this[object key]
        {
            get { if (Owner == null) return GetRequired(key); lock (CommonParticipantGate.SyncRoot) return GetRequired(key); }
            set => Set(key, value);
        }
        object GetRequired(object key)
        {
            key = Owner == null ? V.NormalizeKey(key) : TrackedParticipantOwner.NormalizeProtected(key, true);
            if (RawStorage.Index.TryGetValue(key, out int at)) return RawStorage.Values[at];
            throw new KeyNotFoundException($"GdDict has no key '{key}'.");
        }
        public object this[string key] { get => this[(object)key]; set => Set(key, value); }
        public void Set(object key, object value)
        {
            key = Owner == null ? V.NormalizeKey(key) : TrackedParticipantOwner.NormalizeProtected(key, true); value = Owner == null ? V.Normalize(value) : TrackedParticipantOwner.NormalizeProtected(value);
            if (Owner == null) { RawStorage.Set(key, value); return; }
            if (Owner.TryProjectedDictionarySet(this, key, value)) return;
            lock (CommonParticipantGate.SyncRoot)
            {
                var next = RawStorage.Clone(); next.Set(key, value);
                bool touch = Owner.PrepareMutationUnderGate(this, next);
                RawStorage = next; Owner.FinishMutationUnderGate(touch);
            }
        }
        public object Get(object key, object fallback = null)
        {
            if (Owner == null) return GetRaw(key, fallback);
            lock (CommonParticipantGate.SyncRoot) return GetRaw(key, fallback);
        }
        object NormalizeLookupKey(object key) => Owner == null ? V.NormalizeKey(key) : TrackedParticipantOwner.NormalizeProtected(key, true);
        object GetRaw(object key, object fallback)
        { return RawStorage.Index.TryGetValue(NormalizeLookupKey(key), out int at) ? RawStorage.Values[at] : fallback; }
        public bool TryGetValue(object key, out object value)
        {
            if (Owner == null) return TryGetRaw(key, out value);
            lock (CommonParticipantGate.SyncRoot) return TryGetRaw(key, out value);
        }
        bool TryGetRaw(object key, out object value)
        {
            if (RawStorage.Index.TryGetValue(NormalizeLookupKey(key), out int at)) { value = RawStorage.Values[at]; return true; }
            value = null; return false;
        }
        public bool Has(object key)
        {
            if (Owner == null) return RawStorage.Index.ContainsKey(NormalizeLookupKey(key));
            lock (CommonParticipantGate.SyncRoot) return RawStorage.Index.ContainsKey(NormalizeLookupKey(key));
        }
        public bool Erase(object key)
        {
            key = Owner == null ? V.NormalizeKey(key) : TrackedParticipantOwner.NormalizeProtected(key, true);
            if (Owner == null) return RawStorage.Erase(key);
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!RawStorage.Index.ContainsKey(key)) return false;
                var next = RawStorage.Clone(); next.Erase(key);
                bool touch = Owner.PrepareMutationUnderGate(this, next);
                RawStorage = next; Owner.FinishMutationUnderGate(touch); return true;
            }
        }
        public void Clear()
        {
            if (Owner == null) { RawStorage.Keys.Clear(); RawStorage.Values.Clear(); RawStorage.Index.Clear(); return; }
            lock (CommonParticipantGate.SyncRoot)
            {
                var next = new Storage(); bool touch = Owner.PrepareMutationUnderGate(this, next);
                RawStorage = next; Owner.FinishMutationUnderGate(touch);
            }
        }
        public void Merge(GdDict other, bool overwrite = false)
        {
            if (other == null) return;
            if (Owner == null && other.Owner == null)
            {
                for (int i = 0; i < other.RawStorage.Keys.Count; i++)
                    if (overwrite || !Has(other.RawStorage.Keys[i])) Set(other.RawStorage.Keys[i], other.RawStorage.Values[i]);
                return;
            }
            lock (CommonParticipantGate.SyncRoot)
            {
                // Validate the complete merge before any protected write; never partially admit an unsafe child.
                if (Owner != null)
                {
                    var next = RawStorage.Clone(); bool changed = false;
                    foreach (var kv in other)
                        if (overwrite || !next.Index.ContainsKey(kv.Key)) { next.Set(kv.Key, kv.Value); changed = true; }
                    if (!changed) return;
                    bool touch = Owner.PrepareMutationUnderGate(this, next);
                    RawStorage = next; Owner.FinishMutationUnderGate(touch);
                }
                else foreach (var kv in other) if (overwrite || !Has(kv.Key)) Set(kv.Key, kv.Value);
            }
        }
        public GdDict DeepCopy()
        {
            if (Owner == null) return CopyRaw(true);
            lock (CommonParticipantGate.SyncRoot) return CopyRaw(true);
        }
        public GdDict ShallowCopy()
        {
            if (Owner == null) return CopyRaw(false);
            lock (CommonParticipantGate.SyncRoot) return CopyRaw(false);
        }
        GdDict CopyRaw(bool deep)
        {
            var copy = new GdDict();
            for (int i = 0; i < RawStorage.Keys.Count; i++)
                copy.RawStorage.Set(RawStorage.Keys[i], deep ? V.DeepCopy(RawStorage.Values[i]) : RawStorage.Values[i]);
            return copy;
        }
        public IEnumerator<KeyValuePair<object, object>> GetEnumerator()
        {
            if (Owner == null) return LegacyEnumerator();
            lock (CommonParticipantGate.SyncRoot)
            {
                var snapshot = new KeyValuePair<object, object>[RawStorage.Keys.Count];
                for (int i = 0; i < snapshot.Length; i++) snapshot[i] = new KeyValuePair<object, object>(RawStorage.Keys[i], RawStorage.Values[i]);
                return ((IEnumerable<KeyValuePair<object, object>>)snapshot).GetEnumerator();
            }
        }
        IEnumerator<KeyValuePair<object, object>> LegacyEnumerator()
        { for (int i = 0; i < RawStorage.Keys.Count; i++) yield return new KeyValuePair<object, object>(RawStorage.Keys[i], RawStorage.Values[i]); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Add(object key, object value)
        {
            if (Owner == null) { if (Has(key)) throw new ArgumentException($"Duplicate key '{key}' in GdDict initializer."); Set(key, value); return; }
            lock (CommonParticipantGate.SyncRoot)
            { if (Has(key)) throw new ArgumentException($"Duplicate key '{key}' in GdDict initializer."); Set(key, value); }
        }
        public override string ToString() => GdJson.Stringify(this);
        sealed class ProtectedView : IReadOnlyList<object>
        {
            readonly GdDict _dict; readonly bool _keys;
            internal ProtectedView(GdDict dict, bool keys) { _dict = dict; _keys = keys; }
            public int Count => _dict.Count;
            public object this[int index] { get { lock (CommonParticipantGate.SyncRoot) return (_keys ? _dict.RawStorage.Keys : _dict.RawStorage.Values)[index]; } }
            public IEnumerator<object> GetEnumerator()
            { lock (CommonParticipantGate.SyncRoot) return ((IEnumerable<object>)(_keys ? _dict.RawStorage.Keys : _dict.RawStorage.Values).ToArray()).GetEnumerator(); }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
