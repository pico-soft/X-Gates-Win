namespace XGates.Core.Platform;

/// <summary>
/// Аналог Kotlin <c>Thread { … }.start()</c> С СЕМАНТИКОЙ JVM: необработанное исключение в теле убивает ТОЛЬКО этот
/// поток (пишется в лог), а не процесс. В .NET необработанное исключение в любом потоке роняет ВСЁ приложение —
/// портированный «фоновый» код Android (сторожа, финишёры батчей, посев) на это не рассчитан. Потоки фоновые
/// (не держат процесс при выходе) — как isDaemon=true.
/// </summary>
public static class KThread
{
    /// <summary>Кому сообщить о необработанном исключении потока (ставит CrashReporter: отчёт пишется, процесс жив).</summary>
    public static volatile Action<string, Exception>? onUncaught;

    public static Thread start(Action body, string? name = null)
    {
        var t = new Thread(() =>
        {
            try { body(); }
            catch (ThreadInterruptedException) { }
            catch (Exception e)
            {
                Log.e("KThread", $"необработанное исключение в потоке «{name ?? "?"}» (поток завершён, процесс жив)", e);
                try { onUncaught?.Invoke(name ?? "?", e); } catch (Exception) { }
            }
        }) { IsBackground = true, Name = name };
        t.Start();
        return t;
    }
}
