using System.Collections.Concurrent;
using System.Net.Sockets;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Traffic;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;

namespace XGates.Core.Monitor;

/**
 * Цикл АВТОМОНИТОРИНГА. Порт 1:1 с monitor/NetworkMonitor.kt. На Android — корутина в foreground-сервисе; на Windows
 * — асинхронная задача приложения (XrayProxyService.startMonitor), живёт, пока прокси активен.
 * Kotlin suspend/delay → async/Task.Delay; отмена корутины → CancellationToken цикла (loopCt). Android-специфика
 * (будильник Doze, wake-lock, батарея/экран/тип сети) — через MonitorAlarm (no-op) и шов Device.
 *
 * Порядок цикла:
 *   0. Сигнал A (нет интернета) — В САМОМ НАЧАЛЕ, ДО перебора. Нет сети → пауза с удвоением (10м→4ч),
 *      сброс по событию сети / действию пользователя ([MonitorCoordinator.awaitWake]).
 *   1. Сигнал B (внешний IP через активный SOCKS) — туннель жив? да → рутина, ничего не пишем.
 *   2. Тяжёлый замер (прямой русскими CDN + туннель зарубежным) только при провале B.
 *   3. Падение (N неудач подряд) → перебор кандидатов и переключение.
 *
 * Перебор: ВЕСЬ список сверху вниз (от быстрых к медленным по известной скорости); дешёвая проверка
 * (temp-инстанс, реальный запрос) → подключаемся к ПЕРВОМУ живому (связь важнее
 * точного числа), скорость меряем ПОСЛЕ подключения. Не подошёл никто → обновить подписки → ещё проход.
 * Опять никто → предложить включить выключенные источники (не включаем сами) → пауза с удвоением.
 */
public static class NetworkMonitor
{
    private const string TAG = "NetworkMonitor";

    // ТЗ Elyor: пробы «есть ли интернет» (directAlive, ПРЯМОЙ коннект без туннеля) — ТОЛЬКО РОССИЙСКИЕ DNS. Иначе в
    // режиме белых списков (не-RU идёт в туннель/режется) зарубежные 8.8.8.8/1.1.1.1 напрямую недосягаемы → ложное
    // «нет интернета» → монитор зря встаёт. RU-адреса (Yandex) всегда досягаемы напрямую / входят в белый список.
    private static readonly (string host, int port)[] directDnsProbes = [("77.88.8.8", 53), ("77.88.8.1", 53), ("77.88.8.7", 53)];

    // Пр.140: режим белых списков — интервалы. Источники обновляем раз в полчаса; оценку сети — раз в полчаса
    // в норме, но раз в минуту при проблемах (нет серверов/восстановление/нет интернета), чтобы быстро распознать.
    private const long WHITE_REFRESH_MS = 30 * 60_000L;
    private const long WHITE_EVAL_NORMAL_MS = 30 * 60_000L;
    private const long WHITE_EVAL_PROBLEM_MS = 60_000L;
    // Когда режим белых списков АКТИВЕН — проверяем возврат (google вернулся?) ЧАЩЕ обычного, чтобы не сидеть в
    // сужённом режиме лишние полчаса после разблокировки сети. Дешёвая TCP-проба, не пинг/живость.
    private const long WHITE_EVAL_ACTIVE_MS = 5 * 60_000L;

    // Пр.146: в фоновом мониторинге обновляем ВСЕ включённые подписки раз в полчаса (не только белые списки) —
    // чтобы пул серверов не устаревал (мёртвые уходят, новые появляются). Не во время ручного теста/восстановления.
    private const long ALL_SUBS_REFRESH_MS = 30 * 60_000L;
    // Пр.147: если рабочих (живых по пингу) серверов < 10% от всех в подписках — пул сильно деградировал,
    // обновляем подписки ЧАЩЕ (раз в 5 мин), пока доля живых не поднимется выше 10% (ищем свежие серверы).
    private const long ALL_SUBS_REFRESH_DEGRADED_MS = 5 * 60_000L;
    private const int DEGRADED_LIVE_SHARE_PCT = 10;
    // Пр.147: второй признак деградации — >80% ЖИВЫХ (с замером) медленнее 1 Мбит/с → пул почти бесполезен →
    // тоже обновляем чаще (5 мин). Требуем минимум замеренных живых, чтобы не срабатывать на 1-2 серверах.
    private const double SLOW_MBPS_THRESHOLD = 1.0;
    private const int SLOW_LIVE_SHARE_PCT = 80;
    private const int SLOW_RULE_MIN_MEASURED = 3;

    // Пр.146.Ф2: периодический пинг ВСЕХ кандидатов (не только живых) — и прунит мёртвых, и ЛОВИТ воскресших.
    // Раньше пинговали только живых → список монотонно СЖИМАЛСЯ (после рестарта осталось 2 из 146). Пинг дёшев (КБ).
    private const long PING_REFRESH_INTERVAL_MS = 15 * 60_000L;
    // Пр.146 Ф3.5: после переключения (подбор подключает к первому живому) через столько открываем проход
    // «держать лучший» — чтобы вскоре перейти на самый быстрый стабильный, не ждя обычные 6 ч.
    private const long POST_SWITCH_OPTIMIZE_MS = 90_000L;

    private const long SWITCH_THROTTLE_MS = 60_000L;        // в фоне переключаться не чаще раза в 60с
    // Пауза при «НЕТ РАБОЧИХ СЕРВЕРОВ» (интернет есть, но ни один сервер не поднялся): удвоение 10 мин → 4 ч.
    private const long BACKOFF_START_MS = 10 * 60_000L;     // первая пауза «нет серверов» — 10 минут
    // Верхний предел паузы — 4 часа: дальше удваивать бессмысленно (это уже «редкая фоновая проверка»),
    // а событие сети всё равно разбудит мгновенно при появлении сети.
    private const long BACKOFF_MAX_MS = 4 * 3600_000L;

    // НЕТ ИНТЕРНЕТА (прямой канал мёртв): туннель чинить бессмысленно — НЕ трогаем ядро. Честно «нет связи» и
    // ПЕРЕСПРАШИВАЕМ прямой канал по нарастающей: 1→2→4→8→16→…→30 мин, пока интернет не появится. Событие сети
    // поднимает мгновенно; экспонента — лишь верхняя граница проактивной проверки.
    private const long NO_NET_BACKOFF_START_MS = 60_000L;        // первый повтор — через 1 мин
    private const long NO_NET_BACKOFF_MAX_MS = 30 * 60_000L;     // потолок повтора — 30 мин

    // Живая скорость плашки: не чаще раза в ~50с (совпадает с циклом при connectionCheckIntervalSec=60);
    // окно пассивного замера — 6с (счётчик трафика тикает раз в POLL_MS=2.5с → окно должно перекрыть ≥2 тика,
    // иначе дельта случайно 0 при живом трафике); порог «трафик идёт» — 32 КБ за окно (keepalive < этого).
    private const long SPEED_SAMPLE_MIN_GAP_MS = 50_000L;
    private const long SPEED_PASSIVE_WINDOW_MS = 6_000L;
    private const long SPEED_PASSIVE_MIN_BYTES = 32L * 1024L;

    // Пр.143: анти-дребезг обрыва БЕЗ 60-сек ожидания (константа сохранена из Kotlin).
    private const long ANTIFLAP_RETRY_MS = 2_500L;

    // Пр.150: живость активного = ТРАФИК ИЛИ ПИНГ активного (temp, надёжен) + короткий ЗАМЕР. Флапающий ipify-GET
    // убран из ВЕРДИКТА (давал ложные «обрывы»: Read timed out на 5с рукопожатии). Пинг — 8с (воздух рукопожатию).
    private const int PING_LIVENESS_TIMEOUT_MS = 8_000;
    // GRACE: «связь прервалась» объявляем ТОЛЬКО если живость не подтверждалась дольше этого (не по одному провалу).
    // Глючно-живой сервер (редкий успех в окне) → зелёная держится; реально мёртвый (молчит всё окно) → красная.
    // Не const: тест обрыва туннеля укорачивает grace, чтобы проверка восстановления шла секунды, а не минуты.
    internal static long LIVENESS_GRACE_MS = 90_000L;
    internal static long GRACE_RECHECK_MS = 25_000L;      // в grace перепроверяем чаще обычного цикла
    private const long IP_REFRESH_MS = 150_000L;          // внешний IP тянем реже — ТОЛЬКО для показа на плашке

    // ── АДАПТИВНЫЙ ЗАМЕР ЖИВОСТИ (снижение трафика/батареи; ТЗ Elyor поверх Пр.150) ──
    // Пр.150 делал ЗАМЕР СКОРОСТИ активного КАЖДЫЙ цикл (60с) в простое → ~1000-1400 замеров/сут ≈ 0.3-1 ГБ/сут +
    // радио каждую минуту = батарея. Живость активного оставляем на ПИНГЕ (дёшев, КБ, надёжен через свежий сокет —
    // Пр.144). ЗАМЕР СКОРОСТИ — РЕДКО и АДАПТИВНО: пинг стабилен → раз в STABLE; пинг заметно скакнул (признак смены
    // качества канала) → раз в UNSTABLE (чаще). Реальный трафик пользователя по-прежнему даёт скорость бесплатно.
    private const long LIVENESS_MEASURE_STABLE_MS = 30 * 60_000L;   // пинг стабилен → замер раз в 30 мин
    private const long LIVENESS_MEASURE_UNSTABLE_MS = 3 * 60_000L;  // пинг скакнул → замер раз в 3 мин
    private const int PING_JITTER_ABS_MS = 80;         // абсолютный порог «скачка» пинга, мс
    private const int PING_JITTER_FRAC_PCT = 60;       // ИЛИ относительный: изменение > 60% прежнего
    // Q2 Elyor: при ВЫКЛЮЧЕННОМ экране и здоровой связи будим реже (было 60с) — меньше пробуждений = батарея.
    private const long SCREEN_OFF_IDLE_MS = 300_000L;   // 5 мин (экран выкл < 1 ч)
    // ТЗ Elyor 2026-09-27: ЛЕСТНИЦА интервалов простоя на батарее+экран выкл — чем дольше экран не включают (ночь),
    // тем реже будим. Отзывчивость не страдает: включение экрана будит монитор СОБЫТИЕМ мгновенно.
    private const long SCREEN_OFF_MED_MS = 60 * 60_000L;         // экран выкл > 1 ч
    private const long SCREEN_OFF_LONG_MS = 3 * 3600_000L;       // экран выкл > 3 ч
    private const long SCREEN_OFF_IDLE_MED_MS = 10 * 60_000L;    // > 1 ч → пинг раз в 10 мин
    private const long SCREEN_OFF_IDLE_LONG_MS = 15 * 60_000L;   // > 3 ч → раз в 15 мин
    // ЯРУСЫ ПО ЗАРЯДУ: ≤15% на батарее — без скоростных замеров (только пинг-живость); ≤5% — фон почти спит
    // (редкий пинг), вся отзывчивость на СОБЫТИЕ включения экрана. ПОЧЕМУ так (Elyor): «на последнем издыхании»
    // важнее ОТПРАВИТЬ, чем держать фоновый приём — а батарея первична, без заряда нет ничего.
    private const int LOW_BATTERY_PCT = 15;
    private const int CRITICAL_BATTERY_PCT = 5;
    private const long CRITICAL_IDLE_MS = 30 * 60_000L;          // ≤5% заряда → пинг раз в 30 мин (фон почти спит)
    // Утренний рейтинг: раз в сутки к ~6:00 освежаем топ-N живых ОДНОЙ короткой серией (вместо всеночной молотьбы),
    // чтобы к пробуждению был свежий быстрый сервер. Только если заряд выше порога (≤15% — пропускаем).
    private const int MORNING_RATE_HOUR = 6;
    private const int MORNING_RATE_COUNT = 5;

    // ── ФОНОВЫЙ РЕЙТИНГ ТОПА (ТЗ Elyor): развилка ШНУР/БАТАРЕЯ × сеть. На шнуре агрессивно (держим топ-N свежим —
    // батарея не тратится), на батарее экономно (в основном по событию «отвалилось >50% топа»). ≥ порога не перемеряем. ──
    private const long TOP_PING_CHARGING_MS = 3 * 60_000L;        // шнур: пинг топа раз в 3 мин
    private const long TOP_PING_BATTERY_SCREEN_MS = 10 * 60_000L; // батарея+экран вкл: пинг топа раз в 10 мин (экран выкл — не пингуем отдельно)
    // ТЗ Elyor (2026-10-15, «не 5 ГБ/сут»): реже + ОГРАНИЧЕННЫЙ рейтинг (топ-N, не все). С «только несвежие» (6ч)
    // большинство прогонов пустые; реальная работа — лишь когда топ протух. Интервалы увеличены.
    private const long TOP_RATE_CHARGING_WIFI_MS = 30 * 60_000L;  // шнур+Wi-Fi: рейтинг топа раз в 30 мин
    private const long TOP_RATE_CHARGING_MOBILE_MS = 60 * 60_000L;// шнур+моб: раз в 60 мин
    private const long TOP_RATE_BATTERY_WIFI_MS = 60 * 60_000L;   // батарея+Wi-Fi: раз в 60 мин (батарея+моб — только по событию)

    // Пр.143: поддержание списка «Живые» (константа сохранена из Kotlin).
    private const long LIVE_PRUNE_INTERVAL_MS = 10 * 60_000L;

    private enum SwitchResult { SWITCHED, ABORTED, NO_CANDIDATES }

    // Промпт 102 (ГЛАВНЫЙ ФИКС): ключ сервера, ядро которого УЖЕ перезапускали в текущем эпизоде обрыва.
    // ПОЧЕМУ: перезапуск ядра (шаг 1 лестницы) через XrayProxyService.start ставит running=false → сервис
    // пересоздаёт корутину монитора и УБИВАЕТ идущую лестницу ещё ДО перебора серверов; новый монитор снова
    // делает шаг 1 → ВЕЧНЫЙ ЦИКЛ на мёртвом сервере (подтверждено логами P102). Поле переживает рестарт
    // монитора: один и тот же сервер повторно НЕ перезапускаем — сразу к перебору живых. Сбрасывается при OK.
    private static volatile string? restartedKeySinceOk;
    // Утренний рейтинг: день года, в который он уже отработал (чтобы раз в сутки). Поле переживает пересоздание
    // цикла монитора (иначе рестарт дал бы повторный прогон в тот же день).
    private static volatile int lastMorningRateDay = -1;
    // Пр.127.C: когда последний раз делали ШАГ по деградации (throttle между шагами). Переживает пересоздание цикла.
    private static long lastDegradationMs;
    // Сколько ЖИВЫХ по пингу кандидатов пробуем при шаге с деградировавшего активного (ранняя остановка на первом
    // годном). >1 — чтобы мёртвый призрак на вершине по старой скорости не съедал единственную попытку (полевой S908E).
    private const int DEGRADED_STEP_CANDIDATES = 3;

    // Пр.146 Ф3: СТАБИЛЬНОСТЬ. Считаем «падения» сервера за окно (когда он был активным и связь оборвалась).
    // Часто падающий («нестабильный») опускается в подборе В САМЫЙ НИЗ — предпочитаем стабильный, пусть чуть
    // медленнее (просьба Elyor: «постоянные короткие обрывы → искать более стабильный, пусть менее быстрый»).
    // Транзиентно (в памяти, сбрасывается при перезапуске — «недавнее поведение»).
    private static readonly ConcurrentDictionary<string, LinkedList<long>> flapEvents = new();
    private const long STABILITY_WINDOW_MS = 30 * 60_000L;
    private const int FLAP_UNSTABLE_THRESHOLD = 3;

    private static void recordFlap(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var dq = flapEvents.GetOrAdd(key, _ => new LinkedList<long>());
        lock (dq)
        {
            dq.AddLast(now());
            while (dq.Count > 0 && now() - dq.First!.Value > STABILITY_WINDOW_MS) dq.RemoveFirst();
        }
    }

    private static int recentFlaps(string key)
    {
        if (!flapEvents.TryGetValue(key, out var dq)) return 0;
        lock (dq)
        {
            while (dq.Count > 0 && now() - dq.First!.Value > STABILITY_WINDOW_MS) dq.RemoveFirst();
            return dq.Count;
        }
    }

    /// <summary>Пр.146 Ф3: сервер считается НЕСТАБИЛЬНЫМ, если за окно падал ≥ порога раз (в подборе идёт последним).</summary>
    internal static bool isUnstableKey(string key) => recentFlaps(key) >= FLAP_UNSTABLE_THRESHOLD;

    // Отмена цикла (Kotlin: отмена корутины монитора). AsyncLocal — переживает await-переходы между потоками.
    private static readonly AsyncLocal<CancellationToken> loopCt = new();

    private static Task delay(long ms) => Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, ms)), loopCt.Value);

    /// <summary>Kotlin runCatching: исключения глушатся, но ОТМЕНА цикла пробрасывается (иначе стоп монитора тянулся бы).</summary>
    private static async Task<(bool ok, T value, Exception? err)> runCatching<T>(Func<Task<T>> f)
    {
        try { return (true, await f(), null); }
        catch (OperationCanceledException) when (loopCt.Value.IsCancellationRequested) { throw; }
        catch (Exception e) { return (false, default!, e); }
    }

    private static async Task<Exception?> runCatching(Func<Task> f)
    {
        try { await f(); return null; }
        catch (OperationCanceledException) when (loopCt.Value.IsCancellationRequested) { throw; }
        catch (Exception e) { return e; }
    }

    private static Exception? runCatching(Action f)
    {
        try { f(); return null; }
        catch (Exception e) { return e; }
    }

    private static string msg(Exception e) => e.Message ?? "";

    /**
     * Промпт 95 — ФАКТ-ПЕРВЫЙ цикл непрерывности связи. Работает ВСЕГДА, пока прокси активен (НЕ гейтится
     * monitorEnabled — это ОСНОВНОЙ принцип, а не опция; monitorEnabled гейтит лишь вторичную оптимизацию
     * «держать лучший»). ЖИВОСТЬ = ФАКТ прохождения запроса (внешний IP через SOCKS). Провал = мёртвый
     * туннель → немедленная лестница восстановления. Проверка идёт и по расписанию, и по событиям (wake()).
     */
    public static async Task loop(Context context, CancellationToken ct)
    {
        loopCt.Value = ct;
        var app = context.applicationContext;
#pragma warning disable CS0219 // как в Kotlin: счётчик пишется, но вердикт теперь по grace (Пр.150) — оставлен ради паритета
        var failures = 0;
#pragma warning restore CS0219
        var backoffMs = 0L;
        var noNetBackoffMs = 0L;         // экспонента повтора прямого канала при «нет интернета» (1→2→4→…→30 мин)
        var cycles = 0;
        var lastSwitchMs = 0L;
        var lastOptimizeMs = now();
        // Пр.126.A: «через туннель давно нет трафика» — предохранитель перемера. Считаем ФАКТ трафика по
        // приросту туннельных байт между циклами (temp-инстансы кандидатов сюда НЕ попадают).
        var lastUserTrafficMs = now();
        var lastTunnelBytes = 0L;
        var lastSpeedSampleMs = 0L;      // живая скорость плашки: когда последний раз сэмплировали
        var lastActiveProbeMs = 0L;      // когда последний раз делали АКТИВНУЮ пробу (в простое)
        var lastExactAlarm = false;      // Промпт 118: последний тик поставлен ТОЧНЫМ будильником? (для лога/диагностики)
        var lastWhiteEvalMs = 0L;        // Пр.140: когда последний раз оценивали режим белых списков
        var lastWhiteRefreshMs = 0L;     // Пр.140: когда последний раз обновляли источники белого списка
        var lastEconomyRecheckMs = now();   // Пр.141: когда последний раз проверяли активный сервер в экономии
        var lastPingRefreshMs = now();      // Пр.146.Ф2: когда последний раз пинговали ВСЕХ (прунинг + рединдексация)
        var lastAllSubsRefreshMs = now();   // Пр.146: когда последний раз обновляли ВСЕ подписки в фоне (раз в 30 мин)
        var lastLiveConfirmedMs = now();    // Пр.150: когда живость активного подтверждалась (трафик/пинг+замер) — для grace
        var graceRecheck = false;           // Пр.150: в grace-окне перепроверяем чаще (короче интервал следующего цикла)
        var lastLivenessMeasureMs = 0L;     // адаптивный замер: когда последний раз мерили СКОРОСТЬ активного (0 = ещё ни разу)
        var lastActivePingMs = -1;          // адаптивный замер: пинг активного в прошлый раз (для детекта «скачка»)
        var lastFullCycleMs = 0L;           // дебаунс паразитных пробуждений: когда последний раз делали ДОРОГУЮ работу цикла
        var lastTopPingMs = 0L;             // фоновый рейтинг: когда последний раз пинговали ТОП (держим его живость)
        var lastTopRateMs = 0L;             // фоновый рейтинг: когда последний раз делали ПОЛНЫЙ замер топа (перестройка)
        var lastIpRefreshMs = 0L;           // Пр.150: когда последний раз тянули внешний IP для ПОКАЗА на плашке
        var screenOffSinceMs = 0L;          // лестница интервалов: когда экран ПОГАС (0 = экран включён/ещё не гас)
        var cycleLock = MonitorAlarm.newWakeLock(app);   // держит CPU на время ОДНОГО цикла (на Windows — no-op)
        Log.i(TAG, "monitor loop started");

        try
        {
            while (true)
            {
                if (!XrayController.isRunning || !ProxyState.state.value.running) { TunnelHealth.reset(); TunnelSpeed.clear(); return; }

                // Интервал: при восстановлении/нет-серверов — чаще (быстрее вернуть связь); в норме — обычный.
                // Прерываемый wake() — событийные триггеры (сеть/экран/передний план) поднимают ДОСРОЧНО.
                var s = SettingsStore.current();
                var ph = TunnelHealth.snapshot().phase;
                // Лестница интервалов: отмечаем момент, когда экран погас (сброс при включении). Чтение экрана — бесплатно.
                if (screenInteractive(app)) screenOffSinceMs = 0L; else if (screenOffSinceMs == 0L) screenOffSinceMs = now();
                long waitMs;
                if (ph == TunnelHealth.Phase.NO_INTERNET) waitMs = noNetBackoffMs.coerceAtLeast(NO_NET_BACKOFF_START_MS);   // Нет интернета — переспрашиваем по экспоненте
                else if (ph is TunnelHealth.Phase.RECOVERING or TunnelHealth.Phase.NO_SERVERS) waitMs = s.monitorProblemIntervalSec.coerceAtLeast(15) * 1000L;
                else if (graceRecheck) waitMs = GRACE_RECHECK_MS;   // Пр.150: в grace-окне перепроверяем ЧАЩЕ
                else
                {
                    // Q2 Elyor + лестница: здоровая связь + экран ВЫКЛ на батарее → будим реже (5→10→15 мин по времени
                    // без экрана; ≤5% заряда → 30 мин). На ШНУРЕ/экран вкл — обычный интервал (топ свежий, батарея цела).
                    var @base = s.connectionCheckIntervalSec.coerceAtLeast(15) * 1000L;
                    waitMs = idleIntervalMs(app, @base, screenOffSinceMs);
                }
                // ── СОН: отпустить CPU, ждать таймер ИЛИ событие (экран/сеть/пробуждение ПК). На Windows будильник не
                // нужен — awaitWake и есть основной предел (процесс не «замерзает», как в Doze).
                releaseCycleLock(cycleLock);
                lastExactAlarm = MonitorAlarm.scheduleNext(app, waitMs);
                MonitorCoordinator.drainWakeups();
                await MonitorCoordinator.awaitWake(waitMs, ct);
                MonitorAlarm.cancel(app);
                acquireCycleLock(cycleLock);

                var cur = SettingsStore.current();
                if (!XrayController.isRunning || !ProxyState.state.value.running) { TunnelHealth.reset(); TunnelSpeed.clear(); return; }
                if (MonitorCoordinator.fullTestRunning)
                {
                    // Тест сам управляет переключением — лестницу НЕ запускаем. Но статус держим ЧЕСТНЫМ:
                    // нет интернета → сказать; иначе не трогаем (факт подтвердит UI-проверка/следующий цикл).
                    if (!directAlive()) TunnelHealth.setPhase(TunnelHealth.Phase.NO_INTERNET, now(), "нет интернета");
                    continue;
                }
                cycles++;
                Log.i(TAG, $"cycle {cycles} (exactAlarm={(lastExactAlarm ? "true" : "false")}, wait={waitMs / 1000}s, phase={ph})");

                // ── ДЕБАУНС ПАРАЗИТНЫХ ПРОБУЖДЕНИЙ (батарея, ТЗ Elyor) ──
                // Монитор будят ЧАСТО (сеть/уведомление/keepalive), а awaitWake возвращается досрочно → раньше на
                // КАЖДОЕ пробуждение шла дорогая работа. При ЗДОРОВОЙ связи (phase=OK, не grace) выполняем дорогой
                // цикл НЕ ЧАЩЕ эффективного интервала. Проблемные фазы и grace НЕ дебаунсим — реагируем немедленно.
                if (ph == TunnelHealth.Phase.OK && !graceRecheck)
                {
                    var everyMs = idleIntervalMs(app, cur.connectionCheckIntervalSec.coerceAtLeast(15) * 1000L, screenOffSinceMs);
                    if (now() - lastFullCycleMs < everyMs) continue;   // паразитное пробуждение — не тратим пинг/IP/батарею
                }
                lastFullCycleMs = now();

                // Пр.140: режим белых списков — авто-обновление источников (раз в полчаса) + оценка сети. Оценка чаще
                // при проблемах (быстрее распознать «только белый список»), иначе раз в полчаса. Пинг/живость это НЕ
                // трогает — детектор лишь сужает эффективный список серверов (allServers) через whiteListModeActive.
                if (cur.whiteListAutoEnabled)
                {
                    if (now() - lastWhiteRefreshMs >= WHITE_REFRESH_MS)
                    {
                        lastWhiteRefreshMs = now();
                        var e1 = runCatching(() => { SubscriptionManager.refreshWhiteListSources(app); });
                        if (e1 != null) MonitorLog.@event(app, "error", "Ошибка обновления белых списков", msg(e1));
                    }
                    var problem = ph is TunnelHealth.Phase.NO_SERVERS or TunnelHealth.Phase.RECOVERING or TunnelHealth.Phase.NO_INTERNET;
                    // Часть B: активный белый режим переоцениваем чаще (5 мин) — быстрее вернуться в обычный, когда
                    // google снова доступен. При проблеме — ещё чаще (1 мин, распознать «только белый список»).
                    var whiteInterval = problem ? WHITE_EVAL_PROBLEM_MS : cur.whiteListModeActive ? WHITE_EVAL_ACTIVE_MS : WHITE_EVAL_NORMAL_MS;
                    var evalDue = now() - lastWhiteEvalMs >= whiteInterval;
                    if (evalDue)
                    {
                        lastWhiteEvalMs = now();
                        var whiteChanged = false;
                        var e2 = runCatching(() => { whiteChanged = WhiteListDetector.evaluate(app, tunnelForeignOk: false); });
                        if (e2 != null) MonitorLog.@event(app, "error", "Ошибка оценки белых списков", msg(e2));
                        // Часть A: режим белых списков ТОЛЬКО ЧТО включился → активный (зарубежный) сервер на такой сети
                        // не поднимется. НЕ ждём провала по grace/восстановлению — СРАЗУ ищем рабочий сервер из белых
                        // списков (allServers уже сужен до белых источников). Активный УЖЕ белый → не трогаем (живое не
                        // рвём, Пр.95). Возврат к обычному покрыт периодической evaluate выше (google вернулся).
                        if (whiteChanged && SettingsStore.current().whiteListModeActive &&
                            !MonitorCoordinator.fullTestRunning && !MonitorCoordinator.monitorSearchRunning)
                        {
                            var activeKey = ProxyState.state.value.serverKey;
                            var whiteKeys = SubscriptionManager.allServers(app).Select(SubscriptionManager.serverKey).ToHashSet();
                            if (activeKey == null || !whiteKeys.Contains(activeKey))
                            {
                                MonitorLog.@event(app, "net", "Режим белых списков включён — сразу ищу сервер из белых списков",
                                    activeKey == null ? "активного нет" : "активный не из белого списка");
                                TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), "белый список: ищу сервер…");
                                if (await runRecoveryLadder(app, SettingsStore.current()) == SwitchResult.SWITCHED)
                                {
                                    lastSwitchMs = now(); lastLiveConfirmedMs = now(); failures = 0; backoffMs = 0;
                                }
                                continue;   // следующий цикл штатно подтвердит живость нового белого сервера
                            }
                        }
                    }
                }
                else if (cur.whiteListModeActive)
                {
                    // Фичу выключили в настройках, а режим ещё активен → сбросить в обычный.
                    runCatching(() => { WhiteListDetector.evaluate(app, tunnelForeignOk: false); });
                }
                // Пр.146/147: фоновое обновление ВСЕХ включённых подписок. Обычно раз в 30 мин; но если пул сильно
                // деградировал (живых по пингу < DEGRADED_LIVE_SHARE_PCT% от всех) — ЧАЩЕ, раз в 5 мин, пока доля не
                // поднимется (ищем свежие серверы). Не во время ручного теста и не в разгар восстановления.
                {
                    var allSrv = SubscriptionManager.allServers(app);
                    var total = allSrv.Count;
                    var live = allSrv.Where(it => (it.pingMs ?? -1) >= 0).ToList();
                    var liveShare = live.Count;
                    // Признак 1: живых < 10% от всех.
                    var degradedByCount = total > 0 && liveShare * 100 < DEGRADED_LIVE_SHARE_PCT * total;
                    // Признак 2 (Пр.147): среди ЖИВЫХ с замером >80% медленнее 1 Мбит/с (пул почти бесполезен).
                    var measuredLive = live.Where(it => (it.speedMbps ?? -1.0) >= 0.0).ToList();
                    var slowLive = measuredLive.Count(it => (it.speedMbps ?? 0.0) < SLOW_MBPS_THRESHOLD);
                    var degradedBySlow = measuredLive.Count >= SLOW_RULE_MIN_MEASURED &&
                        slowLive * 100 > SLOW_LIVE_SHARE_PCT * measuredLive.Count;
                    var degraded = degradedByCount || degradedBySlow;
                    var interval = degraded ? ALL_SUBS_REFRESH_DEGRADED_MS : ALL_SUBS_REFRESH_MS;
                    if (now() - lastAllSubsRefreshMs >= interval && !MonitorCoordinator.fullTestRunning &&
                        !MonitorCoordinator.monitorSearchRunning && ph != TunnelHealth.Phase.RECOVERING)
                    {
                        lastAllSubsRefreshMs = now();
                        var why = degradedBySlow ? "деградация: медленные, 5 мин" : degradedByCount ? "деградация: мало живых, 5 мин" : "30 мин";
                        MonitorLog.@event(app, "monitor", $"Фон: обновляю подписки ({why})", $"живых {liveShare} из {total}, медленных {slowLive}/{measuredLive.Count}");
                        var e3 = runCatching(() => { SubscriptionManager.refreshAllEnabled(app, cancelled: () => MonitorCoordinator.fullTestRunning, onEach: (_, _) => { }); });
                        if (e3 != null) MonitorLog.@event(app, "error", "Ошибка фонового обновления подписок", msg(e3));
                    }
                }
                // Пр.126.A: отметить факт трафика через туннель (прирост байт с прошлого цикла > порога).
                var tsnap = TrafficTracker.state.value;
                var bytesNow = tsnap.sessionRx + tsnap.sessionTx;
                if (lastTunnelBytes >= 1 && lastTunnelBytes <= bytesNow && bytesNow - lastTunnelBytes > OPTIMIZE_IDLE_TRAFFIC_MIN_BYTES) lastUserTrafficMs = now();
                lastTunnelBytes = bytesNow;

                // ⭐ ПРИНЦИП (Пр.127.A): проверка живости туннеля (СИГНАЛ A directAlive + СИГНАЛ B внешний IP через SOCKS)
                // идёт КАЖДЫЙ цикл с интервалом [connectionCheckIntervalSec] — ОДИНАКОВО в обоих режимах. Режим экономии
                // (trafficSaveMode) её НЕ касается и НЕ разрежает: проверка стоит килобайты, ради неё приложение и живёт.
                // Экономия трогает ТОЛЬКО замеры СКОРОСТИ (мегабайты). Не вздумать «экономить» на проверке живости.
                // ── СИГНАЛ A: есть ли интернет ВООБЩЕ (прямой канал) — отличаем «нет интернета» от «нет серверов» ──
                // Нет интернета → туннель чинить БЕССМЫСЛЕННО (ядро НЕ трогаем): честно «нет связи» + переспрос
                // прямого канала по экспоненте (пауза берётся вверху по фазе NO_INTERNET). Интернет вернётся → ниже
                // проверим туннель и активируем его по нашим правилам (лестница).
                if (!directAlive())
                {
                    failures = 0;
                    noNetBackoffMs = enterNoInternet(app, noNetBackoffMs, cycles);
                    continue;
                }
                noNetBackoffMs = 0L;   // интернет есть — сбросить экспоненту повтора

                // ── ЖИВОСТЬ АКТИВНОГО = ТРАФИК ИЛИ ПИНГ. ЗАМЕР СКОРОСТИ вынесен из живости и делается РЕДКО/АДАПТИВНО
                // (ТЗ Elyor поверх Пр.150 — тот мерил каждый цикл, это был сток трафика/батареи). Живость держит ПИНГ
                // (дёшев). Внешний IP тянем реже, только для плашки. ──
                var curKey0 = ProxyState.state.value.serverKey;
                var curSrv0 = curKey0 == null ? null : SubscriptionManager.allServers(app).FirstOrDefault(it => SubscriptionManager.serverKey(it) == curKey0);
                var aliveNow = false;
                // Пинг АКТИВНОГО — tcping его host:port (ServerTester.ping, быстро, реально), КАЖДЫЙ цикл,
                // НЕЗАВИСИМО от трафика (ТЗ Elyor «на плашке ✗ при живом»).
                var activePm = -1;
                if (curSrv0 != null)
                {
                    activePm = (int)ServerTester.ping(app, curSrv0, PING_LIVENESS_TIMEOUT_MS);
                    SubscriptionManager.applyPingResults(app, new Dictionary<string, int> { [curKey0!] = activePm });   // плашка/список — реальный пинг
                    if (activePm >= 0) TunnelSpeed.setPing(activePm, now(), curKey0!);                                 // живой источник для плашки
                }
                if (await userTrafficActive())
                {
                    aliveNow = true;                                   // реальный трафик — сильнейший факт живости
                }
                else if (curSrv0 != null)
                {
                    var pm = activePm;
                    if (pm >= 0)
                    {
                        aliveNow = true;                               // ПИНГ отвечает → живо (замер скорости — отдельно, ниже)
                        // Адаптивный интервал замера: пинг заметно скакнул (сменилось качество канала) → мерим ЧАЩЕ.
                        var prev = lastActivePingMs;
                        var jumped = prev >= 0 && Math.Abs(pm - prev) > Math.Max(PING_JITTER_ABS_MS, prev * PING_JITTER_FRAC_PCT / 100);
                        lastActivePingMs = pm;
                        var measureDue = now() - lastLivenessMeasureMs >= (jumped ? LIVENESS_MEASURE_UNSTABLE_MS : LIVENESS_MEASURE_STABLE_MS);
                        // Батарея: на «экран выкл + батарея» скоростной замер откладываем — пинг уже подтвердил живость.
                        if (measureDue && heavyMeasureAllowed(app) && !MonitorCoordinator.monitorSearchRunning && !MonitorCoordinator.fullTestRunning)
                        {
                            // Порог замера = порог деградации: короткий замер (бюджет под этот порог) уверенно отвечает на
                            // вопрос «активный ниже порога?». Быстрый сервер подтвердит порог за доли МБ и остановится.
                            var mbps = ServerSpeedTester.measureSufficiency(app, curSrv0, cur.degradationMinMbps.coerceAtLeast(0.1));
                            if (mbps < cur.minUsableMbps)
                            {
                                // пинг есть, но трафик реально НЕ идёт (полу-мёртвый) → в восстановление. Таймер НЕ двигаем:
                                // следующий цикл перемерит; устойчивый провал → grace истечёт → лестница сменит сервер.
                                aliveNow = false;
                            }
                            else
                            {
                                lastLivenessMeasureMs = now();         // успешный замер — держим редкий интервал
                                // АПГРЕЙД С МЕДЛЕННОГО (Q3 Elyor): активный ниже порога деградации → зафиксировать низкую
                                // скорость в «Живых» И шагнуть на явно более быстрого кандидата (чинит «0.4 при топовом 47»,
                                // живое не рвём — см. maybeStep…). Здоровый (≥ порога) НЕ перезаписываем: короткий замер шумный.
                                if (mbps >= 0.0 && mbps <= cur.degradationMinMbps)
                                {
                                    SubscriptionManager.applySpeedResults(app, new Dictionary<string, double> { [curKey0!] = mbps });
                                    await maybeStepFromDegraded(app, cur, curKey0!, mbps);
                                }
                            }
                        }
                    }
                }
                if (aliveNow)
                {
                    lastLiveConfirmedMs = now(); graceRecheck = false;
                    onRecovered(app);
                    // Внешний IP — ТОЛЬКО для показа, реже (флап не влияет на вердикт). Не вышло — держим прежний.
                    string ipShown;
                    if (now() - lastIpRefreshMs >= IP_REFRESH_MS)
                    {
                        lastIpRefreshMs = now(); ipShown = ExternalIpChecker.fetch() ?? TunnelHealth.snapshot().ip;
                    }
                    else ipShown = TunnelHealth.snapshot().ip;
                    TunnelHealth.ok(ipShown, now(), curKey0 ?? "");   // Пр.123.B: подтверждение принадлежит активному серверу
                    failures = 0; backoffMs = 0; restartedKeySinceOk = null;   // Промпт 102: связь ок — сбросить «уже перезапускали»
                    MonitorStatus.update(true, "ок", now(), cycles);
                    // ЖИВАЯ скорость плашки (гибрид): раз в цикл сэмплируем — реальный трафик даёт скорость
                    // бесплатно, иначе активная проба в простое. Держим ↓/↑ и время замера свежими.
                    if (cur.liveSpeedEnabled && now() - lastSpeedSampleMs >= SPEED_SAMPLE_MIN_GAP_MS)
                    {
                        lastSpeedSampleMs = now();
                        var prevProbe = lastActiveProbeMs;
                        var r = await runCatching(() => sampleTunnelSpeed(app, cur, prevProbe));
                        if (r.ok) lastActiveProbeMs = r.value;
                        else MonitorLog.@event(app, "error", "Ошибка замера скорости", msg(r.err!));
                    }
                    // Пр.146.Ф2 + ТЗ Elyor 2026-10-14 (свежий пинг «Живых»): периодически пингуем ВСЕХ кандидатов —
                    // убираем мёртвых И ловим воскресших. Раз в PING_REFRESH_INTERVAL_MS (15 мин). НЕ гейтим по
                    // userTrafficActive — пинг это КБ, активному трафику/замеру НЕ мешает.
                    if (now() - lastPingRefreshMs >= PING_REFRESH_INTERVAL_MS)
                    {
                        lastPingRefreshMs = now();
                        var e4 = await runCatching(() => refreshAllPings(app, cur));
                        if (!e4.ok) MonitorLog.@event(app, "error", "Ошибка пинга всех", msg(e4.err!));
                    }
                    // Вторичное: «держать лучший». Пр.126.B: ДЕФОЛТ ВЫКЛ (monitorOptimizeSec=0) — замеры активного и
                    // кандидата несопоставимы, решения шли бы по шуму. Пр.125.E: в экономии тоже выкл. Когда пользователь
                    // включил — предохранители Пр.126.A (Wi-Fi / давно-нет-трафика / текущий-уже-быстрый) + не во время
                    // активной загрузки (чтобы не делить канал и не портить замер).
                    if (!cur.trafficSaveMode && cur.monitorEnabled && cur.activeOptimizeSec > 0 && heavyMeasureAllowed(app) && now() - lastOptimizeMs >= cur.activeOptimizeSec * 1000L)
                    {
                        lastOptimizeMs = now();
                        var skip = optimizeSkipReason(app, cur, lastUserTrafficMs);
                        if (skip != null) MonitorLog.@event(app, "monitor", "Перемер пропущен (Пр.126)", skip);
                        else if (await userTrafficActive()) { /* идёт загрузка — не мешаем и не портим замер */ }
                        else
                        {
                            var e5 = await runCatching(() => optimizeToFastest(app, cur));
                            if (e5 != null) MonitorLog.@event(app, "error", "Ошибка оптимизации", msg(e5));
                        }
                    }
                    // Пр.141: в ЭКОНОМИИ автоперемер выше выключен, поэтому полу-живой сервер (IP-чек проходит, но
                    // реально плохо) сам не чинится. Раз в economyRecheckSec — ОДИН дешёвый замер достаточности активного;
                    // если ниже порога деградации, шагаем на лучшего кандидата (переиспользуем maybeStepFromDegraded).
                    var recheckKey = ProxyState.state.value.serverKey;
                    if (cur.trafficSaveMode && cur.economyRecheckSec > 0 && !await userTrafficActive() && recheckKey != null &&
                        heavyMeasureAllowed(app) &&   // батарея: не мерить активный ночью на батарее (пинг держит живость)
                        now() - lastEconomyRecheckMs >= cur.economyRecheckSec * 1000L)
                    {
                        lastEconomyRecheckMs = now();
                        var curSrv = SubscriptionManager.allServers(app).FirstOrDefault(it => SubscriptionManager.serverKey(it) == recheckKey);
                        if (curSrv != null)
                        {
                            var e6 = await runCatching(async () =>
                            {
                                var mbps = ServerSpeedTester.measureSufficiency(app, curSrv, cur.sufficientMbps.coerceAtLeast(0.1));
                                MonitorLog.@event(app, "monitor", "Экономия: ре-чек активного", $"{fmt(mbps)} Мбит/с");
                                if (mbps >= 0.0 && mbps <= cur.degradationMinMbps) await maybeStepFromDegraded(app, cur, recheckKey, mbps);
                            });
                            if (e6 != null) MonitorLog.@event(app, "error", "Ошибка ре-чека экономии", msg(e6));
                        }
                    }
                    // ── УТРЕННИЙ РЕЙТИНГ (~6:00, ТЗ Elyor): раз в сутки одной короткой серией освежаем топ-N живых, чтобы
                    // к пробуждению был свежий быстрый сервер — ВМЕСТО всеночной молотьбы. Пропускается при ≤15% заряда.
                    {
                        var cal = DateTime.Now;
                        var hour = cal.Hour;
                        var doy = cal.DayOfYear;
                        var batteryOk = isCharging(app) || batteryLevel(app) > LOW_BATTERY_PCT;
                        if (hour == MORNING_RATE_HOUR && lastMorningRateDay != doy && batteryOk && !await userTrafficActive() &&
                            !MonitorCoordinator.monitorSearchRunning && !MonitorCoordinator.fullTestRunning)
                        {
                            lastMorningRateDay = doy;
                            var e7 = await runCatching(() => morningRateTop(app, cur));
                            if (e7 != null) MonitorLog.@event(app, "error", "Ошибка утреннего рейтинга", msg(e7));
                        }
                    }
                    // ── ФОНОВЫЙ РЕЙТИНГ ТОПА (ТЗ Elyor): держим топ-N свежим. Развилка ШНУР/БАТАРЕЯ × сеть: на шнуре
                    // агрессивно (батарея не тратится), на батарее — в основном по событию «отвалилось >50% топа». Не во
                    // время загрузки пользователя и не во время подбора монитором. На «экран выкл + батарея» — целиком
                    // откладываем до зарядки/экрана (см. heavyMeasureAllowed).
                    if (!await userTrafficActive() && heavyMeasureAllowed(app) &&
                        !MonitorCoordinator.monitorSearchRunning && !MonitorCoordinator.fullTestRunning)
                    {
                        var charging = isCharging(app);
                        var mobile = EconomyNet.lastKnownMobile;
                        var screenOn = screenInteractive(app);
                        // 1) ПИНГ ТОПА (часто, дёшево) → детект «поредел >50%».
                        var pingTopEvery = charging ? TOP_PING_CHARGING_MS : screenOn ? TOP_PING_BATTERY_SCREEN_MS : long.MaxValue;
                        var topDecayed = false;
                        if (now() - lastTopPingMs >= pingTopEvery)
                        {
                            lastTopPingMs = now();
                            var r8 = await runCatching(() => pingTopAndDetectDecay(app, cur));
                            if (r8.ok) topDecayed = r8.value;
                            else MonitorLog.@event(app, "error", "Ошибка пинга топа", msg(r8.err!));
                        }
                        // 2) ПОЛНЫЙ ЗАМЕР ТОПА (рейтинг): по расписанию (шнур/сеть) ИЛИ по событию (топ поредел).
                        var rateEvery =
                            charging && !mobile ? TOP_RATE_CHARGING_WIFI_MS :
                            charging && mobile ? TOP_RATE_CHARGING_MOBILE_MS :
                            !charging && !mobile ? TOP_RATE_BATTERY_WIFI_MS :
                            long.MaxValue;   // батарея + моб → только по событию
                        if (topDecayed || now() - lastTopRateMs >= rateEvery)
                        {
                            lastTopRateMs = now();
                            var e9 = await runCatching(() => rebuildAndRateTop(app, cur));
                            if (e9 != null) MonitorLog.@event(app, "error", "Ошибка фон-рейтинга топа", msg(e9));
                        }
                    }
                    continue;
                }

                // Живость НЕ подтвердилась этот цикл. Интернет мог уйти → это НЕ вина туннеля (ядро не трогаем).
                if (!directAlive())
                {
                    failures = 0;
                    noNetBackoffMs = enterNoInternet(app, noNetBackoffMs, cycles);
                    continue;
                }

                // Пр.150 GRACE: живость была недавно (≤ LIVENESS_GRACE_MS) → НЕ краснеем, держим прежнюю связь, но
                // перепроверяем ЧАЩЕ (graceRecheck). Один провал (частый Read-timeout на живом сервере) красным НЕ мигает.
                if (now() - lastLiveConfirmedMs <= LIVENESS_GRACE_MS)
                {
                    graceRecheck = true;
                    MonitorStatus.update(true, "проверяю связь…", now(), cycles);
                    continue;
                }

                // ── GRACE ИСТЁК → РЕАЛЬНЫЙ ОБРЫВ → восстановление (быстрый замер живых → годный; тщательно при неудаче) ──
                graceRecheck = false;
                failures = 0;
                recordFlap(ProxyState.state.value.serverKey);   // Пр.146 Ф3: отметить падение активного (для стабильности)
                TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), "восстановление…");
                MonitorLog.@event(app, "monitor", $"Связь прервалась (тишина {humanDur(now() - lastLiveConfirmedMs)}) — ищу живой сервер", "ни трафика, ни пинга");
                switch (await runRecoveryLadder(app, cur))
                {
                    case SwitchResult.SWITCHED:
                    {
                        lastSwitchMs = now(); failures = 0; backoffMs = 0; lastLiveConfirmedMs = now();   // Пр.150: связь восстановлена — сброс grace
                        var ip2 = ExternalIpChecker.fetch();   // подтвердить ФАКТОМ сразу
                        if (ip2 != null) { TunnelHealth.ok(ip2, now(), ProxyState.state.value.serverKey ?? ""); onRecovered(app); }
                        else TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), "проверяю после переключения…");
                        // Пр.146 Ф3.5: подбор подключает к ПЕРВОМУ живому (быстро). Чтобы вскоре перейти на САМЫЙ БЫСТРЫЙ
                        // (стабильный) — сдвигаем таймер «держать лучший» так, чтобы его проход открылся через ~90с (а не
                        // ждал полные 6ч). В экономии activeOptimizeSec=0 → проход и так не откроется (МБ не тратим).
                        if (cur.activeOptimizeSec > 0)
                            lastOptimizeMs = now() - cur.activeOptimizeSec * 1000L + POST_SWITCH_OPTIMIZE_MS;
                        break;
                    }
                    case SwitchResult.ABORTED:
                        /* пользователь вмешался — оставляем как есть */
                        break;
                    case SwitchResult.NO_CANDIDATES:
                        // РАБОЧИХ СЕРВЕРОВ НЕТ НИ ОДНИМ СПОСОБОМ — СКАЗАТЬ ПРЯМО (Промпт 95.E) + уведомление.
                        TunnelHealth.setPhase(TunnelHealth.Phase.NO_SERVERS, now(), "рабочих серверов нет");
                        NotificationHelper.notifyNoServers(app);
                        backoffMs = backoffMs == 0L ? BACKOFF_START_MS : Math.Min(backoffMs * 2, BACKOFF_MAX_MS);
                        MonitorLog.@event(app, "monitor", $"Рабочих серверов нет — пауза {humanDur(backoffMs)}", "выход по любому событию");
                        MonitorStatus.update(true, $"нет рабочих серверов, пауза {humanDur(backoffMs)}", now(), cycles);
                        releaseCycleLock(cycleLock);
                        MonitorAlarm.scheduleNext(app, backoffMs);
                        MonitorCoordinator.drainWakeups(); await MonitorCoordinator.awaitWake(backoffMs, ct);
                        MonitorAlarm.cancel(app);
                        acquireCycleLock(cycleLock);
                        break;
                }
            }
        }
        finally
        {
            releaseCycleLock(cycleLock);
            MonitorAlarm.cancel(app);   // цикл монитора гаснет — снять запланированный тик
        }
    }

    private const long CYCLE_WAKELOCK_TIMEOUT_MS = 10 * 60_000L;   // страховка от утечки; нормальный цикл — секунды
    // Пр.126.A: прирост туннельных байт за цикл выше этого = «шёл трафик» (не keepalive). 1 МБ — заведомо реальный.
    private const long OPTIMIZE_IDLE_TRAFFIC_MIN_BYTES = 1_048_576L;
    // Пр.127.C: минимум трафика в окне пассивного замера, чтобы наблюдаемая скорость была ОСМЫСЛЕННОЙ признаком
    // капасити (а не «мало качали»). 512 КБ за окно 6с ≈ ≥0.7 Мбит/с реально прошло — только тогда судим о деградации.
    private const long DEGRADATION_MIN_TRAFFIC_BYTES = 512L * 1024L;

    private static void acquireCycleLock(MonitorAlarm.WakeLock? wl)
    {
        try { if (wl != null && !wl.isHeld) wl.acquire(CYCLE_WAKELOCK_TIMEOUT_MS); } catch (Exception) { }
    }

    private static void releaseCycleLock(MonitorAlarm.WakeLock? wl)
    {
        try { if (wl?.isHeld == true) wl.release(); } catch (Exception) { }
    }

    /**
     * НЕТ ИНТЕРНЕТА (прямой канал мёртв): честно сказать «нет связи», ядро НЕ трогаем. Возвращает СЛЕДУЮЩУЮ паузу
     * экспоненты (1→2→4→…→30 мин) — само ожидание делает верх цикла по фазе NO_INTERNET (прерывается событием сети).
     */
    private static long enterNoInternet(Context app, long prevBackoffMs, int cycles)
    {
        TunnelHealth.setPhase(TunnelHealth.Phase.NO_INTERNET, now(), "нет интернета");
        NotificationHelper.cancelNoServers(app);   // это НЕ «нет серверов» — туннель не виноват
        var b = prevBackoffMs == 0L ? NO_NET_BACKOFF_START_MS : Math.Min(prevBackoffMs * 2, NO_NET_BACKOFF_MAX_MS);
        MonitorStatus.update(true, $"нет интернета · повтор через {humanDur(b)}", now(), cycles);
        return b;
    }

    /// <summary>Связь подтверждена — снять предложения/уведомления «нет серверов»/«включить источники».</summary>
    private static void onRecovered(Context context)
    {
        if (MonitorPrompt.pending) { MonitorPrompt.clear(); NotificationHelper.cancelEnableSources(context); }
        MonitorPrompt.resetDeclined();
        NotificationHelper.cancelNoServers(context);
    }

    private static bool allowedCandidate(ServerProfile it, AppSettings s, Blocklist bl) =>
        ServerFilter.protocolAllowed(it, s) && !ServerFilter.isBlocked(it, bl) && !ServerFilter.isPaused(it, bl);

    /**
     * ЛЕСТНИЦА ВОССТАНОВЛЕНИЯ (Пр.95.C, ТЗ Elyor 2026-09-21). ТОЛЬКО по подтверждённому обрыву активного (grace истёк):
     *  Фаза 1: пинг НЕДАВНИХ живых. Свежий провал пинга → сервер уходит из «Живых».
     *  Фаза 2a (кто-то ОТВЕТИЛ по пингу): СРАЗУ подключаемся к лучшему ответившему + подтверждаем IP (плашка не
     *      висит серой) + мерим ЖИВОЙ туннель. ≥ порога (degradationMinMbps ≈2) — держим, домеряем остальных живых
     *      (обновить список). < порога — домеряем всех живых и уходим на самый быстрый.
     *  Фаза 2b (никто не ответил): пинг ВСЕХ (прунинг) → добираем до [fastTopTarget] живых замерами по топу пинга
     *      (батчи по 10) → подключаемся к самому быстрому.
     *  Никого живого — глубокий фолбэк: перезапуск ядра (Пр.102) → обновить подписки → белые списки → предложить.
     * После подключения — подтверждение ФАКТОМ (внешний IP). Отменяется, если вмешался пользователь (aborted).
     */
    private static async Task<SwitchResult> runRecoveryLadder(Context app, AppSettings s)
    {
        MonitorCoordinator.monitorSearchRunning = true;
        try
        {
            // Ночь/батарея (экран выкл + не на шнуре): восстановление ТОЛЬКО пингом+подтверждением IP, без скоростных
            // замеров (иначе Фаза 2a домеряет остальных, 2b мерит всех живых → полевой факт: ~210 MB за 34 мин на
            // деградировавшем пуле). Связь (Пр.95) держим фактом: ping-alive + внешний IP подтверждён = сервер рабочий;
            // ранжирование по скорости откладываем до зарядки/утреннего рейтинга.
            var doMeasure = heavyMeasureAllowed(app);
            var startKey = ProxyState.state.value.serverKey;
            var bl = BlocklistStore.current();
            var keepThreshold = s.degradationMinMbps.coerceAtLeast(0.1);   // «≥2 Мбит/с» — держим; иначе ищем быстрее
            var target = s.fastTopTarget.coerceAtLeast(1);                 // стараемся иметь N живых (деф. 10)
            var pingTimeout = s.pingTimeoutMs.coerceAtMost(1500);
            List<ServerProfile> allowed() => SubscriptionManager.allServers(app)
                .Where(it => SubscriptionManager.serverKey(it) != ProxyState.state.value.serverKey)
                .Where(it => allowedCandidate(it, s, bl)).ToList();

            // ── Фаза 1: пинг НЕДАВНИХ живых. ТЗ Elyor (2026-10-15): «при быстром восстановлении сначала идём по
            // списку 1 (Живые — свежий замер), потом по списку 2 (Ранее измеренные — замер устарел)». Пингуем
            // именно в этом порядке; вероятно-живые без измеренной скорости (tier NONE) — хвостом, как запас
            // (связь любой ценой, Пр.95; пинг дёшев). Не ответившие на пинг уходят из «Живых» (прунинг ниже).
            var nowRec = now();
            ServerFilter.Tier tierOf(ServerProfile p) =>
                ServerFilter.tier(p, p.pingMs, p.speedMbps, ServerFilter.speedMeasuredAtMs(p), nowRec, s, bl);
            var poolAlive = allowed().Where(isLikelyAlive).ToList();
            Func<ServerProfile, bool> unstable = it => isUnstableKey(SubscriptionManager.serverKey(it));
            var list1 = orderFastCandidates(poolAlive.Where(it => tierOf(it) == ServerFilter.Tier.LIVE).ToList(), unstable);
            var list2 = orderFastCandidates(poolAlive.Where(it => tierOf(it) == ServerFilter.Tier.PREVIOUS).ToList(), unstable);
            var restLikely = orderFastCandidates(poolAlive.Where(it => tierOf(it) == ServerFilter.Tier.NONE).ToList(), unstable);
            var recent = list1.Concat(list2).Concat(restLikely).DistinctBy(SubscriptionManager.serverKey).ToList();
            var pingUpd = new Dictionary<string, int>();
            var responders = new List<ServerProfile>();
            for (int i = 0; i < recent.Count; i++)
            {
                var c = recent[i];
                if (aborted(startKey)) { SubscriptionManager.applyPingResults(app, pingUpd); return SwitchResult.ABORTED; }
                TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), $"пингую недавние: {i + 1}/{recent.Count}");
                MonitorStatus.update(true, $"пинг недавних {i + 1}/{recent.Count} · {ServerLabels.display(c)}", now(), 0);
                var p = (int)ServerTester.ping(app, c, pingTimeout);
                pingUpd[SubscriptionManager.serverKey(c)] = p;
                if (p >= 0) responders.Add(c);
            }
            SubscriptionManager.applyPingResults(app, pingUpd);   // прунинг: не ответившие уходят из «Живых» (Баг 1)

            // ── Фаза 2a: кто-то ответил → подключаемся СРАЗУ к лучшему, мерим ЖИВОЙ, решаем по порогу ──
            if (responders.Count > 0)
            {
                var order = responders.OrderByDescending(it => it.speedMbps ?? 0.0).ThenBy(it => it.pingMs ?? int.MaxValue).ToList();
                var ownedKey = startKey;
                ServerProfile? connected = null;
                var connectedMbps = -1.0;
                foreach (var c in order)
                {
                    if (MonitorCoordinator.fullTestRunning) return SwitchResult.ABORTED;
                    if (ProxyState.state.value.serverKey != ownedKey) return SwitchResult.ABORTED;   // вмешался пользователь
                    MonitorStatus.update(true, $"обрыв — подключаюсь к недавнему · {ServerLabels.display(c)}", now(), 0);
                    var (mbps, ok) = await connectMeasureLive(app, c, ownedKey, doMeasure);
                    ownedKey = ProxyState.state.value.serverKey;   // отражаем факт (start мог/не мог взять)
                    if (ok) { connected = c; connectedMbps = mbps; break; }
                }
                if (connected != null)
                {
                    // Связь уже восстановлена (ранний коннект к быстрейшему-по-рейтингу + IP подтверждён).
                    // Ночь/батарея: этого достаточно — не ранжируем по скорости (оптимизация, а не связь).
                    if (!doMeasure) return SwitchResult.SWITCHED;
                    // ТЗ Elyor (коннект сразу, замеры потом): ТЕПЕРЬ, когда связь есть, уточняем рейтинг тем же принципом,
                    // что «Самый быстрый»/фон — МИНИМАЛЬНЫЙ замер кандидатов (≤ cap, «≈») → ПОЛНЫЙ жизнеспособных (bold,
                    // «умно — только несвежие») → переключиться на быстрейшего, если заметно лучше подключённого.
                    var connectedKey = SubscriptionManager.serverKey(connected);
                    var chargingWifi = isCharging(app) && !EconomyNet.lastKnownMobile;
                    var cap = chargingWifi ? int.MaxValue : s.trafficSaveMode ? 3 : 10;
                    var minGate = 0.5;
                    var freshMs = s.topFreshSec.coerceAtLeast(3600) * 1000L;
                    bool freshFullGood(ServerProfile p)
                    {
                        if (p.speedFull != true || (p.speedMbps ?? 0.0) < keepThreshold) return false;
                        var t = KDate.parseMs(p.speedTestedTs);
                        if (t == null) return false;
                        return now() - t <= freshMs;
                    }
                    // Кандидаты на рейтинг: ответившие, кроме подключённого; топ cap по порядку (скорость↓/пинг↑).
                    var cands = order.Where(it => SubscriptionManager.serverKey(it) != connectedKey).Take(cap == int.MaxValue ? order.Count : cap).ToList();
                    // Этап мин: МИНИМАЛЬНЫЙ (~100 КБ) несвежих (стабильные свежие-полные не трогаем) → «≈».
                    var minimalByKey = new Dictionary<string, double>();
                    var minTagged = new Dictionary<string, (double, bool)>();
                    foreach (var c in cands)
                    {
                        if (MonitorCoordinator.fullTestRunning || ProxyState.state.value.serverKey != ownedKey) return SwitchResult.SWITCHED;
                        if (freshFullGood(c)) { minimalByKey[SubscriptionManager.serverKey(c)] = c.speedMbps ?? 0.0; continue; }
                        var m = ServerSpeedTester.measureSpeedMinimal(app, c);
                        minimalByKey[SubscriptionManager.serverKey(c)] = m;
                        minTagged[SubscriptionManager.serverKey(c)] = (m, false);
                    }
                    if (minTagged.Count > 0) SubscriptionManager.applySpeedResultsTagged(app, minTagged);
                    // Этап полн: ПОЛНЫЙ жизнеспособных (≥minGate) по убыв. минимального, несвежих мерим, стабильных реюзим.
                    var viable = cands.Where(it => minimalByKey.GetValueOrDefault(SubscriptionManager.serverKey(it), -1.0) >= minGate)
                        .OrderByDescending(it => minimalByKey.GetValueOrDefault(SubscriptionManager.serverKey(it), 0.0)).ToList();
                    var bestFull = connectedMbps; ServerProfile? bestP = null;
                    foreach (var c in viable)
                    {
                        if (MonitorCoordinator.fullTestRunning || ProxyState.state.value.serverKey != ownedKey) return SwitchResult.SWITCHED;
                        double full;
                        if (freshFullGood(c)) full = c.speedMbps ?? 0.0;
                        else
                        {
                            full = ServerSpeedTester.measureSpeed(app, c);
                            SubscriptionManager.applySpeedResultsTagged(app, new Dictionary<string, (double, bool)> { [SubscriptionManager.serverKey(c)] = (full, true) });
                        }
                        if (full > bestFull) { bestFull = full; bestP = c; }
                    }
                    // Переключаемся на быстрейшего по ПОЛНОМУ числу: подключённый «в норме» (≥порога) → нужен запас
                    // ×keepBestMultiplier (не дёргаемся на шум/занижение активного замера); иначе апгрейд по марже.
                    var need = connectedMbps >= keepThreshold ? connectedMbps * s.keepBestMultiplier : connectedMbps * (1 + s.marginRatio);
                    if (bestP != null && bestFull > need && !MonitorCoordinator.fullTestRunning && ProxyState.state.value.serverKey == ownedKey)
                    {
                        MonitorStatus.update(true, $"нашёл быстрее — перехожу · {ServerLabels.display(bestP)}", now(), 0);
                        await connectMeasureLive(app, bestP, ownedKey);
                    }
                    return SwitchResult.SWITCHED;
                }
                // никто из ответивших не подтвердил связь → идём в полный поиск (Фаза 2b)
            }

            // ── Фаза 2b: недавние молчат → пинг ВСЕХ + добор до [target] живых замерами → самый быстрый ──
            if (aborted(startKey)) return SwitchResult.ABORTED;
            var disc = await discoverBuildLive(app, s, startKey, target, keepThreshold, doMeasure);
            if (disc != SwitchResult.NO_CANDIDATES) return disc;

            // ── Никого живого/годного → глубокий фолбэк: перезапуск ядра (Пр.102) → подписки → белые → предложить ──
            var curKey = ProxyState.state.value.serverKey;
            var alreadyRestarted = curKey != null && curKey == restartedKeySinceOk;
            if (curKey != null && !aborted(curKey) && !alreadyRestarted)
            {
                var curSrv = SubscriptionManager.allServers(app).FirstOrDefault(it => SubscriptionManager.serverKey(it) == curKey);
                string? cfg = null;
                if (curSrv != null) { try { cfg = XrayConfigBuilder.build(curSrv); } catch (Exception) { } }
                if (curSrv != null && cfg != null)
                {
                    restartedKeySinceOk = curKey;
                    MonitorLog.@event(app, "monitor", "Восстановление: перезапуск ядра (запасной)", ServerLabels.display(curSrv));
                    TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), "пробую переподключиться к серверу");
                    XrayProxyService.start(app, cfg, ServerLabels.full(curSrv), curKey);
                    var waited = 0;
                    while (waited < 8000 && (!ProxyState.state.value.running || ProxyState.state.value.serverKey != curKey)) { await delay(500); waited += 500; }
                    await delay(1500);
                    if (aborted(curKey)) return SwitchResult.ABORTED;
                    if (ExternalIpChecker.fetch() != null) { MonitorLog.@event(app, "monitor", "Восстановление: перезапуск ядра помог", ""); return SwitchResult.SWITCHED; }
                }
            }
            return await runSwitchSearchInner(app, s);
        }
        finally
        {
            MonitorCoordinator.monitorSearchRunning = false;
        }
    }

    /**
     * Подключиться к [c] + СРАЗУ подтвердить IP (плашка зеленеет немедленно, не висит серой — Баг 2) + замерить
     * ЖИВОЙ туннель. Возврат: (скорость ↓ Мбит/с — 0/-1 если нет throughput/ошибка, подтверждена ли связь фактом
     * внешнего IP). fromKey — прежний активный (для лога смены).
     */
    private static async Task<(double mbps, bool confirmed)> connectMeasureLive(Context app, ServerProfile c, string? fromKey, bool doMeasure = true)
    {
        var key = SubscriptionManager.serverKey(c);
        string cfg;
        try { cfg = XrayConfigBuilder.build(c); }
        catch (Exception) { MonitorLog.@event(app, "error", $"Кандидат {ServerLabels.display(c)}: ошибка конфига", ""); return (-1.0, false); }
        var from = ServerLabels.displayForKey(app, fromKey);
        XrayProxyService.start(app, cfg, ServerLabels.full(c), key);
        MonitorLog.@switch(app, from, ServerLabels.display(c), "монитор", "обрыв → подключение по пингу");
        var waited = 0;
        while (waited < 8000 && (!ProxyState.state.value.running || ProxyState.state.value.serverKey != key)) { await delay(500); waited += 500; }
        if (ProxyState.state.value.serverKey != key) return (-1.0, false);
        // Баг 2: подтверждаем факт СРАЗУ → зелёная плашка немедленно, а не серая до следующего цикла монитора.
        TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), "подключился — проверяю связь");
        var ip = ExternalIpChecker.fetch();
        var confirmed = ip != null;
        if (confirmed) { TunnelHealth.ok(ip!, now(), key); onRecovered(app); }
        // Ночь/батарея: живость = подтверждённый внешний IP; скорость НЕ мерим (экономим батарею/трафик).
        if (!doMeasure) return (-1.0, confirmed);
        await delay(800);   // дать туннелю осесть
        var mbps = ServerSpeedTester.measureActiveDownloadMbps(app);   // сопоставимо с кандидатами (Пр.130)
        if (mbps > 0)
        {
            SubscriptionManager.applySpeedResults(app, new Dictionary<string, double> { [key] = mbps });
            TunnelSpeed.setProbe(null, mbps, null, now(), key);
            MonitorLog.@event(app, "monitor", "Скорость нового сервера", fmt(mbps));
        }
        return (mbps, confirmed);
    }

    /**
     * Фаза 2b восстановления (ТЗ Elyor): недавние живые молчат → пинг ВСЕХ (прунинг мёртвых) → добираем до [target]
     * ЖИВЫХ (с ненулевым замером) замерами по топу ПИНГА, батчами по 10 → подключаемся к САМОМУ БЫСТРОМУ (сразу
     * подтверждаем IP + мерим). NO_CANDIDATES — никого живого по пингу/годного нет (наверх, к глубокому фолбэку).
     */
    private static async Task<SwitchResult> discoverBuildLive(Context app, AppSettings s, string? startKey, int target, double keepThreshold, bool doMeasure = true)
    {
        TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), "недавние молчат — полный пинг");
        MonitorStatus.update(true, "полный пинг: ищу живые серверы", now(), 0);
        await runCatching(() => refreshAllPings(app, s));
        if (aborted(startKey)) return SwitchResult.ABORTED;
        var bl = BlocklistStore.current();
        var liveByPing = SubscriptionManager.allServers(app)
            .Where(it => SubscriptionManager.serverKey(it) != startKey)
            .Where(it => allowedCandidate(it, s, bl))
            .Where(it => (it.pingMs ?? -1) >= 0)
            .OrderBy(it => it.pingMs ?? int.MaxValue).ToList();
        if (liveByPing.Count == 0) return SwitchResult.NO_CANDIDATES;
        // Ночь/батарея: не мерим скорость всех живых — берём лучшего по ПИНГУ и подтверждаем связь фактом IP.
        if (!doMeasure)
        {
            foreach (var best0 in liveByPing)
            {
                if (aborted(startKey)) return SwitchResult.ABORTED;
                MonitorStatus.update(true, $"подключаюсь к живому по пингу · {ServerLabels.display(best0)}", now(), 0);
                if ((await connectMeasureLive(app, best0, ProxyState.state.value.serverKey, doMeasure: false)).confirmed) return SwitchResult.SWITCHED;
            }
            return SwitchResult.NO_CANDIDATES;
        }
        var measCap = s.sufficientMbps.coerceAtLeast(keepThreshold);
        var liveWithSpeed = new List<(ServerProfile p, double mbps)>();
        var scanned = 0;
        foreach (var batch in liveByPing.Chunk(10))
        {
            var upd = new Dictionary<string, double>();
            foreach (var c in batch)
            {
                if (aborted(startKey)) return SwitchResult.ABORTED;
                scanned++;
                TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), $"замер живых: {scanned}/{liveByPing.Count}");
                MonitorStatus.update(true, $"замер живых {scanned}/{liveByPing.Count} · {ServerLabels.display(c)}", now(), 0);
                var m = ServerSpeedTester.measureSufficiency(app, c, measCap);
                upd[SubscriptionManager.serverKey(c)] = m;
                if (m > 0) liveWithSpeed.Add((c, m));
            }
            SubscriptionManager.applySpeedResults(app, upd);
            if (liveWithSpeed.Count >= target) break;   // набрали [target] живых — хватит (ТЗ: стараться иметь 10)
        }
        if (liveWithSpeed.Count == 0) return SwitchResult.NO_CANDIDATES;
        var best = liveWithSpeed.MaxBy(it => it.mbps).p;
        MonitorStatus.update(true, $"подключаюсь к самому быстрому · {ServerLabels.display(best)}", now(), 0);
        return (await connectMeasureLive(app, best, ProxyState.state.value.serverKey)).confirmed ? SwitchResult.SWITCHED : SwitchResult.NO_CANDIDATES;
    }

    // ---- Перебор кандидатов / переключение ----

    private static async Task<SwitchResult> runSwitchSearch(Context app, AppSettings s)
    {
        MonitorCoordinator.monitorSearchRunning = true;
        try
        {
            return await runSwitchSearchInner(app, s);
        }
        finally
        {
            MonitorCoordinator.monitorSearchRunning = false;
        }
    }

    private static async Task<SwitchResult> runSwitchSearchInner(Context app, AppSettings s)
    {
        var startKey = ProxyState.state.value.serverKey;   // если сменится не нами — значит вмешался пользователь

        var r1 = await probeAndConnect(app, s, startKey, "1");
        if (r1 != SwitchResult.NO_CANDIDATES) return r1;

        // Никто не подошёл → обновить ВСЕ включённые подписки → пройти список заново.
        if (aborted(startKey)) return SwitchResult.ABORTED;
        MonitorLog.@event(app, "monitor", "Никто не подошёл — обновляю подписки", "");
        MonitorStatus.update(true, "обновляю подписки", now(), 0);
        TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), "обновляю список серверов");
        var e = runCatching(() => { SubscriptionManager.refreshAllEnabled(app, cancelled: () => aborted(startKey), onEach: (_, _) => { }); });
        if (e != null) MonitorLog.@event(app, "error", "Ошибка обновления подписок", msg(e));
        if (aborted(startKey)) return SwitchResult.ABORTED;

        var r2 = await probeAndConnect(app, s, startKey, "2");
        if (r2 != SwitchResult.NO_CANDIDATES) return r2;

        // Пр.141: ПОСЛЕДНИЙ ШАНС перед «нет серверов» — авто-включить дефолтные Белые списки (сеются ВЫКЛ),
        // уведомить пользователя, обновить их и пройти ещё раз. Связь любой ценой (Пр.95).
        if (aborted(startKey)) return SwitchResult.ABORTED;
        var wlEnabled = 0;
        runCatching(() => { wlEnabled = SubscriptionManager.enableWhiteListForFallback(app); });
        if (wlEnabled > 0)
        {
            MonitorLog.@event(app, "monitor", "Рабочих серверов нет — включаю «Белые списки»", $"источников: {wlEnabled}");
            NotificationHelper.notifyWhiteListEnabled(app);
            TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), "включил Белые списки — ищу сервер");
            runCatching(() => { SubscriptionManager.refreshWhiteListSources(app); });
            if (aborted(startKey)) return SwitchResult.ABORTED;
            var r3 = await probeAndConnect(app, s, startKey, "3 (белые списки)");
            if (r3 != SwitchResult.NO_CANDIDATES) return r3;
        }

        handleNoAlive(app);   // пункт E: предложить включить прочие выкл-источники / добавить (если выкл нет)
        return SwitchResult.NO_CANDIDATES;
    }

    /// <summary>true, если перебор надо прервать: идёт ручной тест ИЛИ активный сервер сменил кто-то другой (пользователь).</summary>
    private static bool aborted(string? startKey) =>
        MonitorCoordinator.fullTestRunning || ProxyState.state.value.serverKey != startKey;

    /**
     * Один проход по ВСЕМУ списку (от быстрых к медленным). Кандидаты — единый предикат (протокол +
     * стоп-лист), МИНУЯ пинг-фильтр (мёртвый пинг ≠ мёртвый сервер, v2rayN). Дешёвая проверка на temp-
     * инстансе; подключаемся к ПЕРВОМУ живому; скорость меряем ПОСЛЕ.
     */
    private static async Task<SwitchResult> probeAndConnect(Context app, AppSettings s, string? startKey, string round)
    {
        var bl = BlocklistStore.current();
        // Пр.141: СНАЧАЛА вероятно-живые (свежий пинг ≥0 ИЛИ известная скорость >0) — быстро пробуем их, а не
        // грызём весь пул мёртвых по 68 штук (жалоба «17 мин перебирает 13/68 при 2 живых»). Внутри — по скорости,
        // затем по пингу. Остальные (никогда не мерянные/мёртвые) идут ХВОСТОМ — как запас, если живые не подошли.
        var candidates = SubscriptionManager.allServers(app)
            .Where(it => SubscriptionManager.serverKey(it) != startKey)
            .Where(it => allowedCandidate(it, s, bl))
            .OrderByDescending(it => (it.pingMs ?? -1) >= 0 || (it.speedMbps ?? 0.0) > 0.0)
            .ThenByDescending(it => it.speedMbps ?? 0.0)
            .ThenBy(it => it.pingMs ?? int.MaxValue).ToList();
        // Подключиться к кандидату (общий хвост: конфиг → старт → замер). Возврат SWITCHED/null-для-continue.
        async Task<SwitchResult?> tryConnect(ServerProfile c)
        {
            string cfg;
            try { cfg = XrayConfigBuilder.build(c); }
            catch (Exception) { MonitorLog.@event(app, "error", $"Кандидат {ServerLabels.display(c)}: ошибка конфига", ""); return null; }
            var from = ServerLabels.displayForKey(app, startKey);
            XrayProxyService.start(app, cfg, ServerLabels.full(c), SubscriptionManager.serverKey(c));
            MonitorLog.@switch(app, from, ServerLabels.display(c), "монитор", $"первый живой (проход {round})");
            await measureAfterConnect(app, c);
            return SwitchResult.SWITCHED;
        }

        // Пр.142: ПРОХОД 1 — сперва быстрый ПИНГ недавно-рабочих (жалоба: не перебирать вслепую замером). Пингуем
        // короче обычного; отвечающих сразу проверяем temp-инстансом и подключаемся. Непингующихся откладываем.
        var likely = candidates.Where(it => (it.pingMs ?? -1) >= 0 || (it.speedMbps ?? 0.0) > 0.0).ToList();
        var pingTimeout = SettingsStore.current().pingTimeoutMs.coerceAtMost(1500);
        var pingDead = new List<ServerProfile>();
        for (int i = 0; i < likely.Count; i++)
        {
            var c = likely[i];
            if (aborted(startKey)) return SwitchResult.ABORTED;
            TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), $"пингую недавно рабочие: {i + 1}/{likely.Count}");
            MonitorStatus.update(true, $"пинг {round}: {i + 1}/{likely.Count} · {ServerLabels.display(c)}", now(), 0);
            if (ServerTester.ping(app, c, pingTimeout) < 0) { pingDead.Add(c); continue; }   // не отозвался — отложить
            if (!ServerSpeedTester.probeAlive(app, c)) continue;
            var r = await tryConnect(c);
            if (r != null) return r.Value;
        }
        // Пр.142: ПРОХОД 2 — запас: непингующиеся недавно-рабочие (мёртвый пинг ≠ мёртвый под DPI) + никогда-не-мерянные,
        // проверяем temp-инстансом БЕЗ пинг-гейта (как раньше). Так DPI-серверы не теряются, но идут ПОСЛЕ быстрых.
        var likelySet = new HashSet<ServerProfile>(likely);
        var rest = pingDead.Concat(candidates.Where(it => !likelySet.Contains(it))).ToList();
        for (int i = 0; i < rest.Count; i++)
        {
            var c = rest[i];
            if (aborted(startKey)) return SwitchResult.ABORTED;
            TunnelHealth.setPhase(TunnelHealth.Phase.RECOVERING, now(), $"проверяю остальные: {i + 1}/{rest.Count}");
            MonitorStatus.update(true, $"перебор {round}: {i + 1}/{rest.Count} · {ServerLabels.display(c)}", now(), 0);
            if (!ServerSpeedTester.probeAlive(app, c)) continue;
            var r = await tryConnect(c);
            if (r != null) return r.Value;
        }
        return SwitchResult.NO_CANDIDATES;
    }

    /**
     * Пр.146.Ф2: ПЕРЕ-ПИНГ ВСЕХ кандидатов (протокол/стоп-лист/пауза ок), НЕ активного (его живость — по пингу/трафику).
     * И прунит мёртвых (−1 → уходят из «Живых»), И ловит воскресших (были −1, снова отвечают → возвращаются в
     * «Живые»). Раньше пинговали только живых → список сжимался. Пул ServerTester.testAll; ждём без блокировки
     * потока. Только пинг (килобайты). Возвращает число «живых» (пинг≥0) после пере-пинга — для рединдексации.
     */
    private static async Task<int> refreshAllPings(Context app, AppSettings s)
    {
        var bl = BlocklistStore.current();
        var curKey = ProxyState.state.value.serverKey;
        var candidates = SubscriptionManager.allServers(app)
            .Where(it => SubscriptionManager.serverKey(it) != curKey)
            .Where(it => allowedCandidate(it, s, bl)).ToList();
        if (candidates.Count == 0) return 0;
        var results = new ConcurrentDictionary<string, int>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = ServerTester.testAll(app, candidates,
            onResult: (p, ms) => results[SubscriptionManager.serverKey(p)] = (int)ms,
            onProgress: (_, _) => { },
            onFinish: () => done.TrySetResult());
        await using (loopCt.Value.Register(() => { try { handle.cancel(); } catch (Exception) { } done.TrySetCanceled(); }))
            await done.Task;
        var aliveNow = results.Count(it => it.Value >= 0);
        if (!results.IsEmpty)
        {
            SubscriptionManager.applyPingResults(app, new Dictionary<string, int>(results));
            MonitorLog.@event(app, "monitor", $"Пере-пинг всех: {results.Count}, живых {aliveNow}", "прунинг + рединдексация «Живых»");
        }
        return aliveNow;
    }

    /**
     * Пр.143: «вероятно живой» кандидат для быстрого подбора — свежий пинг ≥0 ИЛИ известная скорость >0.
     * Пинг вслепую по серверам без данных смысла нет: гейтом служит реальный ПИНГ при переборе (Фаза 1 восст.).
     */
    internal static bool isLikelyAlive(ServerProfile p) => (p.pingMs ?? -1) >= 0 || (p.speedMbps ?? 0.0) > 0.0;

    /**
     * Пр.143: порядок быстрого подбора — САМЫЙ БЫСТРЫЙ ПЕРВЫМ (по известной скорости убыв.), при равной скорости
     * меньший пинг раньше; берём только «вероятно живых» (isLikelyAlive). Чистая функция — покрыта тестом.
     */
    internal static List<ServerProfile> orderFastCandidates(IReadOnlyList<ServerProfile> list, Func<ServerProfile, bool>? isUnstable = null)
    {
        isUnstable ??= _ => false;
        var alive = list.Where(isLikelyAlive).ToList();
        // Пр.151 «ВРЕМЕННЫЙ АНТИ-ТОП»: часто рвущиеся (Ф3, ≥3 обрывов/30мин) ИСКЛЮЧАЕМ из быстрого подбора, ПОКА
        // есть стабильные живые. Уверенность, что это сервер (а не мы): флап считаем лишь на ПОДТВЕРЖДЁННОМ обрыве
        // активного при живом интернете. Стабильных нет → берём ВСЕХ (связь любой ценой, Пр.95 — не исключаем последних).
        var stable = alive.Where(it => !isUnstable(it)).ToList();
        var pool = stable.Count > 0 ? stable : alive;
        return pool.OrderByDescending(it => it.speedMbps ?? 0.0).ThenBy(it => it.pingMs ?? int.MaxValue).ToList();
    }

    /// <summary>Скорость измеряем ПОСЛЕ подключения (пользователю нужна связь, не число) и записываем серверу.</summary>
    private static async Task measureAfterConnect(Context app, ServerProfile c)
    {
        var key = SubscriptionManager.serverKey(c);
        var waited = 0;
        while (waited < 8000 && (!ProxyState.state.value.running || ProxyState.state.value.serverKey != key))
        {
            await delay(500); waited += 500;
        }
        await delay(1000);   // дать туннелю осесть
        var mbps = ServerSpeedTester.measureActiveDownloadMbps(app);   // сопоставимо с кандидатами (Промпт 90.A)
        if (mbps > 0)
        {
            SubscriptionManager.applySpeedResults(app, new Dictionary<string, double> { [key] = mbps });
            TunnelSpeed.setProbe(null, mbps, null, now(), key);   // начальная туннельная ↓ (прямую/↑ доберёт проба в простое)
            MonitorLog.@event(app, "monitor", "Скорость нового сервера", fmt(mbps));
        }
    }

    /// <summary>Пункт E: живых нет после двух проходов. Есть выключенные источники → предложить (не включать сами).</summary>
    private static void handleNoAlive(Context app)
    {
        var disabled = SubscriptionManager.sources(app).Where(it => !it.enabled).ToList();
        if (disabled.Count > 0 && !MonitorPrompt.declined)
        {
            var srv = SubscriptionManager.serversFromDisabled(app);
            MonitorPrompt.request(disabled.Count, srv);
            NotificationHelper.notifyEnableSources(app, disabled.Count, srv);
            MonitorLog.@event(app, "monitor", "Нет живых серверов", $"есть {disabled.Count} выключенных источников (~{srv} серв.) — предложил включить");
        }
        else if (disabled.Count == 0)
        {
            // Пр.141: включать нечего (все источники уже включены/их нет) → предложить ДОБАВИТЬ подписку.
            NotificationHelper.notifyAddSubscription(app);
            MonitorLog.@event(app, "monitor", "Нет живых серверов", "включать нечего — предложил добавить подписку");
        }
        else
        {
            MonitorLog.@event(app, "monitor", "Нет живых серверов", "пользователь отказался включать");
        }
    }

    /// <summary>Активная сеть — платная (мобильная/лимитный хотспот)? Гейт «только Wi-Fi» для дорогого перемера (Пр.126.A).</summary>
    private static bool isMeteredNetwork(Context app) => Device.isMeteredNetwork();

    /**
     * Пр.126.A: причина ПРОПУСТИТЬ периодический перемер «держать лучший» (null — можно мерить). Предохранители
     * против дорогого трафика в фоне: платная сеть, давно нет трафика через туннель, текущий сервер уже быстрый.
     */
    private static string? optimizeSkipReason(Context app, AppSettings s, long lastUserTrafficMs)
    {
        if (s.monitorOptimizeWifiOnly && isMeteredNetwork(app)) return "мобильная/платная сеть — только по Wi-Fi";
        if (s.monitorOptimizeIdleSkipSec > 0 && now() - lastUserTrafficMs > s.monitorOptimizeIdleSkipSec * 1000L)
            return $"через туннель давно нет трафика ({humanDur(now() - lastUserTrafficMs)}) — нечего оптимизировать";
        if (s.monitorOptimizeSkipAboveMbps > 0)
        {
            var curKey = ProxyState.state.value.serverKey;
            var curSpeed = curKey == null ? 0.0
                : SubscriptionManager.allServers(app).FirstOrDefault(it => SubscriptionManager.serverKey(it) == curKey)?.speedMbps ?? 0.0;
            if (curSpeed >= s.monitorOptimizeSkipAboveMbps)
                return $"текущий уже быстрый ({fmt(curSpeed)} ≥ {fmt(s.monitorOptimizeSkipAboveMbps)}) — улучшать нечего";
        }
        return null;
    }

    /**
     * ПОДДЕРЖАНИЕ ТОПА (Пр.132): не мерить всех подряд, а держать актуальным небольшой топ быстрых серверов.
     * Меряем ТОЛЬКО кандидатов в топ и ТОЛЬКО по поводам (Пр.132.C): ни разу не измерен / замер устарел (Пр.132.D:
     * старее topFreshSec — скорость НЕ действующая) / прошлый замер провалился. Плюс подмешиваем несколько ещё не
     * измеренных (Пр.132.B: пинг плохо предсказывает скорость под DPI — иначе быстрый сервер с плохим пингом не
     * найдётся). Топ свежий → НЕ мерим. Пинг — только для первого приближения/сортировки неизмеренных. Замеры
     * сопоставимы (Пр.130: и активный, и кандидатов — temp-инстансом). Массового замера всех живых в фоне НЕТ.
     */
    private static Task optimizeToFastest(Context app, AppSettings s)
    {
        var curKey = ProxyState.state.value.serverKey;
        if (curKey == null) return Task.CompletedTask;
        var bl = BlocklistStore.current();
        string key(ServerProfile p) => SubscriptionManager.serverKey(p);
        var topN = s.activeTopBatch.coerceAtLeast(1);
        var all = SubscriptionManager.allServers(app).Where(it => allowedCandidate(it, s, bl)).ToList();
        if (all.Count <= 1) return Task.CompletedTask;
        // Пр.146 Ф4: единственный ЖИВОЙ сервер — активный (других живых нет) → оптимизировать не на что: держим,
        // ничего не мерим (не тратим трафик на перебор мёртвых). Фоновый пере-пинг (refreshAllPings) ловит новых
        // живых; как только появится другой живой — следующий проход оптимизации его рассмотрит.
        if (!all.Any(it => key(it) != curKey && isLikelyAlive(it)))
        {
            MonitorLog.@event(app, "monitor", "Единственный живой сервер — держим", "других живых нет; фон ловит новых");
            return Task.CompletedTask;
        }
        // СВЕЖИЙ топ: измерены (speed>0) и НЕ старее предела давности (Пр.132.D). Их перемерять не нужно.
        var freshHours = (s.topFreshSec.coerceAtLeast(600) / 3600).coerceAtLeast(1);
        var freshTop = SubscriptionManager.recentWorkingServers(app, freshHours).Take(topN).ToList();
        var freshKeys = freshTop.Select(key).ToHashSet();
        // КОГО МЕРИТЬ (только поводы Пр.132.C): добираем топ до N из НЕсвежих. Сначала известные-но-устаревшие по
        // скорости (были в топе, пора обновить), затем неизмеренные/провальные по пингу (первое приближение).
        var toMeasure = new List<KeyValuePair<string, ServerProfile>>();   // LinkedHashMap: порядок вставки
        bool has(string k) => toMeasure.Any(kv => kv.Key == k);
        var byPing = all.OrderBy(it => it.pingMs ?? int.MaxValue).ToList();
        var staleByKnown = all.Where(it => (it.speedMbps ?? 0.0) > 0.0 && !freshKeys.Contains(key(it)))
            .OrderByDescending(it => it.speedMbps ?? 0.0).ToList();
        foreach (var p in staleByKnown) { if (toMeasure.Count + freshKeys.Count >= topN) break; if (!has(key(p))) toMeasure.Add(new(key(p), p)); }
        foreach (var p in byPing) { if (toMeasure.Count + freshKeys.Count >= topN) break; var k = key(p); if (!freshKeys.Contains(k) && !has(k)) toMeasure.Add(new(k, p)); }
        // Пр.132.B: подмешать неизмеренных живых (иначе быстрый-с-плохим-пингом не найдём).
        var mixed = 0;
        foreach (var p in byPing) { if (mixed >= s.topMixUnmeasured) break; var k = key(p); if (p.speedMbps == null && !freshKeys.Contains(k) && !has(k)) { toMeasure.Add(new(k, p)); mixed++; } }
        if (toMeasure.Count == 0)
        {                                                                    // топ свежий — повода мерить нет
            MonitorLog.@event(app, "monitor", "Топ свежий — перемер не нужен", $"в топе {freshKeys.Count}/{topN}, все ≤ {freshHours}ч");
            return Task.CompletedTask;
        }
        MonitorLog.@event(app, "monitor", $"Поддержание топа: мерю {toMeasure.Count} (топ {topN})", $"свежих {freshKeys.Count}, подмешано {mixed} · правило ×{s.keepBestMultiplier}");
        MonitorStatus.update(true, $"поддержание топа: {toMeasure.Count} замер(ов)", now(), 0);
        MonitorCoordinator.monitorSearchRunning = true;
        try
        {
            // Пр.130: активный меряем ТЕМ ЖЕ способом, что кандидатов — temp-инстансом (measureSpeed), а НЕ живым
            // SOCKS (measureActiveDownloadMbps систематически занижает, Пр.108/109). Живой замер — только для плашки.
            // Пр.133.B: мерим на ДОСТАТОЧНОСТЬ (стоп при подтверждении порога) — не точное число. Достаточные
            // читаются как ~порог, поэтому правило «держать лучший ×N» между ними НЕ дёргает (нет churn/расхода);
            // переключимся, лишь когда текущий перестал быть достаточным (порог/низкий → ×N сработает).
            var enough = s.sufficientMbps.coerceAtLeast(0.1);
            var curProfile = all.FirstOrDefault(it => key(it) == curKey);
            var curMbps = curProfile != null ? ServerSpeedTester.measureSufficiency(app, curProfile, enough) : -1.0;
            var results = new Dictionary<string, double>();
            if (curMbps > 0) results[curKey] = curMbps;
            string? bestKey = null; var bestMbps = 0.0;
            foreach (var c in toMeasure.Select(kv => kv.Value))
            {
                if (MonitorCoordinator.fullTestRunning) return Task.CompletedTask;               // ручной тест — уступаем
                if (ProxyState.state.value.serverKey != curKey) return Task.CompletedTask;       // пользователь сменил — уступаем
                var k = key(c);
                if (k == curKey) continue;
                var mbps = ServerSpeedTester.measureSufficiency(app, c, enough);   // temp-инстанс, стоп на пороге
                results[k] = mbps;
                // Пр.146 Ф3.5: не переходим НА нестабильный сервер (Ф3) — он не может стать «лучшим» целевым.
                if (mbps > bestMbps && !isUnstableKey(k)) { bestMbps = mbps; bestKey = k; }
            }
            // лучший из СВЕЖЕГО топа тоже участвует в решении (его не перемеряли — он свежий); нестабильных не берём.
            foreach (var p in freshTop) { var sp = p.speedMbps ?? 0.0; if (sp > bestMbps && !isUnstableKey(key(p))) { bestMbps = sp; bestKey = key(p); } }
            SubscriptionManager.applySpeedResults(app, results);
            // ПРАВИЛО «ДЕРЖАТЬ ЛУЧШИЙ» (Промпт 90.A): переключаемся, если лучший быстрее текущего КРАТНО
            // (в keepBestMultiplier раз), даже когда текущий выше порога. Кратно — чтобы шум не дёргал.
            if (bestKey != null && bestKey != curKey && curMbps > 0 && bestMbps >= curMbps * s.keepBestMultiplier)
            {
                var target = all.FirstOrDefault(it => key(it) == bestKey);
                if (target == null) return Task.CompletedTask;
                string cfg;
                try { cfg = XrayConfigBuilder.build(target); } catch (Exception) { return Task.CompletedTask; }
                var from = ServerLabels.displayForKey(app, curKey);
                XrayProxyService.start(app, cfg, ServerLabels.full(target), bestKey);
                MonitorLog.@switch(app, from, ServerLabels.display(target), "монитор: держать лучший",
                    $"было {fmt(curMbps)} → стало {fmt(bestMbps)} (×{s.keepBestMultiplier})");
            }
            else
            {
                MonitorLog.@event(app, "monitor", "Топ обновлён, смены нет",
                    $"текущий {fmt(curMbps)}, лучший {fmt(bestMbps)} (нужно ×{s.keepBestMultiplier})");
            }
        }
        finally
        {
            MonitorCoordinator.monitorSearchRunning = false;
        }
        return Task.CompletedTask;
    }

    // ---- Фоновый рейтинг топа (ТЗ Elyor): шнур/батарея × сеть ----

    /// <summary>
    /// «На шнуре» для агрессивного фон-рейтинга. Android: ⚠️ ГРАБЛЯ S908E — isCharging=false при 100% на шнуре, поэтому
    /// там проверяли ещё EXTRA_PLUGGED. Windows: Device.isCharging по ACLineStatus (на шнуре = true и при 100%),
    /// стационарный ПК без батареи — всегда «на шнуре».
    /// </summary>
    private static bool isCharging(Context app)
    {
        try { return Device.isCharging(); } catch (Exception) { return false; }
    }

    /** ТЗ Elyor: на ШНУРЕ и одновременно Wi-Fi (не мобильная) мерим ВСЕХ без лимита 10/3 (батарея/трафик не жмут).
     *  На мобильной или на батарее — лимит действует. Тот же критерий, что measureAll в rebuildAndRateTop. */
    public static bool chargingAndWifi(Context app) => isCharging(app) && !EconomyNet.lastKnownMobile;

    /// <summary>Публичный доступ к «питание подключено (шнур)?» — для EconomyNet (щадящий «Только Wi-Fi» действует лишь на батарее).</summary>
    public static bool poweredByCharger(Context app) => isCharging(app);

    /**
     * Разрешены ли БАЙТ-ТЯЖЁЛЫЕ замеры скорости прямо сейчас (measureSpeed/measureSufficiency/фон-рейтинг/апгрейд)?
     * ⚠️ ГРАБЛЯ (замер фактом adb, оба телефона на 0.52, 2026-09-27): ночью на батарее при выключенном экране эти
     * замеры сажали батарею В НОЛЬ и лили трафик. ФИКС: на «экран выкл + на батарее» откладываем ВСЕ оптимизационные
     * замеры до зарядки/включения экрана. Continuity (Пр.95) НЕ страдает: живость держит ПИНГ.
     * Ярусы по заряду: на шнуре — всегда да; на батарее ≤15% — нет (даже при экране вкл); иначе — по экрану.
     */
    private static bool heavyMeasureAllowed(Context app)
    {
        if (isCharging(app)) return true;                       // на шнуре батарея не тратится — мерим свободно
        if (batteryLevel(app) <= LOW_BATTERY_PCT) return false; // ≤15% на батарее — никаких скоростных замеров
        return screenInteractive(app);                          // иначе — только при включённом экране
    }

    /// <summary>Уровень заряда 0..100. При ошибке чтения → 100 (не блокируем работу мнимо-низким зарядом).</summary>
    private static int batteryLevel(Context app)
    {
        try { return Device.batteryLevel(); } catch (Exception) { return 100; }
    }

    /// <summary>≤5% на батарее — критический заряд: фон почти спит (реакция на включение экрана всё равно мгновенная).</summary>
    private static bool criticalBattery(Context app) => !isCharging(app) && batteryLevel(app) <= CRITICAL_BATTERY_PCT;

    /**
     * Интервал сна в простое (здоровая связь). На шнуре/экран вкл — base. На батарее+экран выкл — ЛЕСТНИЦА по времени
     * «экран не включали»: <1ч → 5 мин, >1ч → 10 мин, >3ч → 15 мин. Критический заряд (≤5%) → 30 мин. Отзывчивость
     * не страдает: разблокировка/пробуждение будят монитор событием.
     */
    private static long idleIntervalMs(Context app, long baseMs, long screenOffSinceMs)
    {
        if (isCharging(app) || screenInteractive(app)) return baseMs;
        if (criticalBattery(app)) return Math.Max(baseMs, CRITICAL_IDLE_MS);
        var off = screenOffSinceMs > 0 ? now() - screenOffSinceMs : 0L;
        var idle = off >= SCREEN_OFF_LONG_MS ? SCREEN_OFF_IDLE_LONG_MS : off >= SCREEN_OFF_MED_MS ? SCREEN_OFF_IDLE_MED_MS : SCREEN_OFF_IDLE_MS;
        return Math.Max(baseMs, idle);
    }

    /**
     * Шаг 1: пингуем ТЕКУЩИЙ топ (по сохранённой скорости) — дёшево, держим его живость свежей.
     * true = топ «поредел» (>50% не отозвались на пинг) → повод перестроить (шаг 2).
     */
    private static Task<bool> pingTopAndDetectDecay(Context app, AppSettings s)
    {
        var bl = BlocklistStore.current();
        string key(ServerProfile p) => SubscriptionManager.serverKey(p);
        var n = s.fastTopTarget.coerceAtLeast(1);
        var top = SubscriptionManager.allServers(app)
            .Where(it => allowedCandidate(it, s, bl))
            .Where(it => (it.speedMbps ?? 0.0) > 0.0)
            .DistinctBy(key)
            .OrderByDescending(it => it.speedMbps ?? 0.0)
            .Take(n).ToList();
        if (top.Count == 0) return Task.FromResult(true);   // топа нет вовсе → построить
        var pingTimeout = s.pingTimeoutMs.coerceAtMost(1500);
        var upd = new Dictionary<string, int>();
        var dead = 0;
        foreach (var p in top)
        {
            if (MonitorCoordinator.fullTestRunning) return Task.FromResult(false);
            var ms = (int)ServerTester.ping(app, p, pingTimeout);
            upd[key(p)] = ms;
            if (ms < 0) dead++;
        }
        SubscriptionManager.applyPingResults(app, upd);
        var decayed = dead * 2 > top.Count;
        if (decayed) MonitorLog.@event(app, "monitor", $"Фон: топ поредел ({dead}/{top.Count} не отозвались) — перестраиваю", "");
        return Task.FromResult(decayed);
    }

    /**
     * Утренний рейтинг (~6:00): пинг всех → короткий замер (measureSufficiency) топ-[MORNING_RATE_COUNT] живых по пингу.
     * Одна серия раз в сутки — чтобы к пробуждению был свежий быстрый сервер, без всеночных замеров. Активного НЕ
     * переключаем (это освежение списка, не подбор). Вызывается только когда заряд выше порога (проверка у вызова).
     */
    private static async Task morningRateTop(Context app, AppSettings s)
    {
        if (MonitorCoordinator.fullTestRunning) return;
        MonitorCoordinator.monitorSearchRunning = true;
        try
        {
            await runCatching(() => refreshAllPings(app, s));
            var bl = BlocklistStore.current();
            var cap = s.sufficientMbps.coerceAtLeast(s.degradationMinMbps.coerceAtLeast(0.1));
            var top = SubscriptionManager.allServers(app)
                .Where(it => allowedCandidate(it, s, bl))
                .Where(it => (it.pingMs ?? -1) >= 0)
                .DistinctBy(SubscriptionManager.serverKey)
                .OrderBy(it => it.pingMs ?? int.MaxValue)
                .Take(MORNING_RATE_COUNT).ToList();
            var results = new Dictionary<string, double>();
            foreach (var p in top)
            {
                if (MonitorCoordinator.fullTestRunning || ProxyState.state.value.serverKey == null) break;
                results[SubscriptionManager.serverKey(p)] = ServerSpeedTester.measureSufficiency(app, p, cap);
            }
            if (results.Count > 0) SubscriptionManager.applySpeedResults(app, results);
            MonitorLog.@event(app, "monitor", $"Утренний рейтинг топ-{MORNING_RATE_COUNT}", $"замерено {results.Count}");
        }
        finally
        {
            MonitorCoordinator.monitorSearchRunning = false;
        }
    }

    // Фоновый рейтинг топа (ТЗ Elyor 2026-10-15: «в фоне приоритетно перебрать список 2 и перенести живые в список 1,
    // далее из оставшихся живых по пингу после фетча доформировывать топ списка 1»). Порядок: пинг всех → МИНИМАЛЬНЫЙ
    // замер (~100 КБ) сперва СПИСКА 2 (ранее измеренные: пинг жив, замер устарел) — ожившие (speed>0, свежая метка)
    // становятся списком 1; затем ДОБОР из оставшихся живых по пингу (без годной скорости) до targetTop. ПОЛНЫЙ точный
    // замер — топу прошедших minGate (0.5). Уже свежие по [liveFreshSec] и годные (≥ порога) — список 1, НЕ перемеряем
    // (бережём трафик). Активный НЕ трогаем (его скорость даёт live-сэмпл). Рейтинг под heavyMeasureAllowed (батарея).
    private static async Task rebuildAndRateTop(Context app, AppSettings s)
    {
        if (MonitorCoordinator.fullTestRunning) return;
        MonitorCoordinator.monitorSearchRunning = true;
        try
        {
            await runCatching(() => refreshAllPings(app, s));   // пинг всех: прунинг мёртвых + воскресшие (фетч пинга перед добором)
            var bl = BlocklistStore.current();
            string key(ServerProfile p) => SubscriptionManager.serverKey(p);
            var curKey = ProxyState.state.value.serverKey;
            var minGate = 0.5;
            var targetTop = s.trafficSaveMode ? 3 : 10;   // держим топ-N по скорости (НЕ меряем все, без ∞ на шнуре)
            var keepThreshold = s.degradationMinMbps.coerceAtLeast(0.1);
            // СПИСОК 1 «Живые» = измеренная скорость>0 И замер СВЕЖИЙ (не старше liveFreshSec). Такой сервер только что
            // мерен — НЕ перемеряем в этом цикле. В зачёт топа идут лишь ГОДНЫЕ (≥ порога). liveFreshSec (а не topFreshSec)
            // — чтобы список 2 (измеренные старше окна) активно перемерялся и промоутился обратно в список 1 (ТЗ).
            bool isFreshLive(ServerProfile p) =>
                (p.speedMbps ?? 0.0) > 0.0 && ServerFilter.isSpeedFresh(ServerFilter.speedMeasuredAtMs(p), now(), s.liveFreshSec);
            var aliveByPing = SubscriptionManager.allServers(app)
                .Where(it => key(it) != curKey)   // активный мерит live-сэмпл — не перемеряем temp-инстансом
                .Where(it => allowedCandidate(it, s, bl))
                .Where(it => (it.pingMs ?? -1) >= 0)
                .DistinctBy(key)
                .OrderBy(it => it.pingMs ?? int.MaxValue).ToList();
            var stableGood = aliveByPing.Count(it => isFreshLive(it) && (it.speedMbps ?? 0.0) >= keepThreshold);  // уже в топе
            var needTop = (targetTop - stableGood).coerceAtLeast(0);        // сколько ещё добрать в топ-N
            // ПРИОРИТЕТ — СПИСОК 2: ранее измеренные (speed>0), но НЕ свежие → перемерить, чтобы промоутить в список 1.
            // Лучшие по прошлой скорости первыми. Затем ДОБОР — живые по пингу без годной скорости (unmeasured/провал),
            // от лучшего пинга. Свежие-но-медленные (speed>0, fresh, <порога) пропускаем — только что мерены.
            var list2 = aliveByPing.Where(it => (it.speedMbps ?? 0.0) > 0.0 && !isFreshLive(it))
                .OrderByDescending(it => it.speedMbps ?? 0.0).ToList();
            var fillPool = aliveByPing.Where(it => (it.speedMbps ?? 0.0) <= 0.0 && !isFreshLive(it))
                .OrderBy(it => it.pingMs ?? int.MaxValue).ToList();
            var needMeasure = list2.Concat(fillPool).DistinctBy(key).ToList();   // список 2 — приоритет, затем добор
            // ТЗ Elyor: фон «так же ищет до 10 живых» (как ручной «Самый быстрый»). Потолок БОЛЬШОЙ (180) — долгосрочно
            // дешевле: сходится к 10 живых, а не крутит 60 вхолостую. Экономия — 30.
            // ⚠️ Паритет с Kotlin: при needMeasure < targetTop coerceIn бросает (пустой диапазон) → фон-рейтинг уходит в
            // «Ошибка фон-рейтинга топа» и ничего не мерит. Кандидат на исправление в ОБЕИХ версиях (docs/decisions.md).
            var maxScan = (s.trafficSaveMode ? 30 : 180).coerceIn(targetTop, needMeasure.Count);
            // Этап А: МИНИМАЛЬНЫЙ замер (~100 КБ, 1 пробник/3с) от списка 2 к добору; СТОП при наборе needTop годных ИЛИ
            // maxScan. На tcping-пуле из сотен это ключ к «не 5 ГБ/сут»: мерим горстку, а не всё.
            var minimalByKey = new Dictionary<string, double>();
            var minimalTagged = new Dictionary<string, (double, bool)>();
            var good = new List<ServerProfile>();
            var scanned = 0;
            if (needTop > 0)
                foreach (var p in needMeasure)
                {
                    if (MonitorCoordinator.fullTestRunning || ProxyState.state.value.serverKey == null) break;
                    if (good.Count >= needTop || scanned >= maxScan) break;
                    var m = ServerSpeedTester.measureSpeedMinimal(app, p);
                    minimalByKey[key(p)] = m; minimalTagged[key(p)] = (m, false); scanned++;
                    if (m >= minGate) good.Add(p);
                }
            if (minimalTagged.Count > 0) SubscriptionManager.applySpeedResultsTagged(app, minimalTagged);
            // Этап Б: ПОЛНЫЙ точный замер — топ needTop из годных (по минимальному). Ставит свежую метку → промоушен в список 1.
            var reasonable = good.OrderByDescending(it => minimalByKey.GetValueOrDefault(key(it), 0.0)).Take(needTop).ToList();
            var fullTagged = new Dictionary<string, (double, bool)>();
            foreach (var p in reasonable)
            {
                if (MonitorCoordinator.fullTestRunning || ProxyState.state.value.serverKey == null) break;
                fullTagged[key(p)] = (ServerSpeedTester.measureSpeed(app, p), true);   // полный/точный замер
            }
            if (fullTagged.Count > 0) SubscriptionManager.applySpeedResultsTagged(app, fullTagged);
            MonitorLog.@event(app, "monitor", "Фон: топ обновлён (список2→промоушен→добор)",
                $"в топе {stableGood}, цель {targetTop}, список2 {list2.Count}, минимальный {scanned}, полный {fullTagged.Count}");
        }
        finally
        {
            MonitorCoordinator.monitorSearchRunning = false;
        }
    }

    // ---- Замеры/утилиты ----

    private static long now() => JavaSystem.currentTimeMillis();

    /**
     * ЖИВАЯ скорость активного туннеля для плашки (гибрид). Возвращает обновлённое время последней АКТИВНОЙ пробы.
     *  1) ПАССИВНО (бесплатно): дельта туннельных байт (TrafficTracker) за окно ~6с. Идёт реальный трафик →
     *     это и есть актуальная скорость ↓/↑ → source=LIVE. Ничего лишнего не качаем.
     *  2) ПРОСТОЙ (трафика нет): раз в [liveSpeedActiveProbeSec] и ТОЛЬКО при включённом экране — активная проба
     *     download+upload через туннель → source=PROBE. Экономия: не тратим трафик на замер для невидимой плашки.
     * Download активной пробы попутно пишем серверу (обновляет список «Живые» — решение «то, что монитор меряет»).
     */
    private static async Task<long> sampleTunnelSpeed(Context app, AppSettings cur, long lastActiveProbeMs)
    {
        var curKey = ProxyState.state.value.serverKey;
        if (curKey == null) return lastActiveProbeMs;

        // (1) Пассивно: реальный трафик пользователя даёт скорость бесплатно.
        var t0 = SystemClock.elapsedRealtime();
        var rx0 = TrafficTracker.state.value.sessionRx; var tx0 = TrafficTracker.state.value.sessionTx;
        await delay(SPEED_PASSIVE_WINDOW_MS);
        var dtSec = (SystemClock.elapsedRealtime() - t0) / 1000.0;
        var dRx = (TrafficTracker.state.value.sessionRx - rx0).coerceAtLeast(0);
        var dTx = (TrafficTracker.state.value.sessionTx - tx0).coerceAtLeast(0);
        if (dtSec > 0 && dRx + dTx > SPEED_PASSIVE_MIN_BYTES)
        {
            var down = dRx * 8.0 / dtSec / 1_000_000.0;
            var up = dTx * 8.0 / dtSec / 1_000_000.0;
            TunnelSpeed.setLive(down, up, now(), curKey);   // прямую скорость сохраняем от прошлой пробы
            // Пр.127.C: деградация по УЖЕ идущему трафику (бесплатно). Только при СУЩЕСТВЕННОМ трафике — иначе
            // низкая скорость = «мало качали», а не медленный туннель. Тишину (иначе — ниже порога байт) НЕ трактуем.
            if (dRx + dTx >= DEGRADATION_MIN_TRAFFIC_BYTES) await maybeStepFromDegraded(app, cur, curKey, down);
            return lastActiveProbeMs;
        }

        // (2) Простой: активная проба — только если пришло время И экран включён (иначе не тратим трафик).
        // Пр.125.E: в режиме экономии ПЛАНОВАЯ проба плашки в простое ОТКЛЮЧЕНА — замер активного туннеля только
        // по подозрению (лестница восстановления), не по расписанию. Пассивный сэмпл (шаг 1) бесплатен — он остаётся.
        if (cur.trafficSaveMode) return lastActiveProbeMs;
        if (now() - lastActiveProbeMs < cur.liveSpeedActiveProbeSec * 1000L) return lastActiveProbeMs;
        if (!screenInteractive(app)) return lastActiveProbeMs;
        if (MonitorCoordinator.fullTestRunning || ProxyState.state.value.serverKey != curKey) return lastActiveProbeMs;
        probeActiveSpeedNow(app);
        return now();
    }

    /**
     * Пр.127.C: «живой, но медленный». Наблюдаемая по ПАССИВНОМУ трафику скорость [passiveDown] ниже порога
     * полезности → сделать ОДИН шаг: замерить ЛУЧШЕГО по СОХРАНЁННОЙ скорости кандидата (не текущего) и перейти,
     * если он приемлемо быстрее того, что человек реально получает сейчас. Один кандидат, один замер, не перебор.
     * Не чаще [degradationCheckSec]. Работает в ОБОИХ режимах (в экономии — «приемлемый», не «лучший»). Вызывается
     * ТОЛЬКО когда трафик был существенным (см. вызов), поэтому passiveDown — осмысленный признак капасити.
     */
    private static async Task maybeStepFromDegraded(Context app, AppSettings cur, string curKey, double passiveDown)
    {
        if (cur.degradationMinMbps <= 0.0) return;                        // фича выключена
        if (passiveDown >= cur.degradationMinMbps) return;                // не деградация — туннель тянет
        if (now() - Interlocked.Read(ref lastDegradationMs) < cur.degradationCheckSec * 1000L) return;   // не чаще интервала
        if (MonitorCoordinator.fullTestRunning || MonitorCoordinator.monitorSearchRunning) return;
        Interlocked.Exchange(ref lastDegradationMs, now());
        var bl = BlocklistStore.current();
        // Кандидаты по СОХРАНЁННОЙ скорости (не текущий, годный: протокол/стоп-лист/пауза), о ком есть данные.
        var pool = SubscriptionManager.allServers(app)
            .Where(it => SubscriptionManager.serverKey(it) != curKey)
            .Where(it => allowedCandidate(it, cur, bl))
            .Where(it => (it.speedMbps ?? 0.0) > 0.0).ToList();                // о кандидатах нет данных — судить не по чему
        // ⚠️ ПОЛЕВОЙ БАГ S908E (0.44, деградир. пул): раньше брали ОДНОГО — maxByOrNull{speedMbps}, БЕЗ проверки
        // живости. На вершине по старой скорости стоял ПРИЗРАК → measureSpeed призрака проваливался → «остаюсь», и
        // живой-но-чуть-медленнее кандидат позади НЕ получал шанса. Теперь: ПРЕДПОЧИТАЕМ живых по свежему пингу
        // (pingMs≥0) и пробуем ТОП-K с ранней остановкой на первом годном. Живых по пингу нет → 1 лучший по скорости.
        var aliveByPing = pool.Where(it => (it.pingMs ?? -1) >= 0).OrderByDescending(it => it.speedMbps ?? 0.0).ToList();
        var ordered = aliveByPing.Count > 0 ? aliveByPing.Take(DEGRADED_STEP_CANDIDATES).ToList()
                      : pool.OrderByDescending(it => it.speedMbps ?? 0.0).Take(1).ToList();
        if (ordered.Count == 0) return;
        MonitorLog.@event(app, "monitor", $"Туннель медленный ({fmt(passiveDown)}) — проверяю кандидатов ({ordered.Count})",
            string.Join(", ", ordered.Select(ServerLabels.display)));
        MonitorCoordinator.monitorSearchRunning = true;
        try
        {
            foreach (var cand in ordered)
            {
                if (ProxyState.state.value.serverKey != curKey) return;   // пользователь/лестница сменили — не вмешиваемся
                var candMbps = ServerSpeedTester.measureSpeed(app, cand); // замер temp-инстансом (активный не дёргаем)
                SubscriptionManager.applySpeedResults(app, new Dictionary<string, double> { [SubscriptionManager.serverKey(cand)] = candMbps });
                if (ProxyState.state.value.serverKey != curKey) return;
                if (candMbps >= cur.degradationMinMbps && candMbps > passiveDown)
                {
                    string cfg;
                    try { cfg = XrayConfigBuilder.build(cand); } catch (Exception) { continue; }
                    var from = ServerLabels.displayForKey(app, curKey);
                    XrayProxyService.start(app, cfg, ServerLabels.full(cand), SubscriptionManager.serverKey(cand));
                    MonitorLog.@switch(app, from, ServerLabels.display(cand), "деградация: кандидат приемлемо быстрее",
                        $"{fmt(passiveDown)} → {fmt(candMbps)}");
                    await measureAfterConnect(app, cand);
                    return;                                               // шагнули — стоп (ранняя остановка)
                }
            }
            MonitorLog.@event(app, "monitor", "Никто из кандидатов не лучше — остаюсь",
                $"порог {fmt(cur.degradationMinMbps)}, сейчас {fmt(passiveDown)}");
        }
        finally
        {
            MonitorCoordinator.monitorSearchRunning = false;
        }
    }

    /**
     * ПРИОРИТЕТНЫЙ активный замер СЕЙЧАС: прямой канал + туннель ↓/↑ активного сервера → в [TunnelSpeed] (плашка).
     * Блокирующий (зовётся из потока полного теста сразу после подключения И из монитора). Цель: плашка показывает
     * РЕАЛЬНЫЕ числа активного сервера ASAP и они — ЕДИНЫЙ источник правды по скорости.
     * Промпт 104.B: [includeUpload]=false — пропустить замер ОТДАЧИ (display-only сток времени). Полный тест зовёт с
     * false (важна скорость перебора), монитор — с true. Промпт 108: ВОЗВРАЩАЕТ ↓ активного (-1 — нет/провал/сменился).
     */
    public static double probeActiveSpeedNow(Context app, bool includeUpload = true)
    {
        var curKey = ProxyState.state.value.serverKey;
        if (curKey == null) return -1.0;
        var directM = ServerSpeedTester.measureDirectDownloadMbps(app);
        double? direct = directM >= 0.0 ? directM : null;                                   // НАПРЯМУЮ ↓ (без туннеля)
        var down = ServerSpeedTester.measureActiveDownloadMbps(app);                       // В ТУННЕЛЕ ↓
        if (ProxyState.state.value.serverKey != curKey) return -1.0;   // сервер сменили во время замера — не приписываем чужое
        var up = includeUpload ? ServerSpeedTester.measureActiveUploadMbps(app) : -1.0;    // В ТУННЕЛЕ ↑
        TunnelSpeed.setProbe(direct, down >= 0.0 ? down : null, up >= 0.0 ? up : null, now(), curKey);
        if (down > 0) SubscriptionManager.applySpeedResults(app, new Dictionary<string, double> { [curKey] = down });      // обновить «Живые»
        return down;
    }

    /// <summary>Экран включён (пользователь потенциально смотрит на плашку)? Windows: сеанс не заблокирован.</summary>
    private static bool screenInteractive(Context app) => Device.screenOn;

    private static string fmt(double mbps) => $"{(mbps * 10).roundToInt() / 10.0} Мбит/с";

    private static string humanDur(long ms)
    {
        var sec = (ms / 1000).coerceAtLeast(0);
        if (sec < 60) return $"{sec}с";
        if (sec < 3600) return $"{sec / 60}м";
        return $"{sec / 3600}ч {(sec % 3600) / 60}м";
    }

    // internal (не private): переиспользуется кнопкой «Проверить» на главной как интернет-гейт.
    internal static Func<bool>? directAliveOverrideForTest;

    public static bool directAlive()
    {
        if (directAliveOverrideForTest != null) return directAliveOverrideForTest();
        foreach (var (host, port) in directDnsProbes)
        {
            try
            {
                using var s = new TcpClient();
                if (s.ConnectAsync(host, port).Wait(3000) && s.Connected) return true;
            }
            catch (Exception) { /* следующий */ }
        }
        return false;
    }

    /**
     * Идёт ли ЖИВОЙ трафик пользователя через туннель (Промпт 90.D): дельта принятых+отданных байт за ~1.2с.
     * Порог 128 КБ отсекает служебные keepalive, но ловит реальную загрузку. Если идёт — тяжёлый замер
     * пропускаем (испортили бы и замер, и его загрузку). Байты — накопленные из TrafficTracker (поллер
     * сервиса их обновляет; queryTunnelDelta НЕ трогаем — её потребляет поллер).
     */
    internal static async Task<bool> userTrafficActive()
    {
        var b = TrafficTracker.state.value;
        var before = b.sessionRx + b.sessionTx;
        await delay(1200);
        var a = TrafficTracker.state.value;
        var after = a.sessionRx + a.sessionTx;
        return after - before > 128 * 1024;
    }
}
