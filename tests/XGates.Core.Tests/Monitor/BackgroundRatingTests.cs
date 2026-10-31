using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Tests.Libv2ray;
using XGates.Core.Xray;

namespace XGates.Core.Tests.Monitor;

/// <summary>
/// Находка порта №3: фоновый рейтинг топа на МАЛОМ пуле (меньше цели топа — 10) падал
/// «Cannot coerce value to an empty range» и ничего не мерил. Здесь — настоящий монитор на стенде с тремя
/// серверами: первый здоровый цикл (на шнуре рейтинг идёт сразу) обязан ОБНОВИТЬ топ, а не записать ошибку.
/// </summary>
[Collection("xray")]
public sealed class BackgroundRatingTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-bgrate-" + Guid.NewGuid().ToString("N")));
    private readonly IReadOnlyList<string> savedFallbacks = ServerSpeedTester.uploadFallbacks;
    private readonly Func<bool> savedCharging = Device.isCharging;

    public BackgroundRatingTests()
    {
        ServerSpeedTester.probesOverrideForTest = [$"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1"];
        ServerSpeedTester.uploadFallbacks = [];
        NetworkMonitor.directAliveOverrideForTest = () => true;
        ExternalIpChecker.onceOverrideForTest = _ => "198.51.100.1";
        Device.isCharging = () => true;   // шнур: фон-рейтинг разрешён и идёт по расписанию сразу
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

    private static bool WaitUntil(Func<bool> cond, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (cond()) return true; MonitorCoordinator.wake(); Thread.Sleep(250); }
        return cond();
    }

    [Fact]
    public void Background_top_rating_measures_small_pool_instead_of_failing()
    {
        int a = net.StartVlessServer(), b = net.StartVlessServer(), c = net.StartVlessServer();
        SubscriptionManager.init(ctx);
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx, string.Join("\n",
            new[] { a, b, c }.Select((p, i) => $"vless://{LocalTestNet.Uuid}@127.0.0.1:{p}?type=tcp&security=none#S{i}")), "local");
        Assert.True(r.ok, r.error);
        var srvA = SubscriptionManager.allServers(ctx).Single(p => p.remarks == "S0");
        var keyA = SubscriptionManager.serverKey(srvA);

        XrayProxyService.start(ctx, XrayConfigBuilder.build(srvA), "S0", keyA);
        bool Rated() => MonitorLog.state.value.Any(e => e.text.StartsWith("Фон: топ обновлён"));
        bool Failed() => MonitorLog.state.value.Any(e => e.text == "Ошибка фон-рейтинга топа");
        Assert.True(WaitUntil(() => Rated() || Failed(), 90_000),
            "фон-рейтинг не дошёл до итога: " + string.Join(" | ", MonitorLog.state.value.TakeLast(6).Select(e => e.text + " — " + e.detail)));
        Assert.False(Failed(), string.Join(" | ", MonitorLog.state.value.Where(e => e.kind == "error").Select(e => e.text + " — " + e.detail)));

        // Пул меньше цели топа (2 кандидата при цели 10) — оба реально измерены (минимальный замер), не пропущены.
        var rated = MonitorLog.state.value.Last(e => e.text.StartsWith("Фон: топ обновлён"));
        Assert.Contains("цель 10", rated.detail);
        Assert.Contains("минимальный 2", rated.detail);
        var others = SubscriptionManager.allServers(ctx).Where(p => SubscriptionManager.serverKey(p) != keyA).ToList();
        Assert.All(others, p => Assert.True((p.speedMbps ?? 0) > 0, $"{p.remarks}: скорость не записана"));
    }
}
