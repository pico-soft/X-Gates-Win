using XGates.Core.Compat;
using XGates.Core.Crash;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Update;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;
using static XGates.App.UiFormat;

namespace XGates.App;

/// <summary>Строка списка серверов — всё, что рисует ServerRow (Kotlin), уже посчитанное.</summary>
public sealed record ServerRowInfo(
    ServerProfile profile,
    string key,
    string name,
    bool isActive,
    int number,              // номер в «Живых» (1-based); 0 = не показывать
    double? speedMbps,
    bool? speedFull,         // true полный, false минимальный («≈»), null легаси (= полный)
    string speedAge,
    int? pingMs,
    string pingAge,
    string caption,
    bool paused);

/// <summary>Плашка активного сервера — аргументы StatusBox (Kotlin), уже посчитанные.</summary>
public sealed record StatusInfo(
    bool running,
    bool verified,
    string ipText,
    string statusLine,
    string? serverName,
    int serverNumber,
    string? subtitle,
    double? speedMbps,
    int? pingMs,
    string pingAge,
    double? uploadMbps,
    double? directDownMbps,
    double? tunnelDownMbps,
    double? tunnelUpMbps,
    long liveMeasuredAtMs,
    bool tunnelOk,
    bool hidden,
    bool blocked,
    bool problem,
    string message,
    bool paused);

/// <summary>Снимок «Главной» для отрисовки (неизменяемый; пересчитывается из состояния модели).</summary>
public sealed record MainSnapshot(
    StatusInfo status,
    IReadOnlyList<ServerRowInfo> alive,
    IReadOnlyList<ServerRowInfo> prevMeasured,
    IReadOnlyList<ServerRowInfo> all,
    int hiddenCount,
    bool noProtocolsAllowed,
    IReadOnlyList<Protocol> hiddenLiveProtocols,
    bool serversEmpty,
    bool sourcesEmpty,
    bool fullTesting,
    bool refreshingSubs,
    bool checking,
    string checkStatus,
    string subStatus,
    bool showCrashBanner,
    (int code, string name)? updateBanner,
    bool whiteListModeActive,
    bool stopWordsSuspended,
    string modeLine);

/**
 * «Главная» — порт состояния и обработчиков BootScreen (MainActivity.kt) без отрисовки: те же имена функций
 * (onFastest/onFullTest/onCheck/onRefreshAll/connectServer/…), те же правила списков (Живые/Ранее измеренные/Все),
 * та же цепочка автозапуска. Состояние меняется ТОЛЬКО на [ui] (как mutableStateOf в Compose); фоновая работа —
 * потоками, как в Kotlin. Окно перерисовывается по [changed].
 */
public sealed class MainScreenModel : IDisposable
{
    /// <summary>Свежесть подтверждения связи для «зелёного» (Kotlin HEALTH_FRESH_MS).</summary>
    public const long HEALTH_FRESH_MS = 330_000L;

    private readonly Context context;
    private readonly IUiThread uiThread;
    private readonly Timer reloadTimer, ipTimer;
    private bool disposed;

    /// <summary>Автозапуск — один раз за процесс (Kotlin: top-level autoStartDone).</summary>
    private static bool autoStartDone;
    internal static void resetAutoStartForTest() => autoStartDone = false;

    public event Action? changed;

    // ── состояние экрана (только на ui) ──
    private string subStatus = "";
    private List<ServerProfile> servers;
    private IReadOnlyList<SubSource> sources;
    private bool refreshingSubs;
    private volatile bool subRefreshCancel;
    private Dictionary<string, int> pingResults = new();
    private Dictionary<string, long> pingResultsAt = new();
    private Dictionary<string, double> speedResults = new();
    private Dictionary<string, bool> speedResultsFull = new();
    private Dictionary<string, long> speedResultsAt = new();
    private bool fullTesting;
    private FullTestRunner.Handle? fullHandle;
    private bool checking;
    private volatile bool checkCancel;
    private string checkStatus = "";
    private double? activeUploadMbps;
    private bool showCrashBanner;
    private string? lastServerKey;
    private bool lastRunning;
    private CancellationTokenSource? ipDelay;

    public MainScreenModel(Context context, IUiThread ui)
    {
        this.context = context;
        uiThread = ui;
        servers = SubscriptionManager.allServers(context);
        sources = SubscriptionManager.sources(context);
        showCrashBanner = CrashReporter.hasUnseen();
        var p = ProxyState.state.value;
        lastRunning = p.running; lastServerKey = p.serverKey;

        // Перерисовка по внешним состояниям (collectAsState в Kotlin).
        ProxyState.state.changed += onProxyChanged;
        SettingsStore.state.changed += onAnyChanged;
        BlocklistStore.state.changed += onAnyChanged;
        BlocklistStore.stopWordsSuspended.changed += onAnyChanged;
        TunnelHealth.state.changed += onHealthChanged;
        TunnelSpeed.state.changed += onAnyChanged;
        UpdateStore.record.changed += onAnyChanged;

        // Периодически перечитываем реестр (монитор пингует/мерит в фоне) — давности живые; не во время ручного теста.
        reloadTimer = new Timer(_ => post(() => { if (!fullTesting && !checking) reloadServers(); }), null, 15_000, 15_000);
        // Пока прокси активен и окно на экране — подтверждаем связь ФАКТОМ каждые ~12 с (в фоне — монитор сам).
        ipTimer = new Timer(_ => post(() =>
        {
            if (UpdateFlowController.appInForeground && ProxyState.state.value.running && !checking) refreshIp();
        }), null, 12_000, 12_000);
    }

    public void Dispose()
    {
        disposed = true;
        ProxyState.state.changed -= onProxyChanged;
        SettingsStore.state.changed -= onAnyChanged;
        BlocklistStore.state.changed -= onAnyChanged;
        BlocklistStore.stopWordsSuspended.changed -= onAnyChanged;
        TunnelHealth.state.changed -= onHealthChanged;
        TunnelSpeed.state.changed -= onAnyChanged;
        UpdateStore.record.changed -= onAnyChanged;
        reloadTimer.Dispose(); ipTimer.Dispose();
        ipDelay?.Cancel();
    }

    private void post(Action a) { if (!disposed) uiThread.post(a); }
    private void ui(Action a) => post(() => { a(); notifyChanged(); });
    private void notifyChanged() { try { changed?.Invoke(); } catch (Exception e) { Log.w("MainScreenModel", "changed", e); } }
    private void onAnyChanged(object? _) => post(notifyChanged);
    private void onHealthChanged(object? _) => post(() =>
    {
        // Пр.134.C: отметить первый подтверждённый OK (предложение автозагрузки — позже).
        var proxy = ProxyState.state.value;
        if (proxy.running && TunnelHealth.isConfirmedFor(proxy.serverKey, JavaSystem.currentTimeMillis(), HEALTH_FRESH_MS))
            BootPrefs.markHealthyOnce(context);
        notifyChanged();
    });

    /// <summary>LaunchedEffect(proxy.running, proxy.serverKey) и LaunchedEffect(proxy.serverKey) Kotlin.</summary>
    private void onProxyChanged(object? _) => post(() =>
    {
        var proxy = ProxyState.state.value;
        var keyChanged = proxy.serverKey != lastServerKey;
        var runChanged = proxy.running != lastRunning;
        lastServerKey = proxy.serverKey; lastRunning = proxy.running;
        // Результат «Проверить» — про ПРЕЖНИЙ сервер → гасим при смене (кроме момента самой проверки).
        if (keyChanged && !checking) checkStatus = "";
        if (keyChanged || runChanged)
        {
            ipDelay?.Cancel();
            activeUploadMbps = null;   // Пр.123.B: сменился сервер/прокси встал — прежняя отдача не его
            if (proxy.running)
            {
                TunnelHealth.setPhase(TunnelHealth.Phase.VERIFYING, JavaSystem.currentTimeMillis(), "проверяю связь…");
                MonitorCoordinator.wake();   // немедленная проверка (не ждать цикла)
                var cts = ipDelay = new CancellationTokenSource();
                Task.Delay(800, cts.Token).ContinueWith(t => { if (!t.IsCanceled) post(refreshIp); }, TaskScheduler.Default);
            }
        }
        notifyChanged();
    });

    /// <summary>Возврат окна из трея/свёрнутого (resumeTick): сразу освежить IP — иначе плашка серела бы до ~12 с.</summary>
    public void onResume() => post(() =>
    {
        if (ProxyState.state.value.running && !checking) refreshIp();
        showCrashBanner = CrashReporter.hasUnseen();
        notifyChanged();
    });

    public void reloadServers() { servers = SubscriptionManager.allServers(context); CrashContext.registrySize = servers.Count; }
    public void reloadSources() => sources = SubscriptionManager.sources(context);

    /// <summary>Перечитать реестр и источники (после правок на вкладке «Подписки»).</summary>
    public void reloadAll() => ui(() => { reloadSources(); reloadServers(); });

    // ── эффективные значения (сессионное ?: сохранённое) ──
    private int? effPing(ServerProfile p)
    {
        var k = SubscriptionManager.serverKey(p);
        var proxy = ProxyState.state.value;
        var ts = TunnelSpeed.state.value;
        // АКТИВНЫЙ: живой пинг из TunnelSpeed приоритетнее сессионного (тот мог нести старый ✗ из полного теста).
        if (proxy.running && proxy.serverKey == k && ts.serverKey == k && ts.pingMs != null) return ts.pingMs;
        return pingResults.TryGetValue(k, out var v) ? v : p.pingMs;
    }
    private double? effSpeed(ServerProfile p) => speedResults.TryGetValue(SubscriptionManager.serverKey(p), out var v) ? v : p.speedMbps;
    private bool? effSpeedFull(ServerProfile p) => speedResultsFull.TryGetValue(SubscriptionManager.serverKey(p), out var v) ? v : p.speedFull;
    private long? effSpeedAt(ServerProfile p) =>
        speedResultsAt.TryGetValue(SubscriptionManager.serverKey(p), out var v) ? v : ServerFilter.speedMeasuredAtMs(p);
    private string effPingAge(ServerProfile p)
    {
        var key = SubscriptionManager.serverKey(p);
        if (pingResults.ContainsKey(key)) return speedAgeTextMs(pingResultsAt.TryGetValue(key, out var at) ? at : JavaSystem.currentTimeMillis());
        var age = speedAgeText(p.lastTestedTs);
        return age.Length > 0 ? age : p.pingMs != null ? "давно" : "";
    }
    // Как в ServerRow Kotlin: давность скорости — из СОХРАНЁННОЙ метки (speedTestedTs), не сессионной.
    private static string speedAge(ServerProfile p) => speedAgeText(p.speedTestedTs);

    // ── действия ──
    private void startServer(ServerProfile p)
    {
        string cfg;
        try { cfg = XrayConfigBuilder.build(p); }
        catch (Exception e) { subStatus = $"ошибка конфига: {e.Message}"; return; }
        XrayProxyService.start(context, cfg, serverLabel(p, BlocklistStore.current()), SubscriptionManager.serverKey(p));
    }

    /// <summary>Подключение с записью в журнал СМЕНЫ активного сервера (причина: ручной выбор/автозапуск).</summary>
    public void connectServer(ServerProfile p, string cause) => ui(() => connectServerNow(p, cause));

    private void connectServerNow(ServerProfile p, string cause)
    {
        var from = ServerLabels.displayForKey(context, ProxyState.state.value.serverKey);
        MonitorLog.@switch(context, from, displayName(p, BlocklistStore.current()), cause);
        startServer(p);
        MonitorCoordinator.wake();   // действие пользователя сбрасывает паузу монитора / прерывает перебор
    }

    public void onStop() => ui(() =>
    {
        var proxy = ProxyState.state.value;
        if (proxy.running)
            MonitorLog.@event(context, "switch", "Прокси остановлен", $"ручная остановка — {ServerLabels.displayForKey(context, proxy.serverKey) ?? proxy.label ?? "—"}");
        XrayProxyService.stop(context);
    });

    /// <summary>Внешний IP ЧЕРЕЗ активный SOCKS — реальная живость туннеля (не просто «сокет слушает»).</summary>
    public void refreshIp()
    {
        var sk = ProxyState.state.value.serverKey;
        if (sk == null || !ProxyState.state.value.running) return;
        KThread.start(() =>
        {
            var ip = ExternalIpChecker.fetch();
            var nowMs = JavaSystem.currentTimeMillis();
            if (ip != null) TunnelHealth.ok(ip, nowMs, sk);
            else if (TunnelHealth.snapshot().phase == TunnelHealth.Phase.OK)
                TunnelHealth.setPhase(TunnelHealth.Phase.VERIFYING, nowMs, "связь не подтверждена — проверяю");
            MonitorCoordinator.wake();
        }, "refresh-ip");
    }

    /// <summary>Обновить ВСЕ включённые источники. Одна упавшая не роняет прочие. Отменяемо.</summary>
    public void onRefreshAll(Action? onComplete = null) => ui(() => onRefreshAllNow(onComplete));

    private void onRefreshAllNow(Action? onComplete)
    {
        if (refreshingSubs) return;
        var hasUrl = sources.Any(it => it.enabled && it.url.IsNotBlank());
        if (!hasUrl) { subStatus = "нет включённых подписок с URL"; return; }
        refreshingSubs = true; subRefreshCancel = false;
        CrashContext.set("обновление всех источников");
        TestProgress.startIndeterminate("обновление подписок…");
        KThread.start(() =>
        {
            var res = SubscriptionManager.refreshAllEnabled(context, cancelled: () => subRefreshCancel,
                onEach: (src, sum) => ui(() =>
                {
                    TestProgress.phase($"{src.name}: " + (sum.ok ? $"+{sum.added}" : "ошибка"));
                    reloadSources();
                }));
            var okN = res.Values.Count(it => it.ok); var failN = res.Values.Count(it => !it.ok);
            ui(() =>
            {
                refreshingSubs = false;
                TestProgress.finish(subRefreshCancel ? "обновление прервано" : $"подписки обновлены: {okN} ок, {failN} ошибок");
                reloadSources(); reloadServers();
                if (!subRefreshCancel) onComplete?.Invoke();   // цепочка автозапуска: тест по свежему списку
            });
        }, "refresh-all");
    }

    /// <summary>Полный адаптивный тест; [serverList] != null → по заданному списку (перебор из «Проверить»).</summary>
    public void onFullTest(IReadOnlyList<ServerProfile>? serverList = null) => ui(() => onFullTestNow(serverList));

    private void onFullTestNow(IReadOnlyList<ServerProfile>? serverList)
    {
        if (fullTesting) return;
        // Пр.142: «Самый быстрый» БЕЗАКЦЕПТНО перебивает идущую проверку.
        if (checking) { checkCancel = true; ServerSpeedTester.abortCurrentMeasure(); checking = false; MonitorCoordinator.fullTestRunning = false; }
        checkStatus = "";
        IReadOnlyList<ServerProfile> all;
        if (serverList != null) all = serverList;
        else { servers = SubscriptionManager.allServers(context); all = servers; }
        if (all.Count == 0) { subStatus = "нет серверов"; return; }
        fullTesting = true;
        MonitorCoordinator.fullTestRunning = true;   // монитор молчит, пока идёт ручной тест
        // (Android: CPU-wakelock на время теста — на Windows не нужен, процесс не замораживают.)
        MonitorCoordinator.fullTestCancel = () => fullHandle?.cancel();
        MonitorCoordinator.wake();
        CrashContext.set("полный тест: старт");
        TestProgress.startIndeterminate("запуск…");
        pingResults = new(); pingResultsAt = new(); speedResults = new(); speedResultsFull = new(); speedResultsAt = new();

        fullHandle = FullTestRunner.run(
            context: context,
            allServers: all,
            onPhase: ph => { TestProgress.phase(ph); CrashContext.set($"тест: {ph}"); },
            emitProgress: (done, total) => TestProgress.progress(done, total),
            onPingResult: (p, ms) =>
            {
                var k = SubscriptionManager.serverKey(p);
                ui(() => { pingResults = new(pingResults) { [k] = ms }; pingResultsAt = new(pingResultsAt) { [k] = JavaSystem.currentTimeMillis() }; });
            },
            onSpeedResult: (p, mbps, full) =>
            {
                var k = SubscriptionManager.serverKey(p);
                ui(() =>
                {
                    speedResults = new(speedResults) { [k] = mbps };
                    speedResultsFull = new(speedResultsFull) { [k] = full };
                    speedResultsAt = new(speedResultsAt) { [k] = JavaSystem.currentTimeMillis() };
                });
            },
            connect: p => ui(() => startServer(p)),
            onDone: r => ui(() =>
            {
                var pSnap = pingResults; var sSnap = speedResults; var sfSnap = speedResultsFull;   // снимок на ui
                KThread.start(() =>
                {
                    SubscriptionManager.applyPingResults(context, pSnap);
                    SubscriptionManager.applySpeedResultsTagged(context, sSnap.ToDictionary(kv => kv.Key, kv => (kv.Value, sfSnap.TryGetValue(kv.Key, out var f) ? f : true)));
                    ui(() =>
                    {
                        fullTesting = false; fullHandle = null;
                        MonitorCoordinator.fullTestRunning = false;
                        MonitorCoordinator.fullTestCancel = null; CrashContext.set("простой");
                        TestProgress.finish(r.cancelled ? "Отменено" : r.connected != null ? "Готово" : "Готово · рабочих серверов не нашлось");
                        reloadServers();
                        // Активный — реальную скорость на плашку СРАЗУ (не ждём цикл монитора).
                        if (!r.cancelled && r.connected != null)
                            KThread.start(() =>
                            {
                                Thread.Sleep(1500);   // дать туннелю осесть после подключения
                                try { NetworkMonitor.probeActiveSpeedNow(context); } catch (Exception) { }
                            }, "probe-after-test");
                    });
                }, "full-test-persist");
            }));
    }

    /// <summary>«Самый быстрый» (Промпт 91.D): ПЕРЕД тестом обновить все включённые источники, затем полный тест.</summary>
    public void onFastest() => ui(onFastestNow);

    private void onFastestNow()
    {
        if (fullTesting || refreshingSubs) return;
        if (checking) { checkCancel = true; ServerSpeedTester.abortCurrentMeasure(); checking = false; MonitorCoordinator.fullTestRunning = false; checkStatus = ""; }
        var hasUrl = sources.Any(it => it.enabled && it.url.IsNotBlank());
        if (hasUrl) onRefreshAllNow(onComplete: () => onFullTestNow(null)); else onFullTestNow(null);
    }

    /// <summary>Кнопка «Прервать»: на обновлении подписок — флаг, на тесте — отмена теста.</summary>
    public void onCancel() => ui(() => { if (refreshingSubs) subRefreshCancel = true; else onCancelFullNow(); });

    private void onCancelFullNow()
    {
        fullHandle?.cancel(); fullHandle = null;   // Пр.123.D: cancel() рвёт и ИДУЩИЙ замер
        fullTesting = false;
        MonitorCoordinator.fullTestRunning = false;
        MonitorCoordinator.fullTestCancel = null;
        TestProgress.finish("отменено");
        var pSnap = pingResults; var sSnap = speedResults;
        KThread.start(() =>
        {
            Thread.Sleep(1200);                       // дать прерванному замеру закрыть соединение
            ServerSpeedTester.resetMeasureAbort();    // Пр.123.D: вернуть замеры — иначе монитор остаётся «нем»
            SubscriptionManager.applyPingResults(context, pSnap);
            SubscriptionManager.applySpeedResults(context, sSnap);
            ui(reloadServers);
        }, "cancel-full");
    }

    /// <summary>
    /// «Проверить» (трёхступенчатая лестница): пинг текущего → минимальный замер (порог 0.5) → полный (≥1 Мбит/с —
    /// держим) → иначе перебор последних живых (&lt;3 → «Самый быстрый», иначе полный тест по известным живым).
    /// </summary>
    public void onCheck() => ui(onCheckNow);

    private void onCheckNow()
    {
        if (checking) return;
        if (fullTesting || refreshingSubs) { checkStatus = "идёт другой тест — подождите"; return; }
        var curKey = ProxyState.state.value.serverKey;
        if (!ProxyState.state.value.running || curKey == null) { checkStatus = "Прокси не запущен — нажмите «Самый быстрый»"; return; }
        checking = true; checkCancel = false; checkStatus = "Проверяю сервер…";
        ServerSpeedTester.resetMeasureAbort();
        MonitorCoordinator.fullTestRunning = true;   // монитор молчит, пока меряем активный
        string fmt(double v) => v < 0 ? "—" : r1(v);
        var cur = servers.FirstOrDefault(it => SubscriptionManager.serverKey(it) == curKey);
        KThread.start(() =>
        {
            var iterate = false;   // текущий мёртв/без трафика/медленный → в finally перебор последних живых
            try
            {
                if (!NetworkMonitor.directAlive()) { ui(() => checkStatus = "Нет интернета — проверка невозможна"); return; }
                var pingMs = cur != null ? ServerTester.ping(context, cur) : -1L;
                if (checkCancel) { ui(() => checkStatus = ""); return; }
                SubscriptionManager.applyPingResults(context, new Dictionary<string, int> { [curKey] = (int)pingMs });
                if (pingMs < 0)
                {
                    ui(() => checkStatus = "Текущий сервер не отвечает — перебираю живые…");
                    iterate = true; return;
                }
                ui(() => checkStatus = $"Сервер жив (пинг {pingMs} мс) — быстрый замер…");
                var minimal = ServerSpeedTester.measureActiveDownloadMinimal(context);
                if (checkCancel) { ui(() => checkStatus = ""); return; }
                if (minimal < 0.5)
                {
                    ui(() => checkStatus = $"Сервер жив (пинг {pingMs} мс), но трафик не идёт — перебираю живые…");
                    iterate = true; return;
                }
                ui(() => checkStatus = $"Сервер жив (пинг {pingMs} мс, ~{fmt(minimal)}) — точный замер…");
                var down = ServerSpeedTester.measureActiveDownloadStaged(context);
                if (checkCancel) { ui(() => checkStatus = ""); return; }
                var up = ServerSpeedTester.measureActiveUploadMbps(context);
                if (checkCancel) { ui(() => checkStatus = ""); return; }
                if (down >= 1.0)
                {
                    SubscriptionManager.applySpeedResultsTagged(context, new Dictionary<string, (double, bool)> { [curKey] = (down, true) });
                    TunnelSpeed.setLive(down, up > 0 ? up : 0.0, JavaSystem.currentTimeMillis(), curKey);
                    TunnelHealth.ok(TunnelHealth.snapshot().ip, JavaSystem.currentTimeMillis(), curKey);
                    ui(() =>
                    {
                        speedResults = new(speedResults) { [curKey] = down };
                        speedResultsFull = new(speedResultsFull) { [curKey] = true };
                        speedResultsAt = new(speedResultsAt) { [curKey] = JavaSystem.currentTimeMillis() };
                        activeUploadMbps = up > 0 ? up : null;
                        checkStatus = $"Соединение в норме: ↓{fmt(down)} / ↑{fmt(up)} Мбит/с (пинг {pingMs} мс)";
                    });
                }
                else
                {
                    SubscriptionManager.applySpeedResultsTagged(context, new Dictionary<string, (double, bool)> { [curKey] = (down, true) });
                    ui(() => checkStatus = $"Текущий медленный (↓{fmt(down)} < 1 Мбит/с) — перебираю живые…");
                    iterate = true;
                }
            }
            catch (Exception e)
            {
                ui(() => checkStatus = $"Ошибка проверки: {e.Message}");
            }
            finally
            {
                ui(() =>
                {
                    checking = false; MonitorCoordinator.fullTestRunning = false; reloadServers();
                    if (iterate && !checkCancel)
                    {
                        var bl = BlocklistStore.current();
                        var known = servers.Where(it =>
                        {
                            var k = SubscriptionManager.serverKey(it);
                            return k != curKey && effPing(it) is >= 0 && !ServerFilter.isBlocked(it, bl) && !ServerFilter.isPaused(it, bl);
                        }).DistinctBy(SubscriptionManager.serverKey).ToList();
                        if (known.Count < 3) onFastestNow(); else onFullTestNow(known);
                    }
                });
            }
        }, "check");
    }

    /// <summary>Пр.101.B: отмена НЕМЕДЛЕННО — флаг + закрыть идущий замер.</summary>
    public void onCancelCheck() { checkCancel = true; ServerSpeedTester.abortCurrentMeasure(); }

    /// <summary>Промпт 91: «Не использовать» текущий сервер и сразу подобрать быстрейший (через полный тест).</summary>
    public void onRemoveCurrent() => ui(() =>
    {
        var proxy = ProxyState.state.value;
        var curKey = proxy.serverKey;
        if (curKey == null) return;
        var cur = servers.FirstOrDefault(it => SubscriptionManager.serverKey(it) == curKey);
        var name = cur != null ? displayName(cur, BlocklistStore.current()) : proxy.label ?? curKey;
        BlocklistStore.pauseServer(context, curKey, name, JavaSystem.currentTimeMillis());
        MonitorLog.@event(context, "switch", "Сервер исключён («Не использовать»)", name);
        onFullTestNow(null);
    });

    /// <summary>Промпт 91: «использовать ↔ не использовать» сервер (глаз в списках).</summary>
    public void toggleUse(ServerProfile p) => ui(() =>
    {
        var k = SubscriptionManager.serverKey(p);
        if (BlocklistStore.current().isPaused(k)) BlocklistStore.resumeServer(context, k);
        else BlocklistStore.pauseServer(context, k, displayName(p, BlocklistStore.current()), JavaSystem.currentTimeMillis());
    });

    public void dismissCrashBanner() => ui(() => { CrashReporter.markLatestSeen(); showCrashBanner = false; });

    public void dismissUpdateBanner() => ui(() => { UpdateStore.markDismissed(context); NotificationHelper.cancelUpdate(context); });

    public void toggleStopWordsSuspended() => ui(() =>
    {
        BlocklistStore.setStopWordsSuspended(!BlocklistStore.stopWordsSuspended.value);
        MonitorCoordinator.wake();
    });

    public void forceNormalMode() => KThread.start(() => WhiteListDetector.userForceNormal(context), "white-list-normal");

    /// <summary>Появилась сеть, пока окно открыто (Пр.136): повторить посев дефолтной подписки.</summary>
    public void onNetworkAvailable() => KThread.start(() => DefaultSeeder.maybeSeed(context, "network"), "seed-network");

    /**
     * Автозапуск при старте (по умолчанию, отключаемо). Один раз за процесс. 1) МГНОВЕННАЯ СВЯЗЬ — последний
     * успешный сервер (если в списке и не в стоп-листе/паузе); 2) ФОНОМ — посев/обновление подписок → полный тест.
     */
    public void autoStart() => ui(() =>
    {
        if (autoStartDone || !SettingsStore.current().autoStartOnLaunch) return;
        autoStartDone = true;
        if (!ProxyState.state.value.running)
        {
            var lastKey = LastServerStore.load(context);
            var last = lastKey == null ? null : servers.FirstOrDefault(it => SubscriptionManager.serverKey(it) == lastKey);
            var bl = BlocklistStore.current();
            if (last != null && !ServerFilter.isBlocked(last, bl) && !ServerFilter.isPaused(last, bl))
                connectServerNow(last, "автозапуск (последний сервер)");
        }
        KThread.start(() =>
        {
            var justSeeded = SubscriptionManager.trySeedDefaultSource(context);
            var hasSubs = SubscriptionManager.sources(context).Any(it => it.enabled && it.url.IsNotBlank());
            ui(() =>
            {
                reloadSources(); reloadServers();
                if (justSeeded) onFullTestNow(null);
                else if (hasSubs) onRefreshAllNow(onComplete: () => onFullTestNow(null));
                else onFullTestNow(null);
            });
        }, "auto-start");
    });

    // ── снимок для отрисовки (тот же расчёт, что в теле BootScreen) ──
    public MainSnapshot snapshot()
    {
        var proxy = ProxyState.state.value;
        var settings = SettingsStore.current();
        var blocklist = BlocklistStore.current();
        var health = TunnelHealth.state.value;
        var tunnelSpeed = TunnelSpeed.state.value;
        var nowMs = JavaSystem.currentTimeMillis();

        var notBlocked = servers.Where(it => !ServerFilter.isBlocked(it, blocklist)).ToList();
        var allowedServers = notBlocked.Where(it => ServerFilter.protocolAllowed(it, settings)).ToList();
        var pausedShownCount = allowedServers.Count(it => ServerFilter.isPaused(it, blocklist));
        var hiddenCount = servers.Count - allowedServers.Count + pausedShownCount;
        var hiddenLiveProtocols = notBlocked
            .Where(it => !ServerFilter.isPaused(it, blocklist))
            .Where(it => !ServerFilter.protocolAllowed(it, settings))
            .Where(it => (effPing(it) ?? -1) >= 0)
            .Select(it => it.protocol).Distinct().ToList();

        // ТОП по РЕАЛЬНОЙ скорости: измеренные выше непомеренных; среди измеренных — по скорости убыв.; без замера — по пингу.
        var shown = allowedServers.OrderBy(it => it, Comparer<ServerProfile>.Create((a, b) =>
        {
            var sa = effSpeed(a) ?? 0.0; var sb = effSpeed(b) ?? 0.0;
            bool hasA = sa > 0.0, hasB = sb > 0.0;
            if (hasA && hasB) return sb.CompareTo(sa);
            if (hasA != hasB) return hasA ? -1 : 1;
            return (effPing(a) ?? int.MaxValue).CompareTo(effPing(b) ?? int.MaxValue);
        })).ToList();   // OrderBy стабилен — как sortedWith

        var activeServer = servers.FirstOrDefault(it => proxy.running && proxy.serverKey != null && SubscriptionManager.serverKey(it) == proxy.serverKey);
        var discriminators = buildDiscriminators(servers, blocklist);

        ServerFilter.Tier tierOf(ServerProfile p) => ServerFilter.tier(p, effPing(p), effSpeed(p), effSpeedAt(p), nowMs, settings, blocklist);
        var activeProven = proxy.running && health.lastOkMs > 0 && health.serverKey == proxy.serverKey &&
            health.phase != TunnelHealth.Phase.RECOVERING && health.phase != TunnelHealth.Phase.NO_SERVERS;
        bool isProvenActive(ServerProfile p) => activeProven && proxy.serverKey == SubscriptionManager.serverKey(p);
        var alive = shown.Where(p => tierOf(p) == ServerFilter.Tier.LIVE || isProvenActive(p)).ToList();
        var prevMeasured = shown.Where(p => tierOf(p) == ServerFilter.Tier.PREVIOUS && !isProvenActive(p)).ToList();

        ServerRowInfo row(ServerProfile p, int number)
        {
            var k = SubscriptionManager.serverKey(p);
            return new ServerRowInfo(p, k, displayName(p, blocklist), proxy.running && proxy.serverKey == k, number,
                effSpeed(p), effSpeedFull(p), speedAge(p), effPing(p), effPingAge(p),
                discriminators.TryGetValue(k, out var c) ? c : "", ServerFilter.isPaused(p, blocklist));
        }

        var activeHidden = activeServer != null && !ServerFilter.protocolAllowed(activeServer, settings);
        var activeBlocked = activeServer != null && ServerFilter.isBlocked(activeServer, blocklist);
        var healthLine =
            !proxy.running ? "" :
            health.phase == TunnelHealth.Phase.OK ? "" :
            health.phase == TunnelHealth.Phase.NO_INTERNET ? "Нет интернета" :
            health.phase == TunnelHealth.Phase.RECOVERING
                ? (health.lastOkMs > 0 ? $"Связь с сервером прервалась {humanAgo(nowMs - health.lastOkMs)} назад" : "Связь с сервером ещё не установлена")
                  + " · " + health.detail.IfBlank("подключаюсь к живому серверу") :
            health.phase == TunnelHealth.Phase.NO_SERVERS
                ? "Рабочих серверов нет" + (health.lastCheckMs > 0 ? $" · последняя попытка {monitorTime(health.lastCheckMs)}" : "") :
            "Проверяю связь…";
        var ipVerified = proxy.running && TunnelHealth.isConfirmedFor(proxy.serverKey, nowMs, HEALTH_FRESH_MS);
        var ipForActive = health.serverKey == proxy.serverKey ? health.ip : "";
        var ts = tunnelSpeed.serverKey == proxy.serverKey ? tunnelSpeed : new TunnelSpeed.Speed();
        var liveTunnelFresh = (ts.tunnelDownMbps ?? 0.0) > 0.0 && ts.measuredAtMs > 0 && nowMs - ts.measuredAtMs <= HEALTH_FRESH_MS;
        var connConfirmed = ipVerified || liveTunnelFresh;
        var status = new StatusInfo(
            running: proxy.running, verified: connConfirmed, ipText: ipForActive, statusLine: healthLine,
            serverName: activeServer != null ? displayName(activeServer, blocklist) : proxy.label,
            serverNumber: alive.FindIndex(it => proxy.running && proxy.serverKey == SubscriptionManager.serverKey(it)) + 1,
            subtitle: activeServer != null ? protoNetSec(activeServer) : null,
            speedMbps: activeServer != null ? effSpeed(activeServer) : null,
            pingMs: activeServer != null ? effPing(activeServer) : null,
            pingAge: tunnelSpeed.serverKey == proxy.serverKey && tunnelSpeed.pingAtMs > 0L ? speedAgeTextMs(tunnelSpeed.pingAtMs) : "",
            uploadMbps: activeUploadMbps,
            directDownMbps: ts.directDownMbps, tunnelDownMbps: ts.tunnelDownMbps, tunnelUpMbps: ts.tunnelUpMbps,
            liveMeasuredAtMs: ts.measuredAtMs, tunnelOk: connConfirmed, hidden: activeHidden, blocked: activeBlocked,
            problem: proxy.running && health.phase is TunnelHealth.Phase.NO_INTERNET or TunnelHealth.Phase.RECOVERING or TunnelHealth.Phase.NO_SERVERS,
            message: proxy.message, paused: proxy.paused);

        var save = settings.trafficSaveMode;
        var reason = !settings.economyEnabled ? "выкл" : !settings.trafficSaveAutoByNetwork ? "всегда" : save ? "моб. сеть" : "Wi-Fi";

        return new MainSnapshot(
            status: status,
            alive: alive.Select((p, i) => row(p, i + 1)).ToList(),
            prevMeasured: prevMeasured.Select(p => row(p, 0)).ToList(),
            all: shown.Select(p => row(p, 0)).ToList(),
            hiddenCount: hiddenCount,
            noProtocolsAllowed: settings.allowedProtocols.Count == 0 && servers.Count > 0,
            hiddenLiveProtocols: alive.Count < 3 ? hiddenLiveProtocols : [],
            serversEmpty: servers.Count == 0,
            sourcesEmpty: sources.Count == 0,
            fullTesting: fullTesting, refreshingSubs: refreshingSubs,
            checking: checking, checkStatus: checkStatus, subStatus: subStatus,
            showCrashBanner: showCrashBanner,
            updateBanner: UpdateStore.bannerVersion(settings.notifyNewVersions),
            whiteListModeActive: settings.whiteListModeActive,
            stopWordsSuspended: BlocklistStore.stopWordsSuspended.value,
            modeLine: $"Режим: {(save ? "экономия" : "обычный")} · {reason} ›");
    }
}
