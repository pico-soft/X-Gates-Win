using System.Text.Json.Serialization;
using XGates.Core.Platform;

namespace XGates.Core.Update;

/**
 * Хранит РЕЗУЛЬТАТ ПОСЛЕДНЕЙ проверки обновления и её время (для показа «результат последней проверки с
 * временем» в Настройках, переживает перезапуск) + держит в памяти живой [UpdateCheckResult]
 * для действий (кнопка скачивания появляется только для Available). Отдельно — троттлинг авто-проверки
 * при холодном старте (не чаще раза в сутки; это несколько КБ). Порт update/UpdateStore.kt.
 */
public static class UpdateStore
{
    private const string TAG = "UpdateStore";
    private const string FILE = "update_check.json";
    private const string TMP = "update_check.json.tmp";
    private const long DAY_MS = 24L * 60 * 60 * 1000;

    public sealed record Record
    {
        [JsonPropertyName("checkedAtMs")] public long checkedAtMs { get; init; }     // время последней завершённой проверки (0 = не было)
        [JsonPropertyName("summary")] public string summary { get; init; } = "";     // человекочитаемый итог
        [JsonPropertyName("updateAvailable")] public bool updateAvailable { get; init; }
        [JsonPropertyName("details")] public string details { get; init; } = "";     // полная постадийная диагностика (77.E)
        // Промпт 93.J/K/L: доступная версия ПЕРСИСТИТСЯ (полоса переживает перезапуск),
        // + состояние «уже показали»/«отклонено» (одно уведомление на версию, снятие в двух местах).
        [JsonPropertyName("availCode")] public int availCode { get; init; }          // versionCode доступного обновления (0 = нет)
        [JsonPropertyName("availName")] public string availName { get; init; } = "";
        [JsonPropertyName("availNotes")] public string availNotes { get; init; } = "";
        [JsonPropertyName("dismissedCode")] public int dismissedCode { get; init; }  // версия, которую пользователь отклонил
        [JsonPropertyName("notifiedCode")] public int notifiedCode { get; init; }    // версия, о которой уже показали уведомление
    }

    private static readonly MutableStateFlow<Record> _record = new(new Record());
    public static StateFlow<Record> record => _record.asStateFlow();

    // Живой результат этой сессии — для кнопки скачивания (не персистим: Available с URL/размером теряется
    // при перезапуске, пользователь просто перепроверит; авто-проверка на старте его восстановит).
    private static readonly MutableStateFlow<UpdateCheckResult?> _live = new(null);
    public static StateFlow<UpdateCheckResult?> live => _live.asStateFlow();

    /// <summary>Сброс состояния процесса (только тесты: init с пустым каталогом прежнюю запись не стирает).</summary>
    internal static void resetForTest() { _record.value = new Record(); _live.value = null; }

    public static void init(Context context)
    {
        var f = Path.Combine(context.filesDir, FILE);
        if (!File.Exists(f)) return;
        try { _record.value = KJson.decodeFromString<Record>(File.ReadAllText(f), KJson.Compact); }
        catch (Exception e) { Log.w(TAG, "load failed", e); }
    }

    /// <summary>Сохранить итог проверки: живой результат (для кнопок) + персистентная запись (сводка+время+подробности).</summary>
    public static void apply(Context context, CheckReport report, long nowMs)
    {
        var result = report.result;
        _live.value = result;
        var prev = _record.value;
        var avail = result as UpdateCheckResult.Available;
        var rec = prev with
        {
            checkedAtMs = nowMs,
            summary = summarize(result),
            updateAvailable = result is UpdateCheckResult.Available or UpdateCheckResult.AvailableUnverified,
            details = report.details,
            // Доступную версию сохраняем только для проверяемого Available (с update.json); иначе полоса не нужна.
            availCode = avail?.versionCode ?? 0,
            availName = avail?.versionName ?? "",
            availNotes = avail?.notes ?? "",
            // dismissedCode/notifiedCode НЕ сбрасываем — переживают проверки (одно уведомление на версию).
        };
        save(context, rec);
    }

    private static void save(Context context, Record rec)
    {
        _record.value = rec;
        try { KJson.atomicWrite(Path.Combine(context.filesDir, FILE), Path.Combine(context.filesDir, TMP), KJson.encodeToString(rec, KJson.Compact)); }
        catch (Exception e) { Log.w(TAG, "save failed", e); }
    }

    // ── Промпт 93.J/K/L: полоса и уведомление о новой версии ──
    private static int currentCode() => UpdateChecker.currentVersionCode();   // = AppInfo.VERSION_CODE (подменяется в тестах)

    /** Инфо для ПОЛОСЫ: доступная версия НОВЕЕ текущей, не отклонена, настройка вкл. Иначе null. */
    public static (int code, string name)? bannerVersion(bool notifyOn)
    {
        var r = _record.value;
        return notifyOn && r.availCode > currentCode() && r.availCode != r.dismissedCode ? (r.availCode, r.availName) : null;
    }

    public static string availNotesFirstLine() =>
        _record.value.availNotes.Split('\n').Select(it => it.Trim()).FirstOrDefault(it => it.Length > 0) ?? "";

    /// <summary>Показать ли УВЕДОМЛЕНИЕ: новее + не отклонено + ещё НЕ показывали (одно на версию) + настройка вкл.</summary>
    public static bool shouldNotify(bool notifyOn)
    {
        var r = _record.value;
        return notifyOn && r.availCode > currentCode() && r.availCode != r.dismissedCode && r.availCode != r.notifiedCode;
    }

    /// <summary>Уведомление показано — больше про эту версию не напоминаем (даже после перезапусков).</summary>
    public static void markNotified(Context context) { var r = _record.value; if (r.availCode != 0) save(context, r with { notifiedCode = r.availCode }); }

    /// <summary>Пользователь отклонил (крестик полосы / установка) — снять полосу И уведомление для ЭТОЙ версии.</summary>
    public static void markDismissed(Context context) { var r = _record.value; if (r.availCode != 0) save(context, r with { dismissedCode = r.availCode }); }

    /// <summary>Пора ли авто-проверить при холодном старте (не чаще раза в сутки).</summary>
    public static bool dueForAutoCheck(long nowMs)
    {
        var at = _record.value.checkedAtMs;
        return at == 0L || nowMs - at >= DAY_MS;
    }

    private static string summarize(UpdateCheckResult result) => result switch
    {
        UpdateCheckResult.NoReleases => "Релизов пока нет",
        // НЕ утверждаем «последняя» (проверка сравнивает лишь с прочитанным манифестом — он мог быть недоступен/
        // устареть → ложное «последняя»). Номер версии НЕ пишем (запутывает — могли выйти релизы новее).
        UpdateCheckResult.UpToDate u => "Новее версии не найдено" + (u.via.Length > 0 ? $" · {u.via}" : ""),
        // Номер версии НЕ показываем: часто на GitHub уже вышли релизы новее той, что нашла эта проверка.
        UpdateCheckResult.Available a => "Доступно обновление" + (a.via.Length > 0 ? $" · {a.via}" : ""),
        UpdateCheckResult.AvailableUnverified v => $"Есть версия новее по метке {v.tag}, но без update.json — проверить нельзя",
        UpdateCheckResult.Error e => e.kind.text() + (e.detail.Length > 0 ? $" — {e.detail}" : ""),
        _ => "",
    };

    /// <summary>Версия ядра xray. Может быть недоступна — тогда null.</summary>
    public static string? coreVersion()
    {
        try { var v = Libv2ray.Libv2ray.checkVersionX(); return string.IsNullOrWhiteSpace(v) ? null : v; }
        catch (Exception) { return null; }
    }

    public static string appVersion() => $"{AppInfo.VERSION_NAME} (code {AppInfo.VERSION_CODE})";
}

/**
 * Промпт 93.J: после самостоятельной проверки — показать уведомление о новой версии, если нужно
 * (настройка вкл, версия новее, не отклонена, ещё не показывали). ОДНО уведомление на версию. Порт UpdateNotifier.kt.
 */
public static class UpdateNotifier
{
    public static void maybeNotify(Context context)
    {
        var on = Settings.SettingsStore.current().notifyNewVersions;
        if (!UpdateStore.shouldNotify(on)) return;
        Service.NotificationHelper.notifyUpdate(context, UpdateStore.availNotesFirstLine());
        UpdateStore.markNotified(context);
    }
}
