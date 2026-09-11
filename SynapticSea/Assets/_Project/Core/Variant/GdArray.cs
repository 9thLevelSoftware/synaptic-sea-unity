using System.Collections;
using System.Collections.Generic;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// C# equivalent of a GDScript untyped <c>Array</c>. Elements are normalized to the Variant leaf set.
    /// Typed GDScript arrays (<c>Array[String]</c>) may be ported as <c>List&lt;T&gt;</c> instead when they never reach JSON.
    /// </summary>
    public sealed class GdArray : IList<object>, IReadOnlyList<object>
    {
        readonly List<object> _items;

        public GdArray() => _items = new List<object>();
        public GdArray(int capacity) => _items = new List<object>(capacity);

        public GdArray(IEnumerable items) : this()
        {
            if (items == null) return;
            foreach (var item in items) _items.Add(V.Normalize(item));
        }

        public static GdArray Of(params object[] items) => new GdArray(items);

        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public bool IsEmpty => _items.Count == 0;

        public object this[int index]
        {
            get => _items[index];
            set => _items[index] = V.Normalize(value);
        }

        public void Add(object item) => _items.Add(V.Normalize(item));
        public void Append(object item) => Add(item);
        public void Insert(int index, object item) => _items.Insert(index, V.Normalize(item));
        public void RemoveAt(int index) => _items.RemoveAt(index);
        public void Clear() => _items.Clear();

        /// <summary>GDScript <c>has()</c> / <c>in</c>: Variant equality.</summary>
        public bool Contains(object item) => IndexOf(item) >= 0;

        /// <summary>GDScript <c>find()</c>.</summary>
        public int IndexOf(object item)
        {
            item = V.Normalize(item);
            for (int i = 0; i < _items.Count; i++)
                if (V.VariantEquals(_items[i], item)) return i;
            return -1;
        }

        /// <summary>GDScript <c>erase()</c>: removes the first matching element.</summary>
        public bool Remove(object item)
        {
            int i = IndexOf(item);
            if (i < 0) return false;
            _items.RemoveAt(i);
            return true;
        }

        public void AppendArray(GdArray other)
        {
            if (other == null) return;
            _items.AddRange(other._items);
        }

        public object Front() => _items.Count > 0 ? _items[0] : null;
        public object Back() => _items.Count > 0 ? _items[_items.Count - 1] : null;

        public object PopBack()
        {
            if (_items.Count == 0) return null;
            var last = _items[_items.Count - 1];
            _items.RemoveAt(_items.Count - 1);
            return last;
        }

        public object PopFront()
        {
            if (_items.Count == 0) return null;
            var first = _items[0];
            _items.RemoveAt(0);
            return first;
        }

        /// <summary>GDScript <c>sort_custom(func(a, b): return a &lt; b)</c> with a "less than" predicate.</summary>
        public void SortCustom(System.Func<object, object, bool> less) => GdSort.SortCustom(_items, less);

        public GdArray DeepCopy()
        {
            var copy = new GdArray(_items.Count);
            foreach (var item in _items) copy._items.Add(V.DeepCopy(item));
            return copy;
        }

        public GdArray ShallowCopy()
        {
            var copy = new GdArray(_items.Count);
            copy._items.AddRange(_items);
            return copy;
        }

        public void CopyTo(object[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public IEnumerator<object> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public override string ToString() => GdJson.Stringify(this);
    }
}
