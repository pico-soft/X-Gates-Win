using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Traffic;
using static XGates.App.UiFormat;

namespace XGates.App;

/// <summary>Блок «Трафик»: заголовок и строки «метка — значение» (жирная — «Итого»).</summary>
public sealed record MetricBlock(string title, IReadOnlyList<(string label, string value, bool bold)> rows);

/**
 * Разделы «Автомониторинг» (признак жизни + журнал) и «Трафик» вкладки «Настройки» — порт MonitorSection/
 * MonitorLogRow/TrafficSection (MainActivity.kt): те же тексты и формулы времени работы.
 */
public static class StatsModel
{
    /// <summary>Признак жизни монитора; пусто — монитор выключен (тогда строку не показываем).</summary>
    public static string heartbeatLine(AppSettings s, MonitorHeartbeat h, long nowMs)
    {
        if (!s.monitorEnabled) return "";
        var t = h.lastCheckMs > 0 ? monitorTime(h.lastCheckMs) : "—";
        var ago = h.lastCheckMs > 0 ? $"{Math.Max((nowMs - h.lastCheckMs) / 1000, 0)} с назад" : "—";
        var interval = NetworkMonitor.healthyCheckMs(s) / 1000;   // на шнуре — чаще (решение Elyor 2026-10-30)
        var power = NetworkMonitor.fastChecksOnAc(s) ? " (на шнуре)" : "";
        return $"● Проверка связи каждые {interval} с{power} · последняя: {t} ({ago}) · циклов: {h.cycles} · {h.state}";
    }

    /// <summary>Журнал: свежие сверху, строка «дата · событие» + подробность.</summary>
    public static IReadOnlyList<(string kind, string line, string detail)> journal() =>
        MonitorLog.state.value.AsEnumerable().Reverse().Select(e => (e.kind, $"{monitorTime(e.ts)} · {e.text}", e.detail)).ToList();

    /// <summary>Журнал целиком текстом для «Скопировать журнал»: версия и время, затем события, свежие сверху.</summary>
    public static string journalText(long nowMs)
    {
        var lines = new List<string> { $"X-Gates Win {XGates.Core.AppInfo.VERSION_NAME}, {monitorTime(nowMs)}" };
        foreach (var (_, line, detail) in journal())
        {
            lines.Add(line);
            if (detail.Length > 0) lines.Add("  " + detail);
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string journalTitle() => $"Журнал событий ({MonitorLog.state.value.Count})";

    public const string JOURNAL_EMPTY = "Происшествий не было — журнал пуст. Здесь появятся падения, восстановления и смены сервера.";

    private static IReadOnlyList<(string, string, bool)> bucket(DayBucket b) =>
    [
        ("Туннель ↓ (приём)", fmtBytes(b.tunnelRx), false),
        ("Туннель ↑ (отдача)", fmtBytes(b.tunnelTx), false),
        ("Тест", fmtBytes(b.test), false),
        ("Итого", fmtBytes(b.total()), true),
    ];

    public static IReadOnlyList<MetricBlock> traffic(TrafficSnapshot t, long nowElapsed)
    {
        // Время работы блока = время СЕССИИ приложения (от той же точки, что и байты); прокси — отдельно.
        var uptimeMs = t.sessionStartElapsed == 0L ? 0L : nowElapsed - t.sessionStartElapsed;
        var proxyUptime =
            t.proxyActive ? nowElapsed - t.proxyStartElapsed :
            t.proxyEndElapsed > t.proxyStartElapsed && t.proxyStartElapsed > 0L ? t.proxyEndElapsed - t.proxyStartElapsed :
            -1L;
        var nonEmpty = t.days.Where(d => !d.bucket.isEmpty()).ToList();
        return
        [
            new("С последнего запуска",
            [
                ("Время работы", fmtUptime(uptimeMs), false),
                ("Прокси активен", proxyUptime < 0 ? "—" : fmtUptime(proxyUptime), false),
                ("Туннель ↓ (приём)", fmtBytes(t.sessionRx), false),
                ("Туннель ↑ (отдача)", fmtBytes(t.sessionTx), false),
                ("Тест", fmtBytes(t.sessionTest), false),
                ("Итого", fmtBytes(t.sessionRx + t.sessionTx + t.sessionTest), true),
            ]),
            new("За сутки", bucket(t.today)),
            new("За 30 дней", [.. bucket(t.total30), .. nonEmpty.Select(d => (d.date, fmtBytes(d.bucket.total()), false))]),
        ];
    }

    public static string resetTodayText(TrafficSnapshot t) => $"Трафик за сегодня ({fmtBytes(t.today.total())}) будет удалён безвозвратно.";

    public static string resetAllText(TrafficSnapshot t) =>
        $"Будет удалена вся история трафика за {t.days.Count(d => !d.bucket.isEmpty())} дн. ({fmtBytes(t.total30.total())}), включая список по дням. Безвозвратно.";

    public static long nowElapsed() => SystemClock.elapsedRealtime();
}
