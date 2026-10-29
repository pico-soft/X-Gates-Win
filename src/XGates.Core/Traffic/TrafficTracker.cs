using System.Text.Json.Serialization;
using XGates.Core.Platform;

namespace XGates.Core.Traffic;

/// <summary>Дневная корзина: раздельно ПРИЁМ/ОТДАЧА туннеля и ТЕСТОВЫЙ трафик (пробники фазы скорости).</summary>
public sealed record DayBucket
{
    [JsonPropertyName("tunnelRx")] public long tunnelRx { get; init; }   // принято через активный прокси
    [JsonPropertyName("tunnelTx")] public long tunnelTx { get; init; }   // отдано через активный прокси
    [JsonPropertyName("test")] public long test { get; init; }           // скачано пробниками замера скорости

    public DayBucket() { }
    public DayBucket(long tunnelRx, long tunnelTx, long test) { this.tunnelRx = tunnelRx; this.tunnelTx = tunnelTx; this.test = test; }

    public long total() => tunnelRx + tunnelTx + test;
    public bool isEmpty() => tunnelRx == 0L && tunnelTx == 0L && test == 0L;
}

/**
 * @param days      история по дням: сумма = «за 30 дней» + список по дням. Сбрасывается ТОЛЬКО «За 30 дней».
 * @param todayDate дата НЕЗАВИСИМОГО суточного счётчика
 * @param today     «За сутки» — ОТДЕЛЬНЫЙ счётчик (не производный от days), сбрасывается ТОЛЬКО «За сутки».
 * Три счётчика независимы: сброс одного не трогает другие (требование Elyor).
 */
public sealed record TrafficFile
{
    [JsonPropertyName("days")] public IReadOnlyDictionary<string, DayBucket> days { get; init; } = new Dictionary<string, DayBucket>();
    [JsonPropertyName("todayDate")] public string todayDate { get; init; } = "";
    [JsonPropertyName("today")] public DayBucket today { get; init; } = new();
}

/// <summary>Снимок для UI: сессия «с последнего запуска» + сегодня + сумма 30 дней + список по дням.</summary>
public sealed record TrafficSnapshot
{
    public TrafficSnapshot() { }

    public TrafficSnapshot(long sessionStartElapsed, long sessionRx, long sessionTx, long sessionTest, bool proxyActive,
        long proxyStartElapsed, long proxyEndElapsed, DayBucket today, DayBucket total30, IReadOnlyList<(string date, DayBucket bucket)> days)
    {
        this.sessionStartElapsed = sessionStartElapsed; this.sessionRx = sessionRx; this.sessionTx = sessionTx; this.sessionTest = sessionTest;
        this.proxyActive = proxyActive; this.proxyStartElapsed = proxyStartElapsed; this.proxyEndElapsed = proxyEndElapsed;
        this.today = today; this.total30 = total30; this.days = days;
    }

    public long sessionStartElapsed { get; init; }   // точка отсчёта СЕССИИ (запуск приложения / последний сброс) — И байты, И таймер
    public long sessionRx { get; init; }
    public long sessionTx { get; init; }
    public long sessionTest { get; init; }
    // ОТДЕЛЬНО «Прокси активен» — время работы туннеля, не путать с временем сессии приложения.
    public bool proxyActive { get; init; }
    public long proxyStartElapsed { get; init; }
    public long proxyEndElapsed { get; init; }
    public DayBucket today { get; init; } = new();
    public DayBucket total30 { get; init; } = new();
    public IReadOnlyList<(string date, DayBucket bucket)> days { get; init; } = [];  // свежие сверху
}

/// <summary>Персистенция дневных корзин (traffic.json в filesDir).</summary>
internal static class TrafficStore
{
    private const string TAG = "TrafficStore";
    private const string FILE = "traffic.json";
    private const string TMP = "traffic.json.tmp";

    public static TrafficFile load(Context context)
    {
        var f = Path.Combine(context.filesDir, FILE);
        if (!File.Exists(f)) return new TrafficFile();
        try { return KJson.decodeFromString<TrafficFile>(File.ReadAllText(f), KJson.Compact); }
        catch (Exception e) { Log.w(TAG, "load failed", e); return new TrafficFile(); }
    }

    public static void save(Context context, TrafficFile data)
    {
        try { KJson.atomicWrite(Path.Combine(context.filesDir, FILE), Path.Combine(context.filesDir, TMP), KJson.encodeToString(data, KJson.Compact)); }
        catch (Exception e) { Log.w(TAG, "save failed", e); }
    }
}

/**
 * Учёт трафика ДВУМЯ раздельными потоками: ТУННЕЛЬНЫЙ (через активный прокси, из core queryStats)
 * и ТЕСТОВЫЙ (пробники фазы скорости, из всего_байт). Никогда не смешиваются. Дневные корзины
 * (локальная полночь), хранение 30 дней. Сессия «с последнего запуска» живёт в памяти (переживает
 * сворачивание, не переживает перезапуск процесса). На диск — раз в ~20с и при остановке.
 * Порт 1:1 с traffic/TrafficTracker.kt.
 */
public static class TrafficTracker
{
    private const int RETAIN_DAYS = 30;
    private const long PERSIST_INTERVAL_MS = 20_000L;

    private static readonly object @lock = new();
    private static Dictionary<string, DayBucket> days = new();   // история (30д + список), НЕЗАВИСИМА от суточного
    private static string todayDate = "";                        // дата независимого суточного счётчика
    private static long todayRx, todayTx, todayTest;
    // Сессия «с последнего запуска»: отсчёт с запуска приложения / последнего сброса. Пополняется БЕЗУСЛОВНО.
    private static long sessionStartElapsed;
    private static long sessionRx, sessionTx, sessionTest;
    // «Прокси активен» — отдельная величина (время работы туннеля).
    private static bool proxyActive;
    private static long proxyStartElapsed, proxyEndElapsed;
    private static long lastPersistElapsed;
    private static Context? appContext;

    private static readonly MutableStateFlow<TrafficSnapshot> _state = new(new TrafficSnapshot());
    public static StateFlow<TrafficSnapshot> state => _state.asStateFlow();

    private static void loadFrom(TrafficFile file)
    {
        days = new Dictionary<string, DayBucket>(file.days);
        todayDate = file.todayDate;
        todayRx = file.today.tunnelRx; todayTx = file.today.tunnelTx; todayTest = file.today.test;
    }

    private static void ensureSessionStarted()
    {
        if (sessionStartElapsed == 0L) sessionStartElapsed = SystemClock.elapsedRealtime();
    }

    /** Загрузить историю + суточный счётчик, отсечь старше 30 дней. Вызвать при старте приложения. */
    public static void init(Context context)
    {
        lock (@lock)
        {
            appContext = context.applicationContext;
            loadFrom(TrafficStore.load(appContext));
            ensureSessionStarted();   // отсчёт сессии = запуск приложения
            prune();
            publish();
        }
    }

    /** Гарантировать контекст (если сервис стартовал раньше UI). Не перетирает уже загруженное. */
    public static void attachContext(Context context)
    {
        lock (@lock)
        {
            if (appContext == null)
            {
                appContext = context.applicationContext;
                loadFrom(TrafficStore.load(appContext));
                prune();
            }
            ensureSessionStarted();
            publish();
        }
    }

    /** Прокси поднялся — только отметка «активен» (сессию НЕ трогаем: она считается безусловно). */
    public static void onServiceStart()
    {
        lock (@lock)
        {
            proxyActive = true;
            proxyStartElapsed = SystemClock.elapsedRealtime();
            proxyEndElapsed = 0;
            lastPersistElapsed = SystemClock.elapsedRealtime();
            publish();
        }
    }

    /** Прокси остановлен — зафиксировать его аптайм, сохранить. Сессию НЕ трогаем. */
    public static void onServiceStop()
    {
        lock (@lock)
        {
            proxyActive = false;
            proxyEndElapsed = SystemClock.elapsedRealtime();
            persist();
            publish();
        }
    }

    /** Сброс суточного счётчика при смене локальной даты (иначе за сутки покажет вчерашнее). */
    private static void rollToday()
    {
        var k = today();
        if (todayDate != k) { todayDate = k; todayRx = 0; todayTx = 0; todayTest = 0; }
    }

    /** Дельты туннеля из core queryStats (приём/отдача). Обновляют ТРИ счётчика БЕЗУСЛОВНО из ОДНОЙ дельты. */
    public static void addTunnel(long rx, long tx)
    {
        lock (@lock)
        {
            long r = rx.coerceAtLeast(0), t = tx.coerceAtLeast(0);
            if (r > 0 || t > 0)
            {
                ensureSessionStarted();
                var k = today();
                var b = days.GetValueOrDefault(k) ?? new DayBucket();
                days[k] = b with { tunnelRx = b.tunnelRx + r, tunnelTx = b.tunnelTx + t };  // история/30д
                rollToday(); todayRx += r; todayTx += t;                                   // «за сутки»
                sessionRx += r; sessionTx += t;                                            // сессия — БЕЗУСЛОВНО
                maybePersist();
                publish();
            }
        }
    }

    /** Тестовый трафик (всего_байт пробника). Обновляет ТРИ счётчика БЕЗУСЛОВНО (полный тест идёт без сервиса!). */
    public static void addTest(long bytes)
    {
        lock (@lock)
        {
            if (bytes > 0)
            {
                ensureSessionStarted();
                var k = today();
                var b = days.GetValueOrDefault(k) ?? new DayBucket();
                days[k] = b with { test = b.test + bytes };      // история/30д
                rollToday(); todayTest += bytes;                 // «за сутки»
                sessionTest += bytes;                            // сессия — БЕЗУСЛОВНО
                maybePersist();
                publish();
            }
        }
    }

    // Три кнопки — три НЕЗАВИСИМЫХ счётчика: сброс одного не трогает другие.

    /** «С последнего запуска»: обнулить ТОЛЬКО счётчики сессии и её таймер (общая точка отсчёта). */
    public static void resetSession()
    {
        lock (@lock)
        {
            sessionRx = 0; sessionTx = 0; sessionTest = 0;
            sessionStartElapsed = SystemClock.elapsedRealtime();   // байты И таймер — от одной точки
            publish();
        }
    }

    /** «За сутки»: обнулить ТОЛЬКО суточный счётчик. Историю (30д/список) и сессию НЕ трогаем. Диск немедленно. */
    public static void resetToday()
    {
        lock (@lock)
        {
            todayDate = today(); todayRx = 0; todayTx = 0; todayTest = 0;
            persist();
            publish();
        }
    }

    /** «За 30 дней»: стереть ТОЛЬКО историю (все корзины + список). Суточный счётчик и сессию НЕ трогаем. Диск немедленно. */
    public static void resetAll()
    {
        lock (@lock)
        {
            days.Clear();
            persist();
            publish();
        }
    }

    // --- внутреннее ---

    private static void maybePersist()
    {
        if (SystemClock.elapsedRealtime() - lastPersistElapsed >= PERSIST_INTERVAL_MS) persist();
    }

    private static void persist()
    {
        var ctx = appContext;
        if (ctx == null) return;
        lastPersistElapsed = SystemClock.elapsedRealtime();
        prune();
        TrafficStore.save(ctx, new TrafficFile
        {
            days = new Dictionary<string, DayBucket>(days),
            todayDate = todayDate,
            today = new DayBucket(todayRx, todayTx, todayTest),
        });
    }

    private static void prune()
    {
        var cutoff = dateKeyDaysAgo(RETAIN_DAYS - 1);   // храним последние 30 дней включая сегодня
        foreach (var k in days.Keys.Where(it => string.CompareOrdinal(it, cutoff) < 0).ToList()) days.Remove(k);
    }

    private static void publish()
    {
        // «За сутки» = независимый суточный счётчик (0, если его дата — не сегодня, т.е. новый день без трафика).
        var todayB = todayDate == today() ? new DayBucket(todayRx, todayTx, todayTest) : new DayBucket();
        // «За 30 дней» = сумма истории days.
        long rx = 0, tx = 0, test = 0;
        foreach (var b in days.Values) { rx += b.tunnelRx; tx += b.tunnelTx; test += b.test; }
        var list = days.OrderByDescending(it => it.Key, StringComparer.Ordinal).Select(it => (it.Key, it.Value)).ToList();
        _state.value = new TrafficSnapshot(
            sessionStartElapsed: sessionStartElapsed,
            sessionRx: sessionRx, sessionTx: sessionTx, sessionTest: sessionTest,
            proxyActive: proxyActive, proxyStartElapsed: proxyStartElapsed, proxyEndElapsed: proxyEndElapsed,
            today: todayB, total30: new DayBucket(rx, tx, test), days: list);
    }

    private static string today() => KDate.now(KDate.DAY);   // локальная таймзона

    private static string dateKeyDaysAgo(int n) => KDate.format(KDate.DAY, DateTime.Now.AddDays(-n));
}
