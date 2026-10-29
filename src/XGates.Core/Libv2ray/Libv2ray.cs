using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XGates.Core.Libv2ray;

/// <summary>
/// Аналог статического API <c>libv2ray.Libv2ray</c> (AndroidLibXrayLite Lib v39, Xray-core v26.7.28) поверх xray.exe.
/// Kotlin: <c>Libv2ray.newCoreController(cb)</c>, <c>Libv2ray.measureOutboundDelay(cfg, url)</c>, <c>checkVersionX()</c>.
/// </summary>
public static partial class Libv2ray
{
    /// <summary>Путь к бинарю ядра. По умолчанию — <c>xray\xray.exe</c> рядом с приложением.</summary>
    public static string xrayPath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "xray", OperatingSystem.IsWindows() ? "xray.exe" : "xray");

    /// <summary>Сколько ждать «core: Xray … started». Старт процесса на Windows ~50–150 мс; с запасом на медленные ПК.</summary>
    public static TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary><c>initCoreEnv(assetPath, key)</c>: на Windows geo-файлы лежат рядом с xray.exe — задаётся путём бинаря.</summary>
    public static void initCoreEnv(string? xrayExecutable, string key)
    {
        if (!string.IsNullOrEmpty(xrayExecutable)) xrayPath = xrayExecutable;
    }

    public static CoreController newCoreController(CoreCallbackHandler callbackHandler) => new(callbackHandler);

    private static string? versionCache;

    [GeneratedRegex(@"Xray (\d+\.\d+\.\d+)")]
    private static partial Regex XrayVersion();

    /// <summary>Как <c>CheckVersionX()</c> («Lib v39, Xray-core v26.7.28»); на Windows библиотеки нет → «Xray-core v…».</summary>
    public static string checkVersionX()
    {
        if (versionCache != null) return versionCache;
        try
        {
            var psi = new ProcessStartInfo(xrayPath, "version")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var m = XrayVersion().Match(output);
            versionCache = m.Success ? "Xray-core v" + m.Groups[1].Value : output.Trim();
        }
        catch (Exception e) { return "Xray-core: " + e.Message; }
        return versionCache;
    }

    /// <summary>Свободный TCP-порт на петле (как ServerSocket(0) в Kotlin; гонка возможна — вызывающие повторяют).</summary>
    internal static int freePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    /// <summary>
    /// <c>MeasureOutboundDelay(config, url)</c> Lib v39: из конфига — только outbounds (+log), inbounds убраны, временный
    /// инстанс; затем <c>measureInstDelay</c>: ДВА GET подряд по одному keep-alive соединению, каждый ≤12 с, статус
    /// 200/204, тело дочитывается; результат — МИНИМУМ (второй обычно = чистый RTT через туннель). URL пуст →
    /// google generate_204. Ошибка — исключение (Kotlin ловит и даёт -1).
    /// На Windows временный инстанс — процесс xray.exe с socks-inbound на эфемерном порту (диал через ядро в Go
    /// заменён на SOCKS5 к нему; лишний локальный хоп — доли мс). Время старта процесса в задержку НЕ входит.
    /// </summary>
    public static long measureOutboundDelay(string configureFileContent, string url)
    {
        JsonObject src;
        try { src = JsonNode.Parse(configureFileContent)?.AsObject() ?? throw new FormatException("empty config"); }
        catch (Exception e) { throw new XrayCoreException("config load error: " + e.Message, e); }

        int port = freePort();
        var cfg = new JsonObject
        {
            ["log"] = src["log"]?.DeepClone() ?? new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["tag"] = "socks-in", ["listen"] = "127.0.0.1", ["port"] = port, ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false },
            }),
            ["outbounds"] = src["outbounds"]?.DeepClone() ?? new JsonArray(),
        };
        using var inst = XrayProcess.Start(xrayPath, cfg.ToJsonString(), StartTimeout);
        return measureInstDelay(port, url);
    }

    internal static long measureInstDelay(int socksPort, string url)
    {
        if (string.IsNullOrEmpty(url)) url = "https://www.google.com/generate_204";
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

        long minDuration = -1;
        bool success = false;
        Exception? lastErr = null;
        const int attempts = 2;
        for (int i = 0; i < attempts; i++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = client.Send(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
                resp.Content.ReadAsStream().CopyTo(Stream.Null);
                if (resp.StatusCode != HttpStatusCode.OK && resp.StatusCode != HttpStatusCode.NoContent)
                {
                    lastErr = new HttpRequestException("invalid status: " + (int)resp.StatusCode);
                    continue;
                }
            }
            catch (Exception e)
            {
                lastErr = e;
                continue;
            }
            long duration = sw.ElapsedMilliseconds;
            if (!success || duration < minDuration) minDuration = duration;
            success = true;
        }
        if (!success) throw new XrayCoreException(lastErr?.Message ?? "delay probe failed", lastErr);
        return minDuration;
    }
}
