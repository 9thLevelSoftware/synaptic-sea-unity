using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// Godot 4.7 float-to-text rules, ported from <c>core/string/ustring.cpp</c>:
    /// <c>String::num</c> (printf "%.Nlf" then trailing-zero trim keeping one digit after the period),
    /// <c>String::num_real</c> (used by <c>str(float)</c>), and the JSON writer's precision rule.
    /// Fixed-point digits reproduce the Windows C runtime printf that Godot's official builds use (17 significant
    /// digits, then decimal rounding; see <see cref="FormatFixed"/>), independent of the .NET/Mono runtime's formatting.
    /// </summary>
    public static class GdFloatFormat
    {
        const int MaxDecimals = 32;

        /// <summary><c>String::num(p_num, p_decimals)</c>.</summary>
        public static string Num(double num, int decimals)
        {
            if (double.IsNaN(num)) return "nan";
            if (double.IsInfinity(num)) return num < 0 ? "-inf" : "inf";

            if (decimals < 0)
            {
                decimals = 14;
                double abs = Math.Abs(num);
                if (abs > 10) decimals -= (int)Math.Floor(Math.Log10(abs));
            }
            if (decimals > MaxDecimals) decimals = MaxDecimals;

            // Godot: decimals < 0 here means "%lf" (6 decimals).
            string fixedText = FormatFixed(num, decimals < 0 ? 6 : decimals);
            return TrimTrailingZeros(fixedText);
        }

        /// <summary><c>String::num_real(double, trailing)</c> — the text of <c>str(float)</c>.</summary>
        public static string NumReal(double num, bool trailing)
        {
            if (double.IsNaN(num) || double.IsInfinity(num)) return Num(num, 0);
            if (IsIntegral(num))
            {
                string whole = ((long)num).ToString(CultureInfo.InvariantCulture);
                return trailing ? whole + ".0" : whole;
            }
            int decimals = 14;
            double abs = Math.Abs(num);
            if (abs > 10) decimals -= (int)Math.Floor(Math.Log10(abs));
            return Num(num, decimals);
        }

        /// <summary><c>String::num_real(float, trailing)</c> — used for Vector3 components.</summary>
        public static string NumReal(float num, bool trailing)
        {
            if (float.IsNaN(num) || float.IsInfinity(num)) return Num(num, 0);
            if (num == (float)(long)num)
            {
                string whole = ((long)num).ToString(CultureInfo.InvariantCulture);
                return trailing ? whole + ".0" : whole;
            }
            int decimals = 6;
            float abs = Math.Abs(num);
            if (abs > 10) decimals -= (int)Math.Floor(Math.Log10(abs));
            return Num(num, decimals);
        }

        /// <summary>Number text written by <c>JSON.stringify</c> (non-full-precision path) for a float Variant.</summary>
        public static string JsonNumber(double num)
        {
            if (double.IsPositiveInfinity(num)) return "1e99999";
            if (double.IsNegativeInfinity(num)) return "-1e99999";
            if (double.IsNaN(num)) return "null";
            if (num == 0.0) return "0.0";
            double magnitude = Math.Log10(Math.Abs(num));
            int precision = Math.Max(1, 14 - (int)Math.Floor(magnitude));
            return Num(num, precision);
        }

        static bool IsIntegral(double num)
        {
            if (num >= 9.2233720368547758E+18 || num < -9.2233720368547758E+18) return false;
            return num == (double)(long)num;
        }

        static string TrimTrailingZeros(string s)
        {
            int period = s.IndexOf('.');
            if (period < 0) return s;
            int end = s.Length - 1;
            while (end > period && s[end] == '0') end--;
            if (end == period) return s.Substring(0, period + 1) + "0";
            return s.Substring(0, end + 1);
        }

        /// <summary>
        /// <c>"%.Nf"</c> as Godot's official Windows builds print it (the Microsoft C runtime printf). Only 17
        /// significant digits are generated, rounded with ties toward zero, and every later digit is zero; the "%.Nf"
        /// rounding to <paramref name="decimals"/> places is then applied to that decimal string, half away from zero.
        /// So 0.5 prints as "1" at zero decimals, 0.125 as "0.13" at two, 1e23 as "99999999999999992000000", and
        /// 0.6709878396987915 as "0.670987839698792" at fifteen. Verified against 4,202 Godot 4.7.1 outputs
        /// (<c>KernelParityTests</c> float_format_msvcrt fixture).
        /// </summary>
        public static string FormatFixed(double value, int decimals)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            bool negative = bits < 0;
            int exponentBits = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;

            BigInteger num;
            BigInteger den;
            if (exponentBits == 0)
            {
                num = fraction;
                den = BigInteger.One << 1074;
            }
            else
            {
                BigInteger mantissa = fraction | (1L << 52);
                int exponent = exponentBits - 1075;
                num = exponent >= 0 ? mantissa << exponent : mantissa;
                den = exponent >= 0 ? BigInteger.One : BigInteger.One << -exponent;
            }

            BigInteger scaled = BigInteger.Zero;
            if (!num.IsZero)
            {
                // |value| lies in [10^(k-1), 10^k).
                int k = (int)Math.Floor(Math.Log10(Math.Abs(value))) + 1;
                while (!LessThanPow10(num, den, k)) k++;
                while (LessThanPow10(num, den, k - 1)) k--;

                // The 17 significant digits, as an integer q with value ~= q * 10^-s.
                int s = 17 - k;
                BigInteger qn = s >= 0 ? num * BigInteger.Pow(10, s) : num;
                BigInteger qd = s >= 0 ? den : den * BigInteger.Pow(10, -s);
                BigInteger q = BigInteger.DivRem(qn, qd, out BigInteger rem);
                if ((rem << 1).CompareTo(qd) > 0) q += BigInteger.One;

                if (decimals >= s)
                {
                    scaled = q * BigInteger.Pow(10, decimals - s);
                }
                else
                {
                    BigInteger p = BigInteger.Pow(10, s - decimals);
                    scaled = BigInteger.DivRem(q, p, out BigInteger r);
                    if ((r << 1).CompareTo(p) >= 0) scaled += BigInteger.One;
                }
            }

            string digits = scaled.ToString(CultureInfo.InvariantCulture);
            if (digits.Length <= decimals) digits = new string('0', decimals - digits.Length + 1) + digits;

            var sb = new StringBuilder(digits.Length + 2);
            if (negative) sb.Append('-');
            if (decimals == 0)
            {
                sb.Append(digits);
            }
            else
            {
                sb.Append(digits, 0, digits.Length - decimals);
                sb.Append('.');
                sb.Append(digits, digits.Length - decimals, decimals);
            }
            return sb.ToString();
        }

        /// <summary>num/den &lt; 10^e.</summary>
        static bool LessThanPow10(BigInteger num, BigInteger den, int e) =>
            e >= 0 ? num < den * BigInteger.Pow(10, e) : num * BigInteger.Pow(10, -e) < den;
    }
}
