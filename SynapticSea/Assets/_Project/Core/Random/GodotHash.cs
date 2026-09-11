using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Rng
{
    /// <summary>
    /// Godot 4.7 hashing as seen from GDScript:
    ///  - <c>String.hash()</c> / <c>hash(String)</c>: additive djb2 over Unicode code points (char32), uint32.
    ///  - <c>hash(int)</c>: Thomas Wang 64-to-32 (<c>hash_one_uint64</c>).
    /// Both return the uint32 widened to a non-negative int64, as GDScript does.
    /// </summary>
    public static class GodotHash
    {
        /// <summary><c>String.hash()</c>.</summary>
        public static long StringHash(string s)
        {
            uint h = 5381;
            if (!string.IsNullOrEmpty(s))
            {
                int i = 0;
                while (i < s.Length)
                {
                    uint c = (uint)V.NextCodePoint(s, ref i);
                    h = unchecked(((h << 5) + h) + c);
                }
            }
            return h;
        }

        /// <summary><c>hash(int)</c> (hash_one_uint64).</summary>
        public static long IntHash(long value)
        {
            ulong v = unchecked((ulong)value);
            v = unchecked((~v) + (v << 18));
            v ^= v >> 31;
            v = unchecked(v * 21);
            v ^= v >> 11;
            v = unchecked(v + (v << 6));
            v ^= v >> 22;
            return (uint)v;
        }

        /// <summary>GDScript global <c>hash(variant)</c> for the Variant types the game hashes.</summary>
        public static long Hash(object value)
        {
            switch (V.Normalize(value))
            {
                case string s: return StringHash(s);
                case long l: return IntHash(l);
                case bool b: return b ? 1 : 0;
                case null: return 0;
                default:
                    throw new NotSupportedException($"hash() of {value.GetType().Name} is not ported; add it with a fixture.");
            }
        }
    }
}
