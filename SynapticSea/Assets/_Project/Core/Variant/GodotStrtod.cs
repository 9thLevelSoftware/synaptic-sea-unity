namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// Exact port of Godot 4.7 <c>built_in_strtod</c> (the Tcl strtod) from <c>core/string/ustring.cpp</c>.
    /// Godot's JSON parser and <c>String.to_float()</c> use it. It is NOT correctly rounded
    /// (e.g. it keeps at most 18 mantissa digits and scales by a product of powers of ten),
    /// so matching Godot's parsed doubles bit-for-bit requires this instead of <c>double.Parse</c>.
    /// </summary>
    public static class GodotStrtod
    {
        const int MaxExponent = 511;

        static readonly double[] PowersOf10 = { 10.0, 100.0, 1.0e4, 1.0e8, 1.0e16, 1.0e32, 1.0e64, 1.0e128, 1.0e256 };

        static bool IsDigit(char c) => c >= '0' && c <= '9';

        static char At(string s, int i) => i < s.Length ? s[i] : '\0';

        /// <summary>Parses from <paramref name="start"/>; <paramref name="end"/> receives the index after the number.</summary>
        public static double Parse(string s, int start, out int end)
        {
            bool sign, expSign = false;
            double fraction;
            int exp = 0;
            int fracExp;
            int mantSize;
            int decPt;
            int pExp;

            int p = start;
            while (At(s, p) == ' ' || At(s, p) == '\t' || At(s, p) == '\n') p++;
            if (At(s, p) == '-')
            {
                sign = true;
                p++;
            }
            else
            {
                if (At(s, p) == '+') p++;
                sign = false;
            }

            decPt = -1;
            for (mantSize = 0; ; mantSize++)
            {
                char c = At(s, p);
                if (!IsDigit(c))
                {
                    if (c != '.' || decPt >= 0) break;
                    decPt = mantSize;
                }
                p++;
            }

            pExp = p;
            p -= mantSize;
            if (decPt < 0) decPt = mantSize;
            else mantSize -= 1;

            if (mantSize > 18)
            {
                fracExp = decPt - 18;
                mantSize = 18;
            }
            else
            {
                fracExp = decPt - mantSize;
            }

            if (mantSize == 0)
            {
                end = start;
                return sign ? -0.0 : 0.0;
            }

            int frac1 = 0;
            for (; mantSize > 9; mantSize--)
            {
                char c = At(s, p);
                p++;
                if (c == '.')
                {
                    c = At(s, p);
                    p++;
                }
                frac1 = 10 * frac1 + (c - '0');
            }
            int frac2 = 0;
            for (; mantSize > 0; mantSize--)
            {
                char c = At(s, p);
                p++;
                if (c == '.')
                {
                    c = At(s, p);
                    p++;
                }
                frac2 = 10 * frac2 + (c - '0');
            }
            fraction = (1.0e9 * frac1) + frac2;

            p = pExp;
            if (At(s, p) == 'E' || At(s, p) == 'e')
            {
                p++;
                if (At(s, p) == '-')
                {
                    expSign = true;
                    p++;
                }
                else
                {
                    if (At(s, p) == '+') p++;
                    expSign = false;
                }
                if (!IsDigit(At(s, p)))
                {
                    end = pExp;
                    return sign ? -fraction : fraction;
                }
                while (IsDigit(At(s, p)))
                {
                    exp = unchecked(exp * 10 + (At(s, p) - '0'));
                    p++;
                }
            }
            exp = expSign ? fracExp - exp : fracExp + exp;

            if (exp < 0)
            {
                expSign = true;
                exp = -exp;
            }
            else
            {
                expSign = false;
            }

            if (exp > MaxExponent) exp = MaxExponent;

            double dblExp = 1.0;
            for (int d = 0; exp != 0; exp >>= 1, ++d)
            {
                if ((exp & 1) != 0) dblExp *= PowersOf10[d];
            }
            if (expSign) fraction /= dblExp;
            else fraction *= dblExp;

            end = p;
            return sign ? -fraction : fraction;
        }

        public static double Parse(string s) => Parse(s ?? string.Empty, 0, out _);
    }
}
