namespace XGates.Core.Platform;

/// <summary>Аналог <c>android.os.SystemClock</c>.</summary>
public static class SystemClock
{
    /// <summary>
    /// Монотонные мс с загрузки, ВКЛЮЧАЯ сон (как elapsedRealtime на Android: таймеры монитора не «замерзают» на
    /// время сна). Windows GetTickCount64 (Environment.TickCount64) сон включает.
    /// </summary>
    public static long elapsedRealtime() => Environment.TickCount64;

    public static void sleep(long ms) => Thread.Sleep(TimeSpan.FromMilliseconds(ms));
}

/// <summary>Аналог <c>System.currentTimeMillis()</c>/<c>System.nanoTime()</c> Java.</summary>
public static class JavaSystem
{
    public static long currentTimeMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static readonly System.Diagnostics.Stopwatch Mono = System.Diagnostics.Stopwatch.StartNew();

    public static long nanoTime() => (long)(Mono.ElapsedTicks * (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
}
