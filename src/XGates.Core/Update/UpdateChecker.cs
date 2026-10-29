using System.Text;
using System.Text.RegularExpressions;
using XGates.Core.Compat;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Traffic;
using XGates.Core.Xray;

namespace XGates.Core.Update;

/**
 * Проверка обновления (Промпт 70, приборы+запасной путь — Промпт 76). БЛОКИРУЮЩАЯ — вызывать в фоне.
 * Порт update/UpdateChecker.kt. Windows-отличия: свой репозиторий релизов, сборка «win-x64» вместо ABI, и
 * ОБЯЗАТЕЛЬНАЯ подпись update.json (update.json.sig, UpdateSignature) — без неё обновление не предлагаем.
 *
 * Порядок: API releases/latest (через каскад) → вложение update.json (+ .sig) → сравнить versionCode.
 * ЗАПАСНОЙ путь (76.E): если API недоступен ИЛИ вложение не скачалось — тянем update.json НАПРЯМУЮ по
 * предсказуемому адресу `releases/latest/download/update.json` (без API), адреса установщика строим по тегу из его
 * редиректа `releases/download/<тег>/<файл>` (запасные — `releases/latest/download/<файл>`).
 *
 * ВАЖНО (76.A): браузер ходит на github.com (HTML), а мы — на api.github.com и на CDN вложений
 * (release-assets.githubusercontent.com по редиректу). Это РАЗНЫЕ узлы, блокируются раздельно.
 */
public static partial class UpdateChecker
{
    private const string TAG = "UpdateChecker";
    public const string OWNER = "pico-soft";
    public const string UA = "X-Gates-Updater";
    private const string MANIFEST_NAME = "update.json";
    /// <summary>Платформа сборки в update.json (на Android — ABI).</summary>
    public const string PLATFORM = "win-x64";

    /// <summary>Один адрес репозитория и его URL-ы (API / запасной без API). Промпт 121.B.</summary>
    public sealed record Repo(string owner, string repo, string web = "https://github.com", string api = "https://api.github.com")
    {
        public string slug => $"{owner}/{repo}";
        public string apiLatest => $"{api}/repos/{owner}/{repo}/releases/latest";
        public string latestDownload => $"{web}/{owner}/{repo}/releases/latest/download";
        public string tagDownload(string tag) => $"{web}/{owner}/{repo}/releases/download/{tag}";
    }

    [GeneratedRegex("/releases/download/([^/?#]+)/")]
    private static partial Regex TagInRedirect();

    /**
     * Тег релиза, на который РЕАЛЬНО указал `releases/latest/download/…`: GitHub редиректит его на
     * `releases/download/<тег>/…` — берём тег из цепочки редиректов. null — цепочки нет (напр. temp-инстанс).
     */
    internal static string? tagFromRedirects(IReadOnlyList<string> chain)
    {
        foreach (var u in chain)
        {
            var m = TagInRedirect().Match(u);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>СПИСОК АДРЕСОВ (Промпт 121.B). Windows-версия публикуется в своём репозитории (docs/decisions.md §13.3).</summary>
    public static IReadOnlyList<Repo> REPOS { get; internal set; } = [new Repo(OWNER, "X-Gates-Win")];

    /**
     * Сколько ждать подъёма СВОЕГО SOCKS перед АВТО-проверкой на холодном старте (Промпт 80.B). Автозапуск
     * коннектится не мгновенно; на сети с заблокированным напрямую GitHub проверка без туннеля обречена,
     * поэтому авто-проверка ждёт порт, а не уходит впустую напрямую.
     */
    public const int AUTO_CHECK_PROXY_WAIT_MS = 20_000;

    // Пр.135: для РУЧНОЙ проверки разрешаем ступень 6 (temp-инстанс) на запрос СВЕДЕНИЙ (API/манифест) —
    // иначе при заблокированном напрямую api.github.com и невключённом туннеле обновиться нельзя вовсе.
    private const int UPDATE_TEMP_CANDIDATES = 2;
    private const int UPDATE_TEMP_TIMEOUT_MS = 12_000;

    // Три РАЗНЫХ узла GitHub, блокируются в РФ раздельно (Промпт 76/80.C).
    private const string NODE_API = "https://api.github.com/";
    private const string NODE_WEB = "https://github.com/robots.txt";
    private static string NODE_CDN => $"{REPOS[0].latestDownload}/{MANIFEST_NAME}";
    private const int NODE_PROBE_TIMEOUT_MS = 4000;

    /// <summary>Идентификатор/версия установленного приложения (подменяемы в тестах).</summary>
    internal static Func<string> applicationId = () => AppInfo.APPLICATION_ID;
    internal static Func<int> currentVersionCode = () => AppInfo.VERSION_CODE;
    internal static Func<string> currentVersionName = () => AppInfo.VERSION_NAME;

    private static string host(string url)
    {
        try { return new Uri(url).Host; } catch (Exception) { return url; }
    }

    /// <summary>Подробный разбор попыток каскада: ступень, HTTP/исключение, host, размер, редиректы.</summary>
    private static string diag(CascadeResult cascade) => string.Join("\n", cascade.attempts.Select(a =>
    {
        var r = a.result;
        var body =
            a.skipped ? $"пропущено ({a.note})" :
            r == null ? "нет ответа" :
            r.exceptionClass != null ? $"{r.exceptionClass}: {r.errorMessage} · host={host(r.finalUrl)}" :
            $"HTTP {r.httpCode} · {r.bodyBytes}б · host={host(r.finalUrl)}" + (a.accepted ? " ✓" : "");
        var redir = r != null && r.redirectChain.Count > 1 ? " · редиректы: " + string.Join(" → ", r.redirectChain.Select(host)) : "";
        return $"  • {a.stage.label()}: {body}{redir}";
    }));

    /// <summary>Пр.135.C: сведения получены через temp-инстанс (обошли заблокированный напрямую GitHub) — показать это.</summary>
    private static string tempNote(CascadeResult c) =>
        c.stage == FetchStage.TEMP_RECENT ? " · через запасной сервер (GitHub напрямую недоступен)" : "";

    /// <summary>КОНЕЧНЫЙ host, до которого не дошли (последняя реальная попытка), + причина — для сводки (77.C).</summary>
    private static string lastHost(CascadeResult cascade)
    {
        var a = cascade.attempts.LastOrDefault(it => !it.skipped && it.result != null);
        if (a == null) return "нет попыток";
        var r = a.result!;
        var why = r.exceptionClass ?? $"HTTP {r.httpCode}";
        return $"{host(r.finalUrl)} ({why})";
    }

    /** [manual] — РУЧНОЕ нажатие «Проверить обновление»/тап баннера: ходим по ВСЕМ ступеням, включая temp-инстанс
     *  (Пр.135). Авто-проверка на старте — [manual]=false: только дешёвые ступени, при неудаче молча ждём. */
    public static CheckReport check(Context context, bool manual = false)
    {
        var s = SettingsStore.current();
        var verbose = s.verboseLogs;
        var directT = s.subTimeoutSec * 1000;
        var proxyT = s.subTimeoutSec * 1000 + 10_000;
        // При ручной проверке даём бюджет ещё и на temp-ступень (2 кандидата × короткий таймаут).
        var totalT = directT + proxyT + 5_000 + (manual ? UPDATE_TEMP_CANDIDATES * UPDATE_TEMP_TIMEOUT_MS : 0);
        var det = new StringBuilder();
        void addTest(CascadeResult c) { if (c.result is { bodyBytes: > 0 } r) TrafficTracker.addTest(r.bodyBytes); }
        // Один запрос обновления = один блок диагностики (лог + подробности UI). proxyFirst=true (77.B).
        CascadeResult request(string label, string url)
        {
            var c = CascadeFetch.fetch(context, url, UA, directT, proxyT, totalT,
                acceptBody: it => it.ok && it.body.IsNotBlank(), proxyFirst: true,
                allowTempInstance: manual, maxTempCandidates: UPDATE_TEMP_CANDIDATES, tempTimeoutMs: UPDATE_TEMP_TIMEOUT_MS);
            addTest(c);
            var d = diag(c);
            det.Append(label).Append('\n').Append(d).Append("\n\n");
            if (verbose) Log.i(TAG, $"{label}\n{d}");
            return c;
        }

        if (verbose) Log.i(TAG, "──────── проверка обновления ────────");

        // Windows: манифест принимаем ТОЛЬКО с верной подписью нашим ключом (update.json.sig рядом с ним).
        UpdateCheckResult? verifySignature(string manifestBody, string sigUrl)
        {
            var sig = request($"подпись {sigUrl}", sigUrl);
            if (!sig.ok) return new UpdateCheckResult.Error(UpdateErrorKind.SIGNATURE_MISSING, $"не скачалась подпись: {lastHost(sig)}");
            if (!UpdateSignature.verify(manifestBody, sig.result!.body)) return new UpdateCheckResult.Error(UpdateErrorKind.SIGNATURE_MISMATCH);
            det.Append("Подпись update.json: верна (ключ разработчика)\n\n");
            return null;
        }

        // Проверка ОДНОГО адреса: API → вложение update.json → запасной адрес без API. Repo-aware.
        UpdateCheckResult checkRepo(Repo repo)
        {
            // ── 1. API releases/latest ──
            var rel = request($"API {repo.apiLatest} (UA={UA})", repo.apiLatest);
            if (rel.attempts.Any(it => it.result?.httpCode == 404)) return new UpdateCheckResult.NoReleases();

            if (rel.ok)
            {
                GithubRelease? release = null;
                try { release = KJson.decodeFromString<GithubRelease>(rel.result!.body, KJson.Compact); } catch (Exception) { }
                if (release != null)
                {
                    var manifestAsset = release.assets.FirstOrDefault(it => string.Equals(it.name, MANIFEST_NAME, StringComparison.OrdinalIgnoreCase));
                    if (manifestAsset == null || manifestAsset.browserDownloadUrl.IsBlank())
                    {
                        det.Append($"В релизе нет вложения update.json → путь по метке {release.tagName}\n\n");
                        return fallbackByTag(release);
                    }
                    var mf = request($"update.json ЧЕРЕЗ API: {manifestAsset.browserDownloadUrl}", manifestAsset.browserDownloadUrl);
                    if (mf.ok)
                    {
                        UpdateManifest? manifest = null;
                        try { manifest = KJson.decodeFromString<UpdateManifest>(mf.result!.body, KJson.Compact); } catch (Exception) { }
                        if (manifest == null) return new UpdateCheckResult.Error(UpdateErrorKind.MANIFEST_PARSE, "вложение скачано, но JSON не разобран");
                        var sigAsset = release.assets.FirstOrDefault(it => it.name == MANIFEST_NAME + UpdateSignature.SIG_SUFFIX);
                        var sigErr = verifySignature(mf.result!.body, sigAsset?.browserDownloadUrl ?? manifestAsset.browserDownloadUrl + UpdateSignature.SIG_SUFFIX);
                        if (sigErr != null) return sigErr;
                        return compareByManifest(manifest, release, repo, via: $"через API · {repo.slug}{tempNote(mf)}",
                            assetUrl: fn => release.assets.FirstOrDefault(it => it.name == fn)?.browserDownloadUrl);
                    }
                    // вложение не скачалось → пробуем запасной адрес ниже
                }
                else det.Append("API: 200, но JSON не разобран\n\n");
            }

            // ── 2. ЗАПАСНОЙ адрес (76.E/77.C): update.json напрямую, БЕЗ API (тот же CDN — не панацея) ──
            var fbUrl = $"{repo.latestDownload}/{MANIFEST_NAME}";
            var fb = request($"update.json ЗАПАСНОЙ (без API): {fbUrl}", fbUrl);
            if (fb.attempts.Any(it => it.result?.httpCode == 404)) return new UpdateCheckResult.NoReleases();
            if (fb.ok)
            {
                UpdateManifest? manifest = null;
                try { manifest = KJson.decodeFromString<UpdateManifest>(fb.result!.body, KJson.Compact); } catch (Exception) { }
                if (manifest == null) return new UpdateCheckResult.Error(UpdateErrorKind.MANIFEST_PARSE, "запасной скачан, но JSON не разобран");
                // Адрес установщика — ПО ТЕГУ релиза, а не «latest/download/<файл>»: тот ломается, как только выходит релиз
                // новее — имя файла в манифесте старое, а latest уже ведёт на новый релиз → 404 (поле Android, Fold 0.58).
                var tag = tagFromRedirects(fb.result?.redirectChain ?? []);
                var sigErr = verifySignature(fb.result!.body, tag != null ? $"{repo.tagDownload(tag)}/{MANIFEST_NAME}{UpdateSignature.SIG_SUFFIX}" : $"{fbUrl}{UpdateSignature.SIG_SUFFIX}");
                if (sigErr != null) return sigErr;
                return compareByManifest(manifest, release: null, repo, via: $"через запасной адрес · {repo.slug}{tempNote(fb)}",
                    assetUrl: fn => tag != null ? $"{repo.tagDownload(tag)}/{fn}" : $"{repo.latestDownload}/{fn}");
            }

            // ── всё не вышло → типизированная ошибка (77.C / 80.C) ──
            // Прямой путь к GitHub заблокирован, а свой SOCKS не слушает = НЕ «GitHub недоступен» (узел жив, недоступен
            // ПУТЬ). Условие проверяем по факту порта, не по флагу (77.A).
            if (!CascadeFetch.isOwnProxyUp())
                return new UpdateCheckResult.Error(UpdateErrorKind.NO_TUNNEL, $"напрямую не дошли до вложения: {lastHost(fb)}");
            if (!rel.ok)
                return new UpdateCheckResult.Error(UpdateErrorKind.API_UNAVAILABLE, $"не дошли: API={lastHost(rel)}; запасной={lastHost(fb)}");
            return new UpdateCheckResult.Error(UpdateErrorKind.MANIFEST_DOWNLOAD_FAILED, $"не дошли: вложение={lastHost(fb)}");
        }

        // ── СПИСОК АДРЕСОВ (121.B): по очереди. Available/UpToDate/AvailableUnverified — определённый НАШ ответ (стоп).
        UpdateCheckResult result = new UpdateCheckResult.Error(UpdateErrorKind.API_UNAVAILABLE, "адреса не пробованы");
        for (int idx = 0; idx < REPOS.Count; idx++)
        {
            var repo = REPOS[idx];
            if (REPOS.Count > 1) det.Append($"═══ Адрес {idx + 1}/{REPOS.Count}: {repo.slug} ═══\n");
            var r = checkRepo(repo);
            if (r is UpdateCheckResult.Available or UpdateCheckResult.UpToDate or UpdateCheckResult.AvailableUnverified) { result = r; break; }
            result = r;
            var why = r is UpdateCheckResult.Error e ? e.kind.ToString() : r.GetType().Name;
            if (idx < REPOS.Count - 1) det.Append($"→ адрес {repo.slug}: {why} — пробую следующий\n\n");
        }

        // Разбор по ТРЁМ узлам GitHub раздельно (80.C) — только на неуспехе (диагностика; happy-path не грузим).
        if (result is UpdateCheckResult.Error) det.Append('\n').Append(probeNodes()).Append('\n');

        return new CheckReport(result, det.ToString().Trim());
    }

    /**
     * Три РАЗНЫХ узла GitHub раздельно (80.C): по каждому — попытка напрямую и (если SOCKS слушает) через
     * свой прокси, короткий таймаут. Только для «Подробностей» на неуспехе.
     */
    private static string probeNodes()
    {
        var socksUp = CascadeFetch.isOwnProxyUp();
        var nodes = new[] { ("api.github.com", NODE_API), ("github.com", NODE_WEB), ("release-assets.githubusercontent.com", NODE_CDN) };
        var sb = new StringBuilder("Узлы GitHub (блокируются раздельно):\n");
        foreach (var (label, url) in nodes)
        {
            var direct = probeOne(url, direct: true);
            var viaProxy = socksUp ? probeOne(url, direct: false) : "прокси не запущен";
            sb.Append($"  • {label} — напрямую: {direct} · через прокси: {viaProxy}\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string probeOne(string url, bool direct)
    {
        var route = direct ? HttpRoute.DIRECT : new HttpRoute.Socks("127.0.0.1", CascadeFetch.ownSocksPort());
        var r = SubscriptionFetcher.fetch(url, UA, NODE_PROBE_TIMEOUT_MS, route);
        var finalHost = host(r.finalUrl);
        return r.exceptionClass ?? $"HTTP {r.httpCode}" + (finalHost.IsNotBlank() && !url.Contains(finalHost) ? $" ({finalHost})" : "");
    }

    /**
     * Сравнение по versionCode из update.json + выбор сборки под платформу. [assetUrl] отдаёт адрес установщика по
     * имени файла. [release] может быть null (запасной путь) — тогда размер вложения неизвестен.
     */
    private static UpdateCheckResult compareByManifest(UpdateManifest manifest, GithubRelease? release, Repo repo, string via, Func<string, string?> assetUrl)
    {
        // Промпт 121.C: релиз НАШ? Идентификатор из манифеста сверяем ДО скачивания.
        if (manifest.packageName.IsNotBlank() && manifest.packageName != applicationId())
            return new UpdateCheckResult.Error(UpdateErrorKind.FOREIGN_RELEASE, $"манифест указывает {manifest.packageName}, наш {applicationId()}");

        var current = currentVersionCode();
        var latestName = manifest.versionName.IfBlank(release == null ? "" : release.name.IfBlank(release.tagName));
        // Промпт 121.C: строго БОЛЬШЕ установленного (не «отличается») — иначе редирект на старый репозиторий
        // мог бы предложить ОТКАТ на прежнюю версию.
        if (manifest.versionCode <= current) return new UpdateCheckResult.UpToDate(current, latestName, via);

        var artifact = manifest.artifacts.FirstOrDefault(it => string.Equals(it.abi, PLATFORM, StringComparison.OrdinalIgnoreCase));
        if (artifact == null || artifact.fileName.IsBlank()) return new UpdateCheckResult.Error(UpdateErrorKind.NO_ARTIFACT);

        // Промпт 121.B: адреса скачивания по порядку — сначала с текущего адреса, затем releases/latest/download каждого адреса.
        var urls = new List<string>();
        var first = assetUrl(artifact.fileName);
        if (first.IsNotBlank()) urls.Add(first!);
        foreach (var r in REPOS) urls.Add($"{r.latestDownload}/{artifact.fileName}");
        urls = urls.Distinct().ToList();
        if (urls.Count == 0) return new UpdateCheckResult.Error(UpdateErrorKind.ASSET_MISSING, artifact.fileName);
        var size = release?.assets.FirstOrDefault(it => it.name == artifact.fileName)?.size is long sz && sz > 0 ? sz : -1;

        return new UpdateCheckResult.Available(
            versionCode: manifest.versionCode,
            versionName: latestName,
            notes: manifest.notes.IfBlank(release?.body ?? ""),
            artifact: artifact,
            downloadUrls: urls,
            sizeBytes: size,
            via: via);
    }

    /**
     * Запасной путь: update.json нет — разбираем метку (v0.12) и сравниваем числа с нашей versionName.
     * Новее? Сообщаем как НЕНАДЁЖНОЕ (нет SHA-256/имени файла → скачивать и ставить нельзя).
     */
    private static UpdateCheckResult fallbackByTag(GithubRelease release)
    {
        var tagTuple = versionTuple(release.tagName);
        var curTuple = versionTuple(currentVersionName());
        return compareTuples(tagTuple, curTuple) > 0
            ? new UpdateCheckResult.AvailableUnverified(tag: release.tagName, name: release.name.IfBlank(release.tagName), notes: release.body)
            : new UpdateCheckResult.UpToDate(currentVersionCode(), release.name.IfBlank(release.tagName), via: "по метке (без update.json)");
    }

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Digits();

    /// <summary>Все числа из строки версии по порядку: "v0.11 beta" → [0, 11], "1.2.3" → [1, 2, 3].</summary>
    private static List<int> versionTuple(string s) =>
        Digits().Matches(s).Select(m => m.Value.ToIntOrNull()).Where(v => v != null).Select(v => v!.Value).ToList();

    private static int compareTuples(List<int> a, List<int> b)
    {
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}
