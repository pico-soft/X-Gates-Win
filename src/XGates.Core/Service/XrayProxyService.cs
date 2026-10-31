using System.Net;
using System.Net.Sockets;
using XGates.Core.Monitor;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Traffic;
using XGates.Core.Update;
using XGates.Core.Xray;

namespace XGates.Core.Service;

/**
 * Порт service/XrayProxyService.kt для Windows. На Android это foreground-сервис (держит ядро живым в фоне, START_STICKY);
 * на Windows процесс приложения и так живёт в трее, поэтому «сервис» — статический контроллер с тем же API
 * (start/stop/startFromBoot) и той же логикой: подъём ядра в фоне, опрос трафика, реактивный запуск монитора по
 * ProxyState.running, «Только Wi-Fi», восстановление последнего сервера. Состояние отдаём через [ProxyState].
 *
 * Отличия Windows (осознанные): нет foreground-уведомления (вместо — подпись значка в трее через NotificationHelper);
 * обход чужого VPN — фаза 2 (SystemVpnState остаётся NONE); события сети/питания/экрана приходят от приложения
 * (onNetworkChanged/onPowerChanged/onScreenOn), а не BroadcastReceiver'ов.
 */
public static class XrayProxyService
{
    private const string TAG = "XrayProxyService";
    private const long POLL_MS = 2_500L;

    // Поколение опроса трафика: поток опроса живёт, пока текущее поколение — ЕГО. Старт берёт новое, стоп — инкремент.
    // Был голый флаг polling — он тёк: старый поток спит POLL_MS, а рестарт ядра при переключении сервера успевал
    // снова поднять флаг → старый просыпался и продолжал. +1 вечный поток на каждое переключение (15 потоков за 4.5ч).
    private static long pollGeneration;

    // Цикл автомониторинга. Живёт ТОЛЬКО когда туннель активен (реактивно). Исключения цикла глушатся (фон не
    // должен ронять процесс — Промпт 66.B).
    private static readonly object Sync = new();
    private static CancellationTokenSource? monitorCts;
    private static Task? monitorJob;

    private static volatile bool created;
    private static Context? app;
#pragma warning disable CS0414 // на Android coreActive гейтит обход чужого VPN; на Windows обход — фаза 2, поле ради паритета
    private static volatile bool coreActive;
#pragma warning restore CS0414
    // ТЗ Elyor «Только Wi-Fi»: туннель поставлен на паузу (ядро стоп), потому что сеть мобильная. Сервис ЖИВ и
    // слушает сеть — на Wi-Fi сам поднимает последний сервер. Отличает «пауза» от пользовательского Стопа.
    private static volatile bool wifiOnlyPaused;
    private static bool lastRunning;
    private static bool lastWifiOnly;
    /// <summary>Только для тестов гонки стартов (находка №5): пауза в старте между «остановить прежнее» и подъёмом.</summary>
    internal static Action<string?>? beforeCoreUpForTest;

    // Находка порта №5: операции с ядром (старт/стоп/пауза) шли параллельными потоками — два почти одновременных
    // старта («первый рабочий» → «точный топ») могли оставить поднятым НЕ последний сервер (второй видел «уже
    // запущено»), а опоздавший старт — поднять ядро ПОСЛЕ «Стоп». Теперь каждая операция берёт номер в момент
    // ЗАПРОСА и выполняется под одной блокировкой, только если она всё ещё последняя.
    private static readonly object CoreOpSync = new();
    private static long coreOpGen;

    private static long newCoreOp() => Interlocked.Increment(ref coreOpGen);

    private static void runCoreOp(long gen, string what, Action op)
    {
        lock (CoreOpSync)
        {
            if (gen != Interlocked.Read(ref coreOpGen)) { Log.i(TAG, $"{what}: есть более поздний запрос — пропуск"); return; }
            op();
        }
    }

    /// <summary>Аналог onCreate: подписки на состояние (реактивный монитор, «Только Wi-Fi»). Идемпотентно.</summary>
    private static void ensureCreated(Context context)
    {
        lock (Sync)
        {
            app = context.applicationContext;   // как applicationContext сервиса: в приложении он один (тесты — разные каталоги)
            if (created) return;
            created = true;
        }
        // Промпт 95: цикл НЕПРЕРЫВНОСТИ СВЯЗИ работает ВСЕГДА, пока прокси активен (НЕ гейтится monitorEnabled —
        // это ОСНОВНОЙ принцип, а не опция; monitorEnabled внутри цикла гейтит лишь вторичную оптимизацию).
        lastRunning = ProxyState.state.value.running;
        ProxyState.state.changed += _ =>
        {
            var active = ProxyState.state.value.running;
            if (active == lastRunning) return;   // distinctUntilChanged
            lastRunning = active;
            // Пр.146.Ф2 (КОРЕНЬ «зацикливания»): когда СМЕНУ сервера инициировал САМ монитор (подбор/лестница/
            // держать-лучший), stop()+start() на миг роняет running=false → раньше коллектор рвал цикл монитора
            // посреди подбора. Пока идёт поиск монитора — НЕ трогаем его.
            if (active) startMonitor();
            else if (!MonitorCoordinator.monitorSearchRunning) stopMonitor();
        };
        // ТЗ Elyor «Только Wi-Fi»: переключение тумблера применяем немедленно (не ждём смены сети).
        lastWifiOnly = SettingsStore.current().wifiOnlyEnabled;
        SettingsStore.state.changed += _ =>
        {
            var w = SettingsStore.current().wifiOnlyEnabled;
            if (w == lastWifiOnly) return;
            lastWifiOnly = w;
            applyWifiOnly();
        };
        // Пр.139: сразу оценить ТЕКУЩУЮ сеть.
        EconomyNet.applyNow(app!);
        // Промпт 93.I: пока прокси активен — раз в сутки проверять новую версию ЧЕРЕЗ уже работающий туннель
        // (несколько КБ). Раз в час смотрим «пора ли»; dueForAutoCheck троттлит до суток. Тихо при неудаче.
        KThread.start(() =>
        {
            while (true)
            {
                Thread.Sleep(60 * 60 * 1000);
                var a = app;
                if (a != null) UpdateAutoCheck.periodicTick(a, ProxyState.state.value.running);
            }
        }, "update-auto");
    }

    // ---- события платформы (приложение пробрасывает их сюда) ----

    /// <summary>Сеть появилась/сменилась (NetworkChange): будим монитор, пересчёт экономии, «Только Wi-Fi», белые списки.</summary>
    public static void onNetworkChanged()
    {
        var a = app;
        MonitorCoordinator.wake();
        if (a == null) return;
        EconomyNet.onDefaultNetwork(a);
        applyWifiOnly(EconomyNet.lastKnownMobile);
        WhiteListDetector.onNetworkChange(a);
    }

    /// <summary>Шнур подключили/отключили, ПК вышел из сна: пересчитать щадящий режим и немедленно проверить связь.</summary>
    public static void onPowerChanged()
    {
        var a = app;
        if (a != null) EconomyNet.applyNow(a);
        MonitorCoordinator.wake();
    }

    /// <summary>Пользователь вернулся (разблокировка/развернул окно) — немедленная проверка связи (Промпт 95.B).</summary>
    public static void onScreenOn() => MonitorCoordinator.wake();

    // ---- команды (аналог Intent-действий) ----

    /// <summary>Запуск с готовым конфигом.</summary>
    public static void start(Context context, string config, string label, string serverKey)
    {
        ensureCreated(context);
        handleStart(config, label, serverKey);
    }

    public static void stop(Context context)
    {
        ensureCreated(context);
        handleStop();
    }

    /// <summary>Пр.134: автозапуск (на Windows — при входе в систему): поднять последний сервер без полного теста.</summary>
    public static void startFromBoot(Context context)
    {
        ensureCreated(context);
        handleRestartAfterKill(boot: true);
    }

    /// <summary>«Повторить обход» системного VPN (Промпт 62.C). Windows: обход — фаза 2, no-op.</summary>
    public static void retryVpnBypass(Context context) { }

    private static void handleStart(string? config, string? label, string? serverKey)
    {
        if (string.IsNullOrEmpty(config)) return;
        NotificationHelper.updateStatus(label);

        // ТЗ Elyor «Только Wi-Fi»: запуск на МОБИЛЬНОЙ сети → ядро не поднимаем, сразу пауза. Запоминаем выбранный
        // сервер (на Wi-Fi возобновление поднимет именно его из LastServerStore).
        if (SettingsStore.current().wifiOnlyEnabled && EconomyNet.currentMobile(app!))
        {
            if (serverKey != null) LastServerStore.save(app!, serverKey);
            enterWifiOnlyPause(label, serverKey, "запуск на мобильной сети");
            return;
        }
        ProxyState.update(running: false, label: label, serverKey: serverKey, message: "запуск…");

        coreActive = true;
        var gen = newCoreOp();
        KThread.start(() => runCoreStart(config, label, serverKey, gen), "core-start");
    }

    /**
     * Промпт 115 (Android: возврат после убийства системой) / Пр.134 (автозапуск): поднимаем ПОСЛЕДНИЙ успешный сервер
     * из [LastServerStore] БЕЗ полного теста. Нет сохранённого сервера/профиля/конфига — ядро вслепую НЕ поднимаем,
     * пишем событие.
     */
    private static void handleRestartAfterKill(bool boot = false)
    {
        var a = app!;
        ProxyState.update(running: false, label: null, serverKey: null, message: boot ? "автозапуск…" : "восстановление после сбоя…");
        coreActive = true;
        if (SettingsStore.current().wifiOnlyEnabled && EconomyNet.currentMobile(a))
        {
            enterWifiOnlyPause(null, null, boot ? "автозапуск на мобильной сети" : "восстановление на мобильной сети");
            return;
        }
        var lastAlive = LastServerStore.lastAlive(a);
        var downtimeMs = lastAlive > 0L ? JavaSystem.currentTimeMillis() - lastAlive : -1L;
        var gen = newCoreOp();
        KThread.start(() =>
        {
            try
            {
                var lastKey = LastServerStore.load(a);
                var profile = lastKey == null ? null : SubscriptionManager.allServers(a).FirstOrDefault(it => SubscriptionManager.serverKey(it) == lastKey);
                // Пр.137: последний сервер оказался в стоп-листе/на паузе — авто-подъём НЕ поднимает заблокированный,
                // берём лучшую незаблокированную замену по сохранённым данным. Нет замены — ниже «поднимать нечем».
                if (profile != null)
                {
                    var bl = BlocklistStore.current();
                    if (ServerFilter.isBlocked(profile, bl) || ServerFilter.isPaused(profile, bl))
                    {
                        MonitorLog.@event(a, "monitor", "Последний сервер в стоп-листе — беру замену", ServerLabels.full(profile));
                        profile = SubscriptionManager.bestSavedSelectable(a);
                    }
                }
                string? config = null;
                if (profile != null) { try { config = XrayConfigBuilder.build(profile); } catch (Exception) { } }
                if (profile == null || config == null)
                {
                    MonitorLog.@event(a, "monitor",
                        boot ? "Автозапуск — поднимать нечем" : "Восстановить нечем",
                        lastKey == null ? "нет сохранённого сервера" : "последнего сервера нет в списке / конфиг не собрался");
                    ProxyState.update(running: false, label: null, serverKey: null, message: "нет сервера для подъёма");
                    return;
                }
                var label = ServerLabels.full(profile);
                var serverKey = SubscriptionManager.serverKey(profile);
                var downtimeStr = downtimeMs >= 0 && downtimeMs <= 48L * 3600_000L ? $"простой ~{downtimeMs / 1000}с" : "простой неизвестен";
                MonitorLog.@event(a, "monitor",
                    boot ? "Автозапуск при входе в Windows" : "Восстанавливаюсь после сбоя",
                    boot ? $"последний сервер: {label}" : $"{downtimeStr} · сервер: {label}");
                NotificationHelper.updateStatus(label);
                runCoreStart(config, label, serverKey, gen);
            }
            catch (Exception e)
            {
                Log.w(TAG, $"восстановление последнего сервера упало (проглочено): {e.Message}", e);
            }
        }, "restart-last");
    }

    /// <summary>Блокирующий подъём ядра (старт xray → пробы портов). Зовётся из ФОНОВОГО потока.</summary>
    private static void runCoreStart(string config, string? label, string? serverKey, long gen) =>
        runCoreOp(gen, $"старт «{label}»", () => runCoreStartLocked(config, label, serverKey));

    private static void runCoreStartLocked(string config, string? label, string? serverKey)
    {
        // Весь поток старта под перехватом: любая ошибка → сообщение + стоп, НЕ падение.
        try
        {
            stopTrafficPolling();   // остановить опрос прежнего туннеля при переключении сервера
            if (XrayController.isRunning)
            {
                var d = XrayController.queryTunnelDelta();   // финал старого туннеля
                if (d != null) TrafficTracker.addTunnel(d.Value.down, d.Value.up);
                XrayController.stop();   // авто-переключение сервера
            }
            beforeCoreUpForTest?.Invoke(label);
            // Windows (§13.7): порт занят ДРУГОЙ программой → понятная причина вместо «ядро не запустилось».
            // Наш прежний туннель уже остановлен выше, так что занятость — чужая.
            var busy = new[] { XrayConfig.SOCKS_PORT, XrayConfig.HTTP_PORT }.Where(p => !XrayConfig.portFree(p)).ToList();
            if (busy.Count > 0)
            {
                ProxyState.update(running: false, label: label, serverKey: serverKey,
                    message: $"Порт {string.Join(" и ", busy)} занят другой программой — закройте её или смените порт в «Настройки → Локальный прокси»");
                NotificationHelper.clearStatus();
                return;
            }
            bool ok;
            try { ok = XrayController.start(app!, config); }
            catch (Exception e)
            {
                ProxyState.update(running: false, label: label, serverKey: serverKey, message: $"ОШИБКА: {e.Message}");
                NotificationHelper.clearStatus();
                return;
            }
            if (ok)
            {
                var socks = probe(XrayConfig.SOCKS_PORT);
                var http = probe(XrayConfig.HTTP_PORT);
                ProxyState.update(running: true, label: label, serverKey: serverKey,
                    message: "", socksOk: socks, httpOk: http);   // порты — отдельными флагами (одна строка в UI)
                LastServerStore.save(app!, serverKey);   // запомнить для автозапуска
                startTrafficPolling();
                // Монитор поднимается РЕАКТИВНО (подписка на ProxyState в ensureCreated) — здесь не зовём.
            }
            else
            {
                ProxyState.update(running: false, label: label, serverKey: serverKey, message: "ядро не запустилось");
                NotificationHelper.clearStatus();
            }
        }
        catch (Exception e)
        {
            Log.w(TAG, $"старт упал (проглочено): {e.Message}", e);
            try { ProxyState.update(running: false, label: label, serverKey: serverKey, message: $"ОШИБКА старта: {e.Message}"); } catch (Exception) { }
        }
    }

    /// <summary>Запустить цикл монитора (идемпотентно). Зовётся реактивной подпиской.</summary>
    private static void startMonitor()
    {
        lock (Sync)
        {
            if (monitorJob is { IsCompleted: false }) return;
            var cts = new CancellationTokenSource();
            monitorCts = cts;
            var ctx = app!;
            monitorJob = Task.Run(async () =>
            {
                try { await NetworkMonitor.loop(ctx, cts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception e) { Log.w(TAG, $"цикл монитора упал (проглочено): {e.Message}", e); }
            });
        }
    }

    private static void stopMonitor()
    {
        lock (Sync)
        {
            try { monitorCts?.Cancel(); } catch (ObjectDisposedException) { }
            monitorCts = null;
            monitorJob = null;
        }
    }

    private static void handleStop()
    {
        stopMonitor();
        wifiOnlyPaused = false;   // пользователь нажал Стоп — выходим из «Только Wi-Fi» паузы
        TunnelHealth.reset();     // Промпт 95: прокси остановлен — статус не зелёный
        NotificationHelper.cancelNoServers(app!);
        coreActive = false;
        SystemVpnState.reset();
        var gen = newCoreOp();
        KThread.start(() => runCoreOp(gen, "стоп", () =>
        {
            stopTrafficPolling();
            var d = XrayController.queryTunnelDelta();   // финальный замер туннеля
            if (d != null) TrafficTracker.addTunnel(d.Value.down, d.Value.up);
            XrayController.stop();
            TrafficTracker.onServiceStop();
            ProxyState.update(running: false, label: null, serverKey: null, message: "остановлено");
            NotificationHelper.clearStatus();
        }), "core-stop");
    }

    // ---- ТЗ Elyor: режим «Только Wi-Fi» (пауза на мобильной, авто-подъём на Wi-Fi) ----

    /**
     * Привести состояние к текущей сети при включённом «Только Wi-Fi». Мобильная → ПАУЗА (ядро стоп, сервис жив);
     * Wi-Fi/Ethernet ИЛИ фича выключена (а мы были на паузе) → ВОЗОБНОВЛЕНИЕ. Идемпотентно.
     * Windows: «мобильная» — фаза 2 (Device.isMobileNetwork=false), поэтому пауза пока не наступает.
     */
    private static void applyWifiOnly(bool? isMobileArg = null)
    {
        var a = app;
        if (a == null) return;
        var isMobile = isMobileArg ?? EconomyNet.currentMobile(a);
        var enabled = SettingsStore.current().wifiOnlyEnabled;
        if (enabled && isMobile)
        {
            if (!wifiOnlyPaused)
            {
                var st = ProxyState.state.value;
                enterWifiOnlyPause(st.label, st.serverKey, "сеть стала мобильной");
            }
        }
        else if (wifiOnlyPaused) resumeFromWifiOnly();   // Wi-Fi вернулся ИЛИ «Только Wi-Fi» выключили
    }

    /// <summary>Войти в паузу «Только Wi-Fi»: остановить ядро, остаться и ждать Wi-Fi.</summary>
    private static void enterWifiOnlyPause(string? label, string? serverKey, string reasonDetail)
    {
        wifiOnlyPaused = true;
        coreActive = false;
        MonitorLog.@event(app!, "net", "Только Wi-Fi: пауза — жду Wi-Fi", reasonDetail);
        TunnelHealth.reset();
        NotificationHelper.updateStatus("Пауза — жду Wi-Fi");
        ProxyState.update(running: false, label: label, serverKey: serverKey, message: "Пауза — жду Wi-Fi (мобильная сеть)", paused: true);
        var gen = newCoreOp();
        KThread.start(() => runCoreOp(gen, "пауза Wi-Fi", () =>
        {
            stopTrafficPolling();
            try { var d = XrayController.queryTunnelDelta(); if (d != null) TrafficTracker.addTunnel(d.Value.down, d.Value.up); } catch (Exception) { }
            try { XrayController.stop(); } catch (Exception) { }
            TrafficTracker.onServiceStop();
        }), "wifi-only-pause");
    }

    /// <summary>Wi-Fi вернулся (или «Только Wi-Fi» выключили) — поднять ПОСЛЕДНИЙ рабочий сервер. Монитор поднимется реактивно.</summary>
    private static void resumeFromWifiOnly()
    {
        if (!wifiOnlyPaused) return;
        wifiOnlyPaused = false;
        coreActive = true;
        MonitorLog.@event(app!, "net", "Только Wi-Fi: возобновление", "сеть не мобильная — поднимаю последний сервер");
        ProxyState.update(running: false, label: null, serverKey: null, message: "возобновление (Wi-Fi)…", paused: false);
        var gen = newCoreOp();
        KThread.start(() => resumeLastServerBlocking(gen), "wifi-only-resume");
    }

    /// <summary>Блокирующий подъём последнего сервера при возобновлении (как restart, но без оценки простоя).</summary>
    private static void resumeLastServerBlocking(long gen)
    {
        var a = app!;
        try
        {
            var lastKey = LastServerStore.load(a);
            var profile = lastKey == null ? null : SubscriptionManager.allServers(a).FirstOrDefault(it => SubscriptionManager.serverKey(it) == lastKey);
            if (profile != null)
            {
                var bl = BlocklistStore.current();
                if (ServerFilter.isBlocked(profile, bl) || ServerFilter.isPaused(profile, bl))
                    profile = SubscriptionManager.bestSavedSelectable(a);
            }
            string? config = null;
            if (profile != null) { try { config = XrayConfigBuilder.build(profile); } catch (Exception) { } }
            if (profile == null || config == null)
            {
                MonitorLog.@event(a, "monitor", "Только Wi-Fi: поднимать нечем", "нет сохранённого сервера / конфиг не собрался");
                ProxyState.update(running: false, label: null, serverKey: null, message: "нет сервера для подъёма", paused: false);
                return;
            }
            var label = ServerLabels.full(profile);
            var serverKey = SubscriptionManager.serverKey(profile);
            NotificationHelper.updateStatus(label);
            runCoreStart(config, label, serverKey, gen);
        }
        catch (Exception e)
        {
            Log.w(TAG, $"возобновление wifi-only упало (проглочено): {e.Message}", e);
        }
    }

    /// <summary>Опрос трафика туннеля раз в ~2.5с, только пока прокси активен.</summary>
    private static void startTrafficPolling()
    {
        var a = app!;
        TrafficTracker.attachContext(a);
        TrafficTracker.onServiceStart();
        var gen = Interlocked.Increment(ref pollGeneration);   // заодно гасит прежний поток, если стоп его не погасил
        LastServerStore.heartbeat(a);   // Промпт 115: отметить живость сразу при старте туннеля
        KThread.start(() =>
        {
            var lastHeartbeatMs = JavaSystem.currentTimeMillis();
            while (Interlocked.Read(ref pollGeneration) == gen && XrayController.isRunning)
            {
                Thread.Sleep((int)POLL_MS);
                if (Interlocked.Read(ref pollGeneration) != gen) break;   // за время сна был стоп/рестарт — этот поток уже не наш
                // Промпт 115: «пульс живости» раз в ~15с (не на каждый опрос — бережём запись prefs).
                var nowMs = JavaSystem.currentTimeMillis();
                if (nowMs - lastHeartbeatMs >= 15_000L) { lastHeartbeatMs = nowMs; LastServerStore.heartbeat(a); }
                // Ошибка одного опроса не должна ронять поток/процесс — пропускаем тик.
                try
                {
                    var d = XrayController.queryTunnelDelta();
                    if (d != null)
                    {
                        if (SettingsStore.current().verboseLogs) Log.i("TrafficPoll", $"delta ↓{d.Value.down} ↑{d.Value.up} (байт с прошлого опроса)");
                        TrafficTracker.addTunnel(d.Value.down, d.Value.up);
                    }
                }
                catch (Exception e)
                {
                    Log.w("TrafficPoll", $"опрос трафика упал (проглочено): {e.Message}");
                }
            }
        }, "traffic-poll");
    }

    /// <summary>Погасить поток опроса: его поколение устаревает, он выходит на ближайшем тике (≤ POLL_MS).</summary>
    private static void stopTrafficPolling() => Interlocked.Increment(ref pollGeneration);

    /// <summary>Аналог onDestroy (выход из приложения): погасить монитор, опрос и ядро. Без «сирот» xray.exe.</summary>
    public static void shutdown()
    {
        newCoreOp();   // опоздавшие старты/стоп после выхода не выполняются (блокировку не ждём — как onDestroy Android)
        stopTrafficPolling();
        coreActive = false;
        SystemVpnState.reset();
        stopMonitor();
        TunnelHealth.reset();
        if (XrayController.isRunning)
        {
            var d = XrayController.queryTunnelDelta();
            if (d != null) TrafficTracker.addTunnel(d.Value.down, d.Value.up);
            XrayController.stop();
            TrafficTracker.onServiceStop();
        }
        if (ProxyState.state.value.running)
            ProxyState.update(running: false, label: null, serverKey: null, message: "остановлено");
    }

    private static bool probe(int port)
    {
        try
        {
            using var s = new TcpClient();
            return s.ConnectAsync(IPAddress.Parse(XrayConfig.LISTEN), port).Wait(500) && s.Connected;
        }
        catch (Exception) { return false; }
    }
}
