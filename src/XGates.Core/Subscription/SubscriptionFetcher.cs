using System.Globalization;
using System.Text;
using XGates.Core.Compat;
using XGates.Core.Net;
using XGates.Core.Platform;

namespace XGates.Core.Subscription;

/// <summary>Богатый результат загрузки подписки — для точной классификации отказа и диагностики.</summary>
public sealed record FetchResult
{
    public FetchResult(bool ok, string finalUrl, int httpCode, string? contentType, long contentLength, int bodyBytes, string body,
        string? exceptionClass, string? errorMessage, IReadOnlyList<string>? redirectChain = null, int? retryAfterSec = null)
    {
        this.ok = ok; this.finalUrl = finalUrl; this.httpCode = httpCode; this.contentType = contentType; this.contentLength = contentLength;
        this.bodyBytes = bodyBytes; this.body = body; this.exceptionClass = exceptionClass; this.errorMessage = errorMessage;
        this.redirectChain = redirectChain ?? []; this.retryAfterSec = retryAfterSec;
    }

    public bool ok { get; init; }
    public string finalUrl { get; init; }          // URL после нормализации и редиректов
    public int httpCode { get; init; }             // -1, если до ответа не дошли (сеть/исключение)
    public string? contentType { get; init; }
    public long contentLength { get; init; }       // из заголовка (может быть -1)
    public int bodyBytes { get; init; }            // фактически скачано байт
    public string body { get; init; }              // тело (UTF-8); при не-2xx — тело ошибки (для диагностики)
    public string? exceptionClass { get; init; }   // напр. UnknownHostException / SocketTimeoutException (имена — как в Java, см. JavaExceptionNames)
    public string? errorMessage { get; init; }
    public IReadOnlyList<string> redirectChain { get; init; }   // все URL по цепочке редиректов (для диагностики)
    public int? retryAfterSec { get; init; }       // заголовок Retry-After (сек), если сервер прислал (HTTP 429/503)
}

/// <summary>
/// Скачивание тела подписки. Порт 1:1 с subscription/SubscriptionFetcher.kt (там — HttpURLConnection).
/// САМИ проходим редиректы, В ТОМ ЧИСЛЕ http↔https. UA/таймаут — из настроек (панели по неизвестному UA часто
/// отдают Clash YAML). Таймауты — как у HttpURLConnection: connectTimeout на соединение, readTimeout на КАЖДОЕ чтение.
/// (На Android cleartext http:// блокировался политикой; на Windows такого ограничения нет.)
///
/// ВНИМАНИЕ: [fetch] синхронный — вызывать в ФОНОВОМ потоке.
/// </summary>
public static class SubscriptionFetcher
{
    private const int MAX_REDIRECTS = 5;

    /**
     * Заголовок Retry-After (HTTP 429/503): либо число секунд, либо HTTP-дата. Возвращаем секунды до
     * повтора (0..) или null, если заголовка/разобрать нет. Отрицательную дельту зажимаем в 0.
     */
    internal static int? parseRetryAfter(string? raw)
    {
        var v = raw?.KTrim() ?? "";
        if (v.Length == 0) return null;
        if (v.ToIntOrNull() is int secs) return secs.coerceAtLeast(0);   // форма «секунды»
        try                                                             // форма HTTP-дата (RFC 1123)
        {
            if (!DateTimeOffset.TryParseExact(v, "ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var when)) return null;
            return (int)((when.ToUnixTimeMilliseconds() - JavaSystem.currentTimeMillis()) / 1000L).coerceAtLeast(0L);
        }
        catch (Exception) { return null; }
    }

    /**
     * [route] — как открыть соединение для очередного URL: напрямую, через прокси (SOCKS/HTTP) или системный прокси.
     * Редиректы (в т.ч. http↔https) проходятся тем же способом.
     */
    public static FetchResult fetch(string url, string userAgent, int timeoutMs, HttpRoute? route = null)
    {
        route ??= HttpRoute.DIRECT;
        var current = url;
        var redirects = 0;
        var hops = new List<string>();
        try
        {
            while (true)
            {
                hops.Add(current);
                using var handler = route.CreateHandler(timeoutMs);
                using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                HttpResponseMessage resp;
                try
                {
                    using var headersCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs) * 2L));
                    resp = http.SendAsync(HttpUrlConnectionCompat.Get(current, userAgent), HttpCompletionOption.ResponseHeadersRead, headersCts.Token)
                        .GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    return new FetchResult(
                        ok: false, finalUrl: current, httpCode: -1, contentType: null,
                        contentLength: -1, bodyBytes: 0, body: "",
                        exceptionClass: JavaExceptionNames.simpleName(e), errorMessage: JavaExceptionNames.message(e), redirectChain: hops);
                }
                using (resp)
                {
                    int code = (int)resp.StatusCode;
                    if (code is >= 300 and <= 399 && redirects < MAX_REDIRECTS)
                    {
                        var loc = HttpUrlConnectionCompat.Header(resp, "Location");
                        if (loc.IsBlank()) break;
                        current = new Uri(new Uri(current), loc!).ToString();   // разрешаем относительный Location
                        redirects++;
                        continue;
                    }

                    var contentType = resp.Content.Headers.ContentType?.ToString();
                    var contentLength = resp.Content.Headers.ContentLength ?? -1;
                    var retryAfter = parseRetryAfter(HttpUrlConnectionCompat.Header(resp, "Retry-After"));
                    string body;
                    try
                    {
                        var s = resp.Content.ReadAsStream();
                        body = Encoding.UTF8.GetString(HttpUrlConnectionCompat.ReadAllWithReadTimeout(s, timeoutMs).GetAwaiter().GetResult());
                    }
                    catch (Exception)
                    {
                        body = "";
                    }
                    return new FetchResult(
                        ok: code is >= 200 and <= 299,
                        finalUrl: current, httpCode: code, contentType: contentType,
                        contentLength: contentLength, bodyBytes: Encoding.UTF8.GetByteCount(body),
                        body: body, exceptionClass: null, errorMessage: null, redirectChain: hops,
                        retryAfterSec: retryAfter);
                }
            }
            // вышли по исчерпанию редиректов без финального ответа
            return new FetchResult(
                ok: false, finalUrl: current, httpCode: -1, contentType: null,
                contentLength: -1, bodyBytes: 0, body: "",
                exceptionClass: "TooManyRedirects", errorMessage: $"редиректов больше {MAX_REDIRECTS}", redirectChain: hops);
        }
        catch (Exception e)
        {
            return new FetchResult(
                ok: false, finalUrl: current, httpCode: -1, contentType: null,
                contentLength: -1, bodyBytes: 0, body: "",
                exceptionClass: JavaExceptionNames.simpleName(e), errorMessage: JavaExceptionNames.message(e), redirectChain: hops);
        }
    }
}
