using System.Text;
using XGates.Core.Compat;

namespace XGates.Core.Subscription;

/// <summary>
/// Тело подписки → список строк-ссылок.
///
/// Тело обычно закодировано base64 целиком (после декода каждая строка = ссылка), реже — плейн-текст.
/// Эвристика ЧИНЕНАЯ относительно Python-эталона: считаем декод «похожим на ссылки», если он содержит
/// ЛЮБУЮ известную схему (vless/vmess/trojan/ss) ИЛИ хотя бы "://" — а не только vless/trojan
/// (иначе подписки только из vmess/ss не распознавались).
///
/// Строки НЕ фильтруются на валидность — это делает ServerLinkParser на следующем шаге (Manager).
/// Возвращаются только непустые (обрезанные) строки.
/// </summary>
public static class SubscriptionDecoder
{
    private static readonly string[] KNOWN_SCHEMES = ["vless://", "vmess://", "trojan://", "ss://"];

    public static List<string> decode(string body)
    {
        var text = maybeBase64(body) ?? body;
        return text.KLines()
            .Select(it => it.KTrim())
            .Where(it => it.Length > 0)
            .ToList();
    }

    /// <summary>Пытается декодировать ВСЁ тело как base64; возвращает текст, только если он «похож на ссылки».</summary>
    private static string? maybeBase64(string body)
    {
        var compact = body.KTrim().Replace("\n", "").Replace("\r", "");
        if (compact.Length == 0) return null;
        try
        {
            var norm = compact.Replace('-', '+').Replace('_', '/');          // url-safe → std
            var padded = norm + "=".Repeat((4 - norm.Length % 4) % 4);        // паддинг
            var decoded = Encoding.UTF8.GetString(AndroidBase64.Decode(padded, AndroidBase64.NO_WRAP));
            return looksLikeLinks(decoded) ? decoded : null;
        }
        catch (Exception)
        {
            return null;   // не base64 (например, тело уже плейн-текст со ссылками) → откат к body
        }
    }

    private static bool looksLikeLinks(string text) =>
        KNOWN_SCHEMES.Any(it => text.ContainsIgnoreCase(it)) || text.Contains("://");
}
