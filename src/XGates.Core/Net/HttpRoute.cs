using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using XGates.Core.Platform;

namespace XGates.Core.Net;

/// <summary>
/// Маршрут HTTP-запроса — аналог Kotlin-лямбды <c>open: (URL) -&gt; URLConnection</c> в SubscriptionFetcher/CascadeFetch
/// (<c>it.openConnection()</c> / <c>it.openConnection(proxy)</c> / <c>network.openConnection(it)</c>).
/// ВАЖНО для Windows: «напрямую» = БЕЗ системного прокси (.NET по умолчанию берёт прокси WinINET — тогда «прямой»
/// путь молча шёл бы через чужой/наш же прокси и ломал смысл ступеней каскада).
/// </summary>
public abstract record HttpRoute
{
    private HttpRoute() { }

    public sealed record Direct : HttpRoute;
    /// <summary>SOCKS5 (домен резолвит прокси — как Proxy.Type.SOCKS в Java c неразрешённым адресом).</summary>
    public sealed record Socks(string host, int port) : HttpRoute;
    public sealed record HttpProxy(string host, int port) : HttpRoute;
    /// <summary>Системный прокси Windows (только ЧТЕНИЕ настроек, ничего не меняем).</summary>
    public sealed record SystemProxy(IWebProxy proxy) : HttpRoute;

    public static readonly HttpRoute DIRECT = new Direct();

    /// <summary>Свежий handler без авто-редиректов (их проходим сами, как в Kotlin), gzip — как HttpURLConnection/OkHttp.</summary>
    internal SocketsHttpHandler CreateHandler(int connectTimeoutMs)
    {
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip,
            ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(1, connectTimeoutMs)),
            PooledConnectionLifetime = TimeSpan.Zero,
            UseCookies = false,
        };
        switch (this)
        {
            case Direct: h.UseProxy = false; break;
            case Socks s: h.UseProxy = true; h.Proxy = new WebProxy($"socks5://{s.host}:{s.port}"); break;
            case HttpProxy p: h.UseProxy = true; h.Proxy = new WebProxy($"http://{p.host}:{p.port}"); break;
            case SystemProxy sp: h.UseProxy = true; h.Proxy = sp.proxy; break;
        }
        return h;
    }
}

/// <summary>Имена исключений «как в Java» — Kotlin-код классифицирует отказы по ним (UnknownHostException…).</summary>
public static class JavaExceptionNames
{
    public static string simpleName(Exception e)
    {
        for (Exception? x = e; x != null; x = x.InnerException)
        {
            switch (x)
            {
                case SocketException se when se.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain:
                    return "UnknownHostException";
                case SocketException se when se.SocketErrorCode == SocketError.TimedOut:
                    return "SocketTimeoutException";
                case SocketException se when se.SocketErrorCode == SocketError.ConnectionRefused:
                    return "ConnectException";
                case SocketException se when se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted:
                    return "SocketException";
                case TimeoutException:
                    return "SocketTimeoutException";
                case TaskCanceledException tce when tce.InnerException is TimeoutException:
                    return "SocketTimeoutException";
                case HttpBodyTooLargeException:
                    return "ResponseTooLarge";
                case OperationCanceledException:
                    return "SocketTimeoutException";
                case AuthenticationException:
                    return "SSLHandshakeException";
            }
            if (x is HttpRequestException hre && hre.HttpRequestError == HttpRequestError.NameResolutionError) return "UnknownHostException";
            if (x is HttpRequestException hre2 && hre2.HttpRequestError == HttpRequestError.ProxyTunnelError) return "SocketException";
        }
        return e switch
        {
            HttpRequestException => "IOException",
            IOException => "IOException",
            UriFormatException => "MalformedURLException",
            _ => e.GetType().Name,
        };
    }

    /// <summary>Сообщение самого «глубокого» осмысленного исключения (Java getMessage() обычно конкретнее обёртки .NET).</summary>
    public static string? message(Exception e)
    {
        Exception x = e;
        while (x.InnerException != null && x is HttpRequestException or TaskCanceledException) x = x.InnerException;
        return x.Message;
    }
}

/// <summary>Разжатое тело больше разрешённого предела; не является обычным сетевым обрывом.</summary>
internal sealed class HttpBodyTooLargeException(long limit) : IOException($"тело ответа больше {limit} байт");

/// <summary>Общие кусочки HttpURLConnection-семантики для fetch/stream.</summary>
internal static class HttpUrlConnectionCompat
{
    public static long deadlineAfterMs(int timeoutMs) => JavaSystem.nanoTime() + Math.Max(1, timeoutMs) * 1_000_000L;

    /// <summary>Остаток до абсолютного monotonic-deadline; 0 означает, что время уже вышло.</summary>
    public static int remainingMs(long deadlineNanos)
    {
        var left = deadlineNanos - JavaSystem.nanoTime();
        if (left <= 0) return 0;
        return (int)Math.Min(int.MaxValue, (left + 999_999L) / 1_000_000L);
    }

    /// <summary>
    /// Тело с «readTimeout» на КАЖДОЕ чтение, абсолютным deadline и лимитом РАЗЖАТЫХ байт. Читаем максимум
    /// на один байт сверх остатка: ответ ровно [maxBytes] допустим, а следующий байт уже отклоняется.
    /// </summary>
    public static async Task<byte[]> ReadAllWithReadTimeout(
        Stream s, int readTimeoutMs, int maxBytes, long deadlineNanos, CancellationToken outer = default)
    {
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        while (true)
        {
            var remaining = remainingMs(deadlineNanos);
            if (remaining == 0) throw new TimeoutException("Общий таймаут загрузки");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(Math.Min(Math.Max(1, readTimeoutMs), remaining));
            int n;
            var allowed = Math.Min(buf.Length, maxBytes - (int)ms.Length + 1);
            try { n = await s.ReadAsync(buf.AsMemory(0, allowed), cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!outer.IsCancellationRequested)
            {
                throw new TimeoutException(remainingMs(deadlineNanos) == 0 ? "Общий таймаут загрузки" : "Read timed out");
            }
            if (n <= 0) break;
            if (ms.Length + n > maxBytes) throw new HttpBodyTooLargeException(maxBytes);
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    public static string? Header(HttpResponseMessage r, string name)
    {
        if (r.Headers.TryGetValues(name, out var v)) return v.FirstOrDefault();
        if (r.Content.Headers.TryGetValues(name, out var c)) return c.FirstOrDefault();
        return null;
    }

    public static HttpRequestMessage Get(string url, string userAgent)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return req;
    }
}
