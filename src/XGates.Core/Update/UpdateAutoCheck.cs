using XGates.Core.Net;
using XGates.Core.Platform;

namespace XGates.Core.Update;

/**
 * Самостоятельные (не по кнопке) проверки обновления. Порт двух кусков Android: MainActivity.onCreate (холодный старт,
 * Промпт 80.B) и цикла в XrayProxyService.onCreate (Промпт 93.I). Вынесено в ядро: WPF-приложение и «сервис» только
 * вызывают, а тесты проверяют ту же логику. Сам установщик без согласия не качаем — только сведения (несколько КБ).
 */
public static class UpdateAutoCheck
{
    /// <summary>Сколько ждать подъёма своего SOCKS на холодном старте (подменяется в тестах).</summary>
    internal static int proxyWaitMs = UpdateChecker.AUTO_CHECK_PROXY_WAIT_MS;
    /// <summary>Тест запуска программы не должен ходить в настоящий GitHub (подменяется в тестах).</summary>
    internal static bool skipOnLaunch;

    /**
     * Холодный старт (ТЗ Elyor 2026-11-01: запустил программу — сразу видишь, что вышло обновление): при КАЖДОМ запуске,
     * в фоновом потоке (окно открывается сразу). Сначала без туннеля: на Android проверка ждала свой SOCKS, и здесь она
     * молча пропадала, пока подбор сервера не поднимал связь. Не вышло — ждём свой SOCKS не дольше [proxyWaitMs] и
     * пробуем через него. Снова неудача — молчим, ничего не записывая (ручная кнопка объяснит честно).
     */
    public static Thread coldStart(Context context)
    {
        var app = context.applicationContext;
        if (skipOnLaunch) return KThread.start(() => { }, "update-cold-start");
        return KThread.start(() =>
        {
            if (tryCheck(app)) return;
            var waited = 0;
            while (waited < proxyWaitMs && !CascadeFetch.isOwnProxyUp()) { Thread.Sleep(1_000); waited += 1_000; }
            if (CascadeFetch.isOwnProxyUp()) tryCheck(app);
        }, "update-cold-start");
    }

    /**
     * Шаг цикла «сервиса» (раз в час): пока прокси активен — проверить новую версию ЧЕРЕЗ уже работающий туннель, если
     * прошли сутки; тихо при неудаче. true — проверка выполнена.
     */
    public static bool periodicTick(Context app, bool proxyRunning)
    {
        if (!proxyRunning) return false;
        if (!UpdateStore.dueForAutoCheck(JavaSystem.currentTimeMillis())) return false;
        return checkAndNotify(app);
    }

    /// <summary>Как [checkAndNotify], но ошибку не записывает: при запуске о ней молчим. true — ответ получен.</summary>
    private static bool tryCheck(Context app)
    {
        CheckReport r;
        try { r = UpdateChecker.check(app); } catch (Exception) { return false; }
        if (r.result is UpdateCheckResult.Error) return false;
        UpdateStore.apply(app, r, JavaSystem.currentTimeMillis());
        UpdateNotifier.maybeNotify(app);
        return true;
    }

    private static bool checkAndNotify(Context app)
    {
        CheckReport r;
        try { r = UpdateChecker.check(app); } catch (Exception) { return false; }
        UpdateStore.apply(app, r, JavaSystem.currentTimeMillis());
        UpdateNotifier.maybeNotify(app);   // Промпт 93.J: уведомить, если новее (одно на версию)
        return true;
    }
}
