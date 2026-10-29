using System.Globalization;
using System.Text.RegularExpressions;

namespace XGates.Core.Compat;

/// <summary>
/// Числовые преобразования Java (<c>Double.valueOf</c>, <c>Double.toString</c>, <c>Long.parseLong(s, radix)</c>,
/// приведение double→int), нужные порту <c>org.json</c> Android. Семантика — как на JVM, не как в .NET.
/// </summary>
internal static partial class JavaNumbers
{
    // Java FloatingDecimal: [+-]? (NaN | Infinity | десятичное [eE]exp? | hex-float p-exp) [fFdD]?
    [GeneratedRegex(@"\A[+-]?(?:NaN|Infinity|(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?[fFdD]?)\z")]
    private static partial Regex DecimalFloat();

    [GeneratedRegex(@"\A([+-]?)0[xX]([0-9a-fA-F]*)(?:\.([0-9a-fA-F]*))?[pP]([+-]?[0-9]+)[fFdD]?\z")]
    private static partial Regex HexFloat();

    /// <summary>Java <c>Double.valueOf(String)</c>/<c>parseDouble</c>: null — NumberFormatException.</summary>
    public static double? ParseJavaDouble(string s)
    {
        // Java сначала делает trim() (символы ≤ ' ').
        string t = JavaTrim(s);
        if (t.Length == 0) return null;
        if (DecimalFloat().IsMatch(t))
        {
            bool neg = t[0] == '-';
            string body = t[0] is '+' or '-' ? t.Substring(1) : t;
            if (body == "NaN") return double.NaN;
            if (body == "Infinity") return neg ? double.NegativeInfinity : double.PositiveInfinity;
            if (body[^1] is 'f' or 'F' or 'd' or 'D')
            {
                bool isFloat = body[^1] is 'f' or 'F';
                body = body.Substring(0, body.Length - 1);
                double v = double.Parse(body, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
                if (isFloat) v = (float)v;
                return neg ? -v : v;
            }
            double r = double.Parse(body, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
            return neg ? -r : r;
        }
        var m = HexFloat().Match(t);
        if (m.Success && (m.Groups[2].Value.Length + m.Groups[3].Value.Length) > 0)
        {
            string digits = m.Groups[2].Value + m.Groups[3].Value;
            int fracLen = m.Groups[3].Value.Length;
            double mant = 0;
            foreach (var c in digits) mant = mant * 16 + Convert.ToInt32(c.ToString(), 16);
            int exp = int.Parse(m.Groups[4].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            double v = mant * Math.Pow(2, exp - 4 * fracLen);
            return m.Groups[1].Value == "-" ? -v : v;
        }
        return null;
    }

    /// <summary>Java <c>String.trim()</c>: срезает символы ≤ U+0020.</summary>
    public static string JavaTrim(string s)
    {
        int st = 0, len = s.Length;
        while (st < len && s[st] <= ' ') st++;
        while (st < len && s[len - 1] <= ' ') len--;
        return st > 0 || len < s.Length ? s.Substring(st, len - st) : s;
    }

    /// <summary>Java <c>Long.parseLong(s, radix)</c>: знак +/-, цифры радикса; null — NumberFormatException.</summary>
    public static long? ParseJavaLong(string s, int radix)
    {
        if (s.Length == 0) return null;
        int i = 0; bool negative = false;
        long limit = -long.MaxValue;
        char first = s[0];
        if (first < '0')
        {
            if (first == '-') { negative = true; limit = long.MinValue; }
            else if (first != '+') return null;
            if (s.Length == 1) return null;
            i++;
        }
        long multmin = limit / radix;
        long result = 0;
        while (i < s.Length)
        {
            int digit = JavaDigit(s[i++], radix);
            if (digit < 0 || result < multmin) return null;
            result *= radix;
            if (result < limit + digit) return null;
            result -= digit;
        }
        return negative ? result : -result;
    }

    /// <summary>Java <c>Character.digit(c, radix)</c> (ASCII-буквы, полноширинные буквы, цифры Nd).</summary>
    private static int JavaDigit(char c, int radix)
    {
        int v = -1;
        if (c >= 'a' && c <= 'z') v = c - 'a' + 10;
        else if (c >= 'A' && c <= 'Z') v = c - 'A' + 10;
        else if (c >= 'Ａ' && c <= 'Ｚ') v = c - 'Ａ' + 10;
        else if (c >= 'ａ' && c <= 'ｚ') v = c - 'ａ' + 10;
        else if (char.GetUnicodeCategory(c) == UnicodeCategory.DecimalDigitNumber) v = CharUnicodeInfo.GetDecimalDigitValue(c);
        return v >= 0 && v < radix ? v : -1;
    }

    /// <summary>Java <c>(int) d</c>: NaN → 0, насыщение на границах, отбрасывание дробной части.</summary>
    public static int D2I(double d)
    {
        if (double.IsNaN(d)) return 0;
        if (d >= int.MaxValue) return int.MaxValue;
        if (d <= int.MinValue) return int.MinValue;
        return (int)d;
    }

    /// <summary>Java <c>(long) d</c>.</summary>
    public static long D2L(double d)
    {
        if (double.IsNaN(d)) return 0;
        if (d >= long.MaxValue) return long.MaxValue;
        if (d <= long.MinValue) return long.MinValue;
        return (long)d;
    }

    /// <summary>
    /// Java <c>Double.toString(d)</c>: кратчайшие цифры; [1e-3, 1e7) — «123.45»/«443.0», иначе «1.0E10»/«1.5E-5».
    /// </summary>
    public static string JavaDoubleToString(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";

        string sign = d < 0 ? "-" : "";
        double a = Math.Abs(d);
        // Кратчайшие цифры, восстанавливающие значение: "E16" не годится (лишние цифры) → "R".
        string r = a.ToString("R", CultureInfo.InvariantCulture);
        string mant = r; int exp10 = 0;
        int e = r.IndexOfAny(['E', 'e']);
        if (e >= 0) { mant = r.Substring(0, e); exp10 = int.Parse(r.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); }
        int dot = mant.IndexOf('.');
        string intPart = dot >= 0 ? mant.Substring(0, dot) : mant;
        string fracPart = dot >= 0 ? mant.Substring(dot + 1) : "";
        string digits = (intPart + fracPart).TrimStart('0');
        // десятичная позиция точки относительно начала digits
        int leadingZeros = (intPart + fracPart).Length - (intPart + fracPart).TrimStart('0').Length;
        int pointPos = intPart.Length + exp10 - leadingZeros;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        if (a >= 1e-3 && a < 1e7)
        {
            string ip, fp;
            if (pointPos <= 0) { ip = "0"; fp = new string('0', -pointPos) + digits; }
            else if (pointPos >= digits.Length) { ip = digits + new string('0', pointPos - digits.Length); fp = "0"; }
            else { ip = digits.Substring(0, pointPos); fp = digits.Substring(pointPos); }
            return sign + ip + "." + fp;
        }
        string m1 = digits.Substring(0, 1);
        string m2 = digits.Length > 1 ? digits.Substring(1) : "0";
        return sign + m1 + "." + m2 + "E" + (pointPos - 1).ToString(CultureInfo.InvariantCulture);
    }
}
