using XGates.Core.Crash;
using XGates.Core.Platform;

namespace XGates.Core.Tests.Crash;

/// <summary>
/// Отчёт о сбое: исключение фонового потока порта (KThread) процесс не роняет, но отчёт пишется — очищенный
/// (без адресов/UUID/IP), с версией; «непоказанный» отчёт снимается пометкой.
/// </summary>
[Collection("xray")]   // KThread.onUncaught — общее состояние процесса
public sealed class CrashReporterTests : IDisposable
{
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-crash-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        KThread.onUncaught = null;
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    [Fact]
    public void Background_thread_exception_writes_sanitized_report_and_process_lives()
    {
        Directory.CreateDirectory(ctx.filesDir);
        CrashReporter.install(ctx, "9.9 beta", 99);
        CrashReporter.clearAll();
        Assert.False(CrashReporter.hasUnseen());

        var t = KThread.start(() => throw new InvalidOperationException(
            "упал на vless://3f2504e0-4f89-41d3-9a0c-0305e82c3301@203.0.113.77:443#N"), "monitor");
        Assert.True(t.Join(10_000));

        var report = CrashReporter.read(Assert.Single(CrashReporter.reports()));
        Assert.Contains("Версия: 9.9 beta (versionCode 99)", report);
        Assert.Contains("Поток: monitor", report);
        Assert.Contains("упал на <адрес>", report);
        // Имя класса исключения (≥24 символов) в отчёте остаётся (находка порта №6: раньше маска его съедала).
        Assert.Contains("System.InvalidOperationException", report);
        Assert.DoesNotContain("vless://", report);
        Assert.DoesNotContain("3f2504e0-4f89-41d3-9a0c-0305e82c3301", report);
        Assert.DoesNotContain("203.0.113.77", report);

        Assert.True(CrashReporter.hasUnseen());
        CrashReporter.markLatestSeen();
        Assert.False(CrashReporter.hasUnseen());

        Assert.Contains("--- Что делал пользователь перед сбоем ---", CrashReporter.emailBody("нажал ▶ на 10.0.0.1", report));
        Assert.DoesNotContain("10.0.0.1", CrashReporter.emailBody("нажал ▶ на 10.0.0.1", report));
        Assert.Contains("9.9 beta (99)", CrashReporter.emailSubject());
    }
}
