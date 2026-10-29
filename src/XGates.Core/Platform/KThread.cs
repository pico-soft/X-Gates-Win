namespace XGates.Core.Platform;

/// <summary>
/// Аналог Kotlin <c>Thread { … }.start()</c> С СЕМАНТИКОЙ JVM: необработанное исключение в теле убивает ТОЛЬКО этот
/// поток (пишется в лог), а не процесс. В .NET необработанное исключение в любом потоке роняет ВСЁ приложение —
/// портированный «фоновый» код Android (сторожа, финишёры батчей, посев) на это не рассчитан. Потоки фоновые
/// (не держат процесс при выходе) — как isDaemon=true.
/// </summary>
public static class KThread
{
    public static Thread start(Action body, string? name = null)
    {
        var t = new Thread(() =>
        {
            try { body(); }
            catch (ThreadInterruptedException) { }
            catch (Exception e) { Log.e("KThread", $"необработанное исключение в потоке «{name ?? "?"}» (поток завершён, процесс жив)", e); }
        }) { IsBackground = true, Name = name };
        t.Start();
        return t;
    }
}
