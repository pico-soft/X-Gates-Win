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
/// Решение Elyor 2026-10-30: редкая проверка (60 с + grace 90 с ≈ 2–2,5 мин до переключения) — только на БАТАРЕЕ;
/// на шнуре — раз в 10 с, обрыв после 30 с тишины (перепроверка 10 с). Здесь — правила интервалов и НАСТОЯЩИЙ обрыв
/// на шнуре с боевыми (не укороченными) значениями: монитор не будим, засекаем время до переключения.
/// </summary>
[Collection("xray")]
public sealed class AcPowerChecksTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-ac-" + Guid.NewGuid().ToString("N")));
    private readonly Func<bool> savedCharging = Device.isCharging;
    private readonly IReadOnlyList<string> savedFallbacks = ServerSpeedTester.uploadFallbacks;

    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public AcPowerChecksTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        this.output = output;
        ServerSpeedTester.probesOverrideForTest = [$"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1"];
        ServerSpeedTester.uploadFallbacks = [];
        NetworkMonitor.directAliveOverrideForTest = () => true;
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
        Device.isCharging = savedCharging;
        ServerSpeedTester.probesOverrideForTest = null;
        ServerSpeedTester.uploadFallbacks = savedFallbacks;
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

    [Fact]
    public void Intervals_fast_on_ac_unchanged_on_battery_economy_and_mobile()
    {
        var s = SettingsStore.DEFAULTS;   // ползунок 60 с
        Device.isCharging = () => false;
        Assert.False(NetworkMonitor.fastChecksOnAc(s));
        Assert.Equal(60_000L, NetworkMonitor.healthyCheckMs(s));
        Assert.Equal(90_000L, NetworkMonitor.graceMs(s));
        Assert.Equal(25_000L, NetworkMonitor.graceRecheckMs(s));

        Device.isCharging = () => true;
        Assert.True(NetworkMonitor.fastChecksOnAc(s));
        Assert.Equal(10_000L, NetworkMonitor.healthyCheckMs(s));
        Assert.Equal(30_000L, NetworkMonitor.graceMs(s));
        Assert.Equal(10_000L, NetworkMonitor.graceRecheckMs(s));
        // Экономия трафика — как на батарее (трафик важнее скорости реакции).
        Assert.False(NetworkMonitor.fastChecksOnAc(s with { trafficSaveMode = true }));
        Assert.Equal(60_000L, NetworkMonitor.healthyCheckMs(s with { trafficSaveMode = true }));
    }

    [Fact]
    public void On_ac_outage_switches_within_a_minute_with_real_timings()
    {
        Device.isCharging = () => true;
        int a = net.StartVlessServer(), b = net.StartVlessServer();
        SubscriptionManager.init(ctx);
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx,
            $"vless://{LocalTestNet.Uuid}@127.0.0.1:{a}?type=tcp&security=none#A\nvless://{LocalTestNet.Uuid}@127.0.0.1:{b}?type=tcp&security=none#B", "local");
        Assert.True(r.ok, r.error);
        var servers = SubscriptionManager.allServers(ctx);
        var srvA = servers.Single(p => p.remarks == "A");
        var keyA = SubscriptionManager.serverKey(srvA);
        var keyB = SubscriptionManager.serverKey(servers.Single(p => p.remarks == "B"));
        SubscriptionManager.applyPingResults(ctx, new Dictionary<string, int> { [keyA] = 5, [keyB] = 7 });
        SubscriptionManager.applySpeedResults(ctx, new Dictionary<string, double> { [keyA] = 50, [keyB] = 40 });

        XrayProxyService.start(ctx, XrayConfigBuilder.build(srvA), "A", keyA);
        var end = Environment.TickCount64 + 30_000;
        while (!TunnelHealth.isConfirmedFor(keyA, JavaSystem.currentTimeMillis(), 60_000) && Environment.TickCount64 < end) { MonitorCoordinator.wake(); Thread.Sleep(250); }
        Assert.True(TunnelHealth.isConfirmedFor(keyA, JavaSystem.currentTimeMillis(), 60_000), $"{TunnelHealth.snapshot()}");
        Thread.Sleep(1_000);   // дать монитору уйти в обычный сон (дальше НЕ будим — меряем настоящий ритм)

        net.StopVlessServer(a);
        var t0 = Environment.TickCount64;
        end = t0 + 90_000;
        while (!(ProxyState.state.value.serverKey == keyB && ProxyState.state.value.running) && Environment.TickCount64 < end) Thread.Sleep(250);
        var sec = (Environment.TickCount64 - t0) / 1000.0;
        output.WriteLine($"шнур: от обрыва до переключения {sec:F1} с");
        Assert.True(ProxyState.state.value.serverKey == keyB,
            $"за 90 с не переключился: {TunnelHealth.snapshot()} · {string.Join(" | ", MonitorLog.state.value.TakeLast(5).Select(e => e.text))}");
        // Grace 30 с соблюдён (не мгновенно), и быстрее батарейных 90 с grace + 60 с интервала.
        Assert.InRange(sec, 25, 60);
    }
}
