using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Subscription;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;
using L = XGates.Core.Libv2ray.Libv2ray;

namespace XGates.Core.Tests.Libv2ray;

/// <summary>
/// Каскад загрузки на герметичной локальной сети: прямая ступень, редиректы, терминальные коды (обрыв каскада),
/// Retry-After, ступень «через свой прокси» через НАСТОЯЩИЙ xray-туннель (наш SOCKS на нестандартном порту),
/// маршрут «напрямую» НЕ использует системный прокси.
/// </summary>
[Collection("xray")]
public sealed class CascadeFetchIntegrationTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-cascade-" + Guid.NewGuid().ToString("N")));
    private readonly Func<int> savedPort = CascadeFetch.ownSocksPort;
    private readonly Func<string, System.Net.IWebProxy?> savedSys = CascadeFetch.systemProxyFor;

    public CascadeFetchIntegrationTests()
    {
        CascadeFetch.ownSocksPort = () => LocalTestNet.FreePort(); // по умолчанию «нашего прокси нет»
        CascadeFetch.systemProxyFor = _ => null;
    }

    public void Dispose()
    {
        CascadeFetch.ownSocksPort = savedPort;
        CascadeFetch.systemProxyFor = savedSys;
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private string U(string path) => $"http://127.0.0.1:{net.HttpPort}{path}";

    [Fact]
    public void Direct_stage_fetches_and_follows_redirect()
    {
        var r = CascadeFetch.fetch(ctx, U("/redirect?to=" + Uri.EscapeDataString("/sub")), "v2rayNG/1.8.0", 3000, 3000, 10_000, allowTempInstance: false);
        Assert.True(r.ok);
        Assert.Equal(FetchStage.DIRECT, r.stage);
        Assert.Equal(200, r.result!.httpCode);
        Assert.Equal(2, r.result.redirectChain.Count);
        Assert.True(SubscriptionManager.hasSupportedLinks(r.result.body));
        Assert.Single(r.attempts); // принято первой же ступенью
    }

    [Theory]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(403)]
    public void Terminal_http_code_stops_cascade(int code)
    {
        var r = CascadeFetch.fetch(ctx, U($"/status/{code}"), "ua", 3000, 3000, 10_000);
        Assert.False(r.ok);
        Assert.Equal(FetchStage.DIRECT, r.stage);
        Assert.Equal(code, r.result!.httpCode);
        Assert.Single(r.attempts); // обрыв сразу — остальные ступени (и temp-инстанс) не трогали
        if (code == 429) Assert.Equal(42, r.result.retryAfterSec);
    }

    [Fact]
    public void Own_proxy_stage_goes_through_real_xray_tunnel()
    {
        // «Прямой» путь мёртв (непринимающий порт), наш прокси жив → ступень 2 через настоящий xray-туннель.
        int server = net.StartVlessServer();
        int socks = LocalTestNet.FreePort();
        var p = VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{server}?type=tcp&security=none#t")!;
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(XrayConfigBuilder.buildForSpeedTest(p, socks), 0);
        try
        {
            CascadeFetch.ownSocksPort = () => socks;
            // acceptBody отвергает прямую ступень (как «заглушка блокировщика»), туннель отдаёт то же тело → принято.
            int calls = 0;
            var r = CascadeFetch.fetch(ctx, U("/sub"), "ua", 3000, 5000, 20_000,
                acceptBody: it => it.ok && ++calls > 1, allowTempInstance: false);
            Assert.True(r.ok, string.Join(" | ", r.attempts));   // одно падение на Windows без подробностей не разобрать
            Assert.Equal(FetchStage.OWN_PROXY, r.stage);
            Assert.False(r.attempts[0].accepted);
        }
        finally { core.stopLoop(); }
    }

    [Fact]
    public void Unreachable_direct_gives_java_like_exception_class()
    {
        int dead = LocalTestNet.FreePort();
        // Таймаут с запасом: Windows на отказ (RST) повторяет попытку соединения и отвечает «отказано» только через ~2 с —
        // при таймауте 2 с это выглядело таймаутом (первый прогон на Windows 2026-10-31).
        var r = SubscriptionFetcher.fetch($"http://127.0.0.1:{dead}/x", "ua", 10_000);
        Assert.False(r.ok);
        Assert.Equal(-1, r.httpCode);
        Assert.Equal("ConnectException", r.exceptionClass);
        var r2 = SubscriptionFetcher.fetch("http://nonexistent-host.invalid/x", "ua", 3000);
        Assert.Equal("UnknownHostException", r2.exceptionClass);
    }

    [Fact]
    public void Direct_route_ignores_system_proxy()
    {
        // Даже если системный прокси указывает в никуда, «напрямую» обязано идти мимо него.
        var saved = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new System.Net.WebProxy($"http://127.0.0.1:{LocalTestNet.FreePort()}");
            var r = SubscriptionFetcher.fetch(U("/sub"), "ua", 3000, HttpRoute.DIRECT);
            Assert.True(r.ok);
        }
        finally { HttpClient.DefaultProxy = saved; }
    }

    [Fact]
    public void Temp_instance_stage_skips_candidate_whose_core_fails_to_start()
    {
        // Находка порта №4: исключение запуска ядра temp-инстанса (здесь — битый ключ reality, ядро не стартует)
        // летело из fetchViaTempInstance наружу и обрывало ВЕСЬ каскад. Теперь кандидат пропускается, идём к следующему.
        int server = net.StartVlessServer();
        SubscriptionManager.init(ctx);
        var (_, add) = SubscriptionManager.addLocalFromBody(ctx,
            $"vless://{LocalTestNet.Uuid}@127.0.0.1:{LocalTestNet.FreePort()}?type=tcp&security=reality&pbk=PUB&sid=00&sni=s.example.com&fp=chrome#bad\n" +
            $"vless://{LocalTestNet.Uuid}@127.0.0.1:{server}?type=tcp&security=none#good", "local");
        Assert.True(add.ok, add.error);
        var all = SubscriptionManager.allServers(ctx);
        var bad = all.Single(p => p.remarks == "bad");
        // Оба «недавно рабочие»; битый быстрее → его temp-инстанс пробуется ПЕРВЫМ.
        SubscriptionManager.applySpeedResults(ctx, new Dictionary<string, double>
        {
            [SubscriptionManager.serverKey(bad)] = 90,
            [SubscriptionManager.serverKey(all.Single(p => p.remarks == "good"))] = 50,
        });
        Assert.Null(ServerSpeedTester.fetchViaTempInstance(ctx, bad, U("/sub"), "ua", 5000));   // раньше — исключение

        // Каскад целиком: тела не принимаем нигде (как «заглушка блокировщика»), чтобы дойти до 6-й ступени.
        var r = CascadeFetch.fetch(ctx, U("/sub"), "ua", 3000, 5000, 60_000, acceptBody: _ => false);
        var temp = r.attempts.Where(a => a.stage == FetchStage.TEMP_RECENT && !a.skipped).ToList();
        Assert.Equal(new[] { "bad", "good" }, temp.Select(a => a.note));
        Assert.Null(temp[0].result);                              // ядро не поднялось — кандидат пропущен
        Assert.True(temp[1].result is { ok: true }, "через живой temp-инстанс тело получено");
        Assert.True(SubscriptionManager.hasSupportedLinks(temp[1].result!.body));
    }

    [Fact]
    public void RetryAfter_parsing()
    {
        Assert.Equal(120, SubscriptionFetcher.parseRetryAfter(" 120 "));
        Assert.Equal(0, SubscriptionFetcher.parseRetryAfter("-5"));
        Assert.Null(SubscriptionFetcher.parseRetryAfter(""));
        Assert.Null(SubscriptionFetcher.parseRetryAfter("soon"));
        var future = DateTimeOffset.UtcNow.AddMinutes(2).ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(SubscriptionFetcher.parseRetryAfter(future)!.Value, 100, 121);
    }

    [Fact]
    public void Mirror_urls_for_igareck_only()
    {
        var m = SubscriptionManager.mirrorUrls(SubscriptionManager.DEFAULT_SOURCE_URL);
        Assert.Equal(3, m.Count);
        Assert.StartsWith("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/BLACK_VLESS_RUS_mobile.txt", m[0]);
        Assert.Equal(new[] { SubscriptionManager.YZEWE_SOURCE_URL }, SubscriptionManager.mirrorUrls(SubscriptionManager.YZEWE_SOURCE_URL));
    }
}
