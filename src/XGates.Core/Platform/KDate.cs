using System.Globalization;

namespace XGates.Core.Platform;

/// <summary>
/// Аналог <c>SimpleDateFormat(pattern, Locale.US)</c> для фиксированных форматов меток в данных
/// ("yyyy-MM-dd HH:mm", "yyyy-MM-dd") — ЛОКАЛЬНОЕ время, как на Android. Метки совместимы с файлами Android.
/// </summary>
public static class KDate
{
    public const string TS = "yyyy-MM-dd HH:mm";
    public const string DAY = "yyyy-MM-dd";

    public static string format(string pattern, DateTime local) => local.ToString(pattern, CultureInfo.InvariantCulture);

    public static string now(string pattern = TS) => format(pattern, DateTime.Now);

    /// <summary>Эпоха-мс локальной метки; null — не разобралась (как parse(...) → исключение → runCatching null).</summary>
    public static long? parseMs(string? s, string pattern = TS)
    {
        if (s == null) return null;
        if (!DateTime.TryParseExact(s, pattern, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)) return null;
        return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
    }
}
