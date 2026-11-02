using System.Net;
using System.Net.Sockets;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Xray.Link;

namespace XGates.Core.Xray;

/**
 * Пинг серверов из списка (TCP-коннект host:port — см. [ping]). Всё в фоне. Порт 1:1 с xray/ServerTester.kt.
 * (Из Kotlin опущен неиспользуемый jniPool: он остался от прежнего пинга через measureOutboundDelay;
 * сам [ping] его давно не использует — см. комментарий ТЗ 2026-10-15 ниже.)
 */
public static class ServerTester
{
    public const string GSTATIC_204 = "https://www.gstatic.com/generate_204";

    /**
     * Один замер: TCP-коннект до сервера. Возвращает мс (≥1) или -1 (недостижим/таймаут/ошибка).
     * [timeoutMs] — мягкий верхний предел (из настроек), зажат в 1..6 с.
     */
    /// <summary>Транспорт поверх UDP: TCP-коннект к такому серверу не проходит никогда, какой бы живой он ни был.</summary>
    public static bool isUdp(ServerProfile profile) => profile.network is "kcp" or "mkcp";

    public static long ping(Context context, ServerProfile profile, int? timeoutMs = null)
    {
        int timeout = timeoutMs ?? SettingsStore.current().pingTimeoutMs;
        if (isUdp(profile)) return udpPing(context, profile, timeout);
        // ТЗ Elyor (2026-10-15): tcping — TCP-коннект до host:port сервера (как «real/tcp ping» в v2rayN на ноутбуке).
        // Пингует ВСЕ URL быстро, показывает ДОСТИЖИМЫЕ серверы, НЕ виснет и НЕ переполняет пул (чистый таймаут
        // коннекта) — в отличие от прежнего gstatic-204 через ХОЛОДНЫЙ temp-инстанс (ложно падал на xhttp/reality/
        // медленных и залипал на мёртвом пуле, «пинг 3→300»). Реальную работу туннеля далее доказывает замер скорости.
        // Возврат: мс (≥0) или -1 (недостижим/таймаут/ошибка). Обычный сокет, прерывается таймаутом.
        var host = profile.address;
        var port = profile.port;
        if (Compat.KotlinText.IsBlank(host) || port <= 0) return -1L;
        Socket? sock = null;
        try
        {
            var t0 = JavaSystem.nanoTime();
            // Как Java InetSocketAddress(host, port): резолв (в замер входит) → ПЕРВЫЙ адрес → connect с таймаутом.
            var addr = IPAddress.TryParse(host, out var ip) ? ip : Dns.GetHostAddresses(host)[0];
            sock = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var connect = sock.ConnectAsync(addr, port);
            if (!connect.Wait(timeout.coerceIn(1000, 6000)) || !sock.Connected) return -1L;
            return ((JavaSystem.nanoTime() - t0) / 1_000_000).coerceAtLeast(1L);
        }
        catch (Exception)
        {
            return -1L;
        }
        finally
        {
            try { sock?.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>UDP-сервер проверяем настоящей передачей данных через временное ядро; в отклик входит и запуск ядра.</summary>
    private static long udpPing(Context context, ServerProfile profile, int timeout)
    {
        udpPingSlots.Wait();   // очередь за слотом в отклик не входит
        try
        {
            var t0 = JavaSystem.nanoTime();
            if (!ServerSpeedTester.probeAlive(context, profile, maxProbes: 1, timeoutMs: timeout.coerceIn(1000, 6000))) return -1L;
            return ((JavaSystem.nanoTime() - t0) / 1_000_000).coerceAtLeast(1L);
        }
        finally { udpPingSlots.Release(); }
    }

    /// <summary>Потолок времени на батч. UDP-пинги идут по очереди из <see cref="UDP_PING_SLOTS"/> слотов: без их доли сторож объявил бы живые серверы мёртвыми, не дождавшись.</summary>
    internal static long batchBudgetMs(int total, int udp, int concurrency, long perServerMs)
    {
        var waves = ((total + concurrency - 1) / concurrency).coerceAtLeast(1);
        var udpWaves = (udp + UDP_PING_SLOTS - 1) / UDP_PING_SLOTS;
        return 15_000L + (waves + udpWaves) * perServerMs;
    }

    // Каждый такой пинг поднимает отдельный xray.exe, а потоков пинга 48: подписка с десятками mKCP запускала бы столько же процессов разом.
    internal const int UDP_PING_SLOTS = 3;
    private static readonly SemaphoreSlim udpPingSlots = new(UDP_PING_SLOTS, UDP_PING_SLOTS);

    /// <summary>Управление запущенным батчем.</summary>
    public interface TestHandle
    {
        void cancel();
    }

    private sealed class Handle(Action onCancel) : TestHandle
    {
        public void cancel() => onCancel();
    }

    /**
     * Батч: ограниченный пул [concurrency] одновременно; на каждый готовый результат СРАЗУ
     * [onResult] (прогрессивно, не ждём всех) + [onProgress]. По завершении всех — [onFinish].
     *
     * Колбэки вызываются на фоновых потоках пула — маршалинг в UI на вызывающем.
     * Отмена ([TestHandle.cancel]) прерывает очередь; недотестированные остаются как есть,
     * onFinish при отмене НЕ вызывается.
     */
    public static TestHandle testAll(
        Context context,
        IReadOnlyList<ServerProfile> servers,
        Action<ServerProfile, long> onResult,
        Action<int, int> onProgress,
        Action? onFinish = null,
        int? concurrency = null)
    {
        onFinish ??= () => { };
        var appCtx = context.applicationContext;
        var total = servers.Count;
        var conc = (concurrency ?? SettingsStore.current().pingPool).coerceAtLeast(1);
        var done = 0;
        var cancelled = false;
        // Терминальное состояние батча: onFinish РОВНО один раз (штатно ИЛИ по сторожу), после — воркеры немы.
        var finished = 0;
        long lastProgressMs = JavaSystem.currentTimeMillis();
        var pool = new FixedThreadPool(conc, "ping");

        // UDP в конец: ожидая слот, он держал бы поток и задерживал обычные пинги за собой. Порядок выдачи результатов и так не гарантирован.
        foreach (var p in servers.OrderBy(isUdp))
        {
            pool.execute(() =>
            {
                if (Volatile.Read(ref cancelled) || Volatile.Read(ref finished) == 1) return;
                var ms = -1L;
                try
                {
                    ms = ping(appCtx, p);   // ping() уже не бросает (Exception → -1), но подстрахуемся
                }
                catch (Exception t)
                {
                    Log.w("ServerTester", $"задача пинга упала (проглочено): {t.Message}");
                }
                finally
                {
                    // ПРОГРЕСС ДВИГАЕМ ВСЕГДА (в finally): даже если пинг упал/пул отказал — счётчик не залипает,
                    // батч доходит до конца и onFinish срабатывает, «Стоп» действует. Полевой баг «пинг 33/140
                    // навсегда» был именно из-за пропуска onProgress на исключении. Гейт по finished — чтобы
                    // «опоздавший» воркер (после срабатывания сторожа) не двигал бар уже во время Этапа 2.
                    if (!Volatile.Read(ref cancelled) && Volatile.Read(ref finished) == 0)
                    {
                        try { onResult(p, ms); } catch (Exception) { }
                        Interlocked.Exchange(ref lastProgressMs, JavaSystem.currentTimeMillis());
                        onProgress(Interlocked.Increment(ref done), total);
                    }
                }
            });
        }
        pool.shutdown(); // новых не принимаем; уже поданные выполняются

        void finishOnce()
        {
            if (Interlocked.CompareExchange(ref finished, 1, 0) == 0 && !Volatile.Read(ref cancelled)) onFinish();
        }

        // ⏱️ ЖЁСТКИЙ СТОРОЖ ЗАВЕРШЕНИЯ (полевой баг Fold SM-F936N, 0.43, «пинг 25/36» намертво): у Этапа 1 своего
        // сторожа зависания НЕ было (в отличие от Этапа 2/экономии), а awaitTermination(MAX_VALUE) ждал зомби-поток
        // ВЕЧНО. Гарантия: батч ЗАВЕРШАЕТСЯ по застою (нет ни одного нового результата за stall) ИЛИ по общему
        // бюджету — недотестированные считаем мёртвыми, onFinish идёт с тем, что успели напинговать.
        var perServerMs = (long)(SettingsStore.current().pingTimeoutMs + 2_000);
        var hardBudgetMs = batchBudgetMs(total, servers.Count(isUdp), conc, perServerMs);   // общий потолок на весь батч
        var stallLimitMs = (perServerMs * 3).coerceAtLeast(20_000L);  // нет НИ ОДНОГО нового результата столько → бросаем
        KThread.start(() =>
        {
            var startMs = JavaSystem.currentTimeMillis();
            while (true)
            {
                if (pool.awaitTermination(2_000)) { finishOnce(); break; }  // все воркеры отработали штатно
                if (Volatile.Read(ref cancelled) || Volatile.Read(ref finished) == 1) break;
                var nowMs = JavaSystem.currentTimeMillis();
                var overBudget = nowMs - startMs > hardBudgetMs;
                var stalled = nowMs - Interlocked.Read(ref lastProgressMs) > stallLimitMs;
                if (overBudget || stalled)
                {
                    Log.w("ServerTester", $"батч пинга завершён СТОРОЖЕМ ({(overBudget ? "бюджет" : "застой")}): {Volatile.Read(ref done)}/{total}");
                    pool.shutdownNow();   // снять недоданные
                    finishOnce();
                    break;
                }
            }
        }, "ping-watchdog");

        return new Handle(() =>
        {
            Volatile.Write(ref cancelled, true);
            Volatile.Write(ref finished, 1);  // штатный onFinish уже не нужен — отмена
            pool.shutdownNow(); // снять очередь
        });
    }
}
