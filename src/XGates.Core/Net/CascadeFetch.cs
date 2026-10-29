using System.Net;
using System.Net.Sockets;
using XGates.Core.Compat;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Subscription;
using XGates.Core.Xray;

namespace XGates.Core.Net;

/**
 * ОБЩИЙ каскад загрузки внешнего ресурса (подписки, обновляемый конфиг, проверка версии и САМ установщик
 * обновления — всё на GitHub, из РФ нестабильно, а туннель часто поднят). Ступени пробуются по
 * порядку; переход по ЛЮБОЙ неудаче (таймаут/код≠200/пусто/TLS/«скачано, но контент не подошёл» —
 * решает [acceptBody]). Неприменимая ступень ПРОПУСКАЕТСЯ (не неудача, не засоряет отчёт).
 *
 * Порядок (Промпт 51 + 54):
 *   1. напрямую;
 *   2. через свой активный SOCKS (если прокси поднят);
 *   3. напрямую МИМО системного VPN (bind к физической сети — только если МЫ ВНУТРИ VPN и ещё не мимо);
 *   4. ЧЕРЕЗ системный VPN (явно на VPN-сети — только если мы внутри; единственный путь при lockdown);
 *   5. через системный прокси (если задан);
 *   6. temp-инстанс на недавно рабочих серверах (до 5, только для ТЕКСТОВОГО [fetch], не во время теста).
 *
 * Порт 1:1 с net/CascadeFetch.kt. Отличия Windows: «напрямую» — В ОБХОД системного прокси (иначе .NET взял бы
 * WinINET-прокси); ступени 3–4 (обход/через чужой VPN) — фаза 2 (HANDOFF §7): пропускаются с причиной;
 * ступень 5 — системный прокси Windows (только ЧИТАЕМ настройки, ничего не меняем; PAC тоже понимается).
 */
public enum FetchStage
{
    DIRECT,
    OWN_PROXY,
    NON_VPN_DIRECT,
    VIA_VPN,
    SYSTEM_PROXY,
    TEMP_RECENT,
}

public static class FetchStageExt
{
    public static string label(this FetchStage s) => s switch
    {
        FetchStage.DIRECT => "напрямую",
        FetchStage.OWN_PROXY => "через свой прокси",
        FetchStage.NON_VPN_DIRECT => "напрямую мимо системного VPN",
        FetchStage.VIA_VPN => "через системный VPN",
        FetchStage.SYSTEM_PROXY => "через системный прокси",
        FetchStage.TEMP_RECENT => "через недавно рабочий сервер",
        _ => s.ToString(),
    };
}

public sealed record CascadeAttempt(
    FetchStage stage,
    bool skipped,
    bool accepted,
    FetchResult? result,
    string note = "");     // причина пропуска / имя сервера для ступени 6 / краткий исход бинарной ступени

public sealed record CascadeResult(
    bool ok,
    FetchStage? stage,
    FetchResult? result,
    IReadOnlyList<CascadeAttempt> attempts);

/// <summary>Исход бинарной загрузки: какой ступенью удалось, сколько байт, отмена, поступенчатый отчёт.</summary>
public sealed record CascadeDownloadResult(
    bool ok,
    FetchStage? stage,
    long bytes,
    IReadOnlyList<CascadeAttempt> attempts,
    bool cancelled = false);

public static class CascadeFetch
{
    private const int MAX_TEMP_CANDIDATES = 5;
    private const int MAX_REDIRECTS = 5;
    private const int DOWNLOAD_BUFFER = 64 * 1024;

    /**
     * Коды, при которых каскад ОБРЫВАЕТСЯ немедленно (Промпт 81.A) — маршрут ни при чём, перебор ступеней
     * бессмыслен и вреден: 429 = «слишком много запросов» (смена пути бьёт по ТОЙ ЖЕ панели повторно и
     * усугубляет лимит); 401/403 = доступ отклонён (токен/адрес); 404 = ресурса нет. Ответ ОТ СЕРВЕРА —
     * значит путь рабочий, дело в самом запросе/ресурсе.
     */
    private static readonly HashSet<int> TERMINAL_HTTP = [401, 403, 404, 429];
    public static bool isTerminalHttp(int code) => TERMINAL_HTTP.Contains(code);

    /// <summary>План одной HTTP-ступени: либо [open] (пробуем), либо [skipNote]≠"" (пропускаем с причиной).</summary>
    private sealed record StagePlan(FetchStage stage, int timeoutMs, HttpRoute? open, string skipNote);

    /// <summary>Порт SOCKS нашего прокси (на Windows может быть изменён в настройках — §13.7).</summary>
    public static Func<int> ownSocksPort { get; set; } = () => XrayConfig.SOCKS_PORT;

    /// <summary>Системный прокси Windows для URL (null — не задан/«напрямую»). Подменяем в тестах.</summary>
    public static Func<string, IWebProxy?> systemProxyFor { get; set; } = defaultSystemProxyFor;

    /**
     * Построить ступени 1–5 в порядке каскада. Каждая — либо маршрут, либо пропуск с причиной.
     * overBudget/тело/стрим — забота вызывающего (text vs binary), сама логика ступеней тут ОДНА.
     */
    private static List<StagePlan> httpStagePlans(string url, int directTimeoutMs, int proxyTimeoutMs)
    {
        var plans = new List<StagePlan>(5);

        // 1 — напрямую (всегда пробуем)
        plans.Add(new StagePlan(FetchStage.DIRECT, directTimeoutMs, HttpRoute.DIRECT, ""));

        // 2 — через свой активный SOCKS. Доступность определяем ПОПЫТКОЙ СОЕДИНЕНИЯ с портом (Промпт 77.A),
        //     а НЕ флагом ProxyState.running: флаг — производная величина и может отстать; открытый порт — ФАКТ.
        var socks = ownSocksPort();
        if (socksReachable())
            plans.Add(new StagePlan(FetchStage.OWN_PROXY, proxyTimeoutMs, new HttpRoute.Socks("127.0.0.1", socks), ""));
        else
            plans.Add(new StagePlan(FetchStage.OWN_PROXY, proxyTimeoutMs, null,
                $"SOCKS-порт {socks} не отвечает (флаг running={(ProxyState.state.value.running ? "true" : "false")})"));

        // Отношение НАШЕГО трафика к VPN — из единого источника.
        var vpn = SystemVpnState.state.value;

        // 3 — напрямую МИМО системного VPN. Windows: фаза 2 (HANDOFF §7) — сейчас идём маршрутом ОС.
        if (vpn.relation != VpnRelation.INSIDE)
            plans.Add(new StagePlan(FetchStage.NON_VPN_DIRECT, proxyTimeoutMs, null,
                vpn.relation == VpnRelation.EXCLUDED ? "мы уже вне VPN" : "мы не внутри VPN"));
        else
            plans.Add(new StagePlan(FetchStage.NON_VPN_DIRECT, proxyTimeoutMs, null, "обход системного VPN на Windows — фаза 2"));

        // 4 — ЧЕРЕЗ системный VPN. Windows: маршрут ОС и так идёт через VPN, если он включён → отдельная ступень не нужна.
        if (vpn.relation != VpnRelation.INSIDE)
            plans.Add(new StagePlan(FetchStage.VIA_VPN, proxyTimeoutMs, null,
                vpn.relation == VpnRelation.EXCLUDED ? "мы вне VPN — в чужой туннель не попасть" : "VPN не активен"));
        else
            plans.Add(new StagePlan(FetchStage.VIA_VPN, proxyTimeoutMs, null, "на Windows ступень 1 уже идёт маршрутом ОС"));

        // 5 — через системный прокси (настройки Windows / другое приложение)
        var sysProxy = systemProxyFor(url);
        if (sysProxy == null) plans.Add(new StagePlan(FetchStage.SYSTEM_PROXY, proxyTimeoutMs, null, "системный прокси не задан"));
        else plans.Add(new StagePlan(FetchStage.SYSTEM_PROXY, proxyTimeoutMs, new HttpRoute.SystemProxy(sysProxy), ""));

        return plans;
    }

    /// <summary>Ступень OWN_PROXY доступна, если SOCKS-порт РЕАЛЬНО слушает (факт), а не по флагу состояния (77.A).</summary>
    private static bool socksReachable()
    {
        try
        {
            using var s = new TcpClient();
            return s.ConnectAsync(IPAddress.Loopback, ownSocksPort()).Wait(300) && s.Connected;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Публичная проверка: слушает ли наш SOCKS-порт (факт). Для авто-проверки обновления — дождаться прокси.</summary>
    public static bool isOwnProxyUp() => socksReachable();

    /** Порядок ступеней. [proxyFirst] (для обновлений, 77.B): если свой SOCKS доступен — ставим его ПЕРВЫМ
     *  (прямая попытка к GitHub чаще уходит в таймаут впустую). Иначе — обычный порядок «сначала напрямую». */
    private static List<StagePlan> orderedPlans(List<StagePlan> plans, bool proxyFirst)
    {
        if (!proxyFirst) return plans;
        var own = plans.FirstOrDefault(it => it.stage == FetchStage.OWN_PROXY && it.open != null);
        if (own == null) return plans;
        return new[] { own }.Concat(plans.Where(it => !ReferenceEquals(it, own))).ToList();
    }

    private static CascadeAttempt skip(FetchStage stage, string note) =>
        new(stage, skipped: true, accepted: false, result: null, note: note);

    /** ТЕКСТОВАЯ загрузка (подписки/JSON): тело в строку, плюс 6-я ступень temp-инстанс.
     *  [proxyFirst] — для запросов обновления: пробовать свой SOCKS ПЕРВЫМ (77.B). */
    public static CascadeResult fetch(
        Context context,
        string url,
        string userAgent,
        int directTimeoutMs,
        int proxyTimeoutMs,
        int totalTimeoutMs,
        Func<FetchResult, bool>? acceptBody = null,
        bool proxyFirst = false,
        bool allowTempInstance = true,
        int maxTempCandidates = MAX_TEMP_CANDIDATES,
        int tempTimeoutMs = -1)   // -1 → взять proxyTimeoutMs; для лёгкого манифеста задаём короче (Пр.135)
    {
        acceptBody ??= it => it.ok && it.body.IsNotBlank();
        var app = context.applicationContext;
        var attempts = new List<CascadeAttempt>();
        var start = JavaSystem.nanoTime();
        bool overBudget() => (JavaSystem.nanoTime() - start) / 1_000_000 > totalTimeoutMs;

        // Ступени 1–5 из общего планировщика (с учётом proxyFirst для обновлений).
        var plans = orderedPlans(httpStagePlans(url, directTimeoutMs, proxyTimeoutMs), proxyFirst);
        for (int i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            if (plan.open == null) { attempts.Add(skip(plan.stage, plan.skipNote)); continue; }
            if (i > 0 && overBudget()) { attempts.Add(skip(plan.stage, "общий таймаут")); continue; }
            var r = SubscriptionFetcher.fetch(url, userAgent, plan.timeoutMs, plan.open);
            var ok = acceptBody(r);
            attempts.Add(new CascadeAttempt(plan.stage, skipped: false, accepted: ok, result: r));
            if (ok) return new CascadeResult(true, plan.stage, r, attempts);
            // Терминальный код (429/401/403/404, Промпт 81.A): сервер ОТВЕТИЛ — путь рабочий, менять
            // маршрут бессмысленно и вредно. ОБРЫВАЕМ каскад, остальные ступени/temp-инстанс не трогаем.
            if (isTerminalHttp(r.httpCode)) return new CascadeResult(false, plan.stage, r, attempts);
        }

        // 6 — temp-инстанс на недавно рабочих серверах (только текстовый путь). Промпт 81.E: для обновлений
        //     (килобайтный манифест) отключаем — незачем перебирать временные серверы ради мелкого файла.
        if (!allowTempInstance)
        {
            attempts.Add(skip(FetchStage.TEMP_RECENT, "temp-инстанс отключён для этого запроса"));
        }
        else if (MonitorCoordinator.fullTestRunning || MonitorCoordinator.monitorSearchRunning)
        {
            attempts.Add(skip(FetchStage.TEMP_RECENT, "идёт полный тест / перебор монитора"));
        }
        else
        {
            var tempT = tempTimeoutMs > 0 ? tempTimeoutMs : proxyTimeoutMs;
            var candidates = SubscriptionManager.recentWorkingServers(app).Take(maxTempCandidates.coerceAtLeast(0)).ToList();
            if (candidates.Count == 0) attempts.Add(skip(FetchStage.TEMP_RECENT, "нет недавно рабочих серверов"));
            else foreach (var c in candidates)
            {
                if (overBudget()) { attempts.Add(skip(FetchStage.TEMP_RECENT, "общий таймаут")); break; }
                var name = c.remarks.IfBlank(c.address);
                var r = ServerSpeedTester.fetchViaTempInstance(app, c, url, userAgent, tempT);
                var ok = r != null && acceptBody(r);
                attempts.Add(new CascadeAttempt(FetchStage.TEMP_RECENT, skipped: false, accepted: ok, result: r, note: name));
                if (ok) return new CascadeResult(true, FetchStage.TEMP_RECENT, r, attempts);
            }
        }

        return new CascadeResult(false, null, null, attempts);
    }

    /**
     * БИНАРНАЯ загрузка (установщик обновления): стрим в [dest] с прогрессом и отменой, те же ступени 1–5.
     * 6-ю ступень (temp-инстанс) для бинарного стрима не поддерживаем — для метаданных обновления (JSON,
     * килобайты) их и так тянет [fetch].
     *
     * [onProgress] (скачано, всего): всего=-1, если сервер не отдал Content-Length и [expectedSize] нет.
     * [isCancelled] опрашивается между чтениями — отмена оставляет частичный файл вызывающему на удаление.
     */
    public static CascadeDownloadResult download(
        Context context,
        string url,
        string userAgent,
        string dest,
        int directTimeoutMs,
        int proxyTimeoutMs,
        int totalTimeoutMs,
        long expectedSize = -1,
        Func<bool>? isCancelled = null,
        Action<long, long>? onProgress = null)
    {
        isCancelled ??= () => false;
        onProgress ??= (_, _) => { };
        var attempts = new List<CascadeAttempt>();
        var start = JavaSystem.nanoTime();
        bool overBudget() => (JavaSystem.nanoTime() - start) / 1_000_000 > totalTimeoutMs;

        var plans = httpStagePlans(url, directTimeoutMs, proxyTimeoutMs);
        for (int i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            if (plan.open == null) { attempts.Add(skip(plan.stage, plan.skipNote)); continue; }
            if (i > 0 && overBudget()) { attempts.Add(skip(plan.stage, "общий таймаут")); continue; }
            if (isCancelled()) return new CascadeDownloadResult(false, null, 0, attempts, cancelled: true);

            var res = streamTo(url, userAgent, plan.timeoutMs, plan.open, dest, expectedSize, isCancelled, onProgress);
            if (res.cancelled)
            {
                attempts.Add(new CascadeAttempt(plan.stage, skipped: false, accepted: false, result: null, note: "отменено"));
                return new CascadeDownloadResult(false, null, res.bytes, attempts, cancelled: true);
            }
            if (res.ok)
            {
                attempts.Add(new CascadeAttempt(plan.stage, skipped: false, accepted: true, result: null, note: $"OK, {res.bytes} б"));
                return new CascadeDownloadResult(true, plan.stage, res.bytes, attempts);
            }
            attempts.Add(new CascadeAttempt(plan.stage, skipped: false, accepted: false, result: null, note: res.note));
            // Терминальный код (Промпт 81.A): ОБРЫВ, остальные ступени не пробуем.
            if (isTerminalHttp(res.code)) return new CascadeDownloadResult(false, plan.stage, res.bytes, attempts);
        }

        attempts.Add(skip(FetchStage.TEMP_RECENT, "бинарная загрузка через temp-инстанс не поддерживается"));
        return new CascadeDownloadResult(false, null, 0, attempts);
    }

    private sealed record StreamOutcome(bool ok, long bytes, string note, bool cancelled = false, int code = -1);

    /// <summary>Одна попытка стрима в файл: проходим редиректы (в т.ч. GitHub asset → CDN), пишем чанками.</summary>
    private static StreamOutcome streamTo(
        string url, string userAgent, int timeoutMs, HttpRoute open, string dest, long expectedSize,
        Func<bool> isCancelled, Action<long, long> onProgress)
    {
        var current = url;
        var redirects = 0;
        try
        {
            while (true)
            {
                if (isCancelled()) return new StreamOutcome(false, 0, "отменено", cancelled: true);
                using var handler = open.CreateHandler(timeoutMs);
                using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                HttpResponseMessage resp;
                try
                {
                    using var headersCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs) * 2L));
                    resp = http.SendAsync(HttpUrlConnectionCompat.Get(current, userAgent), HttpCompletionOption.ResponseHeadersRead, headersCts.Token)
                        .GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    return new StreamOutcome(false, 0, $"сеть: {JavaExceptionNames.simpleName(e)}");
                }
                using (resp)
                {
                    int code = (int)resp.StatusCode;
                    if (code is >= 300 and <= 399 && redirects < MAX_REDIRECTS)
                    {
                        var loc = HttpUrlConnectionCompat.Header(resp, "Location");
                        if (loc.IsBlank()) return new StreamOutcome(false, 0, "редирект без Location");
                        current = new Uri(new Uri(current), loc!).ToString();
                        redirects++;
                        continue;
                    }
                    if (code is < 200 or > 299) return new StreamOutcome(false, 0, $"HTTP {code}", code: code);

                    var len = resp.Content.Headers.ContentLength ?? -1;
                    var total = len > 0 ? len : expectedSize;
                    long downloaded = 0L;
                    try
                    {
                        using var @out = File.Create(dest);
                        using var inp = resp.Content.ReadAsStream();
                        var buf = new byte[DOWNLOAD_BUFFER];
                        while (true)
                        {
                            if (isCancelled()) return new StreamOutcome(false, downloaded, "отменено", cancelled: true);
                            using var cts = new CancellationTokenSource(Math.Max(1, timeoutMs));   // readTimeout на каждое чтение
                            int n = inp.ReadAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult();
                            if (n <= 0) break;
                            @out.Write(buf, 0, n);
                            downloaded += n;
                            onProgress(downloaded, total);
                        }
                    }
                    catch (Exception e)
                    {
                        return new StreamOutcome(false, downloaded, $"обрыв: {JavaExceptionNames.simpleName(e)}");
                    }
                    return new StreamOutcome(true, downloaded, "ok");
                }
            }
        }
        catch (Exception e)
        {
            return new StreamOutcome(false, 0, $"ошибка: {JavaExceptionNames.simpleName(e)}");
        }
    }

    /// <summary>Системный прокси Windows для URL (null — не задан или «напрямую» для этого адреса).</summary>
    private static IWebProxy? defaultSystemProxyFor(string url)
    {
        try
        {
            var p = HttpClient.DefaultProxy;
            var u = new Uri(url);
            if (p.IsBypassed(u)) return null;
            var via = p.GetProxy(u);
            if (via == null || via == u) return null;
            return p;
        }
        catch (Exception) { return null; }
    }
}
