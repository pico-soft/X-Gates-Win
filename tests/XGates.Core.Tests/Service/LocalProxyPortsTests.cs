using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Tests.Libv2ray;
using XGates.Core.Xray;

namespace XGates.Core.Tests.Service;

/// <summary>
/// Порты локального прокси (§13.7): по умолчанию 10815/10816 как на Android; смена в настройках доходит до конфига
/// ядра и до всех потребителей (каскад, внешний IP); неверная пара не сохраняется; занятый ЧУЖОЙ программой порт —
/// понятная ошибка вместо «ядро не запустилось»; на новых портах прокси реально работает.
/// </summary>
[Collection("xray")]
public sealed class LocalProxyPortsTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-ports-" + Guid.NewGuid().ToString("N")));

    public LocalProxyPortsTests()
    {
        Directory.CreateDirectory(ctx.filesDir);
        NetworkMonitor.directAliveOverrideForTest = () => true;
        ExternalIpChecker.onceOverrideForTest = _ => "198.51.100.1";
    }

    public void Dispose()
    {
        XrayProxyService.shutdown();
        WinSettingsStore.resetForTest();
        NetworkMonitor.directAliveOverrideForTest = null;
        ExternalIpChecker.onceOverrideForTest = null;
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults_match_android_and_bad_pairs_are_rejected()
    {
        Assert.Equal(10815, XrayConfig.SOCKS_PORT);
        Assert.Equal(10816, XrayConfig.HTTP_PORT);
        Assert.Null(XrayConfig.portsError(10815, 10816));
        Assert.NotNull(XrayConfig.portsError(10815, 10815));
        Assert.NotNull(XrayConfig.portsError(80, 10816));
        Assert.NotNull(XrayConfig.portsError(10815, 70000));

        Assert.False(WinSettingsStore.update(ctx, new WinSettings { socksPort = 2000, httpPort = 2000 }));
        Assert.Equal(10815, XrayConfig.SOCKS_PORT);   // неверная пара не применена
        Assert.False(File.Exists(Path.Combine(ctx.filesDir, "win_settings.json")));
    }

    [Fact]
    public void Changed_ports_persist_and_reach_core_config_and_consumers()
    {
        Assert.True(WinSettingsStore.update(ctx, new WinSettings { socksPort = 20815, httpPort = 20816 }));
        WinSettingsStore.resetForTest();
        WinSettingsStore.init(ctx);   // «перезапуск»
        Assert.Equal(20815, XrayConfig.SOCKS_PORT);
        Assert.Equal(20816, XrayConfig.HTTP_PORT);
        Assert.Equal(20815, Net.CascadeFetch.ownSocksPort());
        Assert.Equal(20815, ServerSpeedTester.activeSocksPort());

        var p = Xray.Link.VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:443?type=tcp&security=none#t")!;
        var inbounds = JsonNode.Parse(XrayConfigBuilder.build(p))!["inbounds"]!.AsArray();
        Assert.Equal(20815, (int)inbounds[0]!["port"]!);
        Assert.Equal(20816, (int)inbounds[1]!["port"]!);

        // Ручная порча файла (одинаковые порты) → при загрузке дефолты, прокси поднимется.
        File.WriteAllText(Path.Combine(ctx.filesDir, "win_settings.json"), "{\"socksPort\":3000,\"httpPort\":3000}");
        WinSettingsStore.init(ctx);
        Assert.Equal(10815, XrayConfig.SOCKS_PORT);
    }

    private static bool Accepts(int port)
    {
        try { using var c = new TcpClient(); return c.ConnectAsync(IPAddress.Loopback, port).Wait(3000) && c.Connected; }
        catch (Exception) { return false; }
    }

    private static bool WaitUntil(Func<bool> cond, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(100); }
        return cond();
    }

    [Fact]
    public void Busy_port_gives_clear_error_and_free_custom_ports_work()
    {
        int socks = LocalTestNet.FreePort(), http = LocalTestNet.FreePort();
        Assert.True(WinSettingsStore.update(ctx, new WinSettings { socksPort = socks, httpPort = http }));
        SubscriptionManager.init(ctx);
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx, $"vless://{LocalTestNet.Uuid}@127.0.0.1:{net.StartVlessServer()}?type=tcp&security=none#srv", "local");
        Assert.True(r.ok, r.error);
        var p = SubscriptionManager.allServers(ctx)[0];
        var key = SubscriptionManager.serverKey(p);

        // Чужая программа держит HTTP-порт.
        var squatter = new TcpListener(IPAddress.Loopback, http);
        squatter.Start();
        try
        {
            XrayProxyService.start(ctx, XrayConfigBuilder.build(p), "srv", key);
            Assert.True(WaitUntil(() => ProxyState.state.value.message.Contains("занят"), 10_000), ProxyState.state.value.message);
            Assert.False(ProxyState.state.value.running);
            Assert.Contains($"Порт {http} занят другой программой", ProxyState.state.value.message);
        }
        finally { squatter.Stop(); }

        // Порт освободили → тот же старт поднимает прокси на НАСТРОЕННЫХ портах, трафик идёт.
        XrayProxyService.start(ctx, XrayConfigBuilder.build(p), "srv", key);
        Assert.True(WaitUntil(() => ProxyState.state.value.running, 10_000), ProxyState.state.value.message);
        // Флаги socksOk/httpOk сервис ставит ОДНОЙ пробой 500 мс сразу после старта (как Android); на нагруженном
        // Windows-раннере она однажды не успела (прогон 41190901945), хотя порты слушали. Окно Windows эти флаги не
        // показывает → проверяем сами порты (а трафик ниже — что они рабочие).
        Assert.True(WaitUntil(() => Accepts(socks) && Accepts(http), 10_000),
            $"порты не слушают: socks={Accepts(socks)} http={Accepts(http)} (флаги сервиса: {ProxyState.state.value.socksOk}/{ProxyState.state.value.httpOk})");
        // Тело непустое: ответ БЕЗ тела (204/200 с Content-Length 0) через HTTP-вход xray в режиме пересылки (не CONNECT)
        // на этом стенде не доходит — см. docs/decisions.md, «Факты xray»; CONNECT и SOCKS такие ответы пропускают.
        using (var c = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{http}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(15) })
            Assert.Equal(4096, c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=4096").Result.Length);
        using (var c = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{socks}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(15) })
            Assert.Equal(System.Net.HttpStatusCode.NoContent, c.GetAsync($"http://127.0.0.1:{net.HttpPort}/generate_204").Result.StatusCode);
        XrayProxyService.stop(ctx);
        Assert.True(WaitUntil(() => !ProxyState.state.value.running, 10_000));
    }
}
