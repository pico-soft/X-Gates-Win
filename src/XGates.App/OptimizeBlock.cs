using System.Globalization;
using XGates.Core.Compat;
using XGates.Core.Settings;

namespace XGates.App;

/**
 * «Периодический перемер и проверки» (OptimizeChecksBlock Kotlin, Пр.131/133): интервалы ползунками с ЦЕНОЙ,
 * раздельные наборы обычный/экономия (правки идут в АКТИВНЫЙ набор по trafficSaveMode), проверки живости — общие.
 */
public static class OptimizeBlock
{
    public static readonly IReadOnlyList<int> OPT_SECS = [900, 1800, 3600, 10800, 21600, 43200, 86400, 0];
    public static readonly IReadOnlyList<string> OPT_LABELS = ["15 мин", "30 мин", "1 час", "3 часа", "6 часов", "12 часов", "сутки", "Выкл"];
    public static readonly IReadOnlyList<int> CHK_SECS = [30, 60, 120, 300, 600, 900];
    public static readonly IReadOnlyList<string> CHK_LABELS = ["30 сек", "1 мин", "2 мин", "5 мин", "10 мин", "15 мин"];
    private const long EST_CHECK_BYTES = 3000L;

    public static int nearestIndex(IReadOnlyList<int> values, int cur)
    {
        var i = values.ToList().IndexOf(cur);
        if (i >= 0) return i;
        if (cur <= 0 && values[^1] == 0) return values.Count - 1;   // 0 = «Выкл» справа
        return Enumerable.Range(0, values.Count).MinBy(k => Math.Abs(values[k] - cur));
    }

    /// <summary>Расход поддержания топа, МБ/мес: замер на ДОСТАТОЧНОСТЬ ≈ прогрев 0.5 МБ + (порог/8)×1.2 МБ.</summary>
    public static double optimizeMbPerMonth(int optSec, int topBatch, int mix, double sufficientMbps)
    {
        if (optSec <= 0) return 0.0;
        var cyclesPerMonth = 2_592_000.0 / optSec;
        var perMeasureMb = 0.5 + sufficientMbps / 8.0 * 1.2;
        return cyclesPerMonth * (topBatch + mix) * perMeasureMb;
    }

    /// <summary>Kotlin "%.1f".format — по ЯЗЫКУ системы (на русской Windows — запятая, как на телефоне).</summary>
    public static string fmtData(double mb, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        return mb >= 1024.0 ? $"≈ {(mb / 1024.0).ToString("F1", c)} ГБ/мес" : $"≈ {mb.ToString("F0", c)} МБ/мес";
    }

    public static string modeName(AppSettings s) => s.trafficSaveMode ? "Экономия" : "Обычный";

    public static string modeLine(AppSettings s)
    {
        var reason = !s.economyEnabled ? "экономия выключена" : !s.trafficSaveAutoByNetwork ? "экономия всегда" : s.trafficSaveMode ? "мобильная сеть" : "Wi-Fi (экономия только на моб.)";
        return $"⚙ Правите набор режима: {modeName(s)} ({reason}). Смена сети подставит другой набор — цифры изменятся сами (это не сбой).";
    }

    public static int optimizeIndex(AppSettings s) => nearestIndex(OPT_SECS, s.activeOptimizeSec);
    public static int checkIndex(AppSettings s) => nearestIndex(CHK_SECS, s.connectionCheckIntervalSec);

    public static AppSettings pickOptimize(AppSettings s, int i) =>
        s.trafficSaveMode ? s with { optimizeSecSave = OPT_SECS[i] } : s with { optimizeSecNormal = OPT_SECS[i] };

    public static AppSettings pickCheck(AppSettings s, int i) => s with { connectionCheckIntervalSec = CHK_SECS[i] };

    private static SettingRow intRow(string label, string unit, Func<AppSettings, int> get, int def, Func<AppSettings, int, AppSettings> set, int min, int max) =>
        new("Перемер", label, unit, SettingKind.INT, s => get(s).ToString(),
            (s, t) => t.ToIntOrNull() is int v && v >= min && v <= max ? set(s, v) : null, def.ToString());

    /// <summary>Строки блока для ТЕКУЩЕГО набора (состав зависит от режима и от того, включён ли перемер).</summary>
    public static IReadOnlyList<SettingRow> rows(AppSettings s)
    {
        var dd = SettingsStore.DEFAULTS;
        var save = s.trafficSaveMode;
        var list = new List<SettingRow>
        {
            intRow("Сколько быстрых набирать («Самый быстрый»)", "", x => x.fastTopTarget, dd.fastTopTarget, (x, v) => x with { fastTopTarget = v }, 1, 50),
            intRow("Свежесть «Живых» (список 1)", "мин", x => x.liveFreshSec / 60, dd.liveFreshSec / 60, (x, v) => x with { liveFreshSec = v * 60 }, 5, 1440),
        };
        if (s.activeOptimizeSec > 0)
        {
            list.Add(intRow("Размер топа (быстрых серверов)", "", x => x.activeTopBatch, save ? dd.topBatchSave : dd.topBatchNormal,
                (x, v) => x.trafficSaveMode ? x with { topBatchSave = v } : x with { topBatchNormal = v }, 1, 50));
            list.Add(new SettingRow("Перемер", "Порог достаточности (видео)", "Мбит/с", SettingKind.DOUBLE,
                x => JavaNumbers.JavaDoubleToString(x.sufficientMbps),
                (x, t) => JavaNumbers.ParseJavaDouble(t.Replace(',', '.')) is double v && v >= 1.0 && v <= 100.0 ? x with { sufficientMbps = v } : null,
                JavaNumbers.JavaDoubleToString(dd.sufficientMbps)));
            list.Add(intRow("Бюджет ТОЧНОГО замера", "МБ", x => x.activeMeasureMb, save ? dd.measureMbSave : dd.measureMbNormal,
                (x, v) => x.trafficSaveMode ? x with { measureMbSave = v } : x with { measureMbNormal = v }, 1, 100));
            list.Add(intRow("Предел давности замера", "ч", x => x.topFreshSec / 3600, dd.topFreshSec / 3600, (x, v) => x with { topFreshSec = v * 3600 }, 1, 168));
            list.Add(intRow("Подмешивать неизмеренных", "", x => x.topMixUnmeasured, dd.topMixUnmeasured, (x, v) => x with { topMixUnmeasured = v }, 0, 5));
            list.Add(new SettingRow("Перемер", "Перемер только по Wi-Fi", "", SettingKind.BOOL, x => x.monitorOptimizeWifiOnly ? "true" : "false",
                (x, t) => t switch { "true" => x with { monitorOptimizeWifiOnly = true }, "false" => x with { monitorOptimizeWifiOnly = false }, _ => null },
                dd.monitorOptimizeWifiOnly ? "вкл" : "выкл"));
            list.Add(intRow("Не перемерять при простое дольше", "мин (0=всегда)", x => x.monitorOptimizeIdleSkipSec / 60, dd.monitorOptimizeIdleSkipSec / 60,
                (x, v) => x with { monitorOptimizeIdleSkipSec = v * 60 }, 0, 240));
            list.Add(new SettingRow("Перемер", "Не перемерять, если текущий быстрее", "Мбит/с (0=всегда)", SettingKind.DOUBLE,
                x => JavaNumbers.JavaDoubleToString(x.monitorOptimizeSkipAboveMbps),
                (x, t) => JavaNumbers.ParseJavaDouble(t.Replace(',', '.')) is double v && v >= 0.0 && v <= 1000.0 ? x with { monitorOptimizeSkipAboveMbps = v } : null,
                JavaNumbers.JavaDoubleToString(dd.monitorOptimizeSkipAboveMbps)));
        }
        return list;
    }

    public const string FRESH_HINT = "Сервер с живым пингом и измеренной скоростью держится в «Живых», пока замер не старше этого окна; старее — уходит в «Ранее измеренные» (фон перемеряет их и возвращает ожившие в «Живые»).";
    public const string TOP_HINT = "Держим актуальным ТОП быстрых. Мерим только кандидатов и по поводам: не измерян / замер старше предела давности / провал. Замер идёт на ДОСТАТОЧНОСТЬ — качаем, пока не подтвердится порог видео, и стоп (точное число не нужно). Пинг — лишь первое приближение (под DPI низкий пинг ≠ высокая скорость), поэтому подмешиваем неизмеренных. Массового замера всех НЕТ. «Бюджет точного замера» — для ручного полного теста.";
    public const string OFF_HINT = "Перемер выключен — на замеры скорости в фоне трафик не тратится.";

    private static double optimizeMb(AppSettings s) => optimizeMbPerMonth(s.activeOptimizeSec, s.activeTopBatch, s.topMixUnmeasured, s.sufficientMbps);

    public static string topCostLine(AppSettings s) =>
        $"Расход поддержания топа: {fmtData(optimizeMb(s))} — ОЦЕНКА СВЕРХУ (замер на достаточность стоит доли полного; предохранители и свежий топ пропускают перемеры — реально меньше).";

    private static double checkMb(AppSettings s)
    {
        var chkSec = Math.Max(CHK_SECS[checkIndex(s)], 15);
        return 2_592_000.0 / chkSec * EST_CHECK_BYTES / 1_048_576.0;
    }

    public static string checkCostLine(AppSettings s) =>
        $"Расход проверок: {fmtData(checkMb(s))} — копейки. Реже проверка — ДОЛЬШЕ сидишь с мёртвым туннелем, если он упадёт; поэтому её не отключаем.";

    public static string totalLine(AppSettings s) =>
        $"ИТОГО на замеры в фоне ({modeName(s)}): {(s.activeOptimizeSec > 0 ? fmtData(optimizeMb(s)) : "≈ 0 (перемер выкл — только реактивно при потере связи)")}. Проверки связи: {fmtData(checkMb(s))}. Контраст копеек и гигабайтов и объясняет, почему экономим на замерах, а на проверках — нет.";
}
