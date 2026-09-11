using System;
using System.Collections.Generic;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// Key equality for <see cref="GdDict"/>, mirroring Godot Dictionary key semantics:
    /// values of different Variant types are distinct keys (int 1 and float 1.0 are different),
    /// strings compare by content.
    /// </summary>
    public sealed class VariantKeyComparer : IEqualityComparer<object>
    {
        public static readonly VariantKeyComparer Instance = new VariantKeyComparer();

        public new bool Equals(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.GetType() != b.GetType()) return false;
            switch (a)
            {
                case string sa: return string.Equals(sa, (string)b, StringComparison.Ordinal);
                case long la: return la == (long)b;
                case double da: return da.Equals((double)b);
                case bool ba: return ba == (bool)b;
                case Vec2i va: return va.Equals((Vec2i)b);
                case Vec3 v3: return v3.Equals((Vec3)b);
                default: return ReferenceEquals(a, b);
            }
        }

        public int GetHashCode(object obj)
        {
            if (obj == null) return 0;
            switch (obj)
            {
                case string s: return StringComparer.Ordinal.GetHashCode(s);
                case GdDict _:
                case GdArray _:
                    return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
                default: return obj.GetHashCode();
            }
        }
    }
}
