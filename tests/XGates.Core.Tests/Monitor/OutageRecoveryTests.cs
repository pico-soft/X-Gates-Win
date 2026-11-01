using System.Text;
using XGates.Core.Monitor;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Tests.Libv2ray;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests.Monitor;

/// <summary>
/// Главный принцип продукта — непрерывность связи — на НАСТОЯЩЕМ обрыве: прокси работает через сервер A, монитор
/// подтвердил связь; сервер A «умирает» (процесс остановлен, порт закрыт) → монитор сам видит обрыв, проходит grace,
/// запускает лестницу восстановления и переключает туннель на живой сервер B; связь снова подтверждена фактом.
/// Внешний IP в тесте зависит от туннеля (проход через активный SOCKS до стенда), иначе «живость» была бы фальшивой.
/// </summary>
[Collection("xray")]
public sealed class OutageRecoveryTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-outage-" + Guid.NewGuid().ToString("N")));
    private readonly long savedGrace = NetworkMonitor.LIVENESS_GRACE_MS, savedRecheck = NetworkMonitor.GRACE_RECHECK_MS;

    public OutageRecoveryTests()
    {
        ServerSpeedTester.probesOverrideForTest = [$"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1"];
        ServerSpeedTester.uploadFallbacks = [];
        NetworkMonitor.directAliveOverrideForTest = () => true;   // «интернет есть» (прямой канал жив)
        // Внешний IP — ТОЛЬКО если запрос реально прошёл через туннель активного сервера до стенда.
        ExternalIpChecker.onceOverrideForTest = socks =>
        {
            try
            {
                using var s = Socks5.Connect(socks, "127.0.0.1", net.HttpPort, 3000);
                s.ReceiveTimeout = 3000;
                s.Send(Encoding.ASCII.GetBytes("GET /bytes?n=8 HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"));
                var buf = new byte[64];
                return s.Receive(buf) > 0 && Encoding.ASCII.GetString(buf).StartsWith("HTTP/1.1 200") ? "198.51.100.1" : null;
            }
            catch (Exception) { return null; }
        };
        NetworkMonitor.LIVENESS_GRACE_MS = 3_000L;
        NetworkMonitor.GRACE_RECHECK_MS = 1_000L;
        WinSettingsStore.update(ctx, new WinSettings { socksPort = LocalTestNet.FreePort(), httpPort = LocalTestNet.FreePort() });
        SettingsStore.update(ctx, SettingsStore.current() with
        {
            directProbeUrl = $"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1",
            uploadProbeUrl = $"http://127.0.0.1:{net.HttpPort}/up?slow=1",
        });
    }

    public void Dispose()
    {
        XrayProxyService.shutdown();
        NetworkMonitor.LIVENESS_GRACE_MS = savedGrace;
        NetworkMonitor.GRACE_RECHECK_MS = savedRecheck;
        ServerSpeedTester.probesOverrideForTest = null;
        ServerSpeedTester.uploadFallbacks = ["https://speed.cloudflare.com/__up", "http://speedtest.tele2.net/upload.php"];
        NetworkMonitor.directAliveOverrideForTest = null;
        ExternalIpChecker.onceOverrideForTest = null;
        SettingsStore.update(ctx, SettingsStore.current() with
        {
            directProbeUrl = SettingsStore.DEFAULTS.directProbeUrl,
            uploadProbeUrl = SettingsStore.DEFAULTS.uploadProbeUrl,
        });
        WinSettingsStore.resetForTest();
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private static bool WaitUntil(Func<bool> cond, int ms, bool wake = false)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            if (cond()) return true;
            if (wake) MonitorCoordinator.wake();   // не ждать интервала цикла (≥15 с) — будим, как действие пользователя
            Thread.Sleep(250);
        }
        return cond();
    }

    [Fact]
    public void Monitor_switches_to_live_server_after_active_server_dies()
    {
        int a = net.StartVlessServer(), b = net.StartVlessServer();
        SubscriptionManager.init(ctx);
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx,
            $"vless://{LocalTestNet.Uuid}@127.0.0.1:{a}?type=tcp&security=none#A\nvless://{LocalTestNet.Uuid}@127.0.0.1:{b}?type=tcp&security=none#B", "local");
        Assert.True(r.ok, r.error);
        var servers = SubscriptionManager.allServers(ctx);
        var srvA = servers.Single(p => p.remarks == "A");
        var keyA = SubscriptionManager.serverKey(srvA);
        var keyB = SubscriptionManager.serverKey(servers.Single(p => p.remarks == "B"));
        // Оба «известны живыми» (как после полного теста) — кандидаты лестницы.
        SubscriptionManager.applyPingResults(ctx, new Dictionary<string, int> { [keyA] = 5, [keyB] = 7 });
        SubscriptionManager.applySpeedResults(ctx, new Dictionary<string, double> { [keyA] = 50, [keyB] = 40 });

        XrayProxyService.start(ctx, XrayConfigBuilder.build(srvA), "A", keyA);
        Assert.True(WaitUntil(() => ProxyState.state.value.running && ProxyState.state.value.serverKey == keyA, 10_000), ProxyState.state.value.message);
        Assert.True(WaitUntil(() => TunnelHealth.isConfirmedFor(keyA, JavaSystem.currentTimeMillis(), 60_000), 30_000, wake: true),
            $"связь через A не подтверждена: {TunnelHealth.snapshot()}");

        // ОБРЫВ: сервер A умер.
        net.StopVlessServer(a);
        Assert.True(WaitUntil(() => ProxyState.state.value.serverKey == keyB && TunnelHealth.isConfirmedFor(keyB, JavaSystem.currentTimeMillis(), 60_000),
                90_000, wake: true),
            $"монитор не восстановил связь через B: proxy={ProxyState.state.value.serverKey} health={TunnelHealth.snapshot()} " +
            $"журнал: {string.Join(" | ", MonitorLog.state.value.TakeLast(6).Select(e => e.text + " — " + e.detail))}");
        Assert.True(ProxyState.state.value.running);
        Assert.Contains(MonitorLog.state.value, e => e.kind == "switch");
    }

    private void Start(ServerProfile p)
    {
        XrayProxyService.start(ctx, XrayConfigBuilder.build(p), ServerLabels.full(p), SubscriptionManager.serverKey(p));
        Assert.True(WaitUntil(() => ProxyState.state.value.running, 10_000), ProxyState.state.value.message);
    }

    private List<ServerProfile> Import(params int[] ports)
    {
        var body = string.Join("\n", ports.Select((p, i) => $"vless://{LocalTestNet.Uuid}@127.0.0.1:{p}?type=tcp&security=none#srv{i}"));
        SubscriptionManager.init(ctx);
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx, body, "local");
        Assert.True(r.ok, r.error);
        return SubscriptionManager.allServers(ctx);
    }

    [Fact]
    public void Open_server_port_alone_does_not_make_the_plate_green()
    {
        // Порт отвечает, а данные не идут: приёмник принимает соединение и молчит.
        using var mute = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        mute.Start();
        var active = Import(((System.Net.IPEndPoint)mute.LocalEndpoint).Port)[0];
        // Экран выключен, батарея: монитор не тратит цикл на замеры скорости и сразу доходит до внешнего IP.
        var savedCharging = Device.isCharging;
        Device.isCharging = () => false;
        Device.screenOn = false;
        try
        {
            Start(active);
            TunnelHealth.setPhase(TunnelHealth.Phase.VERIFYING, JavaSystem.currentTimeMillis(), "как после подключения из окна");
            var end = Environment.TickCount64 + 40_000;
            while (Environment.TickCount64 < end && MonitorStatus.state.value.state != "ок")
            {
                MonitorCoordinator.wake();
                Thread.Sleep(250);
                Assert.NotEqual(TunnelHealth.Phase.OK, TunnelHealth.snapshot().phase);
            }
            Assert.Equal("ок", MonitorStatus.state.value.state);   // цикл дошёл до проверки внешнего IP
            Assert.NotEqual(TunnelHealth.Phase.OK, TunnelHealth.snapshot().phase);
        }
        finally { Device.isCharging = savedCharging; Device.screenOn = true; }
    }

    [Fact]
    public void Live_tunnel_is_left_only_for_a_clearly_faster_server()
    {
        var s = SettingsStore.DEFAULTS with { degradationMinMbps = 2.0, keepBestMultiplier = 3.0 };
        Assert.False(NetworkMonitor.worthLeaving(2.5, 1.9, s));   // «чуть быстрее» живой туннель не рвём
        Assert.False(NetworkMonitor.worthLeaving(1.5, 0.1, s));   // кандидат сам ниже порога
        Assert.True(NetworkMonitor.worthLeaving(6.0, 1.9, s));
        Assert.True(NetworkMonitor.worthLeaving(2.0, 0.3, s));
    }

    [Fact]
    public void Stored_speed_alone_never_moves_us_off_a_live_tunnel()
    {
        // №1 в списке: огромная скорость в памяти, а сервер уже мёртв. Третий сервер даёт монитору повод что-то мерить.
        int a = net.StartVlessServer(), c = net.StartVlessServer(), dead = LocalTestNet.FreePort();
        var servers = Import(a, dead, c);
        var ghost = SubscriptionManager.serverKey(servers.First(it => it.remarks == "srv1"));
        SubscriptionManager.applyPingResults(ctx, new Dictionary<string, int> { [ghost] = 20 });
        SubscriptionManager.applySpeedResults(ctx, new Dictionary<string, double> { [ghost] = 1000.0 });
        var active = servers.First(it => it.remarks == "srv0");
        Start(active);

        NetworkMonitor.optimizeToFastest(ctx, SettingsStore.current()).Wait();

        Assert.Equal(SubscriptionManager.serverKey(active), ProxyState.state.value.serverKey);
        Assert.Equal(-1, SubscriptionManager.allServers(ctx).First(it => SubscriptionManager.serverKey(it) == ghost).pingMs);   // ушёл из «Живых»
    }

    [Fact]
    public void Failed_service_measure_does_not_condemn_a_tunnel_that_still_passes_traffic()
    {
        int a = net.StartVlessServer(), b = net.StartVlessServer();
        var servers = Import(a, b);
        var active = servers.First(it => it.remarks == "srv0");
        // Служебный замер не проходит: адрес проверки закрыт. Сам туннель при этом жив: внешний IP через него получен.
        ServerSpeedTester.probesOverrideForTest = [$"http://127.0.0.1:{LocalTestNet.FreePort()}/x"];
        ExternalIpChecker.onceOverrideForTest = _ => "198.51.100.1";   // ответ без трафика: иначе монитор засчитает его за работу пользователя
        Start(active);
        var key = SubscriptionManager.serverKey(active);

        // Служебный замер ждёт, пока закончится фоновый рейтинг, поэтому смотрим дольше одного цикла.
        var end = Environment.TickCount64 + 45_000;
        while (Environment.TickCount64 < end)
        {
            MonitorCoordinator.wake();
            Thread.Sleep(250);
            Assert.Equal(key, ProxyState.state.value.serverKey);
        }
    }

    [Fact]
    public void Next_live_server_is_found_before_pinging_everyone()
    {
        // Сто адресов-«чёрных дыр» с высокой сохранённой скоростью: пинг по одному занял бы 150 с.
        int a = net.StartVlessServer(), b = net.StartVlessServer();
        var holes = Enumerable.Range(1, 100).Select(i => $"vless://{LocalTestNet.Uuid}@10.255.254.{i}:443?type=tcp&security=none#hole{i}");
        var live = new[] { a, b }.Select((p, i) => $"vless://{LocalTestNet.Uuid}@127.0.0.1:{p}?type=tcp&security=none#srv{i}");
        SubscriptionManager.init(ctx);
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx, string.Join("\n", live.Concat(holes)), "local");
        Assert.True(r.ok, r.error);
        var all = SubscriptionManager.allServers(ctx);
        var second = SubscriptionManager.serverKey(all.First(it => it.remarks == "srv1"));
        SubscriptionManager.applyPingResults(ctx, all.ToDictionary(SubscriptionManager.serverKey, _ => 50));
        SubscriptionManager.applySpeedResults(ctx, new Dictionary<string, double> { [second] = 90.0 });
        var active = all.First(it => it.remarks == "srv0");
        Start(active);
        Assert.True(WaitUntil(() => TunnelHealth.snapshot().phase == TunnelHealth.Phase.OK, 25_000, wake: true), "связь не подтвердилась");

        net.StopVlessServer(a);
        Assert.True(WaitUntil(() => ProxyState.state.value.serverKey == second && ProxyState.state.value.running, 40_000, wake: true),
            $"не переключился на следующий живой (сейчас {ProxyState.state.value.serverKey}, журнал: {string.Join(" | ", MonitorLog.state.value.TakeLast(6).Select(e => e.text + " " + e.detail))}): {TunnelHealth.snapshot().phase}, монитор: {MonitorStatus.state.value}");
    }

    [Fact]
    public void All_servers_dead_says_so_and_offers_disabled_sources()
    {
        int a = net.StartVlessServer();
        SubscriptionManager.init(ctx);
        SubscriptionManager.addLocalFromBody(ctx, $"vless://{LocalTestNet.Uuid}@127.0.0.1:{a}?type=tcp&security=none#A", "local");
        // Выключенный источник с серверами — кандидат для предложения «включить» (сами не включаем).
        var (offId, _) = SubscriptionManager.addLocalFromBody(ctx, $"vless://{LocalTestNet.Uuid}@127.0.0.1:{LocalTestNet.FreePort()}?type=tcp&security=none#OFF", "запасной");
        SubscriptionManager.setEnabled(ctx, offId, false);
        var srvA = SubscriptionManager.allServers(ctx).Single();
        var keyA = SubscriptionManager.serverKey(srvA);
        SubscriptionManager.applyPingResults(ctx, new Dictionary<string, int> { [keyA] = 5 });
        var shown = new List<NotificationHelper.Kind>();
        var savedSink = NotificationHelper.Sink;
        NotificationHelper.Sink = new Sink(shown);
        MonitorPrompt.clear(); MonitorPrompt.resetDeclined();
        try
        {
            XrayProxyService.start(ctx, XrayConfigBuilder.build(srvA), "A", keyA);
            Assert.True(WaitUntil(() => TunnelHealth.isConfirmedFor(keyA, JavaSystem.currentTimeMillis(), 60_000), 30_000, wake: true), $"{TunnelHealth.snapshot()}");

            net.StopVlessServer(a);   // единственный включённый сервер умер
            Assert.True(WaitUntil(() => TunnelHealth.snapshot().phase == TunnelHealth.Phase.NO_SERVERS, 120_000, wake: true),
                $"не дошли до «рабочих серверов нет»: {TunnelHealth.snapshot()} · журнал: {string.Join(" | ", MonitorLog.state.value.TakeLast(5).Select(e => e.text))}");
            Assert.True(WaitUntil(() => MonitorPrompt.state.value is { sources: 1, servers: 1 }, 30_000, wake: true), $"предложение не появилось: {MonitorPrompt.state.value}");
            lock (shown)
            {
                Assert.Contains(NotificationHelper.Kind.NO_SERVERS, shown);
                Assert.Contains(NotificationHelper.Kind.ENABLE_SOURCES, shown);
            }
            Assert.False(SubscriptionManager.sources(ctx).Single(x => x.id == offId).enabled);   // сами НЕ включили
            Assert.True(ProxyState.state.value.running);   // сервис жив — ждёт, связь не «выключена» молча
        }
        finally { NotificationHelper.Sink = savedSink; MonitorPrompt.clear(); MonitorPrompt.resetDeclined(); }
    }

    [Fact]
    public void No_internet_is_reported_honestly_and_recovers_without_switching()
    {
        int a = net.StartVlessServer();
        SubscriptionManager.init(ctx);
        SubscriptionManager.addLocalFromBody(ctx, $"vless://{LocalTestNet.Uuid}@127.0.0.1:{a}?type=tcp&security=none#A", "local");
        var srvA = SubscriptionManager.allServers(ctx).Single();
        var keyA = SubscriptionManager.serverKey(srvA);
        var internet = true;
        NetworkMonitor.directAliveOverrideForTest = () => internet;
        XrayProxyService.start(ctx, XrayConfigBuilder.build(srvA), "A", keyA);
        Assert.True(WaitUntil(() => TunnelHealth.isConfirmedFor(keyA, JavaSystem.currentTimeMillis(), 60_000), 30_000, wake: true), $"{TunnelHealth.snapshot()}");

        internet = false;   // пропал прямой канал: туннель чинить бессмысленно — сказать прямо, ядро не трогать
        Assert.True(WaitUntil(() => TunnelHealth.snapshot().phase == TunnelHealth.Phase.NO_INTERNET, 60_000, wake: true), $"{TunnelHealth.snapshot()}");
        Assert.Equal(keyA, ProxyState.state.value.serverKey);
        Assert.True(ProxyState.state.value.running);

        internet = true;    // вернулся → связь через тот же сервер подтверждена фактом, без переключения
        Assert.True(WaitUntil(() => TunnelHealth.snapshot().phase == TunnelHealth.Phase.OK && TunnelHealth.isConfirmedFor(keyA, JavaSystem.currentTimeMillis(), 60_000), 90_000, wake: true),
            $"{TunnelHealth.snapshot()}");
        Assert.Equal(keyA, ProxyState.state.value.serverKey);
    }

    private sealed class Sink(List<NotificationHelper.Kind> shown) : NotificationHelper.INotificationSink
    {
        public void show(NotificationHelper.Kind kind, string title, string text) { lock (shown) shown.Add(kind); }
        public void cancel(NotificationHelper.Kind kind) { }
    }
}
