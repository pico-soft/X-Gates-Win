using System.Text.Json.Serialization;
using XGates.Core.Platform;
using XGates.Core.Xray;

namespace XGates.Core.Settings;

/// <summary>
/// Настройки, которых НЕТ в Android-версии (Windows-специфика). Отдельный файл win_settings.json, чтобы settings.json
/// оставался байт в байт совместим с Android (паритет, StoreCompatTests). Сейчас: порты локального прокси (§13.7) и
/// фрагментация ClientHello.
/// Автозапуск при входе в Windows здесь НЕ хранится — его источник правды сам реестр (HKCU\…\Run), чтобы
/// флажок не расходился с фактом.
/// </summary>
public sealed record WinSettings
{
    [JsonPropertyName("socksPort")] public int socksPort { get; init; } = XrayConfig.DEFAULT_SOCKS_PORT;
    [JsonPropertyName("httpPort")] public int httpPort { get; init; } = XrayConfig.DEFAULT_HTTP_PORT;
    [JsonPropertyName("tlsFragment")] public bool tlsFragment { get; init; }
}

public static class WinSettingsStore
{
    private const string TAG = "WinSettingsStore";
    private const string FILE_NAME = "win_settings.json";
    private const string TMP_NAME = "win_settings.json.tmp";
    private static readonly object Sync = new();

    private static readonly MutableStateFlow<WinSettings> _state = new(new WinSettings());
    public static StateFlow<WinSettings> state => _state.asStateFlow();
    public static WinSettings current() => _state.value;

    public static void init(Context context)
    {
        lock (Sync)
        {
            var f = Path.Combine(context.filesDir, FILE_NAME);
            if (!File.Exists(f)) return;
            try
            {
                var s = KJson.decodeFromString<WinSettings>(File.ReadAllText(f));
                // Битая пара (ручная правка файла) → дефолты: прокси должен подняться, а не молча не стартовать.
                _state.value = XrayConfig.portsError(s.socksPort, s.httpPort) == null ? s : new WinSettings();
            }
            catch (Exception e) { Log.w(TAG, "load failed, keeping defaults", e); }
        }
    }

    /// <summary>Сохранить. Пару портов проверяет вызывающий (<see cref="XrayConfig.portsError"/>); неверную не пишем.</summary>
    public static bool update(Context context, WinSettings @new)
    {
        if (XrayConfig.portsError(@new.socksPort, @new.httpPort) != null) return false;
        lock (Sync)
        {
            _state.value = @new;
            try { KJson.atomicWrite(Path.Combine(context.filesDir, FILE_NAME), Path.Combine(context.filesDir, TMP_NAME), KJson.encodeToString(@new)); }
            catch (Exception e) { Log.w(TAG, "save failed", e); }
        }
        return true;
    }

    /// <summary>Только тесты: вернуть дефолты в памяти.</summary>
    internal static void resetForTest() => _state.value = new WinSettings();
}
