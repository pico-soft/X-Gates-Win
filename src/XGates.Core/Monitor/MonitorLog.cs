using System.Text.Json.Serialization;
using XGates.Core.Platform;

namespace XGates.Core.Monitor;

/**
 * Одна запись журнала — ТОЛЬКО СОБЫТИЕ (смена состояния или происшествие), не рутина.
 * [kind]: "switch" (смена активного сервера любой причины), "monitor" (падение/восстановление/вердикт),
 * "net" (пропал/появился интернет), "error" (ошибка монитора). [text] — заголовок, [detail] — числа/причина.
 */
public sealed record LogEvent
{
    [JsonPropertyName("ts")] public required long ts { get; init; }
    [JsonPropertyName("kind")] public required string kind { get; init; }
    [JsonPropertyName("text")] public required string text { get; init; }
    [JsonPropertyName("detail")] public string detail { get; init; } = "";
}

/**
 * Кольцевой журнал последних [CAP] СОБЫТИЙ. Persist (monitor-log.json, атомарно) — переживает перезапуск.
 * Рутинные подтверждения нормы («ок»/«простой») сюда НЕ пишутся вообще (см. NetworkMonitor + признак
 * жизни MonitorStatus). Свежие в конце; UI показывает в обратном порядке. Порт 1:1 с monitor/MonitorLog.kt.
 */
public static class MonitorLog
{
    private const string TAG = "MonitorLog";
    private const string FILE_NAME = "monitor-log.json";
    private const string TMP_NAME = "monitor-log.json.tmp";
    private const int CAP = 200;
    private static readonly object Sync = new();

    private sealed record LogFile
    {
        [JsonPropertyName("events")] public IReadOnlyList<LogEvent> events { get; init; } = [];
    }

    private static readonly MutableStateFlow<IReadOnlyList<LogEvent>> _state = new([]);
    public static StateFlow<IReadOnlyList<LogEvent>> state => _state.asStateFlow();

    private static string file(Context context) => Path.Combine(context.filesDir, FILE_NAME);

    public static void init(Context context)
    {
        lock (Sync)
        {
            var f = file(context);
            if (!File.Exists(f)) return;
            try
            {
                _state.value = KJson.decodeFromString<LogFile>(File.ReadAllText(f), KJson.Compact).events.TakeLast(CAP).ToList();
            }
            catch (Exception e)
            {
                // Старый формат журнала (до Промпта 48) не парсится в LogEvent — просто начинаем пустым.
                Log.w(TAG, "load failed, keeping empty", e);
            }
        }
    }

    public static void add(Context context, LogEvent @event)
    {
        lock (Sync)
        {
            var next = _state.value.Append(@event).TakeLast(CAP).ToList();
            _state.value = next;
            try
            {
                KJson.atomicWrite(file(context), Path.Combine(context.filesDir, TMP_NAME),
                    KJson.encodeToString(new LogFile { events = next }, KJson.Compact));
            }
            catch (Exception e)
            {
                Log.w(TAG, "save failed", e);
            }
        }
    }

    /** Событие произвольного вида (время ставит сам). */
    public static void @event(Context context, string kind, string text, string detail = "") =>
        add(context, new LogEvent { ts = JavaSystem.currentTimeMillis(), kind = kind, text = text, detail = detail });

    /** Смена активного сервера — фиксируется НЕЗАВИСИМО от причины (старт/тест/ручной выбор/монитор). */
    public static void @switch(Context context, string? from, string to, string cause, string detail = "")
    {
        var d = detail.Length == 0 ? cause : $"{cause} · {detail}";
        add(context, new LogEvent { ts = JavaSystem.currentTimeMillis(), kind = "switch", text = $"{from ?? "—"} → {to}", detail = d });
    }

    public static void clear(Context context)
    {
        lock (Sync)
        {
            _state.value = [];
            try { File.Delete(file(context)); } catch (Exception) { }
        }
    }
}
