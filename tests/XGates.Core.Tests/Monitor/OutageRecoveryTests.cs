using System.Text;
using XGates.Core.Monitor;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Tests.Libv2ray;
using XGates.Core.Xray;

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
}
