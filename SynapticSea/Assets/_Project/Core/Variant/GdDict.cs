using System;
using System.Collections;
using System.Collections.Generic;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// C# equivalent of a GDScript <c>Dictionary</c>: insertion-ordered, Variant-keyed.
    /// Values are restricted to the Variant leaf set (see <see cref="V.Normalize"/>):
    /// null, bool, long, double, string, <see cref="GdDict"/>, <see cref="GdArray"/>, <see cref="Vec2i"/>, <see cref="Vec3"/>.
    /// Removal preserves the order of the remaining keys, like Godot.
    /// </summary>
    public sealed class GdDict : IEnumerable<KeyValuePair<object, object>>
    {
        readonly List<object> _keys = new List<object>();
        readonly List<object> _values = new List<object>();
        readonly Dictionary<object, int> _index = new Dictionary<object, int>(VariantKeyComparer.Instance);

        public GdDict() { }

        public int Count => _keys.Count;
        public bool IsEmpty => _keys.Count == 0;

        /// <summary>Keys in insertion order, like <c>Dictionary.keys()</c>.</summary>
        public IReadOnlyList<object> Keys => _keys;

        /// <summary>Values in insertion order, like <c>Dictionary.values()</c>.</summary>
        public IReadOnlyList<object> Values => _values;

        public object this[object key]
        {
            get
            {
                key = V.NormalizeKey(key);
                if (_index.TryGetValue(key, out int i)) return _values[i];
                throw new KeyNotFoundException($"GdDict has no key '{key}'.");
            }
            set => Set(key, value);
        }

        public object this[string key]
        {
            get => this[(object)key];
            set => Set(key, value);
        }

        public void Set(object key, object value)
        {
            key = V.NormalizeKey(key);
            value = V.Normalize(value);
            if (_index.TryGetValue(key, out int i))
            {
                _values[i] = value;
                return;
            }
            _index[key] = _keys.Count;
            _keys.Add(key);
            _values.Add(value);
        }

        /// <summary><c>dict.get(key, default)</c>.</summary>
        public object Get(object key, object fallback = null)
        {
            key = V.NormalizeKey(key);
            return _index.TryGetValue(key, out int i) ? _values[i] : fallback;
        }

        public bool TryGetValue(object key, out object value)
        {
            key = V.NormalizeKey(key);
            if (_index.TryGetValue(key, out int i))
            {
                value = _values[i];
                return true;
            }
            value = null;
            return false;
        }

        /// <summary><c>dict.has(key)</c>.</summary>
        public bool Has(object key) => _index.ContainsKey(V.NormalizeKey(key));

        /// <summary><c>dict.erase(key)</c>; returns true when the key existed.</summary>
        public bool Erase(object key)
        {
            key = V.NormalizeKey(key);
            if (!_index.TryGetValue(key, out int i)) return false;
            _keys.RemoveAt(i);
            _values.RemoveAt(i);
            _index.Remove(key);
            for (int j = i; j < _keys.Count; j++) _index[_keys[j]] = j;
            return true;
        }

        public void Clear()
        {
            _keys.Clear();
            _values.Clear();
            _index.Clear();
        }

        /// <summary><c>dict.merge(other, overwrite)</c>.</summary>
        public void Merge(GdDict other, bool overwrite = false)
        {
            if (other == null) return;
            for (int i = 0; i < other._keys.Count; i++)
            {
                if (overwrite || !Has(other._keys[i])) Set(other._keys[i], other._values[i]);
            }
        }

        /// <summary><c>dict.duplicate(true)</c>.</summary>
        public GdDict DeepCopy()
        {
            var copy = new GdDict();
            for (int i = 0; i < _keys.Count; i++) copy.SetRaw(_keys[i], V.DeepCopy(_values[i]));
            return copy;
        }

        /// <summary><c>dict.duplicate()</c> / <c>duplicate(false)</c>.</summary>
        public GdDict ShallowCopy()
        {
            var copy = new GdDict();
            for (int i = 0; i < _keys.Count; i++) copy.SetRaw(_keys[i], _values[i]);
            return copy;
        }

        void SetRaw(object key, object value)
        {
            _index[key] = _keys.Count;
            _keys.Add(key);
            _values.Add(value);
        }

        public IEnumerator<KeyValuePair<object, object>> GetEnumerator()
        {
            for (int i = 0; i < _keys.Count; i++) yield return new KeyValuePair<object, object>(_keys[i], _values[i]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Collection-initializer support: <c>new GdDict { { "a", 1 } }</c>.</summary>
        public void Add(object key, object value)
        {
            if (Has(key)) throw new ArgumentException($"Duplicate key '{key}' in GdDict initializer.");
            Set(key, value);
        }

        public override string ToString() => GdJson.Stringify(this);
    }
}
