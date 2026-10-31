using System.Globalization;

namespace XGates.Core.Compat;

/// <summary>
/// Строковые функции Kotlin stdlib с ТОЧНОЙ семантикой (на JVM/Android). Нужны для паритета с Android-кодом:
/// у .NET свои правила «пробельного» символа и разбора чисел, и одна и та же подписка дала бы другой результат.
/// Kotlin <c>Char.isWhitespace()</c> = <c>Character.isWhitespace(ch) || Character.isSpaceChar(ch)</c>.
/// </summary>
internal static class KotlinText
{
    /// <summary>Kotlin <c>Char.isWhitespace()</c>: Zs/Zl/Zp + \t \n \v \f \r + U+001C..U+001F (U+0085 — НЕ пробел).</summary>
    public static bool IsKotlinWhitespace(char c)
    {
        if (c is '\t' or '\n' or '\u000B' or '\f' or '\r' or >= '\u001C' and <= '\u001F') return true;
        var cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }

    /// <summary>Kotlin <c>isBlank()</c> (null → true, как <c>isNullOrBlank()</c>).</summary>
    public static bool IsBlank(this string? s)
    {
        if (s == null) return true;
        foreach (var c in s) if (!IsKotlinWhitespace(c)) return false;
        return true;
    }

    public static bool IsNotBlank(this string? s) => !s.IsBlank();

    /// <summary>Kotlin <c>takeIf { it.isNotBlank() }</c>.</summary>
    public static string? TakeIfNotBlank(this string? s) => s.IsNotBlank() ? s : null;

    /// <summary>Kotlin <c>ifBlank { fallback }</c>.</summary>
    public static string IfBlank(this string s, string fallback) => s.IsBlank() ? fallback : s;

    /// <summary>Kotlin <c>ifBlank { null }</c>.</summary>
    public static string? IfBlankNull(this string? s) => s.IsBlank() ? null : s;

    /// <summary>Kotlin <c>String.trim()</c> — по <see cref="IsKotlinWhitespace"/> (не Java <c>trim()</c> ≤ ' ').</summary>
    public static string KTrim(this string s)
    {
        int start = 0, end = s.Length - 1;
        while (start <= end && IsKotlinWhitespace(s[start])) start++;
        while (end >= start && IsKotlinWhitespace(s[end])) end--;
        return s.Substring(start, end - start + 1);
    }

    /// <summary>Kotlin <c>trimStart()</c>.</summary>
    public static string KTrimStart(this string s)
    {
        int start = 0;
        while (start < s.Length && IsKotlinWhitespace(s[start])) start++;
        return s.Substring(start);
    }

    /// <summary>Kotlin <c>lowercase()</c> (= <c>toLowerCase(Locale.ROOT)</c>).</summary>
    public static string Lowercase(this string s) => s.ToLowerInvariant();

    /// <summary>Kotlin <c>removePrefix</c> (с учётом регистра).</summary>
    public static string RemovePrefix(this string s, string prefix) =>
        s.StartsWith(prefix, StringComparison.Ordinal) ? s.Substring(prefix.Length) : s;

    /// <summary>Kotlin <c>substringBefore(char)</c>: до первого вхождения; нет вхождения — вся строка.</summary>
    public static string SubstringBefore(this string s, char delimiter)
    {
        var i = s.IndexOf(delimiter);
        return i < 0 ? s : s.Substring(0, i);
    }

    /// <summary>Kotlin <c>removeSuffix</c> (с учётом регистра).</summary>
    public static string RemoveSuffix(this string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.Ordinal) ? s.Substring(0, s.Length - suffix.Length) : s;

    /// <summary>Kotlin <c>removeSurrounding(prefix, suffix)</c>: снимает, только если есть И префикс, И суффикс.</summary>
    public static string RemoveSurrounding(this string s, string prefix, string suffix)
    {
        if (s.Length >= prefix.Length + suffix.Length
            && s.StartsWith(prefix, StringComparison.Ordinal) && s.EndsWith(suffix, StringComparison.Ordinal))
            return s.Substring(prefix.Length, s.Length - prefix.Length - suffix.Length);
        return s;
    }

    /// <summary>
    /// Kotlin <c>String.toIntOrNull()</c>: необязательный знак '+'/'-', затем цифры (как <c>Character.digit(c, 10)</c> —
    /// любые десятичные цифры Unicode Nd); переполнение Int → null.
    /// </summary>
    public static int? ToIntOrNull(this string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        int i = 0; bool negative = false;
        long limit = -int.MaxValue;
        char first = s[0];
        if (first < '0')
        {
            if (s.Length == 1) return null;
            i = 1;
            if (first == '-') { negative = true; limit = int.MinValue; }
            else if (first != '+') return null;
        }
        long result = 0;
        for (; i < s.Length; i++)
        {
            int d = DigitOf(s[i]);
            if (d < 0) return null;
            result = result * 10 - d;
            if (result < limit) return null;
        }
        return negative ? (int)result : (int)-result;
    }

    /// <summary>Kotlin <c>String.toLongOrNull()</c> — та же логика для Long.</summary>
    public static long? ToLongOrNull(this string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        int i = 0; bool negative = false;
        char first = s[0];
        if (first < '0')
        {
            if (s.Length == 1) return null;
            i = 1;
            if (first == '-') negative = true;
            else if (first != '+') return null;
        }
        long result = 0;
        for (; i < s.Length; i++)
        {
            int d = DigitOf(s[i]);
            if (d < 0) return null;
            try { result = checked(result * 10 - d); } catch (OverflowException) { return null; }
        }
        if (negative) return result;
        if (result == long.MinValue) return null;
        return -result;
    }

    /// <summary>Java <c>Character.digit(c, 10)</c>: только категория Nd (надстрочные '²' и т.п. — НЕ цифры).</summary>
    private static int DigitOf(char c) =>
        char.GetUnicodeCategory(c) == UnicodeCategory.DecimalDigitNumber ? CharUnicodeInfo.GetDecimalDigitValue(c) : -1;

    /// <summary>
    /// Kotlin <c>startsWith(prefix, ignoreCase = true)</c> (= Java <c>regionMatches(true, …)</c>): символы равны, если
    /// совпали как есть, в верхнем регистре или в нижнем регистре от верхнего.
    /// </summary>
    public static bool StartsWithIgnoreCase(this string s, string prefix) => RegionMatchesIgnoreCase(s, 0, prefix);

    /// <summary>Kotlin <c>contains(other, ignoreCase = true)</c>.</summary>
    public static bool ContainsIgnoreCase(this string s, string other)
    {
        for (int i = 0; i + other.Length <= s.Length; i++)
            if (RegionMatchesIgnoreCase(s, i, other)) return true;
        return false;
    }

    private static bool RegionMatchesIgnoreCase(string s, int offset, string other)
    {
        if (offset < 0 || other.Length > s.Length - offset) return false;
        for (int i = 0; i < other.Length; i++)
        {
            char c1 = s[offset + i], c2 = other[i];
            if (c1 == c2) continue;
            char u1 = char.ToUpperInvariant(c1), u2 = char.ToUpperInvariant(c2);
            if (u1 == u2) continue;
            if (char.ToLowerInvariant(u1) == char.ToLowerInvariant(u2)) continue;
            return false;
        }
        return true;
    }

    /// <summary>Kotlin <c>lines()</c>: разбиение по \r\n, \n, \r (пустой хвост сохраняется: "a\n" → ["a", ""]).</summary>
    public static List<string> KLines(this string s)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\r' && c != '\n') continue;
            result.Add(s.Substring(start, i - start));
            if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
            start = i + 1;
        }
        result.Add(s.Substring(start));
        return result;
    }

    /// <summary>
    /// Kotlin <c>trimIndent()</c>: убрать первую/последнюю строку, если они пустые; срезать минимальный общий отступ
    /// непустых строк; склеить через "\n". Шаблоны конфигов xray в Kotlin — raw-строки с trimIndent() ПОСЛЕ
    /// подстановки многострочных фрагментов, поэтому итоговые пробелы зависят от этого алгоритма — повторяем его.
    /// </summary>
    public static string TrimIndent(this string s)
    {
        var lines = s.KLines();
        int minCommonIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (line.IsBlank()) continue;
            int w = 0;
            while (w < line.Length && IsKotlinWhitespace(line[w])) w++;
            minCommonIndent = Math.Min(minCommonIndent, w);
        }
        if (minCommonIndent == int.MaxValue) minCommonIndent = 0;
        var sb = new System.Text.StringBuilder(s.Length);
        int lastIndex = lines.Count - 1;
        bool first = true;
        for (int index = 0; index < lines.Count; index++)
        {
            var value = lines[index];
            if ((index == 0 || index == lastIndex) && value.IsBlank()) continue;
            if (!first) sb.Append('\n');
            first = false;
            sb.Append(value.Length > minCommonIndent ? value.Substring(minCommonIndent) : "");
        }
        return sb.ToString();
    }

    /// <summary>Kotlin <c>"=".repeat(n)</c>.</summary>
    public static string Repeat(this string s, int n) => n <= 0 ? "" : string.Concat(Enumerable.Repeat(s, n));
}
