using System.Text;
using System.Text.RegularExpressions;
using XGates.Core.Compat;
using XGates.Core.Monitor;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;

namespace XGates.Core.Subscription;

/// <summary>Итог обновления одного источника.</summary>
public sealed record RefreshSummary
{
    public RefreshSummary(bool ok, int added = 0, int unsupported = 0, int invalid = 0, int duplicates = 0, string? error = null,
        SourceOutcome? outcome = null, bool rateLimited = false, int? retryAfterSec = null)
    {
        this.ok = ok; this.added = added; this.unsupported = unsupported; this.invalid = invalid; this.duplicates = duplicates;
        this.error = error; this.outcome = outcome ?? (ok ? SourceOutcome.OK : SourceOutcome.ERROR);
        this.rateLimited = rateLimited; this.retryAfterSec = retryAfterSec;
    }

    public bool ok { get; init; }
    public int added { get; init; }
    public int unsupported { get; init; }   // hysteria2/tuic… — распознаны, но не наше ядро
    public int invalid { get; init; }       // битые/неизвестные строки
    public int duplicates { get; init; }    // повторы в самом источнике — отброшены
    public string? error { get; init; }
    public SourceOutcome outcome { get; init; }
    public bool rateLimited { get; init; }  // HTTP 429 — панель ограничивает частоту (Промпт 81.A)
    public int? retryAfterSec { get; init; } // из Retry-After (когда можно повторить)
}

/// <summary>Итог склейки при импорте (факт-проверка дедупа между источниками).</summary>
public sealed record MergeStats(
    int incoming,          // распознано в этом импорте
    int newInRegistry,     // новых записей добавлено в общий реестр
    int mergedExisting,    // совпало с уже существующими serverKey (склейка, не дубль)
    int registryTotal);    // всего записей в реестре после

/**
 * Оркестратор МУЛЬТИподписок поверх [SubscriptionStore] ([SourcesFile]).
 * Серверы склеиваются по [serverKey] в общий реестр; членство — список источников.
 * Сетевые методы БЛОКИРУЮЩИЕ — вызывать в фоне. Порт 1:1 с subscription/SubscriptionManager.kt.
 */
public static partial class SubscriptionManager
{
    private const string TAG = "SubscriptionManager";

    /** Дефолтная подписка «из коробки» (Промпт 72). Публичный список конфигов для обхода в РФ. */
    public const string DEFAULT_SOURCE_URL = "https://raw.githack.com/igareck/vpn-configs-for-russia/main/BLACK_VLESS_RUS_mobile.txt";
    public const string DEFAULT_SOURCE_NAME = "Обход ограничений в РФ по-умолчанию";

    /** ВТОРОЙ дефолт (igareck BLACK_SS+All): ДРУГОЙ пул — ~45 trojan-серверов, почти все на порту 443 (работают на
     *  сетях, режущих нестандартные порты, напр. МегаФон). Добавлен, т.к. основной vless-пул часто деградирует
     *  (мало живых); второй независимый пул удваивает кандидатов. Сеется ВКЛ, с тем же каскадом зеркал (igareck). */
    public const string BLACK_SS_URL = "https://raw.githack.com/igareck/vpn-configs-for-russia/main/BLACK_SS+All_RUS.txt";
    public const string BLACK_SS_NAME = "Обход в РФ — доп. пул (Trojan)";

    /** Дефолтная подписка «Yzewe» — ВКЛючена по умолчанию, добавляется всем (свежим и существующим при обновлении). */
    public const string YZEWE_SOURCE_URL = "https://vpn.yzewe.ru/sub";
    public const string YZEWE_SOURCE_NAME = "Yzewe";

    /** Пр.140: источники «белого списка РФ» — сеются ВЫКЛ, авто-обновляются раз в полчаса (безакцептно), используются
     *  только в режиме белых списков (или если юзер включит их вручную). Дедуп по URL (не плодим дубли).
     *  `WHITE-CIDR-RU-all.txt` вопреки имени содержит рабочие vless-конфиги. В режиме белых списков прямой путь к
     *  githack закрыт — обновление идёт ЧЕРЕЗ ТУННЕЛЬ рабочего белого сервера (CascadeFetch, ступень SOCKS).
     *  ⛔ Источник `…-Mobile-2.txt` (ВЕЧНО 404 на GitHub) убран из дефолта (просьба Elyor) — см. [DEAD_WHITE_LIST_URL]. */
    public static readonly IReadOnlyList<(string name, string url)> WHITE_LIST_SOURCES =
    [
        ("Серверы для белых списков РФ", "https://raw.githack.com/igareck/vpn-configs-for-russia/main/Vless-Reality-White-Lists-Rus-Mobile.txt"),
        ("Белые списки РФ (CIDR)", "https://raw.githack.com/igareck/vpn-configs-for-russia/main/WHITE-CIDR-RU-all.txt"),
    ];

    /** Вечно-мёртвый (404) источник белого списка — убран из дефолта. Чистим его и у существующих установок (init),
     *  т.к. он был засеян ensureWhiteListSources ранее. Сравниваем по хвосту файла (устойчиво к normalizeUrl). */
    private const string DEAD_WHITE_LIST_URL = "Vless-Reality-White-Lists-Rus-Mobile-2.txt";

    /** Пр.136: состояние посева дефолтной подписки для UI. [attempted]=пробовали хоть раз; [error] пуст = успех/не
     *  пробовали, непуст = последняя попытка не удалась (показываем причину + «Повторить», не пустой экран). */
    public sealed record SeedStatus(bool attempted = false, string error = "");
    private static readonly MutableStateFlow<SeedStatus> _seedStatus = new(new SeedStatus());
    public static StateFlow<SeedStatus> seedStatus => _seedStatus.asStateFlow();

    /**
     * Ключ ПОЛНОЙ идентичности соединения — дедуп внутри источника, склейка МЕЖДУ источниками,
     * привязка pingMs/speed, определение активного сервера. НЕ только addr+port+cred (иначе
     * склеятся транспортно/fp-разные варианты — это разные конфиги). Исключены динамика/метки.
     */
    public static string serverKey(ServerProfile p) => string.Join("|",
        p.protocol.ToString(), p.address, p.port.ToString(System.Globalization.CultureInfo.InvariantCulture), p.credential,
        p.method ?? "", p.flow ?? "",
        p.security, p.sni ?? "", p.fingerprint ?? "", p.alpn ?? "",
        p.network, p.path ?? "", p.hostHeader ?? "", p.serviceName ?? "",
        p.publicKey ?? "", p.shortId ?? "", p.spiderX ?? "");

    // ─────────────────────────── миграция (однократно) ───────────────────────────

    /**
     * Однократная инициализация (СИНХРОННО, без сети). Миграция старого `subscriptions.json`; затем: если у
     * юзера УЖЕ есть подписки — помечаем «посеяно», чтобы дефолт никогда не добавлялся авто. Сетевой посев
     * ПУСТОГО списка — отдельно и по ФАКТУ фетча ([trySeedDefaultSource], из автозапуска, в фоне).
     */
    public static void init(Context context)
    {
        var cur = SubscriptionStore.load(context);
        if (!cur.migratedLegacy)
        {
            var legacy = SubscriptionStore.readLegacy(context);
            var migrated = legacy.Count > 0 ? convertLegacy(legacy) : freshDefault();
            SubscriptionStore.save(context, recount(migrated));
        }
        var f = SubscriptionStore.load(context);
        if (!f.seededDefaultRuBypass && f.sources.Count > 0)
            SubscriptionStore.save(context, f with { seededDefaultRuBypass = true });
        // Пр.140: добавить источники белого списка (ВЫКЛ) существующим установкам, у кого их ещё нет. Только
        // метаданные (URL) — тела подтянет авто-рефреш раз в полчаса. На свежей установке их сеет trySeedDefaultSource.
        runCatching(() => ensureWhiteListSources(context));
        // Убрать вечно-мёртвый (404) источник белого списка `…-Mobile-2.txt` у существующих установок (просьба Elyor).
        runCatching(() => pruneDeadWhiteListSources(context));
        // Yzewe: добавить дефолтную подписку (ВКЛ) существующим установкам — разово (флаг seededYzewe). Тела
        // подтянет ближайший рефреш (автозапуск/«Самый быстрый»/авто раз в полчаса). На свежей — в trySeedDefaultSource.
        runCatching(() => ensureYzewe(context));
        // Разовая починка бага посева (0.52–0.59): вернуть пропущенный igareck-дефолт (BLACK mobile) пострадавшим,
        // у кого на свежей установке остался только Yzewe. ВАЖНО: ДО ensureBlackSs — иначе добавленный BLACK_SS
        // ломает отпечаток «только Yzewe» и починка не сработает. Тело подтянет ближайший рефреш enabled-источников.
        runCatching(() => healMissingBlackDefault(context));
        // Второй BLACK-дефолт (trojan-пул на 443) существующим установкам — разово. Другой пул на случай деградации основного.
        runCatching(() => ensureBlackSs(context));
        // Промпт 85.E: самопроверка — неполные (осиротевшие) профили пересобрать из сырья + записать в журнал.
        runCatching(() => verifyAndHeal(context));
        // Пр.136: восстановить статус посева для UI (переживает перезапуск) — чтобы карточка «не загрузилось»
        // и причина были видны сразу после старта, а не только в сессии, где случилась неудача.
        var ff = SubscriptionStore.load(context);
        _seedStatus.value = new SeedStatus(attempted: ff.seedLastAttemptTs.IsNotBlank() || ff.seededDefaultRuBypass, error: ff.seedLastError);
    }

    private static void runCatching(Action a)
    {
        try { a(); } catch (Exception) { }
    }

    /**
     * Промпт 74: дефолтную подписку «Обход ограничений в РФ» добавляем ТОЛЬКО если её URL реально
     * ЗАФЕТЧИЛСЯ (первый запуск). Не зафетчился → список остаётся ПУСТЫМ (в UI «Добавьте вашу подписку»),
     * флаг НЕ ставим → повторим на следующем холодном старте. Сеть → вызывать в ФОНЕ.
     * Возвращает true, если только что добавила+импортировала (чтобы автозапуск не рефетчил повторно).
     * Идемпотентно: не сеем, если уже посеяно ИЛИ у юзера уже есть подписки (флаг [seededDefaultRuBypass]
     * не даёт дефолту вернуться после удаления пользователем).
     */
    public static bool trySeedDefaultSource(Context context)
    {
        var file = SubscriptionStore.load(context);
        if (file.seededDefaultRuBypass) return false;
        if (file.sources.Count > 0)                       // у юзера уже есть свои → дефолт не навязываем
        {
            SubscriptionStore.save(context, file with { seededDefaultRuBypass = true });
            _seedStatus.value = new SeedStatus(attempted: true, error: "");
            return false;
        }
        var settings = SettingsStore.current();
        var url = normalizeUrl(DEFAULT_SOURCE_URL);
        var directT = settings.subTimeoutSec * 1000;
        var proxyT = settings.subTimeoutSec * 1000 + 10_000;
        var totalT = directT + proxyT + 5_000;
        var cascade = cascadeWithMirrors(context, url, settings.subUserAgent, directT, proxyT, totalT,
            acceptBody: it => it.ok && hasSupportedLinks(it.body));
        // Пр.136: НЕУДАЧА не помечается как выполненная (флаг seededDefaultRuBypass НЕ ставим → повторим позже),
        // но фиксируем ПРИЧИНУ в лог + persist + StateFlow — чтобы человек видел «список не загрузился, почему»,
        // а не пустой экран. На свежей установке работает только прямая ступень (туннеля и известных серверов ещё нет).
        if (!cascade.ok)
        {
            var reason = seedFailReason(cascade);
            Log.w(TAG, $"посев дефолтной подписки не удался: {reason}");
            SubscriptionStore.save(context, SubscriptionStore.load(context) with { seedLastError = reason, seedLastAttemptTs = now() });
            _seedStatus.value = new SeedStatus(attempted: true, error: reason);
            return false;
        }
        var f2 = SubscriptionStore.load(context);               // перечитать (гонка) — вдруг юзер успел добавить
        if (f2.seededDefaultRuBypass || f2.sources.Count > 0) { _seedStatus.value = new SeedStatus(attempted: true, error: ""); return false; }
        var id = newId();
        // Пр.140: вместе с чёрным списком (ВКЛ) сеем источники белого списка (ВЫКЛ, whiteList=true) — тела
        // подтянет авто-рефреш; в обычном режиме они не мешают (выключены), в белом — включатся эффективно.
        var whiteSources = WHITE_LIST_SOURCES.Select(p =>
            new SubSource { id = newId(), name = p.name, url = normalizeUrl(p.url), enabled = false, whiteList = true }).ToList();
        // Yzewe (ВКЛ) сеем ВМЕСТЕ с дефолтом — тело подтянет ближайший рефреш enabled-источников (автозапуск).
        var yzewe = new SubSource { id = newId(), name = YZEWE_SOURCE_NAME, url = normalizeUrl(YZEWE_SOURCE_URL), enabled = true };
        // Второй BLACK-дефолт (trojan-пул на 443, ВКЛ) — другой независимый пул на случай деградации основного.
        var blackSs = new SubSource { id = newId(), name = BLACK_SS_NAME, url = normalizeUrl(BLACK_SS_URL), enabled = true };
        SubscriptionStore.save(context, f2 with
        {
            seededDefaultRuBypass = true,
            seededYzewe = true,
            seededBlackSs = true,
            seedLastError = "",                                // успех — чистим прежнюю ошибку
            sources = new List<SubSource> { new() { id = id, name = DEFAULT_SOURCE_NAME, url = url, enabled = true }, yzewe, blackSs }.Concat(whiteSources).ToList(),
        });
        importInto(context, id, cascade.result!.body, "Дефолтная подписка (первый успешный фетч)");
        _seedStatus.value = new SeedStatus(attempted: true, error: "");
        return true;
    }

    /** Пр.140: добавить недостающие источники белого списка (ВЫКЛ) — для установок, где чёрный список уже был.
     *  Идемпотентно: сверяем по URL, дубли не плодим. Только метаданные (без сети) — тела подтянет авто-рефреш. */
    public static void ensureWhiteListSources(Context context)
    {
        var file = SubscriptionStore.load(context);
        if (file.sources.Count == 0) return;                     // свежая установка — засеет trySeedDefaultSource
        var haveUrls = file.sources.Select(it => it.url).ToHashSet();
        var missing = WHITE_LIST_SOURCES
            .Where(p => !haveUrls.Contains(normalizeUrl(p.url)))
            .Select(p => new SubSource { id = newId(), name = p.name, url = normalizeUrl(p.url), enabled = false, whiteList = true })
            .ToList();
        if (missing.Count == 0) return;
        SubscriptionStore.save(context, file with { sources = file.sources.Concat(missing).ToList() });
        Log.i(TAG, $"добавлены источники белого списка (ВЫКЛ): {missing.Count}");
    }

    /** Убрать ВЕЧНО-МЁРТВЫЙ (404) источник белого списка [DEAD_WHITE_LIST_URL] у существующих установок (просьба
     *  Elyor). Идемпотентно: удаляем источник, чей URL оканчивается на мёртвый файл. На свежей — его и не сеем. */
    public static void pruneDeadWhiteListSources(Context context)
    {
        var file = SubscriptionStore.load(context);
        var kept = file.sources.Where(it => !(it.whiteList && it.url.EndsWith(DEAD_WHITE_LIST_URL, StringComparison.Ordinal))).ToList();
        if (kept.Count == file.sources.Count) return;
        SubscriptionStore.save(context, file with { sources = kept });
        Log.i(TAG, $"убран вечно-мёртвый источник белого списка: {file.sources.Count - kept.Count}");
    }

    /** Yzewe: добавить дефолтную подписку (ВКЛ) РАЗОВО, если ещё не добавляли (флаг seededYzewe) и её URL ещё нет.
     *  Уважает удаление пользователем (флаг остаётся → повторно не вернётся). Только метаданные — тело подтянет
     *  ближайший рефреш enabled-источников. Идемпотентно. */
    public static void ensureYzewe(Context context)
    {
        var file = SubscriptionStore.load(context);
        if (file.seededYzewe) return;
        // КРИТИЧНО (баг посева 0.52–0.59): на СВЕЖЕЙ установке (пустой список) НЕ добавляем Yzewe здесь —
        // иначе trySeedDefaultSource увидит «список не пуст» и пропустит посев igareck-дефолта (BLACK) + белых,
        // оставив ТОЛЬКО Yzewe. Свежую установку целиком сеет trySeedDefaultSource (там Yzewe идёт ВМЕСТЕ с BLACK).
        // ensureYzewe — ТОЛЬКО back-add для СУЩЕСТВУЮЩИХ установок (как ensureWhiteListSources, тот же гейт).
        if (file.sources.Count == 0) return;
        var url = normalizeUrl(YZEWE_SOURCE_URL);
        var already = file.sources.Any(it => it.url == url);
        var sources = already ? file.sources
                      : file.sources.Append(new SubSource { id = newId(), name = YZEWE_SOURCE_NAME, url = url, enabled = true }).ToList();
        SubscriptionStore.save(context, file with { seededYzewe = true, sources = sources });
        if (!already) Log.i(TAG, "добавлена дефолтная подписка Yzewe (ВКЛ)");
    }

    /** Второй BLACK-дефолт (igareck BLACK_SS+All, trojan-пул на 443): добавить РАЗОВО существующим установкам (флаг
     *  seededBlackSs) — другой независимый пул на случай деградации основного. Как ensureYzewe: только для НЕпустого
     *  списка (свежую сеет trySeedDefaultSource), уважает удаление юзером (флаг), дедуп по URL, тело подтянет рефреш. */
    public static void ensureBlackSs(Context context)
    {
        var file = SubscriptionStore.load(context);
        if (file.seededBlackSs) return;
        if (file.sources.Count == 0) return;                     // свежая установка — засеет trySeedDefaultSource
        var url = normalizeUrl(BLACK_SS_URL);
        var already = file.sources.Any(it => it.url == url);
        var sources = already ? file.sources
                      : file.sources.Append(new SubSource { id = newId(), name = BLACK_SS_NAME, url = url, enabled = true }).ToList();
        SubscriptionStore.save(context, file with { seededBlackSs = true, sources = sources });
        if (!already) Log.i(TAG, "добавлен второй BLACK-дефолт (trojan-пул на 443, ВКЛ)");
    }

    /**
     * РАЗОВАЯ ПОЧИНКА бага посева (0.52–0.59): из-за гонки ensureYzewe→trySeedDefaultSource на СВЕЖЕЙ установке
     * оставался ТОЛЬКО Yzewe — igareck-дефолт (BLACK mobile) НЕ посеялся, хотя seededDefaultRuBypass=true. Чиним
     * у ПОСТРАДАВШИХ: если установка явно авто-засеяна (seededYzewe && seededDefaultRuBypass), список НЕ пуст, но
     * BLACK-дефолта в нём НЕТ — back-add BLACK (ВКЛ); тело подтянет ближайший рефреш. Флаг [blackDefaultHealed] →
     * ровно один раз (уважаем последующее удаление юзером). На чистых/правильно-засеянных установках — no-op.
     */
    public static void healMissingBlackDefault(Context context)
    {
        var file = SubscriptionStore.load(context);
        if (file.blackDefaultHealed) return;
        var blackUrl = normalizeUrl(DEFAULT_SOURCE_URL);
        var hasBlack = file.sources.Any(it => it.url == blackUrl);
        // ТОЧНЫЙ отпечаток бага: авто-засеяно (seededYzewe && seededDefaultRuBypass), BLACK-дефолта нет, и
        // ЕДИНСТВЕННАЯ «настоящая» (не-белая) подписка — это ровно Yzewe. Так не трогаем того, кто ДОБАВИЛ свои
        // подписки и удалил дефолт НАМЕРЕННО (у него среди не-белых будет не только Yzewe) — уважаем его выбор.
        var nonWhite = file.sources.Where(it => !it.whiteList).ToList();
        var onlyYzewe = nonWhite.Count == 1 && nonWhite[0].url == normalizeUrl(YZEWE_SOURCE_URL);
        var affected = file.seededYzewe && file.seededDefaultRuBypass && !hasBlack && onlyYzewe;
        if (!affected)
        {
            SubscriptionStore.save(context, file with { blackDefaultHealed = true });
            return;
        }
        var black = new SubSource { id = newId(), name = DEFAULT_SOURCE_NAME, url = blackUrl, enabled = true };
        SubscriptionStore.save(context, file with { blackDefaultHealed = true, sources = file.sources.Append(black).ToList() });
        Log.i(TAG, "починка: back-add igareck-дефолта (BLACK mobile) — был пропущен из-за гонки посева Yzewe");
    }

    /** Пр.140: id источников белого списка. */
    public static HashSet<string> whiteListSourceIds(Context context) =>
        SubscriptionStore.load(context).sources.Where(it => it.whiteList).Select(it => it.id).ToHashSet();

    /** Пр.141: ПОСЛЕДНИЙ ШАНС при «рабочих серверов нет» — включить выключенные источники белого списка (они
     *  сеются ВЫКЛ) для обычного режима, чтобы дать связь любой ценой (Пр.95). Возвращает сколько включили. */
    public static int enableWhiteListForFallback(Context context)
    {
        var disabled = SubscriptionStore.load(context).sources.Where(it => it.whiteList && !it.enabled && it.url.IsNotBlank()).ToList();
        foreach (var it in disabled) setEnabled(context, it.id, true);
        return disabled.Count;
    }

    /** Пр.140: авто-обновление источников белого списка (раз в полчаса, безакцептно). Возвращает сколько ОК. */
    public static int refreshWhiteListSources(Context context)
    {
        var ids = SubscriptionStore.load(context).sources.Where(it => it.whiteList && it.url.IsNotBlank()).Select(it => it.id).ToList();
        var ok = 0;
        foreach (var id in ids)
        {
            bool r;
            try { r = refreshOne(context, id).ok; } catch (Exception) { r = false; }
            if (r) ok++;
        }
        return ok;
    }

    /** Пр.136: короткая причина неудачи посева (последняя реальная попытка каскада) — для показа человеку. */
    private static string seedFailReason(CascadeResult cascade)
    {
        var r = cascade.attempts.LastOrDefault(it => !it.skipped && it.result != null)?.result;
        if (r == null) return "нет сети или прямой путь к адресу подписки заблокирован (туннеля ещё нет)";
        return r.exceptionClass != null
            ? $"нет связи с адресом подписки ({r.exceptionClass})"
            : $"адрес подписки ответил HTTP {r.httpCode}";
    }

    /** Старые вложенные подписки → источники + реестр (измерения серверов сохраняются). */
    private static SourcesFile convertLegacy(List<Subscription> legacy)
    {
        var sources = new List<SubSource>();
        IReadOnlyList<ServerRecord> registry = [];
        foreach (var s in legacy)
        {
            var id = newId();
            sources.Add(new SubSource
            {
                id = id, name = s.name, url = s.url, enabled = true,
                lastRefreshTs = s.lastUpdateTs, lastOk = s.lastUpdateOk,
            });
            registry = mergeIntoRegistry(registry, id, s.servers).registry;  // s.servers несут измерения
        }
        return new SourcesFile { migratedLegacy = true, sources = sources, servers = registry };
    }

    /**
     * Свежая установка: ПУСТО (Промпт 74). Дефолтную подписку добавит [trySeedDefaultSource] из автозапуска —
     * но ТОЛЬКО если её URL реально зафетчится; иначе пусто + «Добавьте вашу подписку». seededDefaultRuBypass
     * оставляем false, чтобы посев повторился на следующем старте, пока не удастся (или пока юзер не добавит своё).
     * (Эволюция: П67 дефолт УБРАЛИ; П72 вернули безусловно; П74 — только по факту успешного фетча.)
     */
    private static SourcesFile freshDefault() => new()
    {
        migratedLegacy = true,
        seededDefaultRuBypass = false,
        sources = [],
        servers = [],
    };

    // ─────────────────────────── чтение ───────────────────────────

    /** Метаданные источников. */
    public static IReadOnlyList<SubSource> sources(Context context) => SubscriptionStore.load(context).sources;

    /** Плоский ДЕДУПЛИЦИРОВАННЫЙ список серверов из ВКЛЮЧЁННЫХ источников (для списка/Авто).
     *  Пр.140: в режиме белых списков (whiteListModeActive) — ТОЛЬКО серверы белых источников, независимо от их
     *  enabled-флага (флаги пользователя НЕ трогаем — это эффективный override, а не разрушающее выключение). */
    public static List<ServerProfile> allServers(Context context)
    {
        var file = SubscriptionStore.load(context);
        var useIds = SettingsStore.current().whiteListModeActive
            ? file.sources.Where(it => it.whiteList).Select(it => it.id).ToHashSet()
            : file.sources.Where(it => it.enabled).Select(it => it.id).ToHashSet();
        return file.servers.Where(rec => rec.sourceIds.Any(useIds.Contains)).Select(it => it.profile).ToList();
    }

    /** Сколько серверов ПОЯВИТСЯ, если включить выключенные источники (сейчас скрыты — нет активного источника). */
    public static int serversFromDisabled(Context context)
    {
        var file = SubscriptionStore.load(context);
        var enabled = file.sources.Where(it => it.enabled).Select(it => it.id).ToHashSet();
        var disabled = file.sources.Where(it => !it.enabled).Select(it => it.id).ToHashSet();
        return file.servers.Count(rec => !rec.sourceIds.Any(enabled.Contains) && rec.sourceIds.Any(disabled.Contains));
    }

    /** Включить ВСЕ выключенные источники (пункт E, по согласию пользователя). Возвращает их id. */
    public static List<string> enableAllDisabled(Context context)
    {
        var ids = SubscriptionStore.load(context).sources.Where(it => !it.enabled).Select(it => it.id).ToList();
        foreach (var id in ids) setEnabled(context, id, true);
        return ids;
    }

    /**
     * Пр.137: ЛУЧШИЙ сервер, пригодный для АВТО-восстановления — НЕ в стоп-листе, НЕ на паузе, протокол разрешён.
     * Приоритет: по известной скорости (быстрые первыми), затем по пингу (живые первыми). Нужен, когда
     * «последний сервер» оказался в стоп-листе: авто-подъём (автозапуск / после убийства системой / после
     * перезагрузки) НЕ должен поднимать заблокированный сервер. null — годной замены нет.
     */
    public static ServerProfile? bestSavedSelectable(Context context)
    {
        var s = SettingsStore.current();
        var bl = BlocklistStore.current();
        return allServers(context)
            .Where(it => ServerFilter.protocolAllowed(it, s) && !ServerFilter.isBlocked(it, bl) && !ServerFilter.isPaused(it, bl))
            .OrderByDescending(it => it.speedMbps ?? 0.0).ThenBy(it => it.pingMs ?? int.MaxValue)   // стабильная сортировка, как sortedWith
            .FirstOrDefault();
    }

    /**
     * Серверы, ПОДТВЕРЖДЁННО работавшие за последние [withinHours] часов (speedMbps>0 и свежий
     * speedTestedTs), по убыванию известной скорости, через ЕДИНЫЙ предикат (протокол + стоп-лист).
     * Для ступени 6 каскада ([CascadeFetch]) — обновить подписку через temp-инстанс, когда прямой путь
     * заблокирован, а активный туннель упал.
     */
    public static List<ServerProfile> recentWorkingServers(Context context, int withinHours = 24)
    {
        var settings = SettingsStore.current();
        var bl = BlocklistStore.current();
        var cutoff = JavaSystem.currentTimeMillis() - withinHours * 3600_000L;
        return allServers(context)
            .Where(it => (it.speedMbps ?? 0.0) > 0.0)
            .Where(p => parseTs(p.speedTestedTs) is long t && t >= cutoff)
            .Where(it => ServerFilter.protocolAllowed(it, settings) && !ServerFilter.isBlocked(it, bl))
            .OrderByDescending(it => it.speedMbps ?? 0.0)
            .ToList();
    }

    private static long? parseTs(string? s) => s == null ? null : KDate.parseMs(s);

    // ─────────────────────────── применение замеров ───────────────────────────

    /** Обновить pingMs (ключ→мс) в реестре и сохранить ОДИН раз. */
    public static void applyPingResults(Context context, IReadOnlyDictionary<string, int> pingByKey)
    {
        if (pingByKey.Count == 0) return;
        var ts = now();
        var file = SubscriptionStore.load(context);
        var servers = file.servers.Select(rec =>
            pingByKey.TryGetValue(serverKey(rec.profile), out var ms)
                ? rec with { profile = rec.profile with { pingMs = ms, lastTestedTs = ts } }
                : rec).ToList();
        SubscriptionStore.save(context, file with { servers = servers });
    }

    /** Обновить speedMbps (ключ→Mbps) в реестре и сохранить ОДИН раз. speedFull НЕ трогаем (легаси/неизвестно). */
    public static void applySpeedResults(Context context, IReadOnlyDictionary<string, double> speedByKey)
    {
        if (speedByKey.Count == 0) return;
        var ts = now();
        var file = SubscriptionStore.load(context);
        var servers = file.servers.Select(rec =>
        {
            if (!speedByKey.TryGetValue(serverKey(rec.profile), out var mbps)) return rec;
            // Провал замера (-1) НЕ затирает прежнюю ВАЛИДНУЮ скорость (Пр.88: не терять данные при массовом
            // провале temp-инстансов). Обновляем измерение только валидным результатом (≥0) или если раньше данных не было.
            if (mbps < 0 && (rec.profile.speedMbps ?? -1.0) >= 0.0) return rec;
            return rec with { profile = rec.profile with { speedMbps = mbps, speedTestedTs = ts } };
        }).ToList();
        SubscriptionStore.save(context, file with { servers = servers });
    }

    /** Как [applySpeedResults], но с КАЧЕСТВОМ замера: ключ→(Mbps, full). full=true — полный/точный замер,
     *  false — минимальный/быстрый («≈» в UI). Для различения в «Живых» быстро- и полностью-померянных. */
    public static void applySpeedResultsTagged(Context context, IReadOnlyDictionary<string, (double mbps, bool full)> speedByKey)
    {
        if (speedByKey.Count == 0) return;
        var ts = now();
        var file = SubscriptionStore.load(context);
        var servers = file.servers.Select(rec =>
        {
            if (!speedByKey.TryGetValue(serverKey(rec.profile), out var pair)) return rec;
            // Тот же guard: провал (-1) не затирает прежнюю валидную скорость (и её качество).
            if (pair.mbps < 0 && (rec.profile.speedMbps ?? -1.0) >= 0.0) return rec;
            return rec with { profile = rec.profile with { speedMbps = pair.mbps, speedTestedTs = ts, speedFull = pair.full } };
        }).ToList();
        SubscriptionStore.save(context, file with { servers = servers });
    }

    // ─────────────────────────── управление источниками ───────────────────────────

    /**
     * Добавить источник по URL (нормализуем, дедуп по ТОЧНОМУ нормализованному url). Возвращает id или null.
     * NB (Промпт 81, уточнение): дубли РАЗНЫХ адресов, ведущих к одному списку (raw.githack.com и свой
     * домен) — НАМЕРЕННАЯ страховка (недоступен один путь — работает другой), их не блокируем и не помечаем.
     * Блокируем лишь ТОЧНОЕ повторное добавление одного и того же url.
     */
    public static string? addUrl(Context context, string url, string? name = null)
    {
        var u = normalizeUrl(url);
        if (u.Length == 0) return null;
        var file = SubscriptionStore.load(context);
        if (file.sources.Any(it => it.url == u)) return null;
        var id = newId();
        var src = new SubSource { id = id, url = u, name = name.TakeIfNotBlank() ?? nameFromUrl(u) };
        SubscriptionStore.save(context, file with { sources = file.sources.Append(src).ToList() });
        return id;
    }

    /** Локальный источник из вставленного текста/файла (без URL). Тот же разбор, что refresh, но оффлайн. */
    public static (string id, RefreshSummary summary) addLocalFromBody(Context context, string body, string? name = null)
    {
        var id = newId();
        var file = SubscriptionStore.load(context);
        var src = new SubSource { id = id, url = "", name = name.TakeIfNotBlank() ?? $"Вставка {now()}" };
        SubscriptionStore.save(context, file with { sources = file.sources.Append(src).ToList() });
        var detail = $"Локальная вставка: {body.Length} симв, похоже на {sniff(body)}";
        return (id, importInto(context, id, body, detail));
    }

    /** Переименовать источник. Имя persist и НЕ затирается обновлением (refresh не трогает name). */
    public static void rename(Context context, string id, string name)
    {
        var n = name.KTrim().IfBlank("Без имени");
        var file = SubscriptionStore.load(context);
        SubscriptionStore.save(context, file with { sources = file.sources.Select(it => it.id == id ? it with { name = n } : it).ToList() });
    }

    /**
     * Удалить источник. Промпт 85: реестр ПЕРЕСОБИРАЕМ из оставшихся источников (по их сырым телам), а НЕ
     * вычитаем записи на месте. Вычитание оставляло осиротевшие профили у пары-страховки (общий сервер
     * числился за второй подпиской, но с данными от удалённой первой → список есть, подключиться нельзя).
     */
    public static void remove(Context context, string id)
    {
        var file = SubscriptionStore.load(context);
        var raw = new Dictionary<string, string>(file.rawBodies);
        raw.Remove(id);
        var trimmed = file with
        {
            sources = file.sources.Where(it => it.id != id).ToList(),
            rawBodies = raw,
        };
        SubscriptionStore.save(context, recount(rebuildRegistry(trimmed)));
    }

    /** Сколько серверов ИСЧЕЗНЕТ при удалении источника (принадлежат только ему). */
    public static int serversLostOnRemove(Context context, string id) =>
        SubscriptionStore.load(context).servers.Count(it => it.sourceIds.Count == 1 && it.sourceIds[0] == id);

    /**
     * Вкл/выкл источник. Промпт 85: тоже ПЕРЕСОБИРАЕМ реестр (та же болезнь возможна и здесь). Данные
     * выключенного источника НЕ теряются — пересборка идёт из ВСЕХ источников с сырым телом (не только
     * включённых), а фильтр вкл/выкл применяется при ЧТЕНИИ ([allServers]) → включение обратно возвращает
     * полноценные записи без ручного переимпорта.
     */
    public static void setEnabled(Context context, string id, bool enabled)
    {
        var file = SubscriptionStore.load(context);
        var updated = file with { sources = file.sources.Select(it => it.id == id ? it with { enabled = enabled } : it).ToList() };
        SubscriptionStore.save(context, recount(rebuildRegistry(updated)));
    }

    // ─────────────────────────── обновление ───────────────────────────

    // Java: «\s» = [ \t\n\x0B\f\r], «.» — не терминатор строки; .find — поиск, не полное совпадение.
    [GeneratedRegex("igareck/vpn-configs-for-russia/(?:-/raw/)?(?:refs/heads/)?main/([^\\n\\r\\u0085\\u2028\\u2029]+?)[ \\t\\n\\x0B\\f\\r]*$")]
    private static partial Regex IgareckRel();

    /** Репо-относительный путь файла в igareck/vpn-configs-for-russia (после ветки main), или null — не наш репо.
     *  github/githack: …/igareck/vpn-configs-for-russia/main/&lt;rel&gt;; gitlab: …/-/raw/main/&lt;rel&gt;. */
    private static string? igareckRelPath(string url)
    {
        var m = IgareckRel().Match(url);
        return m.Success ? m.Groups[1].Value.KTrim().IfBlankNull() : null;
    }

    /**
     * ЗЕРКАЛА одного и того же файла подписки igareck на разных CDN. ЗАЧЕМ: githack (на нём сидят зашитые дефолты)
     * нестабилен — даёт стойкие TLS-таймауты, и подписка «не обновляется», хотя файл жив на GitHub. Возвращаем тот
     * же файл по нескольким CDN в порядке НАДЁЖНОСТИ (github raw → jsdelivr → githack) — это один и тот же
     * контент, не разные подписки (дублей не создаёт: фетчим ТЕЛО, сам источник/URL в списке не меняем). Для НЕ-igareck
     * URL (Yzewe, пользовательские) возвращаем только сам URL — поведение ровно прежнее. */
    internal static List<string> mirrorUrls(string url)
    {
        var rel = igareckRelPath(url);
        if (rel == null) return [url];
        // gitlab-зеркало убрано: его у автора НЕТ (HTTP 000, сверено 2026-10-29). Три рабочих CDN перекрывают отказы.
        return new List<string>
        {
            $"https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/{rel}",
            $"https://cdn.jsdelivr.net/gh/igareck/vpn-configs-for-russia@main/{rel}",
            $"https://raw.githack.com/igareck/vpn-configs-for-russia/main/{rel}",
        }.Distinct().ToList();
    }

    /**
     * Каскадный фетч С ЗЕРКАЛАМИ: пробуем кандидатов из [mirrorUrls] по очереди (для igareck — тот же файл на разных
     * CDN; иначе — один URL), ПЕРВЫЙ успешный каскад возвращаем. Все неуспешны → каскад ПЕРВОГО кандидата (связное
     * сообщение об ошибке). Каждый кандидат сам проходит прямой→SOCKS→temp (CascadeFetch). */
    private static CascadeResult cascadeWithMirrors(Context context, string url, string ua, int directT, int proxyT, int totalT,
        Func<FetchResult, bool> acceptBody)
    {
        var candidates = mirrorUrls(url);
        CascadeResult? first = null;
        foreach (var u in candidates)
        {
            var c = CascadeFetch.fetch(context, u, ua, directT, proxyT, totalT, acceptBody: acceptBody);
            first ??= c;
            if (c.ok)
            {
                if (u != candidates[0]) Log.i(TAG, $"подписка: зеркало сработало → {u}");
                return c;
            }
        }
        return first!;
    }

    /** Скачать + импортировать ОДИН источник по его url. БЛОКИРУЮЩИЙ. Локальные (url пустой) пропускаем. */
    public static RefreshSummary refreshOne(Context context, string id)
    {
        var settings = SettingsStore.current();
        var verbose = settings.verboseLogs;
        void log(string m) { if (verbose) Log.i(TAG, m); }

        var src = SubscriptionStore.load(context).sources.FirstOrDefault(it => it.id == id);
        if (src == null) return new RefreshSummary(ok: false, error: "источник не найден");
        if (src.url.IsBlank()) return new RefreshSummary(ok: false, error: "локальный источник — нечего скачивать");

        var url = normalizeUrl(src.url);
        // КАСКАД (Промпт 51): напрямую → через свой активный SOCKS (если поднят). Прямой путь часто
        // заблокирован — именно ради этих условий приложение и существует. Таймауты ступеней раздельные
        // (прокси-ступень медленнее, +10с), плюс общий предел. «Скачано, но ссылок нет» = неудача ступени
        // (панель могла отдать заглушку блокировщика) → пробуем следующую.
        var directTimeout = settings.subTimeoutSec * 1000;
        var proxyTimeout = settings.subTimeoutSec * 1000 + 10_000;
        var totalTimeout = directTimeout + proxyTimeout + 5_000;
        log($"── refresh «{src.name}» url={url} UA={settings.subUserAgent} timeout={settings.subTimeoutSec}s (каскад+зеркала)");
        var cascade = cascadeWithMirrors(context, url, settings.subUserAgent, directTimeout, proxyTimeout, totalTimeout,
            acceptBody: it => it.ok && hasSupportedLinks(it.body));

        // Диагностика по КАЖДОЙ ступени (типизация ошибок из Промпта 25 сохранена). Пропущенные ступени
        // помечаем причиной (a.note), не считая неудачей.
        string stageLabel(CascadeAttempt a)
        {
            var suffix = a.note.Length > 0 ? $" «{a.note}»" : "";
            return a.stage.label() + suffix;
        }
        var stagesDetail = string.Join("\n", cascade.attempts.Select(a =>
        {
            var r = a.result;
            return $"• {stageLabel(a)}: " + (
                a.skipped ? $"пропущено ({a.note})" :
                r == null ? "нет ответа" :
                a.accepted ? $"OK (HTTP {r.httpCode}, {r.bodyBytes} б)" :
                !r.ok ? classifyFetchFailure(r) :
                $"скачано {r.bodyBytes} б, ссылок не найдено (похоже на {sniff(r.body)})");
        }));

        if (!cascade.ok)
        {
            // Терминальный код (Промпт 81.A): каскад ОБОРВАН, маршрут ни при чём. Классифицируем отдельно —
            // 429 это ограничение частоты (не поломка), 401/403/404 — адрес/токен/ресурс. НЕ «перебор ступеней».
            var term = cascade.result is { } cr && CascadeFetch.isTerminalHttp(cr.httpCode) ? cr : null;
            if (term != null)
            {
                var rateLimited = term.httpCode == 429;
                var retry = term.retryAfterSec;
                var msg = term.httpCode switch
                {
                    429 => "Панель ограничивает частоту запросов (HTTP 429) — это не блокировка и не поломка " +
                           "подписки. " + (retry != null ? $"Повторите через ~{retry} с." : "Повторите через минуту."),
                    401 or 403 => $"Доступ к подписке отклонён (HTTP {term.httpCode}) — проверьте адрес/токен. " +
                                  "Смена маршрута не поможет.",
                    _ => $"Подписка не найдена (HTTP {term.httpCode}) — проверьте адрес. Смена маршрута не поможет.",
                };
                var outcome = rateLimited ? SourceOutcome.RATE_LIMITED : SourceOutcome.ERROR;
                setStatus(context, id, ok: false, outcome: outcome, error: msg, detail: stagesDetail, retryAfter: retry);
                log($"  → ТЕРМИНАЛ HTTP {term.httpCode} (каскад оборван): {msg}");
                return new RefreshSummary(ok: false, error: msg, outcome: outcome, rateLimited: rateLimited, retryAfterSec: retry);
            }
            // Итог ПО КАЖДОЙ ступени (кроме пропущенных по неприменимости — они отчёт не засоряют).
            var err = "Не скачалось. " + string.Join("; ", cascade.attempts.Where(it => !it.skipped).Select(a =>
            {
                var r = a.result;
                return a.stage.label() + ": " + (
                    r == null ? "нет ответа" :
                    !r.ok ? classifyFetchFailure(r) :
                    $"ссылок не найдено ({sniff(r.body)})");
            })).IfBlank("все применимые ступени пропущены (нет прокси/сети/кандидатов)");
            setStatus(context, id, ok: false, error: err, detail: stagesDetail);
            log($"  → ОТКАЗ (все ступени): {err}");
            return new RefreshSummary(ok: false, error: err);
        }

        var res = cascade.result!;
        var via = cascade.stage!.Value.label();
        log($"  OK ступенью «{via}» http={res.httpCode} bytes={res.bodyBytes}");
        var detail = new StringBuilder();
        detail.Append($"Загружено: {via}\n");                 // КАКОЙ ступенью удалось (Промпт 51.C)
        detail.Append($"URL: {res.finalUrl}\n");
        detail.Append($"HTTP: {res.httpCode}");
        if (res.contentType != null) detail.Append($" · {res.contentType}");
        detail.Append($"\nБайт: {res.bodyBytes}\n\nСтупени:\n{stagesDetail}");
        return importInto(context, id, res.body, detail.ToString());
    }

    /** Есть ли в теле хотя бы одна распознаваемая ссылка ИЛИ JSON-конфиг (для предиката каскада «ответ пригоден»). */
    internal static bool hasSupportedLinks(string body)
    {
        if (body.IsBlank()) return false;
        if (JsonConfigParser.parse(body).Count > 0) return true;   // формат «JSON-конфиги Xray» (напр. Furk)
        foreach (var line in SubscriptionDecoder.decode(body))
            if (ServerLinkParser.parse(line) is ParseResult.Supported) return true;
        return false;
    }

    /** Классификация не-успешной загрузки в русский текст (сеть/таймаут/cleartext/HTTP-код/пусто). */
    private static string classifyFetchFailure(FetchResult res)
    {
        if (res.exceptionClass != null && (res.errorMessage?.ContainsIgnoreCase("CLEARTEXT") == true ||
                                           res.errorMessage?.ContainsIgnoreCase("Cleartext") == true))
            return "cleartext http:// заблокирован политикой Android — используйте https://";
        if (res.exceptionClass == "UnknownHostException") return $"Хост не найден (нет сети/DNS): {res.errorMessage}";
        if (res.exceptionClass == "SocketTimeoutException") return $"Таймаут загрузки ({res.errorMessage})";
        if (res.exceptionClass != null) return $"Сеть недоступна: {res.exceptionClass}: {res.errorMessage}";
        if (res.httpCode is < 200 or > 299) return $"HTTP {res.httpCode} — сервер отклонил запрос";
        return "Неизвестная ошибка загрузки";
    }

    /**
     * Обновить ВСЕ включённые URL-источники. Одна упавшая не роняет прочие. [cancelled] — флаг между
     * источниками. [onEach] — колбэк прогресса (имя, summary). Возвращает карту id→summary.
     */
    public static Dictionary<string, RefreshSummary> refreshAllEnabled(Context context, Func<bool>? cancelled = null,
        Action<SubSource, RefreshSummary>? onEach = null)
    {
        cancelled ??= () => false;
        var result = new Dictionary<string, RefreshSummary>();
        var targets = SubscriptionStore.load(context).sources.Where(it => it.enabled && it.url.IsNotBlank()).ToList();
        // Промпт 81.B (уточнён Elyor): НЕ ограничиваем по хосту — хосты у источников разные, но упираются в
        // ОДНУ панель. Просто разносим запросы во времени (пауза между источниками). 429 обрывает ступени
        // ТОЛЬКО для своего источника (81.A); остальные обновляем как обычно — «другой путь» пары-страховки
        // (тот же список серверов иным адресом) может пройти, в этом и смысл.
        for (int i = 0; i < targets.Count; i++)
        {
            var src = targets[i];
            if (cancelled()) break;
            if (i > 0) Thread.Sleep((int)BETWEEN_SOURCES_PAUSE_MS);
            if (cancelled()) break;
            RefreshSummary summary;
            try { summary = refreshOne(context, src.id); }
            catch (Exception e) { summary = new RefreshSummary(ok: false, error: e.Message ?? "ошибка"); }
            result[src.id] = summary;
            onEach?.Invoke(src, summary);
        }
        return result;
    }

    private const long BETWEEN_SOURCES_PAUSE_MS = 1200L;   // разнос запросов во времени при «Обновить все» (81.B)

    /**
     * Оффлайн-ядро: тело → decode → parse → склейка в реестр под источник [id] → пересчёт → save.
     * Классифицирует отказ разбора (пусто / 0 ссылок / всё unsupported) отдельным русским текстом —
     * это НЕ сетевая ошибка. [detail] — диагностика (URL/код/тело) для подробностей. Тестируемо без сети.
     */
    public static RefreshSummary importInto(Context context, string id, string body, string? detail = null)
    {
        var verbose = SettingsStore.current().verboseLogs;
        void log(string m) { if (verbose) Log.i(TAG, m); }

        var bytes = Encoding.UTF8.GetByteCount(body);
        if (body.IsBlank())
        {
            var err0 = "Тело пустое (0 байт)";
            // Промпт 85: неудача НЕ вычитает серверы — только статус. Прежние записи целы (сырое тело не трогаем).
            setStatus(context, id, ok: false, error: err0, detail: detail);
            log($"  → {err0}");
            return new RefreshSummary(ok: false, error: err0);
        }

        // Формат «JSON-конфиги Xray» (напр. Furk отдаёт массив готовых конфигов, а не ссылки vless://). Построчный
        // парсер ниже такое НЕ видит (всё тело = одна строка → 0 ссылок → раньше ложный «ссылок не найдено»).
        // Разбираем JSON ПЕРВЫМ; успех → сохраняем сырьё и пересобираем реестр (rebuildRegistry тоже JSON-aware).
        var jsonProfiles = JsonConfigParser.parse(body);
        if (jsonProfiles.Count > 0)
        {
            var seenJson = new HashSet<string>();
            int addedJ = 0, dupJ = 0;
            foreach (var p in jsonProfiles) if (seenJson.Add(serverKey(p))) addedJ++; else dupJ++;
            log($"  JSON-конфиги Xray: серверов={addedJ} дубли={dupJ} (тело {bytes} б)");
            storeRawAndRebuild(context, id, body, detail);
            return new RefreshSummary(ok: true, added: addedJ, duplicates: dupJ);
        }

        var lines = SubscriptionDecoder.decode(body);
        var profiles = new List<ServerProfile>();
        var unsupportedKinds = new List<string>();   // LinkedHashSet: порядок первого появления
        var seen = new HashSet<string>();   // дедуп ВНУТРИ источника
        int unsupported = 0, invalid = 0, duplicates = 0;
        foreach (var line in lines)
        {
            switch (ServerLinkParser.parse(line))
            {
                case ParseResult.Supported r:
                    if (seen.Add(serverKey(r.profile))) profiles.Add(r.profile); else duplicates++;
                    break;
                case ParseResult.Unsupported u:
                    unsupported++;
                    if (!unsupportedKinds.Contains(u.scheme)) unsupportedKinds.Add(u.scheme);
                    break;
                case ParseResult.Invalid:
                    invalid++;
                    break;
            }
        }
        log($"  строк={lines.Count} распарсено={profiles.Count} unsupported={unsupported} ([{string.Join(", ", unsupportedKinds)}]) invalid={invalid} дубли={duplicates}");

        if (profiles.Count == 0)
        {
            var err = lines.Count > 0 && unsupported > 0 && invalid == 0
                ? $"Ссылки есть, но все протоколы не поддержаны: {string.Join(", ", unsupportedKinds)}"
                : $"Скачано {bytes} байт, ссылок не найдено (похоже на {sniff(body)})";
            // Промпт 85: разобрано 0 ссылок — статус-ошибка БЕЗ вычитания серверов и БЕЗ перезаписи сырого тела.
            setStatus(context, id, ok: false, error: err, detail: detail);
            log($"  → {err}");
            return new RefreshSummary(ok: false, added: 0, unsupported: unsupported, invalid: invalid, error: err);
        }

        // Промпт 85: успех — СОХРАНИТЬ сырое тело источника и ПЕРЕСОБРАТЬ реестр из всех тел (а не дописывать
        // записи на месте). Профили всегда парсятся заново из сырья → полны и не зависят от порядка источников.
        storeRawAndRebuild(context, id, body, detail);
        return new RefreshSummary(ok: true, added: profiles.Count, unsupported: unsupported, invalid: invalid, duplicates: duplicates);
    }

    /** Сохранить сырое тело источника [id] и ПЕРЕСОБРАТЬ реестр (Промпт 85). Статус источника — успех. */
    private static void storeRawAndRebuild(Context context, string id, string body, string? detail)
    {
        var file = SubscriptionStore.load(context);
        var raw = new Dictionary<string, string>(file.rawBodies) { [id] = body };
        var withRaw = file with { rawBodies = raw };
        var rebuilt = rebuildRegistry(withRaw);
        var sources = rebuilt.sources.Select(it => it.id == id ? applyStatus(it, true, SourceOutcome.OK, null, detail, null) : it).ToList();
        SubscriptionStore.save(context, recount(rebuilt with { sources = sources }));
    }

    /**
     * ПЕРЕСБОРКА реестра из сырых тел источников (Промпт 85) — единственная точка формирования [servers].
     * Для каждого источника С СЫРЫМ ТЕЛОМ парсим профили заново (полные, независимо от порядка) и склеиваем
     * по serverKey (union членства). Источники БЕЗ сырья (legacy до первого обновления) сохраняют прежние
     * записи. ИЗМЕРЕНИЯ (ping/speed/ts) переносятся по serverKey из прежнего реестра — пересборка их не теряет.
     */
    private static SourcesFile rebuildRegistry(SourcesFile file)
    {
        var meas = new Dictionary<string, ServerProfile>();
        foreach (var rec in file.servers) meas[serverKey(rec.profile)] = rec.profile;

        var byKey = new Dictionary<string, ServerRecord>();
        var order = new List<string>();   // LinkedHashMap: порядок вставки ключей
        void add(ServerProfile p, string sid)
        {
            var k = serverKey(p);
            if (!byKey.TryGetValue(k, out var ex))
            {
                var prof = meas.TryGetValue(k, out var m)
                    ? p with { pingMs = m.pingMs, lastTestedTs = m.lastTestedTs, speedMbps = m.speedMbps, speedTestedTs = m.speedTestedTs }
                    : p;
                byKey[k] = new ServerRecord { profile = prof, sourceIds = [sid] };
                order.Add(k);
            }
            else if (!ex.sourceIds.Contains(sid))
            {
                byKey[k] = ex with { sourceIds = ex.sourceIds.Append(sid).ToList() };
            }
        }

        var validIds = file.sources.Select(it => it.id).ToHashSet();
        var withRaw = new HashSet<string>();
        foreach (var src in file.sources)
        {
            if (!file.rawBodies.TryGetValue(src.id, out var raw)) continue;
            withRaw.Add(src.id);
            foreach (var p in parseProfiles(raw)) add(p, src.id);
        }
        // Legacy-источники без сырья: сохранить прежние записи (до первого обновления, которое положит сырьё).
        foreach (var rec in file.servers)
            foreach (var sid in rec.sourceIds)
            {
                if (withRaw.Contains(sid) || !validIds.Contains(sid)) continue;
                add(rec.profile, sid);
            }
        return file with { servers = order.Select(k => byKey[k]).ToList() };
    }

    /** Разбор тела в профили (decode → parse → дедуп внутри источника). Для пересборки реестра (Промпт 85). */
    private static List<ServerProfile> parseProfiles(string body)
    {
        var @out = new List<ServerProfile>();
        var seen = new HashSet<string>();
        // Формат «JSON-конфиги Xray» (напр. платный Furk отдаёт массив готовых конфигов, а не ссылки vless://).
        // Если тело — такой JSON, берём серверы из него; ссылочный парсер к JSON неприменим.
        var jsonProfiles = JsonConfigParser.parse(body);
        if (jsonProfiles.Count > 0)
        {
            foreach (var p in jsonProfiles) if (seen.Add(serverKey(p))) @out.Add(p);
            return @out;
        }
        foreach (var line in SubscriptionDecoder.decode(body))
        {
            var r = ServerLinkParser.parse(line);
            if (r is ParseResult.Supported s && seen.Add(serverKey(s.profile))) @out.Add(s.profile);
        }
        return @out;
    }

    /** Профиль полон (можно подключиться): есть адрес, порт и учётные данные. Пустой credential = осиротевшая запись. */
    private static bool isProfileComplete(ServerProfile p) =>
        p.address.IsNotBlank() && p.port > 0 && p.credential.IsNotBlank();

    /**
     * САМОПРОВЕРКА реестра (Промпт 85.E): если есть записи с НЕПОЛНЫМ профилем — ПЕРЕСОБРАТЬ реестр из сырья
     * и записать событие в журнал, а не оставлять пользователю неподключаемый список. Вызывать на старте.
     */
    public static void verifyAndHeal(Context context)
    {
        var file = SubscriptionStore.load(context);
        var bad = file.servers.Count(it => !isProfileComplete(it.profile));
        if (bad == 0) return;
        var rebuilt = recount(rebuildRegistry(file));
        var after = rebuilt.servers.Count(it => !isProfileComplete(it.profile));
        SubscriptionStore.save(context, rebuilt);
        runCatching(() => MonitorLog.@event(
            context, "monitor", "Реестр: неполные профили — пересборка",
            $"было {bad}, после пересборки {after}" + (after > 0 ? " (нужно обновить источники)" : "")));
        Log.w(TAG, $"verifyAndHeal: неполных было {bad}, стало {after}");
    }

    /**
     * Единая запись статуса источника (Промпт 81.C). Успех обновляет время УДАЧИ (lastOkTs) и ОЧИЩАЕТ
     * lastError; неудача сохраняет прежнее время удачи (ошибка не залипает поверх свежих данных). Время
     * ПОПЫТКИ (lastRefreshTs) обновляется всегда. retryAfterSec держим только для RATE_LIMITED.
     */
    private static SubSource applyStatus(SubSource s, bool ok, SourceOutcome outcome, string? error, string? detail, int? retryAfter)
    {
        var ts = now();
        return s with
        {
            lastRefreshTs = ts,
            lastOkTs = ok ? ts : s.lastOkTs,
            lastOk = ok,
            lastOutcome = outcome,
            lastError = ok ? null : error,
            lastDetail = detail,
            retryAfterSec = outcome == SourceOutcome.RATE_LIMITED ? retryAfter : null,
        };
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+:[ \\t\\n\\x0B\\f\\r]", RegexOptions.Multiline)]
    private static partial Regex YamlKey();

    /** На что похоже тело (для сообщения «ссылок не найдено»). */
    private static string sniff(string body)
    {
        var t = body.KTrimStart();
        if (t.StartsWith('<') || t.ContainsIgnoreCase("<!doctype") || t.ContainsIgnoreCase("<html")) return "HTML (страница, не подписка)";
        if (t.StartsWith('{') || t.StartsWith('[')) return "JSON";
        if (t.Contains("proxies:") || t.Contains("proxy-groups:") || YamlKey().IsMatch(t)) return "YAML/Clash (нужен base64-список, не Clash)";
        return "обычный текст без ссылок";
    }

    /** Факт-проверка дедупа: склеить набор профилей в реестр и вернуть статистику пересечений. */
    public static MergeStats mergeStats(IReadOnlyList<ServerRecord> existing, string sourceId, IReadOnlyList<ServerProfile> profiles)
    {
        var existingKeys = existing.Select(it => serverKey(it.profile)).ToHashSet();
        var incomingKeys = profiles.Select(serverKey).ToHashSet();
        var merged = incomingKeys.Count(existingKeys.Contains);
        var (reg, _) = mergeIntoRegistry(existing, sourceId, profiles);
        return new MergeStats(
            incoming: profiles.Count,
            newInRegistry: incomingKeys.Count - merged,
            mergedExisting: merged,
            registryTotal: reg.Count);
    }

    // ─────────────────────────── склейка реестра ───────────────────────────

    /**
     * Влить [profiles] под [sourceId] в реестр: совпавшие по serverKey — склеить (СОХРАНИВ измерения
     * существующей записи, добавив sourceId), отсутствующие в импорте — убрать sourceId (и запись,
     * если источников не осталось), новые — добавить. Возвращает (новый реестр, число склеек).
     */
    private static (IReadOnlyList<ServerRecord> registry, int merged) mergeIntoRegistry(
        IReadOnlyList<ServerRecord> registry, string sourceId, IReadOnlyList<ServerProfile> profiles)
    {
        var incoming = new Dictionary<string, ServerProfile>();
        var incomingOrder = new List<string>();
        foreach (var p in profiles)
        {
            var k = serverKey(p);
            if (incoming.TryAdd(k, p)) incomingOrder.Add(k);   // putIfAbsent
        }

        var result = new List<ServerRecord>();
        var handled = new HashSet<string>();
        var merged = 0;
        foreach (var rec in registry)
        {
            var k = serverKey(rec.profile);
            if (incoming.TryGetValue(k, out var fresh))
            {
                // Совпадение: сохраняем измерения существующей записи, статические поля берём из fresh.
                var mergedProfile = fresh with
                {
                    pingMs = rec.profile.pingMs, lastTestedTs = rec.profile.lastTestedTs,
                    speedMbps = rec.profile.speedMbps, speedTestedTs = rec.profile.speedTestedTs,
                };
                result.Add(new ServerRecord { profile = mergedProfile, sourceIds = rec.sourceIds.Append(sourceId).Distinct().ToList() });
                if (!rec.sourceIds.Contains(sourceId)) merged++;
                handled.Add(k);
            }
            else
            {
                // Не в этом импорте — убираем данный источник; запись остаётся, если есть другие.
                var ids = rec.sourceIds.ToList();
                ids.Remove(sourceId);   // Kotlin `list - element`: первое вхождение
                if (ids.Count > 0) result.Add(rec with { sourceIds = ids });
            }
        }
        foreach (var k in incomingOrder) if (!handled.Contains(k)) result.Add(new ServerRecord { profile = incoming[k], sourceIds = [sourceId] });
        return (result, merged);
    }

    /** Пересчитать serverCount каждого источника из реестра (денормализация без дрейфа). */
    private static SourcesFile recount(SourcesFile file)
    {
        var counts = new Dictionary<string, int>();
        foreach (var rec in file.servers) foreach (var id in rec.sourceIds) counts[id] = counts.GetValueOrDefault(id) + 1;
        return file with { sources = file.sources.Select(it => it with { serverCount = counts.GetValueOrDefault(it.id) }).ToList() };
    }

    // ─────────────────────────── helpers ───────────────────────────

    /** Выставить статус источника (сетевой/HTTP отказ — без изменения серверов). */
    private static void setStatus(Context context, string id, bool ok, string? error, string? detail,
        SourceOutcome? outcome = null, int? retryAfter = null)
    {
        var oc = outcome ?? (ok ? SourceOutcome.OK : SourceOutcome.ERROR);
        var file = SubscriptionStore.load(context);
        SubscriptionStore.save(context, file with
        {
            sources = file.sources.Select(it => it.id == id ? applyStatus(it, ok, oc, error, detail, retryAfter) : it).ToList(),
        });
    }

    private static string newId() => Guid.NewGuid().ToString();

    /** Нормализация URL: обрезать пробелы/переводы строк с концов; без схемы — подставить https://. */
    public static string normalizeUrl(string raw)
    {
        var u = raw.KTrim().Trim('\n', '\r', ' ', '\t');
        if (u.Length == 0) return "";
        return u.StartsWithIgnoreCase("http://") || u.StartsWithIgnoreCase("https://") ? u : $"https://{u}";
    }

    /** Имя из URL — ТОЛЬКО host. Если хост извлечь не удалось — честно «Без имени» (не огрызок URL). */
    private static string nameFromUrl(string url)
    {
        try { return new JavaUri(normalizeUrl(url)).Host.TakeIfNotBlank() ?? "Без имени"; }
        catch (Exception) { return "Без имени"; }
    }

    private static string now() => KDate.now();
}
