using System;

namespace SynapticSea.Core.Rng
{
    /// <summary>
    /// Bit-exact port of Godot 4.7 <c>RandomPCG</c> (core/math/random_pcg.*, thirdparty/misc/pcg.cpp) exposed with
    /// the GDScript <c>RandomNumberGenerator</c> API. Every procgen/loot/system roll goes through this class so that
    /// a seed produces the same results as the Godot build.
    ///
    /// Note the two distinct float families in Godot:
    ///  - <c>RandomNumberGenerator.randf()/randf_range()/randfn()</c> use the float32 paths (<see cref="Randf"/>, ...).
    ///  - GDScript global <c>randf()</c> is <c>(float)rand() / (float)UINT32_MAX</c> and global <c>randf_range()/randfn()</c>
    ///    use the double path (<see cref="Randd"/>). See <see cref="GodotGlobalRandom"/>.
    /// </summary>
    public sealed class GodotRandom
    {
        public const ulong DefaultSeed = 12047754176567800795UL;
        public const ulong DefaultInc = 1442695040888963407UL;
        const double CmpEpsilon = 0.00001;
        const double Tau = 6.2831853071795864769252867666;

        ulong _state;
        ulong _inc;
        ulong _seed;
        readonly ulong _currentInc;

        public GodotRandom() : this(DefaultSeed, DefaultInc) { }

        public GodotRandom(ulong seed, ulong inc = DefaultInc)
        {
            _currentInc = inc;
            SetSeed(seed);
        }

        /// <summary>GDScript: <c>RandomNumberGenerator.new()</c> followed by <c>rng.seed = seed</c>.</summary>
        public static GodotRandom FromSeed(long seed) => new GodotRandom(unchecked((ulong)seed));

        /// <summary>GDScript <c>rng.seed</c> (int64 in script; stored as uint64).</summary>
        public long Seed
        {
            get => unchecked((long)_seed);
            set => SetSeed(unchecked((ulong)value));
        }

        /// <summary>GDScript <c>rng.state</c>.</summary>
        public long State
        {
            get => unchecked((long)_state);
            set => _state = unchecked((ulong)value);
        }

        void SetSeed(ulong seed)
        {
            _seed = seed;
            // pcg32_srandom_r(&pcg, seed, current_inc)
            _state = 0UL;
            _inc = (_currentInc << 1) | 1UL;
            Next32();
            _state = unchecked(_state + seed);
            Next32();
        }

        /// <summary>pcg32_random_r.</summary>
        public uint Next32()
        {
            ulong old = _state;
            _state = unchecked(old * 6364136223846793005UL + (_inc | 1UL));
            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
        }

        /// <summary>pcg32_boundedrand_r: uniform in [0, bound).</summary>
        public uint Next32(uint bound)
        {
            uint threshold = unchecked(0u - bound) % bound;
            while (true)
            {
                uint r = Next32();
                if (r >= threshold) return r % bound;
            }
        }

        // ------------------------------------------------------------------ RandomNumberGenerator API

        /// <summary><c>rng.randi()</c>: uint32 widened to a non-negative int64.</summary>
        public long Randi() => Next32();

        /// <summary><c>rng.randf()</c>: RandomPCG::randf (float32), widened to double exactly.</summary>
        public double Randf() => RandfF32();

        /// <summary><c>rng.randi_range(from, to)</c>: arguments are truncated to int32 like the C++ binding.</summary>
        public long RandiRange(long from, long to) => RandomInt(unchecked((int)from), unchecked((int)to));

        /// <summary><c>rng.randf_range(from, to)</c>: float32 path.</summary>
        public double RandfRange(double from, double to)
        {
            float f = (float)from, t = (float)to;
            float span = (float)(t - f);
            float scaled = (float)(RandfF32() * span);
            return (float)(scaled + f);
        }

        /// <summary><c>rng.randfn(mean, deviation)</c>: float32 path of RandomPCG::randfn.</summary>
        public double Randfn(double mean = 0.0, double deviation = 1.0)
        {
            float m = (float)mean, dev = (float)deviation;
            float temp = RandfF32();
            if (temp < CmpEpsilon) temp = (float)(temp + CmpEpsilon);
            float angle = (float)((float)Tau * RandfF32());
            float cos = (float)Math.Cos(angle);
            float logTemp = (float)Math.Log(temp);
            double sqrt = Math.Sqrt(-2.0 * logTemp);
            double result = m + dev * (cos * sqrt);
            return (float)result;
        }

        /// <summary>RandomPCG::rand_weighted (float32 weights). Returns -1 for empty input.</summary>
        public long RandWeighted(float[] weights)
        {
            if (weights == null || weights.Length == 0) return -1;
            float sum = 0f;
            for (int i = 0; i < weights.Length; i++) sum = (float)(sum + weights[i]);
            float remaining = (float)(RandfF32() * sum);
            for (int i = 0; i < weights.Length; i++)
            {
                remaining = (float)(remaining - weights[i]);
                if (remaining < 0) return i;
            }
            for (int i = weights.Length - 1; i >= 0; --i)
                if (weights[i] > 0) return i;
            return -1;
        }

        // ------------------------------------------------------------------ RandomPCG primitives

        /// <summary>RandomPCG::randf (float32 with clz exponent trick).</summary>
        public float RandfF32()
        {
            uint protoExpOffset = Next32();
            if (protoExpOffset == 0) return 0f;
            float significand = (float)(Next32() | 0x80000001u);
            return (float)(significand * Pow2(-32 - Clz32(protoExpOffset)));
        }

        /// <summary>
        /// RandomPCG::randd. The C++ expression <c>(((uint64_t)rand()) &lt;&lt; 32) | rand()</c> has unsequenced
        /// calls; <see cref="HighWordFirst"/> selects the evaluation order (verified by the Godot RNG fixture).
        /// </summary>
        public double Randd()
        {
            uint protoExpOffset = Next32();
            if (protoExpOffset == 0) return 0.0;
            ulong first = Next32();
            ulong second = Next32();
            ulong hi = HighWordFirst ? first : second;
            ulong lo = HighWordFirst ? second : first;
            ulong significand = (hi << 32) | lo | 0x8000000000000001UL;
            return UInt64ToDouble(significand) * Pow2(-64 - Clz32(protoExpOffset));
        }

        /// <summary>Evaluation order of the two <c>rand()</c> calls inside RandomPCG::randd (compiler-dependent).</summary>
        public static bool HighWordFirst = true;

        /// <summary>RandomPCG::random(double, double): the double path.</summary>
        public double RandomDouble(double from, double to) => Randd() * (to - from) + from;

        /// <summary>RandomPCG::randfn(double, double): the double path.</summary>
        public double RandfnDouble(double mean, double deviation)
        {
            double temp = Randd();
            if (temp < CmpEpsilon) temp += CmpEpsilon;
            return mean + deviation * (Math.Cos(Tau * Randd()) * Math.Sqrt(-2.0 * Math.Log(temp)));
        }

        /// <summary>RandomPCG::random(int, int).</summary>
        public long RandomInt(int from, int to)
        {
            if (from == to) return from;
            long min = Math.Min(from, to);
            long max = Math.Max(from, to);
            uint diff = unchecked((uint)(max - min));
            if (diff == uint.MaxValue) return unchecked((int)((long)Next32() + min));
            return unchecked((int)((long)Next32(diff + 1u) + min));
        }

        static int Clz32(uint x)
        {
            int n = 0;
            if ((x & 0xFFFF0000u) == 0) { n += 16; x <<= 16; }
            if ((x & 0xFF000000u) == 0) { n += 8; x <<= 8; }
            if ((x & 0xF0000000u) == 0) { n += 4; x <<= 4; }
            if ((x & 0xC0000000u) == 0) { n += 2; x <<= 2; }
            if ((x & 0x80000000u) == 0) { n += 1; }
            return n;
        }

        /// <summary>Exact 2^e for the exponent range used here.</summary>
        static double Pow2(int e) => BitConverter.Int64BitsToDouble((long)(e + 1023) << 52);

        /// <summary>Correctly rounded uint64 to double conversion (independent of runtime conv.r.un behaviour).</summary>
        static double UInt64ToDouble(ulong v)
        {
            if (v < (1UL << 53)) return (long)v;
            int shift = 64 - 53 - Clz64(v);
            ulong mant = v >> shift;
            ulong rem = v & ((1UL << shift) - 1);
            ulong half = 1UL << (shift - 1);
            if (rem > half || (rem == half && (mant & 1) == 1)) mant++;
            return (double)(long)mant * Pow2(shift);
        }

        static int Clz64(ulong x)
        {
            uint hi = (uint)(x >> 32);
            return hi != 0 ? Clz32(hi) : 32 + Clz32((uint)x);
        }
    }

    /// <summary>
    /// GDScript's global RNG (<c>randi()</c>, <c>randf()</c>, <c>randi_range()</c>, <c>randf_range()</c>, <c>randfn()</c>, <c>seed()</c>).
    /// Godot randomizes it at startup, so code that relies on it without calling <c>seed()</c> is nondeterministic.
    /// </summary>
    public static class GodotGlobalRandom
    {
        public static GodotRandom Instance { get; private set; } = new GodotRandom();

        /// <summary>GDScript <c>seed(s)</c>.</summary>
        public static void Seed(long s) => Instance.Seed = s;

        /// <summary>GDScript <c>randomize()</c>.</summary>
        public static void Randomize(long entropy) => Instance.Seed = entropy;

        public static long Randi() => Instance.Next32();

        /// <summary>GDScript global <c>randf()</c>: <c>(float)rand() / (float)UINT32_MAX</c>.</summary>
        public static double Randf() => (float)((float)Instance.Next32() / (float)uint.MaxValue);

        public static long RandiRange(long from, long to) => Instance.RandomInt(unchecked((int)from), unchecked((int)to));

        public static double RandfRange(double from, double to) => Instance.RandomDouble(from, to);

        public static double Randfn(double mean, double deviation) => Instance.RandfnDouble(mean, deviation);

        public static void Reset() => Instance = new GodotRandom();
    }
}
