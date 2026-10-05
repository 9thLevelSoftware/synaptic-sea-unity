using System;
using System.Collections;
using System.Collections.Generic;

namespace SynapticSea.Core.Variant
{
    public sealed class GdArray : IList<object>, IReadOnlyList<object>
    {
        internal List<object> RawStorage;
        internal readonly TrackedParticipantOwner Owner;
        internal EnrolledProjectionNode ProjectionNode;
        public GdArray() => RawStorage = new List<object>();
        public GdArray(int capacity) => RawStorage = new List<object>(capacity);
        internal GdArray(TrackedParticipantOwner owner) : this() { Owner = owner; }
        public GdArray(IEnumerable items) : this()
        { if (items != null) foreach (object item in items) RawStorage.Add(V.Normalize(item)); }
        public static GdArray Of(params object[] items) => new GdArray(items);
        public int Count { get { if (Owner == null) return RawStorage.Count; lock (CommonParticipantGate.SyncRoot) return RawStorage.Count; } }
        public bool IsReadOnly => false;
        public bool IsEmpty => Count == 0;
        public object this[int index]
        {
            get { if (Owner == null) return RawStorage[index]; lock (CommonParticipantGate.SyncRoot) return RawStorage[index]; }
            set
            {
                value = Owner == null ? V.Normalize(value) : TrackedParticipantOwner.NormalizeProtected(value);
                if (Owner == null) { RawStorage[index] = value; return; }
                if (Owner.TryProjectedArraySet(this, index, value)) return;
                lock (CommonParticipantGate.SyncRoot) { var next = new List<object>(RawStorage); next[index] = value; InstallMutation(next); }
            }
        }
        void InstallMutation(List<object> next)
        { bool touch = Owner.PrepareMutationUnderGate(this, next); RawStorage = next; Owner.FinishMutationUnderGate(touch); }
        public void Add(object item)
        {
            item = Owner == null ? V.Normalize(item) : TrackedParticipantOwner.NormalizeProtected(item);
            if (Owner == null) { RawStorage.Add(item); return; }
            lock (CommonParticipantGate.SyncRoot) { var next = new List<object>(RawStorage); next.Add(item); InstallMutation(next); }
        }
        public void Append(object item) => Add(item);
        public void Insert(int index, object item)
        {
            item = Owner == null ? V.Normalize(item) : TrackedParticipantOwner.NormalizeProtected(item);
            if (Owner == null) { RawStorage.Insert(index, item); return; }
            lock (CommonParticipantGate.SyncRoot) { var next = new List<object>(RawStorage); next.Insert(index, item); InstallMutation(next); }
        }
        public void RemoveAt(int index)
        {
            if (Owner == null) { RawStorage.RemoveAt(index); return; }
            lock (CommonParticipantGate.SyncRoot) { var next = new List<object>(RawStorage); next.RemoveAt(index); InstallMutation(next); }
        }
        public void Clear()
        {
            if (Owner == null) { RawStorage.Clear(); return; }
            lock (CommonParticipantGate.SyncRoot) InstallMutation(new List<object>());
        }
        public bool Contains(object item) => IndexOf(item) >= 0;
        public int IndexOf(object item)
        {
            item = Owner == null ? V.Normalize(item) : TrackedParticipantOwner.NormalizeProtected(item);
            if (Owner == null) return FindRaw(item);
            lock (CommonParticipantGate.SyncRoot)
            {
                if (item is GdDict d && !ReferenceEquals(d.Owner, Owner) || item is GdArray a && !ReferenceEquals(a.Owner, Owner)) throw new ArgumentException("unprotected_comparison_operand");
                return FindRaw(item);
            }
        }
        int FindRaw(object item)
        { for (int i = 0; i < RawStorage.Count; i++) if (V.VariantEquals(RawStorage[i], item)) return i; return -1; }
        public bool Remove(object item)
        {
            if (Owner == null) { int at = IndexOf(item); if (at < 0) return false; RawStorage.RemoveAt(at); return true; }
            lock (CommonParticipantGate.SyncRoot) { int at = IndexOf(item); if (at < 0) return false; RemoveAt(at); return true; }
        }
        public void AppendArray(GdArray other)
        {
            if (other == null) return;
            if (Owner == null && other.Owner == null) { RawStorage.AddRange(other.RawStorage); return; }
            lock (CommonParticipantGate.SyncRoot)
            {
                if (Owner == null) RawStorage.AddRange(other.RawStorage);
                else { var next = new List<object>(RawStorage); next.AddRange(other.RawStorage); InstallMutation(next); }
            }
        }
        public object Front() { if (Owner == null) return RawStorage.Count > 0 ? RawStorage[0] : null; lock (CommonParticipantGate.SyncRoot) return RawStorage.Count > 0 ? RawStorage[0] : null; }
        public object Back() { if (Owner == null) return RawStorage.Count > 0 ? RawStorage[RawStorage.Count - 1] : null; lock (CommonParticipantGate.SyncRoot) return RawStorage.Count > 0 ? RawStorage[RawStorage.Count - 1] : null; }
        public object PopBack()
        {
            if (Owner == null) { if (RawStorage.Count == 0) return null; object value = Back(); RawStorage.RemoveAt(RawStorage.Count - 1); return value; }
            lock (CommonParticipantGate.SyncRoot) { if (RawStorage.Count == 0) return null; object value = Back(); RemoveAt(RawStorage.Count - 1); return value; }
        }
        public object PopFront()
        {
            if (Owner == null) { if (RawStorage.Count == 0) return null; object value = Front(); RawStorage.RemoveAt(0); return value; }
            lock (CommonParticipantGate.SyncRoot) { if (RawStorage.Count == 0) return null; object value = Front(); RemoveAt(0); return value; }
        }
        public void SortCustom(Func<object, object, bool> less)
        {
            if (Owner == null) { GdSort.SortCustom(RawStorage, less); return; }
            List<object> candidate; List<object> before; ulong stamp;
            lock (CommonParticipantGate.SyncRoot) { before = RawStorage; candidate = new List<object>(before); stamp = Owner.Stamp; }
            // User comparator never runs under the protected publication fence.
            GdSort.SortCustom(candidate, less);
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!ReferenceEquals(before, RawStorage) || !Owner.MatchesUnderGate(stamp)) throw new InvalidOperationException("stale_sort");
                InstallMutation(candidate);
            }
        }
        public GdArray DeepCopy()
        {
            if (Owner == null) return CopyRaw(true);
            lock (CommonParticipantGate.SyncRoot) return CopyRaw(true);
        }
        public GdArray ShallowCopy()
        {
            if (Owner == null) return CopyRaw(false);
            lock (CommonParticipantGate.SyncRoot) return CopyRaw(false);
        }
        GdArray CopyRaw(bool deep)
        {
            var copy = new GdArray(RawStorage.Count);
            foreach (object item in RawStorage) copy.RawStorage.Add(deep ? V.DeepCopy(item) : item);
            return copy;
        }
        public void CopyTo(object[] array, int arrayIndex)
        { if (Owner == null) RawStorage.CopyTo(array, arrayIndex); else lock (CommonParticipantGate.SyncRoot) RawStorage.CopyTo(array, arrayIndex); }
        public IEnumerator<object> GetEnumerator()
        {
            if (Owner == null) return RawStorage.GetEnumerator();
            lock (CommonParticipantGate.SyncRoot) return ((IEnumerable<object>)RawStorage.ToArray()).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => GdJson.Stringify(this);
    }
}
