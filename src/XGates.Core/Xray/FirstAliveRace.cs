using System.Collections.Concurrent;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Subscription;
using XGates.Core.Xray.Link;

namespace XGates.Core.Xray;

/**
 * «Сначала связь, скорость потом» (ТЗ Elyor 2026-11-01: всегда держать туннель и максимально быстро его поднимать).
 * Несколько кандидатов одновременно проверяются настоящей передачей данных, первый прошедший сразу становится активным,
 * а подбор быстрого идёт уже при живой связи. Этап 2 полного теста для этого не годится: он идёт по одному серверу, и
 * на Windows каждый кандидат — это запуск xray.exe, поэтому до первого рабочего проходили минуты.
 * Скорость здесь не меряем: параллельные замеры мешают друг другу (Пр.101).
 */
public sealed class FirstAliveRace
{
    private const string TAG = "FirstAliveRace";
    public const int POOL = 6;
    private const int MAX_PROBES = 2;
    private const int PROBE_TIMEOUT_MS = 4_000;

    /// <summary>Подтверждённая связь считается живой столько (как свежесть «зелёного» на плашке).</summary>
    public const long TUNNEL_FRESH_MS = 330_000L;

    /// <summary>Связи нет или она не подтверждена — нужен сервер, не дожидаясь замеров.</summary>
    public static bool tunnelNeeded()
    {
        var proxy = ProxyState.state.value;
        return !proxy.running || !TunnelHealth.isConfirmedFor(proxy.serverKey, JavaSystem.currentTimeMillis(), TUNNEL_FRESH_MS);
    }

    private readonly BlockingCollection<ServerProfile> queue = new();
    private readonly ConcurrentDictionary<string, byte> seen = new();
    private readonly CancellationTokenSource cts = new();
    private readonly Action<ServerProfile> onWin;
    private readonly List<Thread> workers;
    private int finished;
    private ServerProfile? winnerValue;

    public ServerProfile? winner => Volatile.Read(ref winnerValue);

    public FirstAliveRace(Context context, Action<ServerProfile> onWin, int pool = POOL)
    {
        this.onWin = onWin;
        var app = context.applicationContext;
        workers = Enumerable.Range(0, pool).Select(i => KThread.start(() => work(app), $"first-alive-{i}")).ToList();
    }

    /// <summary>Кандидат в очередь: раньше поданный проверяется раньше, повторы отбрасываются.</summary>
    public void offer(ServerProfile p)
    {
        if (Volatile.Read(ref finished) != 0 || !seen.TryAdd(SubscriptionManager.serverKey(p), 0)) return;
        try { queue.Add(p); } catch (InvalidOperationException) { /* кандидатов больше не принимаем */ }
    }

    /// <summary>Кандидатов больше не будет: дождаться можно через [join].</summary>
    public void complete() => queue.CompleteAdding();

    public void cancel()
    {
        Interlocked.CompareExchange(ref finished, 1, 0);
        cts.Cancel();
    }

    /// <summary>Ждёт конца гонки не дольше [timeout].</summary>
    public void join(TimeSpan timeout)
    {
        var end = JavaSystem.currentTimeMillis() + (long)timeout.TotalMilliseconds;
        foreach (var t in workers) t.Join(TimeSpan.FromMilliseconds(Math.Max(0L, end - JavaSystem.currentTimeMillis())));
    }

    private void work(Context app)
    {
        try
        {
            foreach (var p in queue.GetConsumingEnumerable(cts.Token))
            {
                var ok = false;
                try { ok = ServerSpeedTester.probeAlive(app, p, MAX_PROBES, PROBE_TIMEOUT_MS); }
                catch (Exception e) { Log.w(TAG, "проба упала", e); }
                if (!ok || Interlocked.CompareExchange(ref finished, 1, 0) != 0) continue;
                Volatile.Write(ref winnerValue, p);
                cts.Cancel();
                onWin(p);
                return;
            }
        }
        catch (OperationCanceledException) { /* гонка окончена */ }
    }
}
