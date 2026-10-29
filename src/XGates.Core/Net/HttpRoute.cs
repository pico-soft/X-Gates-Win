using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;

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

/// <summary>Общие кусочки HttpURLConnection-семантики для fetch/stream.</summary>
internal static class HttpUrlConnectionCompat
{
    /// <summary>Тело с «readTimeout»: таймаут на КАЖДОЕ чтение (как SO_TIMEOUT), а не на всю загрузку.</summary>
    public static async Task<byte[]> ReadAllWithReadTimeout(Stream s, int readTimeoutMs, CancellationToken outer = default)
    {
        var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        while (true)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(Math.Max(1, readTimeoutMs));
            int n;
            try { n = await s.ReadAsync(buf, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!outer.IsCancellationRequested) { throw new TimeoutException("Read timed out"); }
            if (n <= 0) break;
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
