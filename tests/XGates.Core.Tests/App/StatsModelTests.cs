using XGates.App;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Traffic;

namespace XGates.Core.Tests.App;

/// <summary>«Автомониторинг» и «Трафик» в настройках (MonitorSection/TrafficSection Kotlin): тексты и формулы времени.</summary>
[Collection("xray")]   // MonitorLog — общее состояние процесса
public sealed class StatsModelTests : IDisposable
{
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-stats-" + Guid.NewGuid().ToString("N")));
    public void Dispose() { try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { } }

    [Fact]
    public void Heartbeat_line_and_journal()
    {
        var s = SettingsStore.DEFAULTS;
        var now = JavaSystem.currentTimeMillis();
        var savedCharging = Device.isCharging;
        try
        {
            Device.isCharging = () => false;   // батарея: интервал — ползунок, как в Kotlin
            Assert.Equal("", StatsModel.heartbeatLine(s with { monitorEnabled = false }, new MonitorHeartbeat(), now));
            Assert.Equal("● Проверка связи каждые 60 с · последняя: — (—) · циклов: 0 · —", StatsModel.heartbeatLine(s, new MonitorHeartbeat(), now));
            var line = StatsModel.heartbeatLine(s with { connectionCheckIntervalSec = 5 }, new MonitorHeartbeat(true, "OK", now - 7_500, 3), now);
            Assert.StartsWith("● Проверка связи каждые 15 с · последняя: ", line);   // не реже 15 с, как в Kotlin
            Assert.EndsWith(" (7 с назад) · циклов: 3 · OK", line);
            Device.isCharging = () => true;    // шнур: 10 с (решение Elyor), в экономии трафика — снова ползунок
            Assert.StartsWith("● Проверка связи каждые 10 с (на шнуре) · последняя: ", StatsModel.heartbeatLine(s, new MonitorHeartbeat(), now));
            Assert.StartsWith("● Проверка связи каждые 60 с · ", StatsModel.heartbeatLine(s with { trafficSaveMode = true }, new MonitorHeartbeat(), now));
        }
        finally { Device.isCharging = savedCharging; }

        MonitorLog.init(ctx);
        MonitorLog.clear(ctx);
        Assert.Equal("Журнал событий (0)", StatsModel.journalTitle());
        MonitorLog.@event(ctx, "net", "Сеть сменилась", "Wi-Fi");
        MonitorLog.@event(ctx, "switch", "Прокси остановлен", "ручная остановка");
        var text = StatsModel.journalText(JavaSystem.currentTimeMillis());
        Assert.StartsWith("X-Gates Win ", text);
        Assert.Contains("Прокси остановлен", text);
        Assert.Contains("  ручная остановка", text);
        Assert.Contains("  Wi-Fi", text);
        var j = StatsModel.journal();
        Assert.Equal(2, j.Count);
        Assert.EndsWith(" · Прокси остановлен", j[0].line);   // свежие сверху
        Assert.Equal("ручная остановка", j[0].detail);
        Assert.Equal("net", j[1].kind);
        MonitorLog.clear(ctx);
    }

    [Fact]
    public void Traffic_blocks_and_uptime_formulas()
    {
        var day = new DayBucket(1_048_576, 2_097_152, 512);
        var t = new TrafficSnapshot(sessionStartElapsed: 1_000, sessionRx: 10_485_760, sessionTx: 1024, sessionTest: 0, proxyActive: false,
            proxyStartElapsed: 2_000, proxyEndElapsed: 3_663_000, today: day, total30: day,
            days: [("2026-10-30", day), ("2026-10-29", new DayBucket(0, 0, 0))]);
        var b = StatsModel.traffic(t, nowElapsed: 3_666_000);
        Assert.Equal(["С последнего запуска", "За сутки", "За 30 дней"], b.Select(x => x.title));
        Assert.Contains(("Время работы", "1:01:05", false), b[0].rows);
        Assert.Contains(("Прокси активен", "1:01:01", false), b[0].rows);   // завершённый интервал прокси
        Assert.Contains(("Туннель ↓ (приём)", "10.0 МБ", false), b[0].rows);
        Assert.Contains(("Итого", "10.0 МБ", true), b[0].rows);
        Assert.Contains(("Итого", "3.0 МБ", true), b[1].rows);
        Assert.Contains(("2026-10-30", "3.0 МБ", false), b[2].rows);
        Assert.DoesNotContain(b[2].rows, r => r.label == "2026-10-29");   // пустые дни не показываем
        Assert.Contains(("Прокси активен", "—", false), StatsModel.traffic(t with { proxyStartElapsed = 0, proxyEndElapsed = 0 }, 5_000)[0].rows);
        Assert.Equal("Будет удалена вся история трафика за 1 дн. (3.0 МБ), включая список по дням. Безвозвратно.", StatsModel.resetAllText(t));
    }
}
