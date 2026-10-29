using XGates.Core.Libv2ray;
using XGates.Core.Platform;
using L = XGates.Core.Libv2ray.Libv2ray;

namespace XGates.Core.Xray;

/**
 * Тонкая обёртка над ядром — порт 1:1 с xray/XrayController.kt. На Android под ней libv2ray (AndroidLibXrayLite),
 * на Windows — тот же API поверх процесса xray.exe (XGates.Core.Libv2ray), поэтому код здесь почти дословный.
 * Модель локального прокси: startLoop(config, tunFd = 0) — БЕЗ TUN/VPN.
 */
public static class XrayController
{
    private const string TAG = "XrayController";

    /// <summary>tunFd = 0 → xray не создаёт TUN, работает как обычный локальный прокси.</summary>
    private const int NO_TUN_FD = 0;

    private static readonly object Sync = new();
    private static CoreController? controller;
    private static bool envInitialized;

    /// <summary>Пробрасываем статус наружу (для UI-лога).</summary>
    public static bool isRunning => controller?.isRunning == true;

    /**
     * Инициализация окружения ядра. Идемпотентна, потокобезопасна.
     * Нужна и для start(), и для measureOutboundDelay() (замер требует env даже без запущенного прокси).
     * Windows: путь к xray.exe задаётся при старте приложения (Libv2ray.xrayPath); здесь — только отметка в лог.
     */
    public static void ensureEnv(Context context)
    {
        lock (Sync)
        {
            if (envInitialized) return;
            envInitialized = true;
            Log.i(TAG, $"env initialized, {L.checkVersionX()}");
        }
    }

    /**
     * Запуск ядра с переданным конфигом (freedom или vless — решает вызывающий).
     * Возвращает true, если после старта isRunning == true. Бросает исключение
     * при ошибке конфига/старта — вызывающий код должен показать его в UI.
     */
    public static bool start(Context context, string configJson)
    {
        lock (Sync)
        {
            if (isRunning) return true;
            ensureEnv(context);
            var core = L.newCoreController(new CoreCallback());
            controller = core;
            core.startLoop(configJson, NO_TUN_FD);
            Log.i(TAG, $"startLoop done, isRunning={core.isRunning}");
            return core.isRunning;
        }
    }

    /// <summary>Остановка ядра. Идемпотентна.</summary>
    public static void stop()
    {
        lock (Sync)
        {
            var core = controller;
            if (core == null) return;
            try
            {
                core.stopLoop();
            }
            catch (Exception e)
            {
                Log.w(TAG, "stopLoop error", e);
            }
            finally
            {
                controller = null;
            }
        }
    }

    /**
     * Дельта трафика активного туннеля через core queryStats по тегу outbound "proxy".
     * queryStats читает счётчик С СБРОСОМ → возвращает байты с ПРОШЛОГО опроса.
     * @return (принято downlink, отдано uplink) или null, если ядро не запущено/ошибка.
     */
    public static (long down, long up)? queryTunnelDelta()
    {
        lock (Sync)
        {
            var core = controller;
            if (core == null) return null;
            try
            {
                var up = core.queryStats("proxy", "uplink");
                var down = core.queryStats("proxy", "downlink");
                return (down, up);
            }
            catch (Exception e)
            {
                Log.d(TAG, $"queryStats failed: {e.Message}");
                return null;
            }
        }
    }

    /**
     * Real ping: задержка через outbound переданного конфига БЕЗ запуска основного прокси.
     * `measureOutboundDelay` поднимает временный инстанс — не трогает активный прокси, потому безопасно и параллельно.
     *
     * НЕ под lock — чтобы батч из нескольких замеров шёл параллельно; env-init внутри
     * ([ensureEnv]) потокобезопасен сам по себе.
     *
     * @return задержку в мс (≥0) или -1 при недоступности/ошибке (мёртвый сервер).
     */
    public static long measureOutboundDelay(Context context, string configJson, string url)
    {
        ensureEnv(context);
        try
        {
            return L.measureOutboundDelay(configJson, url);
        }
        catch (Exception e)
        {
            Log.d(TAG, $"measureOutboundDelay failed: {e.Message}");
            return -1L;
        }
    }

    /**
     * Колбэк ядра. На этапе локального прокси без VPN все три метода тривиальны (0 = OK).
     */
    private sealed class CoreCallback : CoreCallbackHandler
    {
        public long startup() => 0;
        public long shutdown() => 0;
        public long onEmitStatus(long l, string? s)
        {
            Log.i(TAG, $"onEmitStatus: {l} {s}");
            return 0;
        }
    }
}
