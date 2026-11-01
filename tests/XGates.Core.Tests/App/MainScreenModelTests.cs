using XGates.App;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Tests.Libv2ray;
using XGates.Core.Xray;

namespace XGates.Core.Tests.App;

/// <summary>
/// «Главная» (порт BootScreen) на НАСТОЯЩЕМ xray, герметично: «Самый быстрый» без подписок с URL = сразу полный тест →
/// подключение живого, мёртвый отсеян; списки «Живые/Все» и плашка; «Проверить» без прокси и с прокси (лестница до
/// «Соединение в норме»); «Не использовать» исключает сервер и переподбирает; глаз «использовать ↔ нет»; автозапуск
/// поднимает последний сервер сразу и запускает тест. Порты прокси — свободные (не 10815, чтобы не мешать машине).
/// </summary>
[Collection("xray")]
public sealed class MainScreenModelTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-main-" + Guid.NewGuid().ToString("N")));
    private readonly SerialUiThread ui = new();
    private MainScreenModel? model;
    private readonly IReadOnlyList<string> savedFallbacks = ServerSpeedTester.uploadFallbacks;

    public MainScreenModelTests()
    {
        ServerSpeedTester.probesOverrideForTest = [$"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1"];
        // Герметично: замеры монитора и «Проверить» (прямой канал, отдача) — только на локальный стенд.
        ServerSpeedTester.uploadFallbacks = [];
        SettingsStore.update(ctx, SettingsStore.current() with
        {
            directProbeUrl = $"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1",
            uploadProbeUrl = $"http://127.0.0.1:{net.HttpPort}/up?slow=1",
        });
        NetworkMonitor.directAliveOverrideForTest = () => true;
        ExternalIpChecker.onceOverrideForTest = _ => "198.51.100.1";
        WinSettingsStore.update(ctx, new WinSettings { socksPort = LocalTestNet.FreePort(), httpPort = LocalTestNet.FreePort() });
        SubscriptionManager.init(ctx);
        LastServerStore.save(ctx, null);
        MainScreenModel.resetAutoStartForTest();
        TestProgress.clear();
    }

    public void Dispose()
    {
        model?.Dispose();
        XrayProxyService.shutdown();
        ui.Dispose();
        WinSettingsStore.resetForTest();
        ServerSpeedTester.probesOverrideForTest = null;
        ServerSpeedTester.uploadFallbacks = savedFallbacks;
        SettingsStore.update(ctx, SettingsStore.current() with
        {
            directProbeUrl = SettingsStore.DEFAULTS.directProbeUrl,
            uploadProbeUrl = SettingsStore.DEFAULTS.uploadProbeUrl,
        });
        NetworkMonitor.directAliveOverrideForTest = null;
        ExternalIpChecker.onceOverrideForTest = null;
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private static bool WaitUntil(Func<bool> cond, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(100); }
        return cond();
    }

    private MainSnapshot Snap() { ui.drain(); MainSnapshot? s = null; using var e = new ManualResetEventSlim(); ui.post(() => { s = model!.snapshot(); e.Set(); }); e.Wait(10_000); return s!; }

    private void Import(params int[] ports)
    {
        var body = string.Join("\n", ports.Select((p, i) => $"vless://{LocalTestNet.Uuid}@127.0.0.1:{p}?type=tcp&security=none#srv{i}"));
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx, body, "local");
        Assert.True(r.ok, r.error);
        model = new MainScreenModel(ctx, ui);
    }

    private bool WaitTestDone(int ms = 120_000) => WaitUntil(() => { var s = Snap(); return !s.fullTesting && !s.refreshingSubs && !TestProgress.state.value.active; }, ms);

    [Fact]
    public void Fastest_without_url_sources_runs_full_test_connects_alive_and_lists_it()
    {
        int dead = LocalTestNet.FreePort(), a = net.StartVlessServer(), b = net.StartVlessServer();
        Import(dead, a, b);
        var before = Snap();
        Assert.True(before.status.running == false && before.all.Count == 3 && before.alive.Count == 0);

        model!.onFastest();
        Assert.True(WaitUntil(() => Snap().fullTesting, 5_000), "тест не стартовал");
        Assert.True(WaitTestDone(), "тест не завершился: " + TestProgress.state.value.phase);
        Assert.True(WaitUntil(() => ProxyState.state.value.running, 10_000), ProxyState.state.value.message);
        Assert.Equal("Готово", TestProgress.state.value.phase);

        // Связь подтверждена фактом → активный в «Живых» с номером, плашка называет его же.
        Assert.True(WaitUntil(() => { MonitorCoordinator.wake(); return Snap().status.verified; }, 30_000), "связь не подтверждена");
        var s = Snap();
        var active = Assert.Single(s.all, r => r.isActive);
        Assert.NotEqual("srv0", active.name);   // srv0 — мёртвый порт
        Assert.Contains(s.alive, r => r.isActive && r.number > 0);
        Assert.Equal(active.name, s.status.serverName);
        Assert.True(s.status.serverNumber > 0);
        Assert.Equal("198.51.100.1", s.status.ipText);
        Assert.Equal("VLESS · tcp · none", s.status.subtitle);   // enum как в Kotlin (toString = имя)
        Assert.DoesNotContain(s.alive, r => r.name == "srv0");
        var deadRow = Assert.Single(s.all, r => r.name == "srv0");
        Assert.Equal(-1, deadRow.pingMs);       // пинг ✗ сохранён в реестр
    }

    [Fact]
    public void Live_list_shows_only_what_the_test_found_and_stale_ones_move_to_previous()
    {
        int a = net.StartVlessServer(), b = net.StartVlessServer();
        Import(a, b);
        // srv1 живой, в реестре свежий замер, но этот тест его не проверяет: в «Живых» ему не место.
        var servers = SubscriptionManager.allServers(ctx);
        var tested = servers.First(it => it.remarks == "srv0");
        var skipped = SubscriptionManager.serverKey(servers.First(it => it.remarks == "srv1"));
        SubscriptionManager.applyPingResults(ctx, new Dictionary<string, int> { [skipped] = 50 });
        SubscriptionManager.applySpeedResults(ctx, new Dictionary<string, double> { [skipped] = 99.0 });
        model!.reloadAll();
        Assert.True(WaitUntil(() => Snap().alive.Any(r => r.key == skipped), 5_000), "до теста запись из реестра в «Живых»");

        // Метки в реестре с точностью до минуты: тест должен начаться в следующей.
        Thread.Sleep(TimeSpan.FromSeconds(61 - DateTime.Now.Second));
        model!.onFullTest([tested]);
        Assert.True(WaitUntil(() => Snap().fullTesting, 5_000), "тест не стартовал");
        Assert.DoesNotContain(Snap().alive, r => r.key == skipped);
        Assert.True(WaitTestDone(), "тест не завершился: " + TestProgress.state.value.phase);
        Assert.True(WaitUntil(() => ProxyState.state.value.running, 10_000), ProxyState.state.value.message);

        var after = Snap();
        Assert.Contains(after.alive, r => r.name == "srv0");
        Assert.DoesNotContain(after.alive, r => r.key == skipped);
        Assert.Contains(after.prevMeasured, r => r.key == skipped);
    }

    [Fact]
    public void After_test_monitor_data_is_not_shadowed_by_session_results()
    {
        int a = net.StartVlessServer(), b = net.StartVlessServer();
        Import(a, b);
        model!.onFastest();
        Assert.True(WaitUntil(() => Snap().fullTesting, 5_000), "тест не стартовал");
        Assert.True(WaitTestDone(), "тест не завершился: " + TestProgress.state.value.phase);
        Assert.True(WaitUntil(() => ProxyState.state.value.running, 10_000), ProxyState.state.value.message);

        // Монитор записал в реестр: второй сервер перестал отвечать.
        var other = SubscriptionManager.allServers(ctx).First(it => SubscriptionManager.serverKey(it) != ProxyState.state.value.serverKey);
        SubscriptionManager.applyPingResults(ctx, new Dictionary<string, int> { [SubscriptionManager.serverKey(other)] = -1 });
        model!.reloadAll();
        Assert.True(WaitUntil(() => Snap().alive.All(r => r.key != SubscriptionManager.serverKey(other)), 5_000),
            "сессионный пинг теста перекрыл свежий из реестра");
    }

    [Fact]
    public void Check_without_proxy_says_so_and_with_proxy_confirms_connection()
    {
        Import(net.StartVlessServer());
        model!.onCheck();
        Assert.True(WaitUntil(() => Snap().checkStatus == "Прокси не запущен — нажмите «Самый быстрый»", 5_000), Snap().checkStatus);

        var p = SubscriptionManager.allServers(ctx)[0];
        model.connectServer(p, "ручной выбор");
        Assert.True(WaitUntil(() => ProxyState.state.value.running, 10_000), ProxyState.state.value.message);
        Assert.Contains(MonitorLog.state.value, e => e.text.Contains("ручной выбор") || e.detail.Contains("ручной выбор"));

        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var savedSink = Log.Sink;
        Log.Sink = new CaptureSink(logs);
        model.onCheck();
        var done = WaitUntil(() => { var s = Snap(); return !s.checking && s.checkStatus.Length > 0 && !s.checkStatus.StartsWith("Проверяю") && !s.checkStatus.StartsWith("Сервер жив"); }, 60_000);
        Log.Sink = savedSink;
        Assert.True(done, Snap().checkStatus + "\n" + string.Join("\n", logs.TakeLast(40)));
        var st = Snap();
        Assert.StartsWith("Соединение в норме: ↓", st.checkStatus);
        Assert.Contains("Мбит/с (пинг ", st.checkStatus);
        Assert.True(st.status.verified);
        Assert.NotNull(st.status.speedMbps);

        // Смена активного сервера гасит результат «Проверить» (он про прежний сервер).
        model.onStop();
        Assert.True(WaitUntil(() => !ProxyState.state.value.running, 10_000));
        Assert.True(WaitUntil(() => Snap().checkStatus == "", 5_000), Snap().checkStatus);
        Assert.False(Snap().status.running);
    }

    [Fact]
    public void Exclude_current_pauses_it_and_reselects_and_eye_toggle_resumes()
    {
        int a = net.StartVlessServer(), b = net.StartVlessServer();
        Import(a, b);
        var servers = SubscriptionManager.allServers(ctx);
        model!.connectServer(servers[0], "ручной выбор");
        Assert.True(WaitUntil(() => ProxyState.state.value.running && ProxyState.state.value.serverKey == SubscriptionManager.serverKey(servers[0]), 10_000));

        model.onRemoveCurrent();   // «Не использовать»: на паузу + полный тест
        Assert.True(WaitUntil(() => BlocklistStore.current().isPaused(SubscriptionManager.serverKey(servers[0])), 5_000));
        Assert.True(WaitTestDone(), TestProgress.state.value.phase);
        // Находка порта №8 (исправлено): отложенный активный НЕ инкумбент → тест переключает на второй сервер даже при
        // равной скорости (раньше туннель оставался на отложенном).
        Assert.True(WaitUntil(() => ProxyState.state.value is { running: true } st && st.serverKey == SubscriptionManager.serverKey(servers[1]), 10_000),
            $"не переключился с отложенного: {ProxyState.state.value.label} · {TestProgress.state.value.phase}");
        var s = Snap();
        Assert.Equal(1, s.hiddenCount);                              // отложенный — в счётчике «скрыто»
        Assert.True(Assert.Single(s.all, r => r.name == "srv0").paused);
        Assert.False(Assert.Single(s.all, r => r.name == "srv0").isActive);
        Assert.True(Assert.Single(s.all, r => r.name == "srv1").isActive);

        model.toggleUse(servers[0]);                                  // глаз: снова «использовать»
        Assert.True(WaitUntil(() => !BlocklistStore.current().isPaused(SubscriptionManager.serverKey(servers[0])), 5_000));
        Assert.Equal(0, Snap().hiddenCount);
    }

    [Fact]
    public void Auto_start_connects_last_server_at_once_then_runs_full_test()
    {
        int a = net.StartVlessServer(), b = net.StartVlessServer();
        Import(a, b);
        var last = SubscriptionManager.allServers(ctx)[1];
        LastServerStore.save(ctx, SubscriptionManager.serverKey(last));

        model!.autoStart();
        Assert.True(WaitUntil(() => ProxyState.state.value.serverKey == SubscriptionManager.serverKey(last), 10_000), "последний сервер не поднят сразу");
        Assert.Contains(MonitorLog.state.value, e => (e.text + e.detail).Contains("автозапуск (последний сервер)"));
        Assert.True(WaitUntil(() => Snap().fullTesting || TestProgress.state.value.phase == "Готово", 15_000), "после мгновенной связи не пошёл полный тест");
        Assert.True(WaitTestDone(), TestProgress.state.value.phase);
        Assert.True(ProxyState.state.value.running);

        // Второй раз за процесс — ничего (Kotlin: autoStartDone).
        TestProgress.clear();
        model.autoStart();
        Thread.Sleep(500);
        Assert.False(Snap().fullTesting);
        Assert.Equal("", TestProgress.state.value.phase);
    }

    [Fact]
    public void Auto_start_disabled_in_settings_does_nothing()
    {
        Import(net.StartVlessServer());
        SettingsStore.update(ctx, SettingsStore.current() with { autoStartOnLaunch = false });
        try
        {
            model!.autoStart();
            Thread.Sleep(800);
            Assert.False(ProxyState.state.value.running);
            Assert.False(Snap().fullTesting);
        }
        finally { SettingsStore.update(ctx, SettingsStore.current() with { autoStartOnLaunch = true }); }
    }

    private sealed class CaptureSink(System.Collections.Concurrent.ConcurrentQueue<string> q) : Log.ILogSink
    {
        public void Write(int priority, string tag, string msg, Exception? tr) => q.Enqueue(Log.Format(priority, tag, msg, tr));
    }
}
