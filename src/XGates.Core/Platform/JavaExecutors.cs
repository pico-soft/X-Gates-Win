using System.Collections.Concurrent;

namespace XGates.Core.Platform;

/// <summary>
/// Аналог <c>java.util.concurrent.ExecutorService</c> из <c>Executors.newFixedThreadPool(n)</c> — с ЕГО семантикой,
/// на которую опирается Kotlin-код батчей (пинг/замеры): ровно n рабочих потоков, FIFO-очередь, <see cref="shutdown"/>
/// (новые не принимаем, поданные доделываем), <see cref="shutdownNow"/> (снять очередь + прервать потоки),
/// <see cref="awaitTermination"/>. Потоки — фоновые (не держат процесс при выходе), в отличие от Java.
/// Прерывание: как в Java, работающая задача прерывается только в точках ожидания (Thread.Sleep/Join/Wait).
/// </summary>
public sealed class FixedThreadPool
{
    private readonly BlockingCollection<Action> queue = new(new ConcurrentQueue<Action>());
    private readonly Thread[] workers;
    private readonly CountdownEvent alive;
    private volatile bool isShutdown;

    public FixedThreadPool(int nThreads, string name = "pool")
    {
        if (nThreads < 1) throw new ArgumentOutOfRangeException(nameof(nThreads));
        workers = new Thread[nThreads];
        alive = new CountdownEvent(nThreads);
        for (int i = 0; i < nThreads; i++)
        {
            workers[i] = new Thread(Run) { IsBackground = true, Name = $"{name}-{i + 1}" };
            workers[i].Start();
        }
    }

    private void Run()
    {
        try
        {
            foreach (var task in queue.GetConsumingEnumerable())
            {
                try { task(); }
                catch (ThreadInterruptedException) { /* shutdownNow */ }
                catch (Exception e) { Log.w("FixedThreadPool", "задача упала (проглочено, как в Java-пуле — поток живёт)", e); }
            }
        }
        catch (ThreadInterruptedException) { }
        finally { alive.Signal(); }
    }

    /// <summary><c>execute</c>: после shutdown бросает (Java — RejectedExecutionException).</summary>
    public void execute(Action task)
    {
        if (isShutdown) throw new InvalidOperationException("RejectedExecutionException: pool is shut down");
        try { queue.Add(task); }
        catch (InvalidOperationException) { throw new InvalidOperationException("RejectedExecutionException: pool is shut down"); }
    }

    public void shutdown()
    {
        isShutdown = true;
        queue.CompleteAdding();
    }

    /// <summary>Снять неначатые задачи (возвращаются) и прервать рабочие потоки.</summary>
    public List<Action> shutdownNow()
    {
        shutdown();
        var drained = new List<Action>();
        while (queue.TryTake(out var t)) drained.Add(t);
        foreach (var w in workers) { try { w.Interrupt(); } catch (Exception) { } }
        return drained;
    }

    public bool isTerminated => alive.IsSet;

    /// <summary>Ждать завершения всех потоков после shutdown; true — завершились.</summary>
    public bool awaitTermination(long timeoutMs) =>
        timeoutMs >= int.MaxValue ? alive.Wait(Timeout.Infinite) : alive.Wait((int)Math.Max(0, timeoutMs));   // Long.MAX_VALUE = «вечно»
}
