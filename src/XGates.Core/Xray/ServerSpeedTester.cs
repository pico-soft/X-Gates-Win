using System.Net;
using System.Net.Sockets;
using XGates.Core.Compat;
using XGates.Core.Libv2ray;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Traffic;
using XGates.Core.Xray.Link;
using L = XGates.Core.Libv2ray.Libv2ray;

namespace XGates.Core.Xray;

/**
 * Порт xray/ServerSpeedTester.kt — ПО ЧАСТЯМ. Здесь пока: служебное (freePort/awaitPort/счётчик параллельных
 * замеров) и [fetchViaTempInstance] (ступень 6 каскада загрузки подписок). Замеры скорости (measure…, probeAlive,
 * testAll) — следующим шагом порта.
 */
public static partial class ServerSpeedTester
{
    private const string TAG = "ServerSpeedTester";

    /// <summary>Сколько temp-инстансов работает прямо сейчас (для лога «параллельно=N»).</summary>
    internal static int concurrentMeasuresValue;

    private static int concurrentIncrement() => Interlocked.Increment(ref concurrentMeasuresValue);
    private static int concurrentDecrement() => Interlocked.Decrement(ref concurrentMeasuresValue);

    /**
     * Скачать текстовый ресурс ЧЕРЕЗ временный инстанс на [profile] (ступень 6 каскада: прямой путь заблокирован,
     * активного туннеля нет, а недавно рабочие серверы известны). Не трогает активный прокси. Трафик → «Тест».
     * null — инстанс не поднялся / конфиг не собрался.
     */
    public static FetchResult? fetchViaTempInstance(Context context, ServerProfile profile, string url, string userAgent, int timeoutMs)
    {
        XrayController.ensureEnv(context);
        var verbose = SettingsStore.current().verboseLogs;
        void log(string msg) { if (verbose) Log.i(TAG, msg); }
        var name = profile.remarks.IfBlank(profile.address);
        concurrentIncrement();
        try
        {
            var port = freePort();
            if (port == null) return null;
            string cfg;
            try { cfg = XrayConfigBuilder.buildForSpeedTest(profile, port.Value); }
            catch (Exception e)
            {
                log($"«{name}» fetchViaTempInstance buildForSpeedTest FAIL: {e.Message}"); return null;
            }
            var core = L.newCoreController(new NoopCallback());
            try
            {
                core.startLoop(cfg, 0);
                if (!core.isRunning || !awaitPort(port.Value, deadlineMs: 2_000))
                {
                    log($"«{name}» fetchViaTempInstance temp-инстанс не поднялся"); return null;
                }
                var res = SubscriptionFetcher.fetch(url, userAgent, timeoutMs, new HttpRoute.Socks("127.0.0.1", port.Value));
                if (res.bodyBytes > 0) TrafficTracker.addTest(res.bodyBytes);
                log($"«{name}» fetchViaTempInstance http={res.httpCode} bytes={res.bodyBytes}");
                return res;
            }
            // Паритет с Kotlin: catch НЕТ — исключение startLoop (gomobile/процесс) летит наружу и обрывает каскад
            // (refreshAllEnabled/DefaultSeeder его ловят). Кандидат на улучшение в ОБЕИХ версиях: ловить и идти дальше.
            finally
            {
                try { core.stopLoop(); } catch (Exception) { /* ignore */ }
            }
        }
        finally
        {
            concurrentDecrement();
        }
    }

    private static int? freePort()
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>Ждём, пока socks-порт temp-инстанса начнёт принимать (ретрай на гонку старта).</summary>
    private static bool awaitPort(int port, long deadlineMs)
    {
        var end = JavaSystem.nanoTime() + deadlineMs * 1_000_000;
        while (JavaSystem.nanoTime() < end)
        {
            try
            {
                using var s = new TcpClient();
                if (s.ConnectAsync(IPAddress.Loopback, port).Wait(300) && s.Connected) return true;
            }
            catch (Exception) { }
            Thread.Sleep(100);
        }
        return false;
    }

    private sealed class NoopCallback : CoreCallbackHandler
    {
        public long startup() => 0;
        public long shutdown() => 0;
        public long onEmitStatus(long l, string? s) => 0;
    }
}
