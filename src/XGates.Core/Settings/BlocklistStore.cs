using System.Text.Json.Serialization;
using XGates.Core.Compat;
using XGates.Core.Platform;

namespace XGates.Core.Settings;

// Порт 1:1 с settings/BlocklistStore.kt (data class — tools/port/kt2cs_dataclass.py).

/**
 * Одна персональная блокировка сервера — точечно по serverKey (НАШЕ расширение поверх эталона).
 * [name] храним рядом с ключом, чтобы список заблокированных читался, даже когда сервер пропал из
 * подписки (иначе снять точечную блокировку было бы нечем — ключ без имени нечитаем).
 */
public sealed record BlockedServer
{
    [JsonPropertyName("serverKey")] public required string serverKey { get; init; }
    [JsonPropertyName("name")] public required string name { get; init; }
    [JsonPropertyName("blockedAt")] public long blockedAt { get; init; } = 0L;
}

/**
 * Пользовательское переименование сервера (оверрайд по serverKey). [originalName] — имя провайдера на
 * момент переименования: нужно и для диалога («Имя от сервера: …»), и чтобы список читался, когда
 * сервер пропал из подписки, и для «применить ко всем с таким же ИСХОДНЫМ именем».
 */
public sealed record ServerRename
{
    [JsonPropertyName("serverKey")] public required string serverKey { get; init; }
    [JsonPropertyName("customName")] public required string customName { get; init; }
    [JsonPropertyName("originalName")] public required string originalName { get; init; }
    [JsonPropertyName("renamedAt")] public long renamedAt { get; init; } = 0L;
}

/**
 * Отложенный сервер («на паузе», Промпт 91) — оверрайд по serverKey, ОТДЕЛЬНЫЙ от стоп-листа:
 * стоп-лист прячет отовсюду и снимается через настройки; отложенный скрыт из «Живых» и автовыбора,
 * но ВИДЕН в «Все серверы» с кнопкой возврата на месте. [name] — как у [BlockedServer], для читаемости
 * записи, когда сервер пропал из подписки.
 */
public sealed record PausedServer
{
    [JsonPropertyName("serverKey")] public required string serverKey { get; init; }
    [JsonPropertyName("name")] public required string name { get; init; }
    [JsonPropertyName("pausedAt")] public long pausedAt { get; init; } = 0L;
}

/**
 * Стоп-лист = ДВА типа правил:
 *  - [words] — текстовые правила, ищутся в ИМЕНИ сервера (как эталон Termux check_blacklisted:
 *    подстрока, регистронезависимо). Хранятся уже приведёнными к lower-case.
 *  - [servers] — точечные блокировки по serverKey (наше расширение): режет ИМЕННО этот вариант,
 *    не всех одноимённых.
 *
 * Хранится ОТДЕЛЬНЫМ файлом (blocklist.json в filesDir), НЕ внутри профилей и НЕ в кэше подписки:
 * refresh подписки перезаписывает список серверов целиком и стёр бы правила ([overrides-and-blocklist-spec]).
 */
public sealed record Blocklist
{
    [JsonPropertyName("words")] public IReadOnlyList<string> words { get; init; } = [];
    [JsonPropertyName("servers")] public IReadOnlyList<BlockedServer> servers { get; init; } = [];
    [JsonPropertyName("renames")] public IReadOnlyList<ServerRename> renames { get; init; } = [];  // добавлено в Промпт 44 (старый JSON читается — default)
    [JsonPropertyName("paused")] public IReadOnlyList<PausedServer> paused { get; init; } = [];  // Промпт 91: «на паузе» (старый JSON читается — default)

    private HashSet<string> blockedKeys => servers.Select(it => it.serverKey).ToHashSet();
    private Dictionary<string, string> nameByKey
    {
        get { var m = new Dictionary<string, string>(); foreach (var it in renames) m[it.serverKey] = it.customName; return m; } // associate: последний побеждает
    }
    private HashSet<string> pausedKeys => paused.Select(it => it.serverKey).ToHashSet();

    public bool isServerBlocked(string serverKey) => blockedKeys.Contains(serverKey);

    /** Отложен ли сервер («на паузе»). Отдельная причина от стоп-листа — не смешивать. */
    public bool isPaused(string serverKey) => pausedKeys.Contains(serverKey);

    /** Пользовательское имя для [serverKey] или null, если не переименован. */
    public string? customName(string serverKey) => nameByKey.TryGetValue(serverKey, out var n) ? n : null;

    /**
     * Правило-слово блокирует, если оно подстрока ЛЮБОГО из переданных имён (исходного И пользовательского),
     * регистронезависимо (подстрока — 1:1 с эталоном). Проверять ОБА имени обязательно (см. пункт D2 спеки):
     * иначе переименование молча снимало бы блокировку, а заблокировать по своему имени было бы нельзя.
     */
    public bool matchesWord(params string?[] names)
    {
        if (words.Count == 0) return false;
        var ns = names.Where(n => n != null).Select(it => it!.Lowercase()).ToList();
        return words.Any(w => w.IsNotBlank() && ns.Any(it => it.Contains(w, StringComparison.Ordinal)));
    }

    /** ЕДИНЫЙ предикат блокировки: точечно по ключу ИЛИ по слову в исходном/пользовательском имени. */
    public bool isBlocked(string originalName, string? customName, string serverKey) =>
        isServerBlocked(serverKey) || matchesWord(originalName, customName);

    /**
     * Сколько серверов блокирует слово (для счётчика у чипа; 0 → вероятно опечатка). [names] — пары
     * (исходное имя, пользовательское или null): учитываем ОБА, чтобы счётчик не разошёлся с фактом (D3).
     */
    public int countForWord(string word, IReadOnlyList<(string o, string? c)> names)
    {
        var w = word.Lowercase();
        if (w.IsBlank()) return 0;
        return names.Count(p => p.o.Lowercase().Contains(w, StringComparison.Ordinal) || (p.c != null && p.c.Lowercase().Contains(w, StringComparison.Ordinal)));
    }
}

/**
 * Хранилище стоп-листа. Тот же паттерн, что [SettingsStore] (StateFlow + атомарная запись temp→rename
 * в filesDir). Отдельно от настроек — это оверрайды, а не пороги; сброс настроек к дефолтам стоп-лист
 * не трогает.
 */
public static class BlocklistStore
{
    private const string TAG = "BlocklistStore";
    private const string FILE_NAME = "blocklist.json";
    private const string TMP_NAME = "blocklist.json.tmp";
    private static readonly object Sync = new();

    /**
     * Дефолтный RU/BY стоп-лист — 1:1 с эталоном (DEFAULT_BLACKLIST в xproxy_lib.py). Ставится ТОЛЬКО
     * при первом запуске (файла ещё нет). Если пользователь удалил все слова — файл существует пустым,
     * повторно НЕ засеваем (иначе удаление было бы невозможным).
     */
    public static readonly IReadOnlyList<string> DEFAULT_WORDS =
    [
        "москва", "россия", "санкт-петербург", "беларусь", "минск", "крым",
        "moscow", "russia", "st. petersburg", "belarus", "minsk", "crimea",
    ];

    private static readonly MutableStateFlow<Blocklist> _state = new(new Blocklist());
    public static StateFlow<Blocklist> state => _state.asStateFlow();
    public static Blocklist current() => _state.value;

    // Пр.147: ВРЕМЕННОЕ снятие стоп-СЛОВ (кнопка на главной). Транзиентно (в памяти, НЕ персистится) — при
    // перезапуске ограничения возвращаются («временно»). Пока включено, ServerFilter.isBlocked игнорирует
    // блокировку ПО СЛОВУ (точечные блоки по serverKey и «паузы» остаются — это ручные решения пользователя).
    private static readonly MutableStateFlow<bool> _stopWordsSuspended = new(false);
    public static StateFlow<bool> stopWordsSuspended => _stopWordsSuspended.asStateFlow();
    public static void setStopWordsSuspended(bool v) => _stopWordsSuspended.value = v;

    private static string file(Context context) => Path.Combine(context.filesDir, FILE_NAME);

    /** Загрузить при старте (MainActivity.onCreate). Нет файла → первый запуск → засеять дефолтом. */
    public static void init(Context context)
    {
        lock (Sync)
        {
            var f = file(context);
            if (!File.Exists(f))
            {
                save(context, new Blocklist { words = DEFAULT_WORDS });
                return;
            }
            try
            {
                _state.value = KJson.decodeFromString<Blocklist>(File.ReadAllText(f));
            }
            catch (Exception e)
            {
                Log.w(TAG, "load failed, keeping empty", e);
            }
        }
    }

    private static void save(Context context, Blocklist @new)
    {
        lock (Sync)
        {
            _state.value = @new;
            try { KJson.atomicWrite(file(context), Path.Combine(context.filesDir, TMP_NAME), KJson.encodeToString(@new)); }
            catch (Exception e) { Log.w(TAG, "save failed", e); }
        }
    }

    /** Kotlin `list - element`: убрать ПЕРВОЕ вхождение. */
    private static List<T> minus<T>(IReadOnlyList<T> list, T element)
    {
        var r = list.ToList();
        r.Remove(element);
        return r;
    }

    /** Добавить слово-правило (нормализуем trim+lower, без дублей). false — пусто/дубль. */
    public static bool addWord(Context context, string word)
    {
        var w = word.KTrim().Lowercase();
        if (w.IsBlank() || current().words.Contains(w)) return false;
        var it = current();
        save(context, it with { words = [.. it.words, w] });
        return true;
    }

    public static void removeWord(Context context, string word)
    {
        var it = current();
        save(context, it with { words = minus(it.words, word) });
    }

    /** Точечно заблокировать сервер. [nowMs] передаёт вызывающий (стор без обращения к часам — тестируемость). */
    public static void blockServer(Context context, string serverKey, string name, long nowMs)
    {
        if (current().isServerBlocked(serverKey)) return;
        var it = current();
        save(context, it with { servers = [.. it.servers, new BlockedServer { serverKey = serverKey, name = name, blockedAt = nowMs }] });
    }

    public static void unblockServer(Context context, string serverKey)
    {
        var it = current();
        save(context, it with { servers = it.servers.Where(s => s.serverKey != serverKey).ToList() });
    }

    /**
     * Переименовать. [keysWithOriginal] — пары (serverKey, исходное имя): один элемент = один сервер,
     * несколько = «применить ко всем с таким же исходным именем». Пустое [customName] = СБРОС (удаляем
     * записи для этих ключей). Одна запись в файл на всю операцию (batch), без множественных сохранений.
     */
    public static void rename(Context context, IReadOnlyList<(string key, string orig)> keysWithOriginal, string customName, long nowMs)
    {
        var trimmed = customName.KTrim();
        var keys = keysWithOriginal.Select(it => it.key).ToHashSet();
        var cur = current();
        var without = cur.renames.Where(it => !keys.Contains(it.serverKey)).ToList();
        var next = trimmed.Length == 0 ? without
                   : without.Concat(keysWithOriginal.Select(p => new ServerRename { serverKey = p.key, customName = trimmed, originalName = p.orig, renamedAt = nowMs })).ToList();
        save(context, cur with { renames = next });
    }

    /** Сброс пользовательского имени одного сервера («Вернуть имя от сервера»). */
    public static void clearName(Context context, string serverKey)
    {
        var it = current();
        save(context, it with { renames = it.renames.Where(r => r.serverKey != serverKey).ToList() });
    }

    /** Отложить сервер («На паузе»). Идемпотентно. [nowMs] передаёт вызывающий (стор без часов — тестируемость). */
    public static void pauseServer(Context context, string serverKey, string name, long nowMs)
    {
        if (current().isPaused(serverKey)) return;
        var it = current();
        save(context, it with { paused = [.. it.paused, new PausedServer { serverKey = serverKey, name = name, pausedAt = nowMs }] });
    }

    /** Вернуть сервер из паузы. */
    public static void resumeServer(Context context, string serverKey)
    {
        var it = current();
        save(context, it with { paused = it.paused.Where(p => p.serverKey != serverKey).ToList() });
    }
}
