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

    /**
     * Холодный старт: не чаще раза в сутки, ВСЁ в фоновом потоке (окно открывается сразу). Ждём подъёма своего SOCKS
     * не дольше [proxyWaitMs] (без туннеля на заблокированной сети проверка обречена); туннель так и не поднялся —
     * МОЛЧА выходим, ничего не записывая (ручная кнопка объяснит честно). null — проверка не нужна (рано).
     */
    public static Thread? coldStart(Context context)
    {
        var app = context.applicationContext;
        if (!UpdateStore.dueForAutoCheck(JavaSystem.currentTimeMillis())) return null;
        return KThread.start(() =>
        {
            var waited = 0;
            while (waited < proxyWaitMs && !CascadeFetch.isOwnProxyUp()) { Thread.Sleep(1_000); waited += 1_000; }
            if (!CascadeFetch.isOwnProxyUp()) return;   // туннеля нет — молчим
            checkAndNotify(app);
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

    private static bool checkAndNotify(Context app)
    {
        CheckReport r;
        try { r = UpdateChecker.check(app); } catch (Exception) { return false; }
        UpdateStore.apply(app, r, JavaSystem.currentTimeMillis());
        UpdateNotifier.maybeNotify(app);   // Промпт 93.J: уведомить, если новее (одно на версию)
        return true;
    }
}
