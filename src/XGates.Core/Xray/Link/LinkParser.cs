using XGates.Core.Compat;

namespace XGates.Core.Xray.Link;

/// <summary>Контракт парсера одной схемы: строка ссылки → нормализованный профиль (или null при ошибке разбора).</summary>
public delegate ServerProfile? LinkParser(string uri);

/// <summary>Общие помощники разбора (query, url-decode) — переиспользуются всеми парсерами.</summary>
internal static class LinkUtil
{
    /// <summary>rawQuery ("a=1&amp;b=%2Fx") → map; значения percent-decoded в UTF-8. Повтор ключа — побеждает последний (toMap).</summary>
    public static IReadOnlyDictionary<string, string> queryParams(string? rawQuery)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(rawQuery)) return map;
        foreach (var pair in rawQuery.Split('&'))
        {
            int i = pair.IndexOf('=');
            if (i <= 0) continue;
            map[pair.Substring(0, i)] = urlDecode(pair.Substring(i + 1));
        }
        return map;
    }

    public static string urlDecode(string s)
    {
        try { return JavaUrlDecoder.Decode(s); } catch (Exception) { return s; }
    }

    /// <summary>Kotlin <c>q["key"]</c>.</summary>
    public static string? Get(this IReadOnlyDictionary<string, string> q, string key) => q.TryGetValue(key, out var v) ? v : null;
}
