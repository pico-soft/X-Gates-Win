using XGates.Core.Platform;

namespace XGates.Core.Subscription;

/**
 * Пр.136: повтор посева дефолтной подписки. Разовая неудача НЕ означает «никогда» — пробуем снова при
 * старте, появлении сети, возврате в приложение и по кнопке «Повторить». Бэкофф, чтобы не долбить адрес.
 *
 * Замечание про «поднятие туннеля» (из промпта): для ДЕФОЛТНОГО посева это логически мёртвый триггер —
 * туннель означает наличие активного сервера, а сервер берётся из источника, значит источники уже НЕ пусты
 * и посев и так пропускается. Поэтому отдельного триггера «туннель поднялся» тут нет; реальные —
 * сеть/возврат/кнопка. Само появление сети чинит первую (прямую) ступень каскада, ради чего повтор и нужен.
 */
public static class DefaultSeeder
{
    private const long MIN_RETRY_MS = 60_000L;   // не чаще раза в минуту (кроме ручного force)
    private static readonly object Sync = new();

    private static long lastAttemptElapsed = 0L;
    private static volatile bool running = false;

    /**
     * Попробовать посеять, если ещё нужно (список пуст и не помечено «посеяно»). Бэкофф между авто-попытками;
     * [force] (ручная «Повторить») его игнорирует. Сетевой фетч — в ФОНОВОМ потоке.
     */
    public static void maybeSeed(Context context, string reason, bool force = false)
    {
        var app = context.applicationContext;
        var f = SubscriptionStore.load(app);
        if (f.seededDefaultRuBypass || f.sources.Count > 0) return;   // уже нечего сеять
        var now = SystemClock.elapsedRealtime();
        lock (Sync)
        {
            if (running) return;
            if (!force && lastAttemptElapsed != 0L && now - lastAttemptElapsed < MIN_RETRY_MS) return;
            lastAttemptElapsed = now;
            running = true;
        }
        new Thread(() =>
        {
            try { SubscriptionManager.trySeedDefaultSource(app); }
            catch (Exception) { }
            finally { running = false; }
        }) { IsBackground = true }.Start();
    }
}
