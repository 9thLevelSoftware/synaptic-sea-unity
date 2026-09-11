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
    /// Fixed-point digits are produced from the exact binary value with round-half-even, independent of the
    /// .NET/Mono runtime's formatting (Mono caps "F" formatting at 15 significant digits).
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

        /// <summary>Exact "%.Nf" formatting with round-half-even on the exact binary value.</summary>
        public static string FormatFixed(double value, int decimals)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            bool negative = bits < 0;
            int exponentBits = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;

            BigInteger mantissa;
            int exponent;
            if (exponentBits == 0)
            {
                mantissa = fraction;
                exponent = -1074;
            }
            else
            {
                mantissa = fraction | (1L << 52);
                exponent = exponentBits - 1075;
            }

            BigInteger scaled;
            BigInteger pow10 = BigInteger.Pow(10, decimals);
            if (mantissa.IsZero)
            {
                scaled = BigInteger.Zero;
            }
            else if (exponent >= 0)
            {
                scaled = (mantissa << exponent) * pow10;
            }
            else
            {
                BigInteger numerator = mantissa * pow10;
                BigInteger denominator = BigInteger.One << -exponent;
                scaled = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
                BigInteger twice = remainder << 1;
                int cmp = twice.CompareTo(denominator);
                if (cmp > 0 || (cmp == 0 && !scaled.IsEven)) scaled += BigInteger.One;
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
    }
}
