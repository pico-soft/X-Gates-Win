using XGates.Core.Compat;
using XGates.Core.Settings;

namespace XGates.App;

public enum SettingKind { INT, DOUBLE, TEXT, BOOL }

/**
 * Одна строка раздела «Настройки» (IntSettingRow/DoubleSettingRow/UrlSettingRow/BoolSettingRow Kotlin): подпись,
 * единица, границы, как читать из настроек и как записать. Значения показываются как в Kotlin (Double — по правилам
 * Java Double.toString: «0.5», «2.0»).
 */
public sealed record SettingRow(
    string group,
    string label,
    string unit,
    SettingKind kind,
    Func<AppSettings, string> read,
    Func<AppSettings, string, AppSettings?> commit,   // null — значение не годится (поле подсвечивается, не сохраняем)
    string defaultText)
{
    /// <summary>Подпись как в SettingRowScaffold: «Метка, ед.».</summary>
    public string title => unit.Length == 0 ? label : $"{label}, {unit}";
    public bool changed(AppSettings s) => read(s) != read(SettingsStore.DEFAULTS);
}

/// <summary>Состав и правила раздела «Настройки» — порт SettingsSection (MainActivity.kt) без Android-строк.</summary>
public static class SettingsRows
{
    private static string D(double v) => JavaNumbers.JavaDoubleToString(v);

    private static SettingRow intRow(string group, string label, string unit, Func<AppSettings, int> get, Func<AppSettings, int, AppSettings> set, int min, int max) =>
        new(group, label, unit, SettingKind.INT, s => get(s).ToString(),
            (s, t) => t.ToIntOrNull() is int v && v >= min && v <= max ? set(s, v) : null,
            get(SettingsStore.DEFAULTS).ToString());

    private static SettingRow doubleRow(string group, string label, string unit, Func<AppSettings, double> get, Func<AppSettings, double, AppSettings> set, double min, double max) =>
        new(group, label, unit, SettingKind.DOUBLE, s => D(get(s)),
            // Kotlin: text.replace(',', '.').toDoubleOrNull() in min..max (NaN в диапазон не попадает).
            (s, t) => JavaNumbers.ParseJavaDouble(t.Replace(',', '.')) is double v && v >= min && v <= max ? set(s, v) : null,
            D(get(SettingsStore.DEFAULTS)));

    /// <summary>Текст во всю ширину (URL/UA). [requireHttp] — для URL требуем схему http(s), иначе просто не пусто.</summary>
    private static SettingRow textRow(string group, string label, Func<AppSettings, string> get, Func<AppSettings, string, AppSettings> set, bool requireHttp = true) =>
        new(group, label, "", SettingKind.TEXT, get,
            (s, t) =>
            {
                var v = t.KTrim();
                return v.IsNotBlank() && (!requireHttp || v.StartsWith("http://") || v.StartsWith("https://")) ? set(s, v) : null;
            },
            get(SettingsStore.DEFAULTS));

    private static SettingRow boolRow(string group, string label, Func<AppSettings, bool> get, Func<AppSettings, bool, AppSettings> set) =>
        new(group, label, "", SettingKind.BOOL, s => get(s) ? "true" : "false",
            (s, t) => t switch { "true" => set(s, true), "false" => set(s, false), _ => null },
            get(SettingsStore.DEFAULTS) ? "вкл" : "выкл");

    public static readonly IReadOnlyList<SettingRow> rows =
    [
        // «Автомониторинг» (MonitorSection Kotlin; блок интервалов перемера OptimizeChecksBlock — ещё не перенесён).
        boolRow("Автомониторинг", "Автомониторинг", s => s.monitorEnabled, (s, v) => s with { monitorEnabled = v }),
        doubleRow("Автомониторинг", "Порог прямого канала", "Мбит/с", s => s.monitorDirectThreshold, (s, v) => s with { monitorDirectThreshold = v }, 0.1, 100.0),
        doubleRow("Автомониторинг", "Порог туннеля", "Мбит/с", s => s.monitorTunnelThreshold, (s, v) => s with { monitorTunnelThreshold = v }, 0.1, 100.0),
        intRow("Автомониторинг", "Неудач подряд = падение", "", s => s.monitorFailuresToVerdict, (s, v) => s with { monitorFailuresToVerdict = v }, 1, 10),
        doubleRow("Замер скорости", "Прогрев перед замером", "с", s => s.speedWarmupSec, (s, v) => s with { speedWarmupSec = v }, 0.0, 60.0),
        doubleRow("Замер скорости", "Окно замера", "с", s => s.speedWindowSec, (s, v) => s with { speedWindowSec = v }, 0.5, 120.0),
        intRow("Замер скорости", "Объём прогрева", "МБ", s => s.speedWarmupMb, (s, v) => s with { speedWarmupMb = v }, 1, 500),
        intRow("Замер скорости", "Одновременных замеров (пул)", "", s => s.speedPool, (s, v) => s with { speedPool = v }, 1, 32),
        textRow("Замер скорости", "URL пробника скорости", s => s.speedProbeUrl, (s, v) => s with { speedProbeUrl = v }),
        intRow("Пинг", "Таймаут пинга", "мс", s => s.pingTimeoutMs, (s, v) => s with { pingTimeoutMs = v }, 500, 30_000),
        intRow("Пинг", "Одновременных пингов (пул)", "", s => s.pingPool, (s, v) => s with { pingPool = v }, 1, 32),
        textRow("Подписки", "User-Agent загрузки", s => s.subUserAgent, (s, v) => s with { subUserAgent = v }, requireHttp: false),
        intRow("Подписки", "Таймаут загрузки", "с", s => s.subTimeoutSec, (s, v) => s with { subTimeoutSec = v }, 3, 120),
        doubleRow("Выбор сервера", "Минимальная полезная скорость", "Мбит/с", s => s.minUsableMbps, (s, v) => s with { minUsableMbps = v }, 0.0, 100.0),
        intRow("Выбор сервера", "Запас для апгрейда", "%", s => s.upgradeMarginPercent, (s, v) => s with { upgradeMarginPercent = v }, 0, 100),
        doubleRow("Выбор сервера", "Держать лучший: во сколько раз быстрее", "×", s => s.keepBestMultiplier, (s, v) => s with { keepBestMultiplier = v }, 1.0, 100.0),
        doubleRow("Соединение и отдача", "Порог «медленный туннель»", "Мбит/с (0=выкл)", s => s.degradationMinMbps, (s, v) => s with { degradationMinMbps = v }, 0.0, 100.0),
        intRow("Соединение и отдача", "Проверка деградации не чаще", "мин", s => s.degradationCheckSec / 60, (s, v) => s with { degradationCheckSec = v * 60 }, 1, 240),
        doubleRow("Соединение и отдача", "Минимальная отдача", "Мбит/с", s => s.minUploadMbps, (s, v) => s with { minUploadMbps = v }, 0.0, 1000.0),
        intRow("Соединение и отдача", "Объём замера отдачи", "МБ", s => s.uploadMeasureMb, (s, v) => s with { uploadMeasureMb = v }, 1, 100),
        textRow("Соединение и отдача", "URL приёма отдачи", s => s.uploadProbeUrl, (s, v) => s with { uploadProbeUrl = v }),
        boolRow("Прочее", "Автозапуск при старте", s => s.autoStartOnLaunch, (s, v) => s with { autoStartOnLaunch = v }),
        boolRow("Прочее", "Сообщать о новых версиях", s => s.notifyNewVersions, (s, v) => s with { notifyNewVersions = v }),
        // «Обходить системный VPN» — Android; на Windows обход VPN — фаза 2 (docs/decisions.md), строку не показываем.
        boolRow("Прочее", "Подробные логи", s => s.verboseLogs, (s, v) => s with { verboseLogs = v }),
    ];
}
