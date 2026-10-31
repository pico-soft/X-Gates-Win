using System.Net;
using System.Net.Sockets;
using XGates.Core.Compat;
using XGates.Core.Libv2ray;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Traffic;
using XGates.Core.Xray.Link;
using L = XGates.Core.Libv2ray.Libv2ray;

namespace XGates.Core.Xray;

/**
 * Throughput-тест (реальная скорость сервера, Mbps). Порт 1:1 с xray/ServerSpeedTester.kt.
 *
 * measureOutboundDelay даёт только задержку → поднимаем ЛОКАЛЬНЫЙ временный инстанс ядра (на Windows — процесс
 * xray.exe) с socks-inbound на ЭФЕМЕРНОМ порту (не 10815/10816, не трогая активный прокси), качаем пробник через
 * этот socks, считаем Mbps, гасим. ПОСЛЕДОВАТЕЛЬНО (каждый temp-инстанс насыщает канал — параллель исказит).
 * HttpURLConnection Android → JavaHttpConnection (те же connect/read-таймауты, disconnect из другого потока).
 */
public static class ServerSpeedTester
{
    private const string TAG = "ServerSpeedTester";

    /// <summary>Сколько замеров идёт ОДНОВРЕМЕННО (диагностика: делят ли канал). При пуле 1 всегда 1.</summary>
    private static int concurrentMeasures;

    private const int CONNECT_TIMEOUT_MS = 8_000;

    // Warmup/окно/пул/URL пробника — теперь в [SettingsStore], не константами здесь.

    /**
     * ТЗ Elyor: пробники замера — ГЛОБАЛЬНО достижимые (и из РФ НАПРЯМУЮ, и из-за рубежа через туннель). tele2
     * УБРАН: он RU-only и через туннель отдаёт http=000/таймаут (сверено фактом 2026-10-29 через SOCKS рабочего
     * сервера), из-за чего быстрый замер (1 пробник=tele2) падал у ВСЕХ → «годных 0», хотя серверы живы.
     * Cloudflare anycast — первый (достижим отовсюду); `__down?bytes=N` отдаёт РОВНО N байт (не гигабайт — читаем
     * только бюджет замера, N лишь «длиннее окна»). ovh/thinkbroadband — фоллбэки (EU/UK, тоже глобальные).
     * Порядок = приоритет выбора: directlyReachableProbe берёт ПЕРВЫЙ, что зафетчился НАПРЯМУЮ (из РФ).
     */
    private static readonly IReadOnlyList<string> fallbackProbes =
    [
        "https://speed.cloudflare.com/__down?bytes=20000000",  // Cloudflare anycast — N байт по запросу (не гигабайт)
        "https://proof.ovh.net/files/1Gb.dat",                 // OVH (EU) https — глобальный speedtest-миррор
        "http://ipv4.download.thinkbroadband.com/1GB.zip",     // thinkbroadband (UK) — открытый speedtest-миррор
    ];

    // Выбранный пробник (кэш): через него МЕРИМ туннели, подтверждён ПРЯМЫМ фетчем из РФ. Пере-выбираем по TTL.
    private static volatile string? chosenProbe;
    private static long chosenProbeAtMs;
    private const long PROBE_PICK_TTL_MS = 90_000L;            // сеть могла смениться — пере-выбирать не чаще
    private const long PROBE_DIRECT_TARGET_BYTES = 24 * 1024L;
    private const int PROBE_DIRECT_TIMEOUT_MS = 6_000;

    /// <summary>Для UI/лога: host пробника, через который сейчас меряем (виден в фазах теста).</summary>
    public static volatile string activeProbeHostValue = "";
    public static string activeProbeHost => activeProbeHostValue;

    /// <summary>Подмена списка пробников (только тесты: герметичная сеть без интернета). Сбрасывает кэш выбора.</summary>
    internal static IReadOnlyList<string>? probesOverrideForTest
    {
        get => probesOverride;
        set { probesOverride = value; chosenProbe = null; Interlocked.Exchange(ref chosenProbeAtMs, 0); }
    }
    private static volatile IReadOnlyList<string>? probesOverride;

    /**
     * ТЗ Elyor: выбрать пробник для замера туннелей — тот, что ЗАФЕТЧИЛСЯ НАПРЯМУЮ (из РФ, без туннеля) из списка
     * ГЛОБАЛЬНЫХ кандидатов (достижимых и из-за рубежа). Проверяем по очереди ПРЯМЫМ коннектом (proxy=null); первый
     * отдавший байты — выбран. СНАЧАЛА фетчим напрямую, ПОТОМ им меряем туннели → провал замера = вина туннеля, а не
     * мёртвого пробника. Кэш на [PROBE_PICK_TTL_MS]. Уважаем КАСТОМНЫЙ speedProbeUrl юзера (кроме мёртвого tele2-дефолта)
     * — он идёт первым кандидатом. Ни один не отдал напрямую (нет интернета) → первый глобальный (не кэшируем).
     */
    public static string directlyReachableProbe(Context context)
    {
        var now = JavaSystem.currentTimeMillis();
        var cp = chosenProbe;
        if (cp != null && now - Interlocked.Read(ref chosenProbeAtMs) < PROBE_PICK_TTL_MS) return cp;
        var verbose = SettingsStore.current().verboseLogs;
        foreach (var cand in tunnelProbes())
        {
            long got;
            try { got = downloadSome(cand, null, PROBE_DIRECT_TARGET_BYTES, PROBE_DIRECT_TIMEOUT_MS); } catch (Exception) { got = 0L; }
            if (got >= PROBE_ALIVE_MIN_BYTES)
            {
                chosenProbe = cand; Interlocked.Exchange(ref chosenProbeAtMs, now); activeProbeHostValue = hostOf(cand);
                if (verbose) Log.i(TAG, $"пробник замера ВЫБРАН (зафетчился НАПРЯМУЮ из РФ): {hostOf(cand)} ({got} Б)");
                return cand;
            }
            if (verbose) Log.i(TAG, $"пробник {hostOf(cand)} НЕ фетчится напрямую — пробую следующий");
        }
        // ни один напрямую (нет интернета?) — первый глобальный, кэш НЕ ставим (пере-проверим скоро)
        var first = (probesOverrideForTest ?? fallbackProbes)[0];
        activeProbeHostValue = hostOf(first);
        return first;
    }

    private static string hostOf(string url)
    {
        try { return new Uri(url).Host; } catch (Exception) { return url; }
    }

    /**
     * Пробники для замера ЧЕРЕЗ туннель, по приоритету: кастомный speedProbeUrl юзера (но НЕ мёртвый tele2-дефолт),
     * затем глобальные [fallbackProbes]. Раньше проверка АКТИВНОГО туннеля/живости брала listOf(speedProbeUrl) — у всех
     * со старым дефолтом первым шёл tele2, через туннель мёртвый: каждая проверка теряла ~4.5с на его таймауте
     * (поле, Huawei 2026-10-30: монитор перебирал серверы медленнее). Фикс f8ba302 закрыл только полный тест.
     */
    private static List<string> tunnelProbes()
    {
        if (probesOverrideForTest != null) return probesOverrideForTest.ToList();
        var saved = SettingsStore.current().speedProbeUrl;
        var list = new List<string>();
        if (saved.IsNotBlank() && !saved.Contains("tele2")) list.Add(saved);
        list.AddRange(fallbackProbes);
        return list.Distinct().ToList();
    }

    // Приёмники ОТДАЧИ (POST). Промпт 104.B (проверено ФАКТОМ, POST 2МБ): cloudflare __up отвечает 200 за ~1.2с
    // (принимает тело и сразу отвечает) → БЫСТРЫЙ, ставим первым. tele2/upload.php отвечает 200 напрямую, но
    // ЧЕРЕЗ ТУННЕЛЬ отдаёт байты и НЕ шлёт ответ (http=000) → клиент ждёт весь таймаут = сток 25-75с. Оставляем
    // tele2 как запасной (distinct убирает дубль основного из настроек). httpbin отвергнут (http=100, 15с).
    // Дефолт uploadProbeUrl в SettingsStore теперь cloudflare __up; на устройствах со старым (tele2) сохранённым
    // значением сток обрезает жёсткий потолок UPLOAD_HARD_CAP_MS в uploadMbps.
    private static readonly IReadOnlyList<string> uploadFallbacksDefault =
    [
        "https://speed.cloudflare.com/__up",
        "http://speedtest.tele2.net/upload.php",
    ];
    /// <summary>Запасные адреса отдачи; тесты подменяют (герметичность: без выхода в интернет через стенд).</summary>
    internal static IReadOnlyList<string> uploadFallbacks { get; set; } = uploadFallbacksDefault;
    // Промпт 104.B: жёсткий потолок ОБЩЕГО времени одного замера отдачи. Кооперативное окно (measureMs) между
    // записями НЕ спасает, если приёмник не отвечает / канал насыщён (одна запись блокируется). Отдача display-only —
    // не даём ей быть 25-75с стоком в КАЖДОМ активном замере. Потолок от первой записи (коннект ограничен отдельно).
    private const long UPLOAD_HARD_CAP_MS = 6_000L;

    /**
     * Пробники КАНАЛА (baseline «мой интернет», ПРЯМО, без туннеля) — русские незаблокированные CDN.
     * НЕ для туннеля! Сейчас НЕ используются — заложены для будущего замера канала (сигнал A монитора).
     * Cloudflare здесь нельзя (заблокирован напрямую); vk.RU (не vk.com).
     */
    internal static readonly IReadOnlyList<string> channelProbes =
    [
        "https://mc.yandex.ru/metrika/tag.js",
        "https://yastatic.net/jquery/3.6.4/jquery.min.js",
        "https://vk.ru/js/api/openapi.js",
    ];

    /** Исход замера: УСПЕХ (валидная скорость) или ПРОВАЛ (замер не состоялся — НЕ маленькая скорость).
     *  [bytes] — фактически скачано (для учёта ТЕСТОВОГО трафика, [TrafficTracker]).
     *  [windowMs] — фактическое окно измерения, мс (для этапного замера: если мелкая ступень скачалась слишком
     *  быстро — окно короткое и шумное — добираем бо́льшую ступень). */
    public sealed record Measurement(double mbps, bool ok, string reason, long bytes = 0, long windowMs = 0);

    /// <summary>Минимум байт, ниже которого замер считаем несостоявшимся (backstop к «измерено==0»).</summary>
    private const int MIN_TOTAL_BYTES = 16 * 1024;
    // Потолок правдоподобия ОТДАЧИ: выше — это не отдача, а мгновенная запись в буфер сокета (фейк ~1864 Мбит/с).
    // Реальная отдача через мобильный туннель много ниже; 500 Мбит/с с огромным запасом отсекает буфер-фейк.
    private const double MAX_PLAUSIBLE_UPLOAD_MBPS = 500.0;
    // Пр.148: «много-заходный» замер — окно измерения бьём на под-окна и берём МЕДИАНУ их скоростей. Одиночное
    // окно шумит (Пр.108/109/130): случайная просадка занижает быстрый сервер → он ошибочно уходит вниз в выборе.
    // Медиана под-окон сглаживает шум БЕЗ доп. трафика (та же загрузка). Нужно ≥3 под-окна, иначе берём общее.
    private const long SUB_WINDOW_NANOS = 300_000_000L;
    private static double median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList(); var n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }

    /**
     * Скорость одного сервера, Mbps. >0 — ВАЛИДНЫЙ замер; -1.0 — ПРОВАЛ/«не измерено»
     * (ошибка сборки/connect/порт/чтения, ноль измеренных байт, обрыв до минимума) — это НЕ 0.0
     * и НЕ маленькая скорость. Остановка по бюджету (время/объём) — ВАЛИДНА, не провал.
     * Не трогает активный прокси (свой инстанс + эфемерный порт).
     */
    public static double measureSpeed(Context context, ServerProfile profile, int? warmupMs = null, int? measureMs = null)
    {
        var m = measureSpeedDetailed(context, profile, warmupMs, measureMs);
        return m.ok ? m.mbps : -1.0;
    }

    /**
     * Пр.133.B: замер на ДОСТАТОЧНОСТЬ — не точное число, а «держит ли [thresholdMbps]». Бюджет/окно урезаны до
     * подтверждения порога: сервер быстрее порога упрётся в маленький бюджет за доли секунды → замер оборвётся,
     * трафика на порядок меньше полного. Возврат — как обычный замер (mbps); достаточность = mbps ≥ порога.
     * Бюджет замера = порог×~1.2с (сколько нужно прокачать, чтобы подтвердить скорость), прогрев — короткий.
     */
    public static double measureSufficiency(Context context, ServerProfile profile, double thresholdMbps)
    {
        var measureBudget = ((long)(thresholdMbps / 8.0 * 1_000_000.0 * 1.2)).coerceIn(256 * 1024L, 4 * 1024 * 1024L);
        var warmupBudget = 512 * 1024L;   // короткий прогрев — точность не нужна, нужен факт «тянет порог»
        var m = measureSpeedDetailed(context, profile, warmupMs: 400, measureMs: 1200,
            warmupBudgetOverride: warmupBudget, measureBudgetOverride: measureBudget);
        return m.ok ? m.mbps : -1.0;
    }

    /**
     * МИНИМАЛЬНЫЙ замер кандидата (temp-инстанс), ~100 КБ — как первая ступень активного staged. Цель: ДЁШЕВО
     * узнать «есть ли throughput» (для порога минимальный→полный при переборе всех живых), а НЕ точная скорость.
     * Прогрев крошечный (мелочь не должна уходить целиком в прогрев). >0 валидно, -1 провал. Трафик → «Тест».
     */
    public static double measureSpeedMinimal(Context context, ServerProfile profile)
    {
        // БЫСТРЫЙ замер: только 1 пробник + короткий коннект-таймаут 3с → мёртвый сервер (tcping открыт, но прокси не
        // пропускает) отсеивается за ~3с, а не ~20-30с (4 пробника × 8с). Иначе на пуле из 269 «достижимых» Этап 2 полз.
        var m = measureSpeedDetailed(context, profile, warmupMs: 100, measureMs: 800,
            warmupBudgetOverride: 16 * 1024L, measureBudgetOverride: 100 * 1024L,
            maxProbes: 1, connectTimeoutMs: 3_000);
        return m.ok ? m.mbps : -1.0;
    }

    /** Как [measureSpeed], но с исходом/причиной (для диалога «Перемерить»). */
    public static Measurement measureSpeedDetailed(
        Context context,
        ServerProfile profile,
        int? warmupMs = null,
        int? measureMs = null,
        long? warmupBudgetOverride = null,   // Пр.133: урезанный бюджет для замера достаточности
        long? measureBudgetOverride = null,
        int maxProbes = int.MaxValue,        // для БЫСТРОГО минимального — 1 пробник (не перебирать 4 на мёртвом)
        int connectTimeoutMs = CONNECT_TIMEOUT_MS)   // для минимального — короткий (3с), чтобы мёртвые падали быстро
    {
        var wMs = warmupMs ?? SettingsStore.current().speedWarmupMs;
        var mMs = measureMs ?? SettingsStore.current().speedWindowMs;
        XrayController.ensureEnv(context);
        var verbose = SettingsStore.current().verboseLogs;
        void log(string msg) { if (verbose) Log.i(TAG, msg); }
        // ТЗ Elyor: мерим туннель ТОЛЬКО пробником, подтверждённым ПРЯМЫМ фетчем из РФ (и глобально достижимым — значит
        // и из-за рубежа через туннель). Так провал замера = вина ТУННЕЛЯ, а не мёртвого пробника (был баг: tele2 как
        // единственный пробник быстрого замера мёртв через туннель → «годных 0» у живых серверов). maxProbes игнорируем.
        var probes = new List<string> { directlyReachableProbe(context) };

        var name = profile.remarks.IfBlank(profile.address);
        var warmupBudget = warmupBudgetOverride ?? SettingsStore.current().speedWarmupBudgetBytes;
        var measureBudget = measureBudgetOverride ?? SettingsStore.current().activeMeasureBudgetBytes;   // Пр.131: бюджет per-mode
        var concurrent = Interlocked.Increment(ref concurrentMeasures);
        log($"── measure START «{name}» {profile.address}:{profile.port} net={profile.network} sec={profile.security} · параллельно={concurrent} · warmup={wMs}мс/{warmupBudget}Б окно={mMs}мс/{measureBudget}Б");
        var t0 = JavaSystem.nanoTime();
        try
        {
            var port = freePort();
            if (port == null) { log($"«{name}» freePort FAIL"); return new Measurement(-1.0, false, "нет свободного порта"); }
            string cfg;
            try
            {
                cfg = XrayConfigBuilder.buildForSpeedTest(profile, port.Value);
            }
            catch (Exception e)
            {
                log($"«{name}» buildForSpeedTest FAIL: {e.Message}");
                return new Measurement(-1.0, false, $"ошибка конфига: {e.Message}");
            }

            var core = L.newCoreController(new NoopCallback());
            try
            {
                core.startLoop(cfg, 0);
                if (!core.isRunning) { log($"«{name}» core not running"); return new Measurement(-1.0, false, "ядро не запустилось"); }
                var portReady = awaitPort(port.Value, deadlineMs: 2_000);
                var upMs = (JavaSystem.nanoTime() - t0) / 1_000_000;
                if (!portReady)
                {
                    log($"«{name}» temp-инстанс порт {port} НЕ готов за {upMs}мс");
                    return new Measurement(-1.0, false, "temp-инстанс не поднялся");
                }
                log($"«{name}» temp-инстанс поднят за {upMs}мс (порт={port})");
                var lastFail = new Measurement(-1.0, false, "нет пробников");
                var testBytes = 0L;
                foreach (var probe in probes)
                {
                    var m = downloadMbps(probe, new HttpRoute.Socks("127.0.0.1", port.Value),
                        wMs, mMs, warmupBudget, measureBudget, name, verbose, connectTimeoutMs);
                    testBytes += m.bytes;                    // тестовый трафик: скачанное каждым пробником
                    if (m.ok)
                    {
                        TrafficTracker.addTest(testBytes);
                        log($"── measure DONE «{name}» = {m.mbps} Mbps [{m.reason}] (probe={probe}) · тест-байт={testBytes}");
                        return m;
                    }
                    lastFail = m;
                }
                TrafficTracker.addTest(testBytes);
                log($"── measure FAIL «{name}» — {lastFail.reason} · тест-байт={testBytes}");
                return lastFail;
            }
            catch (Exception e)
            {
                log($"«{name}» measureSpeed error: {e.Message}");
                return new Measurement(-1.0, false, $"исключение: {e.Message}");
            }
            finally
            {
                try { core.stopLoop(); } catch (Exception) { /* ignore */ }
            }
        }
        finally
        {
            Interlocked.Decrement(ref concurrentMeasures);
        }
    }

    // Дешёвая проверка кандидата (Промпт 52): temp-инстанс + РЕАЛЬНАЯ передача небольшого объёма.
    // Критерий — БАЙТЫ ПРИШЛИ, а не время ответа (в отличие от прежнего пинга через measureOutboundDelay,
    // который меряет задержку до generate_204 и отбрасывал бы «не пингуется, но работает» серверы).
    private const long PROBE_ALIVE_TARGET_BYTES = 32 * 1024L;
    private const long PROBE_ALIVE_MIN_BYTES = 8 * 1024L;
    private const int PROBE_ALIVE_TIMEOUT_MS = 6_000;

    /**
     * Жив ли сервер по РЕАЛЬНОЙ передаче: поднять temp-инстанс на эфемерном порту, скачать несколько КБ
     * через его socks. true — пришло ≥ [PROBE_ALIVE_MIN_BYTES]. Не трогает активный прокси. Трафик → «Тест».
     */
    public static bool probeAlive(Context context, ServerProfile profile)
    {
        XrayController.ensureEnv(context);
        var verbose = SettingsStore.current().verboseLogs;
        void log(string msg) { if (verbose) Log.i(TAG, msg); }
        var name = profile.remarks.IfBlank(profile.address);
        var probes = tunnelProbes();
        Interlocked.Increment(ref concurrentMeasures);
        try
        {
            var port = freePort();
            if (port == null) return false;
            string cfg;
            try { cfg = XrayConfigBuilder.buildForSpeedTest(profile, port.Value); }
            catch (Exception e)
            {
                log($"«{name}» probeAlive buildForSpeedTest FAIL: {e.Message}"); return false;
            }
            var core = L.newCoreController(new NoopCallback());
            try
            {
                core.startLoop(cfg, 0);
                if (!core.isRunning || !awaitPort(port.Value, deadlineMs: 2_000))
                {
                    log($"«{name}» probeAlive temp-инстанс не поднялся"); return false;
                }
                var proxy = new HttpRoute.Socks("127.0.0.1", port.Value);
                var testBytes = 0L;
                foreach (var probe in probes)
                {
                    var got = downloadSome(probe, proxy, PROBE_ALIVE_TARGET_BYTES, PROBE_ALIVE_TIMEOUT_MS);
                    testBytes += got;
                    if (got >= PROBE_ALIVE_MIN_BYTES)
                    {
                        TrafficTracker.addTest(testBytes);
                        log($"«{name}» probeAlive OK ({got} Б via {probe})");
                        return true;
                    }
                }
                TrafficTracker.addTest(testBytes);
                log($"«{name}» probeAlive FAIL (пришло {testBytes} Б)");
                return false;
            }
            finally
            {
                try { core.stopLoop(); } catch (Exception) { /* ignore */ }
            }
        }
        finally
        {
            Interlocked.Decrement(ref concurrentMeasures);
        }
    }

    /// <summary>Скачать до [targetBytes] через socks-proxy (или НАПРЯМУЮ при proxy=null) с [timeoutMs]. Возвращает принятые байты.</summary>
    private static long downloadSome(string probe, HttpRoute? proxy, long targetBytes, int timeoutMs)
    {
        var conn = new JavaHttpConnection(probe, proxy ?? HttpRoute.DIRECT) { connectTimeout = CONNECT_TIMEOUT_MS, readTimeout = timeoutMs };
        try
        {
            var code = conn.responseCode();
            if (code is < 200 or > 299) return 0L;
            var total = 0L;
            var start = JavaSystem.nanoTime();
            var buf = new byte[16 * 1024];
            while (true)
            {
                var n = conn.read(buf); if (n < 0) break;
                total += n;
                if (total >= targetBytes || (JavaSystem.nanoTime() - start) / 1_000_000 > timeoutMs) break;
            }
            return total;
        }
        catch (Exception)
        {
            return 0L;
        }
        finally { conn.disconnect(); }
    }

    /**
     * Скачать ПРОИЗВОЛЬНЫЙ URL через temp-инстанс данного сервера (ступень 6 каскада [CascadeFetch]:
     * обновить подписку, когда прямой путь заблокирован, а активный туннель упал). Переиспользует
     * [SubscriptionFetcher] (редиректы/типизированный результат) через socks temp-инстанса. Не трогает
     * активный прокси. Трафик → «Тест». null — temp-инстанс не поднялся.
     */
    public static FetchResult? fetchViaTempInstance(Context context, ServerProfile profile, string url, string userAgent, int timeoutMs)
    {
        XrayController.ensureEnv(context);
        var verbose = SettingsStore.current().verboseLogs;
        void log(string msg) { if (verbose) Log.i(TAG, msg); }
        var name = profile.remarks.IfBlank(profile.address);
        Interlocked.Increment(ref concurrentMeasures);
        try
        {
            var port = freePort();
            if (port == null) return null;
            string cfg;
            try { cfg = XrayConfigBuilder.buildForSpeedTest(profile, port.Value); }
            catch (Exception e)
            {
                log($"«{name}» fetchViaTempInstance buildForSpeedTest FAIL: {e.Message}"); return null;
            }
            var core = L.newCoreController(new NoopCallback());
            try
            {
                core.startLoop(cfg, 0);
                if (!core.isRunning || !awaitPort(port.Value, deadlineMs: 2_000))
                {
                    log($"«{name}» fetchViaTempInstance temp-инстанс не поднялся"); return null;
                }
                var res = SubscriptionFetcher.fetch(url, userAgent, timeoutMs, new HttpRoute.Socks("127.0.0.1", port.Value));
                if (res.bodyBytes > 0) TrafficTracker.addTest(res.bodyBytes);
                log($"«{name}» fetchViaTempInstance http={res.httpCode} bytes={res.bodyBytes}");
                return res;
            }
            // Находка порта №4: исключение startLoop (битый конфиг сервера — ядро не стартует) раньше летело наружу и
            // обрывало ВЕСЬ каскад загрузки. Теперь это провал ОДНОГО кандидата: null → каскад берёт следующего.
            catch (Exception e)
            {
                log($"«{name}» fetchViaTempInstance ядро не стартовало: {e.Message}"); return null;
            }
            finally
            {
                try { core.stopLoop(); } catch (Exception) { /* ignore */ }
            }
        }
        finally
        {
            Interlocked.Decrement(ref concurrentMeasures);
        }
    }

    public interface Handle
    {
        void cancel();
    }

    private sealed class HandleImpl(Action onCancel) : Handle
    {
        public void cancel() => onCancel();
    }

    /**
     * Батч скорости. Пул [pool] одновременных замеров (из настроек, дефолт 1 = строго последовательно —
     * каждый temp-инстанс насыщает канал; пул>1 сделан НАСТРАИВАЕМЫМ специально, чтобы проверить
     * гипотезу деления канала параллельными замерами). Прогрессивный [onResult] сразу; [onProgress];
     * по завершении [onFinish]. Отмена ([Handle.cancel]) — флаг (текущие замеры доработают своё окно).
     * Колбэки на фоновых потоках пула — UI-маршалинг на вызывающем.
     */
    public static Handle testAll(
        Context context,
        IReadOnlyList<ServerProfile> servers,
        Action<ServerProfile, double> onResult,
        Action<int, int> onProgress,
        Action? onFinish = null,
        int? warmupMs = null,
        int? measureMs = null,
        int? pool = null)
    {
        onFinish ??= () => { };
        var wMs = warmupMs ?? SettingsStore.current().speedWarmupMs;
        var mMs = measureMs ?? SettingsStore.current().speedWindowMs;
        var appCtx = context.applicationContext;
        var cancelled = false;
        var total = servers.Count;
        var done = 0;
        var executor = new FixedThreadPool((pool ?? SettingsStore.current().speedPool).coerceAtLeast(1), "speed");

        foreach (var p in servers)
        {
            executor.execute(() =>
            {
                try
                {
                    if (Volatile.Read(ref cancelled)) return;
                    var mbps = measureSpeed(appCtx, p, wMs, mMs);
                    if (Volatile.Read(ref cancelled)) return;
                    onResult(p, mbps);
                    onProgress(Interlocked.Increment(ref done), total);
                }
                catch (Exception e)
                {
                    Log.w(TAG, $"задача замера упала (проглочено): {e.Message}");
                }
            });
        }
        executor.shutdown();   // новых не принимаем; поданные доработают

        KThread.start(() =>
        {
            executor.awaitTermination(long.MaxValue);
            if (!Volatile.Read(ref cancelled)) onFinish();
        }, "speed-finish");

        return new HandleImpl(() =>
        {
            Volatile.Write(ref cancelled, true);
            executor.shutdownNow();
        });
    }

    /// <summary>Свободный TCP-порт от ОС (ServerSocket(0)); null если не удалось.</summary>
    private static int? freePort()
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>Ждём, пока socks-порт temp-инстанса начнёт принимать (ретрай на гонку старта).</summary>
    private static bool awaitPort(int port, long deadlineMs)
    {
        var end = JavaSystem.nanoTime() + deadlineMs * 1_000_000;
        while (JavaSystem.nanoTime() < end)
        {
            try
            {
                using var s = new TcpClient();
                if (s.ConnectAsync(IPAddress.Loopback, port).Wait(300) && s.Connected) return true;
            }
            catch (Exception) { }
            Thread.Sleep(100);
        }
        return false;
    }

    /**
     * Замер с ДВУМЯ бюджетами на КАЖДУЮ фазу — время И объём, кончается наступивший РАНЬШЕ.
     * ОДНА формула: mbps = измерено_байт × 8 / фактическое_окно / 1e6. Прогрев — от ПЕРВОГО БАЙТА
     * ТЕЛА (фикс C), измерение — сразу после прогрева (его фактического конца, не по фикс. времени).
     * Соединение рвём сразу по достижении бюджета — не дочитываем впустую.
     *
     * ВАЖНО (Промпт 32): остановка ПО ОБЪЁМУ — ВАЛИДНЫЙ результат при любой длительности окна.
     * Правило «короткое окно = провал» БОЛЬШЕ НЕ применяется: провал только при ошибке чтения /
     * ноль измеренных байт / всего_байт < минимума. Быстрый сервер упрётся в объём за доли секунды —
     * это НЕ «не измерено».
     */
    private static Measurement downloadMbps(
        string probe, HttpRoute? proxy, int warmupMs, int measureMs,
        long warmupBudget, long measureBudget, string name, bool verbose,
        int connectTimeoutMs = CONNECT_TIMEOUT_MS)
    {
        void log(string msg) { if (verbose) Log.i(TAG, msg); }
        // proxy=null → ПРЯМОЕ соединение (замер прямого канала, без туннеля); иначе через SOCKS.
        var conn = new JavaHttpConnection(probe, proxy ?? HttpRoute.DIRECT)
        {
            connectTimeout = connectTimeoutMs,
            readTimeout = warmupMs + measureMs + 2_000,
        };
        currentMeasureConn = conn;   // Промпт 101.B: чтобы «Прервать проверку» закрыл это соединение немедленно
        var reqStart = JavaSystem.nanoTime();
        int code;
        try
        {
            code = conn.responseCode();
        }
        catch (Exception e)
        {
            log($"«{name}» probe={probe} CONNECT-FAIL {JavaExceptionNames.simpleName(e)}: {e.Message}");
            conn.disconnect();
            if (ReferenceEquals(currentMeasureConn, conn)) currentMeasureConn = null;
            return new Measurement(-1.0, false, $"connect-fail: {JavaExceptionNames.simpleName(e)}");
        }
        var headerMs = (JavaSystem.nanoTime() - reqStart) / 1_000_000;
        log($"«{name}» probe={probe} http={code} хендшейк+заголовки={headerMs}мс contentLength={conn.contentLengthLong}");
        if (code is < 200 or > 299)
        {
            conn.disconnect();
            if (ReferenceEquals(currentMeasureConn, conn)) currentMeasureConn = null;
            return new Measurement(-1.0, false, $"HTTP {code}");
        }

        long totalBytes = 0L, warmupBytes = 0L, measuredBytes = 0L;
        long firstNanos = 0L;
        long warmupTimeEnd = 0L;        // предел прогрева по времени (от первого байта)
        long warmupDoneNanos = 0L;      // фактический конец прогрева (время ИЛИ объём)
        long measureTimeEnd = 0L;       // предел измерения по времени (от конца прогрева)
        long lastMeasuredNanos = 0L;
        var inWarmup = true;
        var eof = false;
        string? readErr = null;
        var warmupEndReason = "—";      // ПО_ВРЕМЕНИ / ПО_ОБЪЁМУ / EOF / ОШИБКА
        var measureEndReason = "—";
        long subStartNanos = 0L;        // Пр.148: под-окна измерения (для медианы — сглаживание шума)
        long subBytes = 0L;
        var subRates = new List<double>();
        try
        {
            var buf = new byte[64 * 1024];
            while (true)
            {
                int n;
                try { n = conn.read(buf); }
                catch (Exception e)
                {
                    readErr = $"{JavaExceptionNames.simpleName(e)}: {e.Message}";
                    if (inWarmup) warmupEndReason = "ОШИБКА"; else measureEndReason = "ОШИБКА";
                    break;
                }
                if (n < 0)
                {
                    eof = true;
                    if (inWarmup) warmupEndReason = "EOF"; else measureEndReason = "EOF";
                    break;
                }
                var now = JavaSystem.nanoTime();
                if (firstNanos == 0L)                           // прогрев стартует от ПЕРВОГО байта
                {
                    firstNanos = now;
                    warmupTimeEnd = now + warmupMs * 1_000_000L;
                }
                totalBytes += n;
                if (inWarmup)
                {
                    warmupBytes += n;
                    // прогрев кончается по ВРЕМЕНИ или по ОБЪЁМУ — что раньше
                    var byTime = now >= warmupTimeEnd;
                    var byVol = warmupBytes >= warmupBudget;
                    if (byTime || byVol)
                    {
                        warmupEndReason = byVol ? "ПО_ОБЪЁМУ" : "ПО_ВРЕМЕНИ";
                        inWarmup = false;
                        warmupDoneNanos = now;
                        measureTimeEnd = now + measureMs * 1_000_000L;
                    }
                }
                else
                {
                    measuredBytes += n;
                    lastMeasuredNanos = now;
                    // Пр.148: копим под-окно; по истечении SUB_WINDOW_NANOS — фиксируем его скорость (для медианы).
                    if (subStartNanos == 0L) subStartNanos = warmupDoneNanos != 0L ? warmupDoneNanos : now;
                    subBytes += n;
                    var subDt = now - subStartNanos;
                    if (subDt >= SUB_WINDOW_NANOS)
                    {
                        subRates.Add(subBytes * 8.0 / (subDt / 1_000_000_000.0) / 1_000_000.0);
                        subStartNanos = now; subBytes = 0L;
                    }
                    var byTime = now >= measureTimeEnd;
                    var byVol = measuredBytes >= measureBudget;
                    if (byTime || byVol)
                    {
                        measureEndReason = byVol && !byTime ? "ПО_ОБЪЁМУ" : "ПО_ВРЕМЕНИ";
                        break;                                    // рвём сразу — не дочитываем впустую
                    }
                }
            }
        }
        catch (Exception e)
        {
            readErr = $"{JavaExceptionNames.simpleName(e)}: {e.Message}";
        }
        finally
        {
            conn.disconnect();
            if (ReferenceEquals(currentMeasureConn, conn)) currentMeasureConn = null;
        }

        var ttfbMs = firstNanos != 0L ? (firstNanos - reqStart) / 1_000_000 : -1;
        var warmupMsActual = firstNanos != 0L && warmupDoneNanos > firstNanos ? (warmupDoneNanos - firstNanos) / 1_000_000 : -1;
        // Фактическое окно измерения = от конца прогрева до последнего измеренного байта.
        var actualWindowMs = warmupDoneNanos != 0L && lastMeasuredNanos > warmupDoneNanos
            ? (lastMeasuredNanos - warmupDoneNanos) / 1_000_000 : 0L;

        // ПРОВАЛ — ТОЛЬКО ошибка/обрыв/ноль. Остановка по объёму (короткое окно) — валидна.
        string? failReason =
            readErr != null ? $"ошибка чтения: {readErr}" :
            measuredBytes == 0L ? "0 измеренных байт (соединение встало после заголовков)" :
            totalBytes < MIN_TOTAL_BYTES ? $"всего {totalBytes} байт < минимума" :
            actualWindowMs <= 0L ? "нулевое окно измерения" :
            null;
        var diag = $"ttfb={ttfbMs}мс прогрев[{warmupEndReason} {warmupMsActual}мс {warmupBytes}Б/бюджет{warmupBudget}Б] " +
                   $"измер[{measureEndReason} {actualWindowMs}мс {measuredBytes}Б/бюджет{measureBudget}Б] всего={totalBytes} eof={(eof ? "true" : "false")}";
        if (failReason != null)
        {
            log($"«{name}» probe={probe} ПРОВАЛ [{failReason}] | {diag}");
            return new Measurement(-1.0, false, failReason, bytes: totalBytes);
        }

        var overallMbps = measuredBytes * 8.0 / (actualWindowMs / 1000.0) / 1_000_000.0;   // общее окно
        // Пр.148: если под-окон ≥3 — берём МЕДИАНУ их скоростей (сглаживает шум); иначе (короткий замер/стоп по
        // объёму у быстрого сервера) — общее окно. Медиана не даёт одиночной просадке занизить хороший сервер.
        var mbps = subRates.Count >= 3 ? median(subRates) : overallMbps;
        var rounded = (mbps * 100).roundToInt() / 100.0;
        log($"«{name}» probe={probe} → {rounded} Mbps (медиана {subRates.Count} под-окон, общее {(overallMbps * 100).roundToInt() / 100.0}) | {diag}");
        return new Measurement(rounded, true, $"ok/{measureEndReason}", bytes: totalBytes, windowMs: actualWindowMs);
    }

    // ══════════════ Промпт 90: замеры АКТИВНОГО туннеля (сопоставимо с кандидатами) + ОТДАЧА ══════════════
    // ВАЖНО (90.A): активный туннель мерим ТЕМ ЖЕ downloadMbps с ОДНИМИ warmup/окном/пробниками, что и
    // measureSpeed кандидатов → числа сравнимы напрямую. Прежний NetworkMonitor.measureTunnelMbps (без
    // прогрева, кэп 2МБ, cloudflare, таймер со старта соединения) СИСТЕМАТИЧЕСКИ занижал → сравнивать было
    // нельзя (сидели на 8, видя 143). Идёт через активный SOCKS (XrayConfig.SOCKS_PORT), не temp-инстанс.

    // Промпт 101.B: НЕМЕДЛЕННО прервать идущий замер (кнопка «Прервать проверку»), НЕ дожидаясь таймаута —
    // закрываем текущее соединение замера, блокирующее чтение/коннект падает с ошибкой → замер вернёт провал,
    // а поток проверки увидит checkCancel и выйдет. Во время ручной проверки замеры идут ПОСЛЕДОВАТЕЛЬНО в
    // одном потоке, а монитор молчит (fullTestRunning) — гонок за этот ref нет. Таймауты/логику НЕ трогаем.
    private static volatile JavaHttpConnection? currentMeasureConn;
    private static volatile StreamingUpload? currentUpload;
    private static volatile bool measureAborted;
    // Закрыть текущее соединение И поднять флаг, чтобы цикл фолбэк-пробников замера активного туннеля СРАЗУ
    // прекратился, а не перебирал оставшиеся адреса (иначе отмена ждёт весь перебор). Reset — в начале проверки.
    public static void abortCurrentMeasure()
    {
        measureAborted = true;
        try { currentMeasureConn?.disconnect(); } catch (Exception) { }
        try { currentUpload?.disconnect(); } catch (Exception) { }
    }
    public static void resetMeasureAbort() => measureAborted = false;

    /// <summary>Порт нашего SOCKS для замеров активного туннеля (на Windows может быть изменён — §13.7).</summary>
    public static Func<int> activeSocksPort { get; set; } = () => XrayConfig.SOCKS_PORT;

    // ══════════════ Этапный замер (Cloudflare-style): маленький файл → больше, пока окно не станет точным ══════════════
    // Cloudflare SpeedTest качает сначала мелочь (~100 КБ), потом крупнее (1/10/25 МБ), подбирая размер под канал:
    // на МЕДЛЕННОМ 100 КБ достаточно (быстрый ответ, мало трафика); на БЫСТРОМ 100 КБ скачиваются за миллисекунды →
    // окно измерения слишком короткое и шумное → добираем бо́льшую ступень. Ступени ограничены бюджетом трафика
    // активного замера (activeMeasureBudgetBytes), чтобы не раздувать расход. Прогрев крошечный (мелкая ступень
    // не должна уходить целиком в прогрев). Критерий «добирать»: окно измерения < MIN_RELIABLE_WINDOW_MS.
    private static readonly long[] STAGED_BUDGETS = [100 * 1024L, 1 * 1_048_576L, 4 * 1_048_576L];
    private const int STAGED_WARMUP_MS = 100;
    private const long STAGED_WARMUP_BUDGET = 16 * 1024L;
    private const long MIN_RELIABLE_WINDOW_MS = 400L;

    /**
     * Этапный замер СКАЧИВАНИЯ активного туннеля (Cloudflare-style). >0 — валидно, -1 — провал.
     * Ступени объёма STAGED_BUDGETS (100 КБ → 1 МБ → 4 МБ), ограниченные [activeMeasureBudgetBytes]: качаем от
     * мелкой к крупной, останавливаемся, как только окно измерения ≥ [MIN_RELIABLE_WINDOW_MS] (точно) — медленному
     * каналу хватит первой ступени, быстрому доберём до крупной. Трафик → «Тест». Отмена рвёт перебор немедленно.
     */
    public static double measureActiveDownloadStaged(Context context)
    {
        XrayController.ensureEnv(context);
        var s = SettingsStore.current();
        var proxy = new HttpRoute.Socks("127.0.0.1", activeSocksPort());
        var probes = tunnelProbes();
        var cap = s.activeMeasureBudgetBytes.coerceAtLeast(STAGED_BUDGETS[0]);
        var stages = STAGED_BUDGETS.Where(it => it <= cap).ToList();
        if (stages.Count == 0) stages = [STAGED_BUDGETS[0]];
        var testBytes = 0L;
        foreach (var probe in probes)
        {
            if (measureAborted) break;   // Промпт 101.B: отмена — не перебираем оставшиеся пробники
            var best = -1.0;
            var probeFailed = false;
            foreach (var budget in stages)
            {
                if (measureAborted) break;
                var m = downloadMbps(probe, proxy, STAGED_WARMUP_MS, s.speedWindowMs,
                    STAGED_WARMUP_BUDGET, budget, $"активный↓{budget / 1024}КБ", s.verboseLogs);
                testBytes += m.bytes;
                if (!m.ok) { probeFailed = true; break; }   // пробник не отдал → следующий пробник
                best = m.mbps;
                if (m.windowMs >= MIN_RELIABLE_WINDOW_MS) break;   // окно достаточно длинное → точно, не добираем
                // иначе канал быстрый (мелкая ступень скачалась мгновенно) → бо́льшая ступень
            }
            if (measureAborted) break;
            if (!probeFailed && best > 0) { TrafficTracker.addTest(testBytes); return best; }
        }
        TrafficTracker.addTest(testBytes);
        return -1.0;
    }

    /** МИНИМАЛЬНЫЙ замер активного туннеля (~100 КБ) — дёшево узнать «есть ли throughput» (кнопка «Проверить»:
     *  пинг→минимальный→полный). >0 валидно, -1 провал. Трафик → «Тест». */
    public static double measureActiveDownloadMinimal(Context context)
    {
        XrayController.ensureEnv(context);
        var s = SettingsStore.current();
        var proxy = new HttpRoute.Socks("127.0.0.1", activeSocksPort());
        var probes = tunnelProbes();
        var testBytes = 0L;
        foreach (var probe in probes)
        {
            if (measureAborted) break;
            var m = downloadMbps(probe, proxy, STAGED_WARMUP_MS, s.speedWindowMs,
                STAGED_WARMUP_BUDGET, STAGED_BUDGETS[0], "активный↓мин", s.verboseLogs);
            testBytes += m.bytes;
            if (m.ok) { TrafficTracker.addTest(testBytes); return m.mbps; }
        }
        TrafficTracker.addTest(testBytes);
        return -1.0;
    }

    /// <summary>Скорость СКАЧИВАНИЯ активного туннеля, Mbps (>0 валидно, -1 провал). Трафик → «Тест».</summary>
    public static double measureActiveDownloadMbps(Context context)
    {
        XrayController.ensureEnv(context);
        var s = SettingsStore.current();
        var probes = tunnelProbes();
        var testBytes = 0L;
        foreach (var probe in probes)
        {
            if (measureAborted) break;   // Промпт 101.B: отмена — не перебираем оставшиеся пробники
            var m = downloadMbps(probe, new HttpRoute.Socks("127.0.0.1", activeSocksPort()),
                s.speedWarmupMs, s.speedWindowMs, s.speedWarmupBudgetBytes, s.activeMeasureBudgetBytes, "активный↓", s.verboseLogs);
            testBytes += m.bytes;
            if (m.ok) { TrafficTracker.addTest(testBytes); return m.mbps; }
        }
        TrafficTracker.addTest(testBytes);
        return -1.0;
    }

    /**
     * Скорость ПРЯМОГО канала (БЕЗ туннеля), Mbps — «Напрямую» на плашке. Тем же надёжным окном/прогревом, что и
     * туннель (иначе числа несравнимы), но по ПРЯМОМУ соединению (proxy=null) и с БОЛЬШОГО файла (directProbeUrl):
     * наивный замер мелкой страницы мерил бы задержку, а не ширину канала. Трафик → «Тест». -1 = провал/недоступно.
     */
    public static double measureDirectDownloadMbps(Context context)
    {
        var s = SettingsStore.current();
        var m = downloadMbps(s.directProbeUrl, null, s.speedWarmupMs, s.speedWindowMs,
            s.speedWarmupBudgetBytes, s.activeMeasureBudgetBytes, "напрямую↓", s.verboseLogs);
        if (m.bytes > 0) TrafficTracker.addTest(m.bytes);
        return m.ok ? m.mbps : -1.0;
    }

    /// <summary>Скорость ОТДАЧИ активного туннеля, Mbps (>0 валидно, -1 провал). Трафик → «Тест».</summary>
    public static double measureActiveUploadMbps(Context context)
    {
        XrayController.ensureEnv(context);
        var s = SettingsStore.current();
        var probes = new[] { s.uploadProbeUrl }.Concat(uploadFallbacks).Distinct().ToList();
        foreach (var url in probes)
        {
            if (measureAborted) break;   // Промпт 101.B: отмена — не перебираем оставшиеся приёмники
            var m = uploadMbps(url, activeSocksPort(), s.speedWarmupMs, s.speedWindowMs,
                s.uploadMeasureBudgetBytes, "активный↑", s.verboseLogs);
            if (m.ok) return m.mbps;
        }
        return -1.0;
    }

    /**
     * Замер ОТДАЧИ (Промпт 90.B): POST-выгрузка через socks на приёмник. Прогрев ПО ВРЕМЕНИ (skip TCP
     * slow-start), затем окно до [measureBudget] байт ИЛИ [measureMs]. Пишем в поток — блокировка записи
     * при насыщении канала = реальная скорость отдачи. Формула та же: байты×8/окно/1e6. Трафик → «Тест».
     */
    internal static Measurement uploadMbps(string url, int socksPort, int warmupMs, int measureMs, long measureBudget, string name, bool verbose)
    {
        void log(string msg) { if (verbose) Log.i(TAG, msg); }
        var up = new StreamingUpload();
        currentUpload = up;   // Промпт 101.B: «Прервать проверку» закрывает и идущую отдачу

        var buf = new byte[64 * 1024];
        long total = 0L, warmupBytes = 0L, measuredBytes = 0L;
        long firstNanos = 0L, warmupTimeEnd = 0L, warmupDoneNanos = 0L;
        long measureTimeEnd = 0L, lastMeasuredNanos = 0L;
        var inWarmup = true; string? writeErr = null; var code = -1;
        try
        {
            code = up.send(url, new HttpRoute.Socks("127.0.0.1", socksPort), CONNECT_TIMEOUT_MS, warmupMs + measureMs + 3_000, "v2rayNG/1.8.0", @out =>
            {
                while (true)
                {
                    // Промпт 104.B: жёсткий потолок ОБЩЕГО времени от первой записи — приёмник может не отвечать /
                    // канал насыщён, и кооперативное окно (measureMs, проверка МЕЖДУ записями) даёт переполнение
                    // до 25-75с. Обрезаем сток независимо от настроек/буферов.
                    if (firstNanos != 0L && (JavaSystem.nanoTime() - firstNanos) / 1_000_000 > UPLOAD_HARD_CAP_MS)
                    {
                        writeErr ??= $"потолок времени отдачи {UPLOAD_HARD_CAP_MS}мс"; break;
                    }
                    try { @out.Write(buf, 0, buf.Length); }
                    catch (Exception e) { writeErr = $"{JavaExceptionNames.simpleName(e)}: {e.Message}"; break; }
                    var now = JavaSystem.nanoTime();
                    if (firstNanos == 0L) { firstNanos = now; warmupTimeEnd = now + warmupMs * 1_000_000L; }
                    total += buf.Length;
                    if (inWarmup)
                    {
                        warmupBytes += buf.Length;
                        if (now >= warmupTimeEnd) { inWarmup = false; warmupDoneNanos = now; measureTimeEnd = now + measureMs * 1_000_000L; }
                    }
                    else
                    {
                        measuredBytes += buf.Length; lastMeasuredNanos = now;
                        if (now >= measureTimeEnd || measuredBytes >= measureBudget) break;
                    }
                }
                try { @out.Flush(); } catch (Exception e) { writeErr ??= $"{JavaExceptionNames.simpleName(e)}: {e.Message}"; }
                // Промпт 104.B: не блокируемся на ответе, если запись оборвалась/упёрлась в потолок (приёмник, скорее
                // всего, не ответит → ждали бы весь readTimeout). Код нужен лишь для лога/правдоподобия при УСПЕХЕ.
                return writeErr == null;
            });
            log($"«{name}» upload={url} http={code} всего={total}Б измер={measuredBytes}Б");
        }
        catch (Exception e)
        {
            writeErr ??= $"{JavaExceptionNames.simpleName(e)}: {e.Message}";
        }
        finally
        {
            up.disconnect();
            if (ReferenceEquals(currentUpload, up)) currentUpload = null;
        }
        if (total > 0) TrafficTracker.addTest(total);

        var windowMs = warmupDoneNanos != 0L && lastMeasuredNanos > warmupDoneNanos
            ? (lastMeasuredNanos - warmupDoneNanos) / 1_000_000 : 0L;
        var mbps = windowMs > 0 ? measuredBytes * 8.0 / (windowMs / 1000.0) / 1_000_000.0 : -1.0;
        // Фейк «буфер сокета» (2МБ за 9мс = ~1864 Мбит/с) отличаем от честного замера НЕ по http-коду:
        // флаки-сервер часто НЕ отвечает (http=-1), хотя передача реально прошла с backpressure (окно ~500мс) —
        // требование 2xx душило валидную отдачу (пользователь видел «—»). Признак фейка — НЕРЕАЛЬНАЯ скорость
        // (реальная отдача через мобильный туннель много ниже потолка); реальный замер имеет осмысленное окно.
        string? failReason =
            writeErr != null ? $"ошибка отдачи: {writeErr}" :
            measuredBytes == 0L ? "0 отданных байт" :
            windowMs <= 0L ? "нулевое окно отдачи" :
            mbps > MAX_PLAUSIBLE_UPLOAD_MBPS ? $"нереально {mbps.roundToInt()} Мбит/с — буфер сокета, не отдача (http={code})" :
            null;
        if (failReason != null) { log($"«{name}» upload ПРОВАЛ [{failReason}]"); return new Measurement(-1.0, false, failReason, bytes: total); }
        var rounded = (mbps * 100).roundToInt() / 100.0;
        log($"«{name}» upload → {rounded} Mbps (окно {windowMs}мс, {measuredBytes}Б, http={code})");
        return new Measurement(rounded, true, "ok-upload", bytes: total);
    }

    private sealed class NoopCallback : CoreCallbackHandler
    {
        public long startup() => 0;
        public long shutdown() => 0;
        public long onEmitStatus(long l, string? s) => 0;
    }
}
