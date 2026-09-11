using System;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// GDScript global math functions with Godot semantics. GDScript <c>float</c> is 64-bit, so these take doubles.
    /// Use these instead of <c>Math.Round</c> (banker's rounding) and never use UnityEngine.Mathf in Core.
    /// </summary>
    public static class GdMath
    {
        public const double CmpEpsilon = 0.00001;
        public const double Pi = Math.PI;
        public const double Tau = 2.0 * Math.PI;

        /// <summary><c>round()</c>: half away from zero.</summary>
        public static double Round(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

        /// <summary><c>roundi()</c>.</summary>
        public static long RoundI(double x) => (long)Round(x);

        public static double Floor(double x) => Math.Floor(x);
        public static long FloorI(double x) => (long)Math.Floor(x);
        public static double Ceil(double x) => Math.Ceiling(x);
        public static long CeilI(double x) => (long)Math.Ceiling(x);

        public static double Clampf(double v, double min, double max) => v < min ? min : (v > max ? max : v);
        public static long Clampi(long v, long min, long max) => v < min ? min : (v > max ? max : v);

        public static double Lerpf(double from, double to, double weight) => from + (to - from) * weight;

        public static double InverseLerp(double from, double to, double value) => (value - from) / (to - from);

        public static double Remap(double value, double istart, double istop, double ostart, double ostop) =>
            Lerpf(ostart, ostop, InverseLerp(istart, istop, value));

        public static double MoveToward(double from, double to, double delta) =>
            Math.Abs(to - from) <= delta ? to : from + Math.Sign(to - from) * delta;

        /// <summary><c>is_equal_approx()</c> for doubles.</summary>
        public static bool IsEqualApprox(double a, double b)
        {
            if (a == b) return true;
            double tolerance = CmpEpsilon * Math.Abs(a);
            if (tolerance < CmpEpsilon) tolerance = CmpEpsilon;
            return Math.Abs(a - b) < tolerance;
        }

        public static bool IsZeroApprox(double a) => Math.Abs(a) < CmpEpsilon;

        /// <summary><c>posmod()</c> for ints.</summary>
        public static long Posmod(long x, long y)
        {
            long v = x % y;
            if ((v < 0 && y > 0) || (v > 0 && y < 0)) v += y;
            return v;
        }

        /// <summary><c>fposmod()</c>.</summary>
        public static double Fposmod(double x, double y)
        {
            double v = x % y;
            if ((v < 0 && y > 0) || (v > 0 && y < 0)) v += y;
            v += 0.0;
            return v;
        }

        /// <summary><c>fmod()</c> / float <c>%</c>.</summary>
        public static double Fmod(double x, double y) => x % y;

        /// <summary><c>snapped()</c>.</summary>
        public static double Snapped(double value, double step) => step != 0 ? Math.Floor(value / step + 0.5) * step : value;

        /// <summary><c>wrapf()</c>.</summary>
        public static double Wrapf(double value, double min, double max)
        {
            double range = max - min;
            if (IsZeroApprox(range)) return min;
            double result = value - range * Math.Floor((value - min) / range);
            if (IsEqualApprox(result, max)) return min;
            return result;
        }

        /// <summary><c>wrapi()</c>.</summary>
        public static long Wrapi(long value, long min, long max)
        {
            long range = max - min;
            return range == 0 ? min : min + ((((value - min) % range) + range) % range);
        }

        /// <summary><c>signf()</c>.</summary>
        public static double Signf(double x) => x > 0 ? 1.0 : (x < 0 ? -1.0 : 0.0);

        /// <summary><c>signi()</c>.</summary>
        public static long Signi(long x) => x > 0 ? 1 : (x < 0 ? -1 : 0);

        public static double DegToRad(double deg) => deg * (Pi / 180.0);
        public static double RadToDeg(double rad) => rad * (180.0 / Pi);

        /// <summary><c>int(x)</c> on a float: truncation toward zero.</summary>
        public static long Trunc(double x)
        {
            if (double.IsNaN(x)) return 0;
            if (x >= 9.2233720368547758E+18) return long.MaxValue;
            if (x <= -9.2233720368547758E+18) return long.MinValue;
            return (long)x;
        }
    }
}
