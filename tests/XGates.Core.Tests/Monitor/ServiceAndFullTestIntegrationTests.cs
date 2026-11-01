using System.Net;
using System.Net.Sockets;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Tests.Libv2ray;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests.Monitor;

/// <summary>
/// Конвейер целиком на НАСТОЯЩЕМ xray (герметично, кроме портов 10815/10816 — как в продукте): подписка → сервис
/// поднимает ядро и порты → монитор стартует реактивно и подтверждает связь фактом → «Самый быстрый» (FullTestRunner)
/// пингует, мерит, подключает живой сервер и обходит мёртвый → стоп гасит ядро и порты.
/// </summary>
[Collection("xray")]
public sealed class ServiceAndFullTestIntegrationTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-svc-" + Guid.NewGuid().ToString("N")));

    public ServiceAndFullTestIntegrationTests()
    {
        ServerSpeedTester.probesOverrideForTest = [$"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1"];
        NetworkMonitor.directAliveOverrideForTest = () => true;          // «интернет есть» без внешних DNS
        ExternalIpChecker.onceOverrideForTest = _ => "198.51.100.1";      // внешний IP — детерминированно
    }

    public void Dispose()
    {
        XrayProxyService.shutdown();
        ServerSpeedTester.probesOverrideForTest = null;
        NetworkMonitor.directAliveOverrideForTest = null;
        ExternalIpChecker.onceOverrideForTest = null;
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private static bool PortOpen(int port)
    {
        try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); return true; } catch (SocketException) { return false; }
    }

    private static bool WaitUntil(Func<bool> cond, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(100); }
        return cond();
    }

    private List<ServerProfile> ImportServers(params int[] ports)
    {
        var body = string.Join("\n", ports.Select((p, i) => $"vless://{LocalTestNet.Uuid}@127.0.0.1:{p}?type=tcp&security=none#srv{i}"));
        SubscriptionManager.init(ctx);
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx, body, "local");
        Assert.True(r.ok, r.error);
        return SubscriptionManager.allServers(ctx);
    }

    [Fact]
    public void Service_start_ports_monitor_confirms_then_stop()
    {
        var servers = ImportServers(net.StartVlessServer());
        var p = servers[0];
        var key = SubscriptionManager.serverKey(p);
        XrayProxyService.start(ctx, XrayConfigBuilder.build(p), ServerLabels.full(p), key);
        Assert.True(WaitUntil(() => ProxyState.state.value.running, 10_000), ProxyState.state.value.message);
        var st = ProxyState.state.value;
        Assert.Equal(key, st.serverKey);
        Assert.True(st.socksOk && st.httpOk, "SOCKS и HTTP слушают");
        Assert.Equal(key, LastServerStore.load(ctx));

        using (var c = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy("socks5://127.0.0.1:10815"), UseProxy = true }))
            Assert.Equal(4096, c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=4096").Result.Length);

        // Монитор поднялся РЕАКТИВНО (по running=true). Будим — цикл подтверждает связь ФАКТОМ (пинг активного).
        Assert.True(WaitUntil(() => { MonitorCoordinator.wake(); return TunnelHealth.snapshot().phase == TunnelHealth.Phase.OK; }, 25_000),
            $"монитор не подтвердил связь: {TunnelHealth.snapshot()}");
        Assert.True(TunnelHealth.isConfirmedFor(key, JavaSystem.currentTimeMillis(), 60_000));
        Assert.True(MonitorStatus.state.value.cycles >= 1);

        XrayProxyService.stop(ctx);
        Assert.True(WaitUntil(() => !ProxyState.state.value.running && !PortOpen(10815), 10_000), "после «Стоп» порт 10815 свободен");
        Assert.False(XrayController.isRunning);
    }

    [Fact]
    public void Without_tunnel_first_alive_connects_before_ping_of_all_servers_finishes()
    {
        // Пинг идёт параллельно, а адреса-«чёрные дыры» держали бы его до таймаута 5 с на каждый.
        int a = net.StartVlessServer();
        var holes = Enumerable.Range(1, 96).Select(i => $"vless://{LocalTestNet.Uuid}@10.255.255.{i}:443?type=tcp&security=none#hole{i}");
        var body = string.Join("\n", holes.Prepend($"vless://{LocalTestNet.Uuid}@127.0.0.1:{a}?type=tcp&security=none#good"));
        SubscriptionManager.init(ctx);
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx, body, "local");
        Assert.True(r.ok, r.error);

        var pingDone = 0;
        var connectedDuringPing = -1;
        using var fin = new ManualResetEventSlim();
        using var conn = new ManualResetEventSlim();
        var h = FullTestRunner.run(ctx, SubscriptionManager.allServers(ctx),
            onPhase: _ => { },
            connect: p =>
            {
                Volatile.Write(ref connectedDuringPing, Volatile.Read(ref pingDone));
                conn.Set();
                XrayProxyService.start(ctx, XrayConfigBuilder.build(p), ServerLabels.full(p), SubscriptionManager.serverKey(p));
            },
            onDone: _ => fin.Set(),
            onPingResult: (_, _) => Interlocked.Increment(ref pingDone));
        Assert.True(conn.Wait(15_000), "первый живой не подключился за 15 с");
        Assert.True(Volatile.Read(ref connectedDuringPing) < 97, $"подключение после конца пинга: {connectedDuringPing}");
        h.cancel();
        fin.Wait(30_000);
        XrayProxyService.stop(ctx);
        Assert.True(WaitUntil(() => !PortOpen(10815), 10_000));
    }

    [Fact]
    public void Race_winner_is_measured_even_if_core_starts_slowly()
    {
        // На Windows ядро поднимается дольше: serverKey уже назначен, а прокси ещё не running.
        var servers = ImportServers(net.StartVlessServer());
        XrayProxyService.beforeCoreUpForTest = _ => Thread.Sleep(2_500);
        FullTestRunner.Result? result = null;
        var speeds = new List<double>();
        using var fin = new ManualResetEventSlim();
        try
        {
            FullTestRunner.run(ctx, servers,
                onPhase: _ => { },
                connect: p => XrayProxyService.start(ctx, XrayConfigBuilder.build(p), ServerLabels.full(p), SubscriptionManager.serverKey(p)),
                onDone: r => { result = r; fin.Set(); },
                onSpeedResult: (_, mbps, _) => { lock (speeds) speeds.Add(mbps); });
            Assert.True(fin.Wait(90_000), "полный тест не завершился");
        }
        finally { XrayProxyService.beforeCoreUpForTest = null; }
        Assert.NotNull(result!.connected);
        Assert.Contains(speeds, v => v > 0);
        XrayProxyService.stop(ctx);
        Assert.True(WaitUntil(() => !PortOpen(10815), 10_000));
    }

    [Fact]
    public void Stuck_speed_probe_is_cut_off_and_plate_stops_saying_measuring()
    {
        var p = ImportServers(net.StartVlessServer())[0];
        var key = SubscriptionManager.serverKey(p);
        using var mute = new TcpListener(IPAddress.Loopback, 0);   // принимает соединение и молчит: замер висит
        mute.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)mute.LocalEndpoint).Port}/x";
        ServerSpeedTester.probesOverrideForTest = [url];
        SettingsStore.update(ctx, SettingsStore.current() with { directProbeUrl = url, liveSpeedEnabled = false });
        NetworkMonitor.ACTIVE_PROBE_CAP_MS = 1_500;
        try
        {
            XrayProxyService.start(ctx, XrayConfigBuilder.build(p), ServerLabels.full(p), key);
            Assert.True(WaitUntil(() => ProxyState.state.value.running, 10_000), ProxyState.state.value.message);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            NetworkMonitor.probeActiveCapped(ctx);
            Assert.True(sw.ElapsedMilliseconds < 15_000, $"потолок не сработал: {sw.ElapsedMilliseconds} мс");
            Assert.False(TunnelSpeed.measuring.value);
            var ts = TunnelSpeed.snapshot();
            Assert.True(ts.serverKey == key && ts.measuredAtMs > 0 && ts.tunnelDownMbps == null, ts.ToString());
        }
        finally
        {
            NetworkMonitor.ACTIVE_PROBE_CAP_MS = 45_000L;
            SettingsStore.update(ctx, SettingsStore.current() with { directProbeUrl = SettingsStore.DEFAULTS.directProbeUrl, liveSpeedEnabled = true });
            XrayProxyService.stop(ctx);
        }
        Assert.True(WaitUntil(() => !PortOpen(10815), 10_000));
    }

    [Fact]
    public void New_server_gets_speed_measured_at_once_not_after_probe_interval()
    {
        var servers = ImportServers(net.StartVlessServer(), net.StartVlessServer());
        var localUrl = $"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1";
        var savedUpload = ServerSpeedTester.uploadFallbacks;
        ServerSpeedTester.uploadFallbacks = [];
        SettingsStore.update(ctx, SettingsStore.current() with { directProbeUrl = localUrl, uploadProbeUrl = $"http://127.0.0.1:{net.HttpPort}/up?slow=1" });
        string a = SubscriptionManager.serverKey(servers[0]), b = SubscriptionManager.serverKey(servers[1]);
        bool measured(string k) { TunnelHealth.setPhase(TunnelHealth.Phase.VERIFYING, JavaSystem.currentTimeMillis(), "тест"); MonitorCoordinator.wake(); return TunnelSpeed.snapshot() is var s && s.serverKey == k && s.measuredAtMs > 0; }
        try
        {
            XrayProxyService.start(ctx, XrayConfigBuilder.build(servers[0]), ServerLabels.full(servers[0]), a);
            Assert.True(WaitUntil(() => measured(a), 40_000), $"первый сервер не измерен: {TunnelSpeed.snapshot()}");
            XrayProxyService.start(ctx, XrayConfigBuilder.build(servers[1]), ServerLabels.full(servers[1]), b);
            Assert.True(WaitUntil(() => measured(b), 40_000), $"новый сервер не измерен: {TunnelSpeed.snapshot()}");
        }
        finally
        {
            ServerSpeedTester.uploadFallbacks = savedUpload;
            SettingsStore.update(ctx, SettingsStore.current() with { directProbeUrl = SettingsStore.DEFAULTS.directProbeUrl, uploadProbeUrl = SettingsStore.DEFAULTS.uploadProbeUrl });
            XrayProxyService.stop(ctx);
        }
        Assert.True(WaitUntil(() => !PortOpen(10815), 10_000));
    }

    [Fact]
    public void FullTest_connects_alive_and_skips_dead()
    {
        int a = net.StartVlessServer(), b = net.StartVlessServer(), dead = LocalTestNet.FreePort();
        var servers = ImportServers(dead, a, b);
        FullTestRunner.Result? result = null;
        var connected = new List<string>();
        var phases = new List<string>();
        using var fin = new ManualResetEventSlim();
        FullTestRunner.run(ctx, servers,
            onPhase: ph => { lock (phases) phases.Add(ph); },
            connect: p =>
            {
                lock (connected) connected.Add(p.remarks);
                XrayProxyService.start(ctx, XrayConfigBuilder.build(p), ServerLabels.full(p), SubscriptionManager.serverKey(p));
            },
            onDone: r => { result = r; fin.Set(); });
        Assert.True(fin.Wait(120_000), "полный тест не завершился: " + string.Join(" | ", phases.TakeLast(5)));
        Assert.NotNull(result!.connected);
        Assert.False(result.cancelled);
        Assert.Equal(2, result.aliveCount);                       // мёртвый отсеян пингом (Этап 1)
        Assert.NotEqual("srv0", result.connected!.remarks);        // srv0 — мёртвый порт
        Assert.DoesNotContain("srv0", connected);
        Assert.True(WaitUntil(() => ProxyState.state.value.running, 10_000), "после теста прокси поднят");
        Assert.Contains(phases, ph => ph.StartsWith("Проверяю серверы"));
        Assert.Contains(phases, ph => ph.StartsWith("Сравниваю скорость"));
        XrayProxyService.stop(ctx);
        Assert.True(WaitUntil(() => !PortOpen(10815), 10_000));
    }
}
