// Small engine-free helpers the coordinator port needs (Godot Dictionary-with-object-values order, Transform3D algebra).
using System.Collections;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// An insertion-ordered map with Godot Dictionary semantics for typed values (object references a GdDict should not
    /// hold, e.g. ShipInstance): iteration is insertion order, overwriting a key keeps its position, erasing removes it.
    /// </summary>
    public sealed class OrderedMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    {
        readonly List<TKey> _keys = new List<TKey>();
        readonly Dictionary<TKey, TValue> _map = new Dictionary<TKey, TValue>();

        public int Count => _keys.Count;
        public IReadOnlyList<TKey> Keys => _keys;

        public IEnumerable<TValue> Values
        {
            get
            {
                foreach (TKey k in _keys)
                    yield return _map[k];
            }
        }

        public TValue this[TKey key]
        {
            get => _map[key];
            set
            {
                if (!_map.ContainsKey(key))
                    _keys.Add(key);
                _map[key] = value;
            }
        }

        public bool ContainsKey(TKey key) => _map.ContainsKey(key);
        public bool TryGetValue(TKey key, out TValue value) => _map.TryGetValue(key, out value);
        public TValue GetOrDefault(TKey key, TValue fallback = default) => _map.TryGetValue(key, out TValue v) ? v : fallback;

        public bool Remove(TKey key)
        {
            if (!_map.Remove(key))
                return false;
            _keys.Remove(key);
            return true;
        }

        public void Clear()
        {
            _keys.Clear();
            _map.Clear();
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            foreach (TKey k in new List<TKey>(_keys))
                yield return new KeyValuePair<TKey, TValue>(k, _map[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Godot <c>Transform3D</c> operations the coordinator used that <see cref="Xform3"/> does not carry.</summary>
    public static class SessionMath
    {
        /// <summary>Godot <c>Basis * Basis</c> (Basis::operator*): rows of a times columns of b, float32.</summary>
        public static Basis3 Mul(Basis3 a, Basis3 b)
        {
            Vec3 c0 = new Vec3(b.Row0.X, b.Row1.X, b.Row2.X);
            Vec3 c1 = new Vec3(b.Row0.Y, b.Row1.Y, b.Row2.Y);
            Vec3 c2 = new Vec3(b.Row0.Z, b.Row1.Z, b.Row2.Z);
            return new Basis3(
                new Vec3(a.Row0.Dot(c0), a.Row0.Dot(c1), a.Row0.Dot(c2)),
                new Vec3(a.Row1.Dot(c0), a.Row1.Dot(c1), a.Row1.Dot(c2)),
                new Vec3(a.Row2.Dot(c0), a.Row2.Dot(c1), a.Row2.Dot(c2)));
        }

        /// <summary>Godot <c>Transform3D * Transform3D</c>: <c>(basis * o.basis, xform(o.origin))</c>.</summary>
        public static Xform3 Mul(Xform3 a, Xform3 b) => new Xform3(Mul(a.Basis, b.Basis), a * b.Origin);

        /// <summary>Godot <c>Basis.inverse()</c> (cofactor inverse, float32).</summary>
        public static Basis3 Inverse(Basis3 m)
        {
            float co0 = (float)((float)(m.Row1.Y * m.Row2.Z) - (float)(m.Row1.Z * m.Row2.Y));
            float co1 = (float)((float)(m.Row1.Z * m.Row2.X) - (float)(m.Row1.X * m.Row2.Z));
            float co2 = (float)((float)(m.Row1.X * m.Row2.Y) - (float)(m.Row1.Y * m.Row2.X));
            float det = (float)((float)((float)(m.Row0.X * co0) + (float)(m.Row0.Y * co1)) + (float)(m.Row0.Z * co2));
            float s = 1.0f / det;
            return new Basis3(
                new Vec3(co0 * s, (float)((float)(m.Row0.Z * m.Row2.Y) - (float)(m.Row0.Y * m.Row2.Z)) * s, (float)((float)(m.Row0.Y * m.Row1.Z) - (float)(m.Row0.Z * m.Row1.Y)) * s),
                new Vec3(co1 * s, (float)((float)(m.Row0.X * m.Row2.Z) - (float)(m.Row0.Z * m.Row2.X)) * s, (float)((float)(m.Row0.Z * m.Row1.X) - (float)(m.Row0.X * m.Row1.Z)) * s),
                new Vec3(co2 * s, (float)((float)(m.Row0.Y * m.Row2.X) - (float)(m.Row0.X * m.Row2.Y)) * s, (float)((float)(m.Row0.X * m.Row1.Y) - (float)(m.Row0.Y * m.Row1.X)) * s));
        }

        /// <summary>Godot <c>Transform3D.affine_inverse()</c>: <c>(basis.inverse(), basis.inverse().xform(-origin))</c>.</summary>
        public static Xform3 AffineInverse(Xform3 x)
        {
            Basis3 inv = Inverse(x.Basis);
            return new Xform3(inv, inv * (-x.Origin));
        }

        /// <summary><c>Transform3D(Basis(), v)</c>.</summary>
        public static Xform3 Translation(Vec3 v) => new Xform3(Basis3.Identity, v);

        /// <summary>Godot <c>AABB.get_center()</c>.</summary>
        public static Vec3 Center(Aabb3 aabb) => aabb.Position + aabb.Size * 0.5f;
    }
}
