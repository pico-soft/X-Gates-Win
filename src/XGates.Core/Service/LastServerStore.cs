using XGates.Core.Platform;

namespace XGates.Core.Service;

/**
 * Персистенция ПОСЛЕДНЕГО УСПЕШНО ПОДКЛЮЧЁННОГО сервера (его serverKey). Порт 1:1 с service/LastServerStore.kt.
 *
 * Нужна автозапуску: при старте приложения сразу коннектимся к последнему рабочему серверу (мгновенная связь),
 * пока фоном идёт обновление подписок + полный тест.
 *
 * Храним ТОЛЬКО serverKey — сам профиль ищем в SubscriptionManager.allServers по ключу (список мог
 * измениться между запусками; если сервера уже нет — просто не подключаемся, тест всё равно идёт).
 */
public static class LastServerStore
{
    private const string PREFS = "last_server";
    private const string KEY = "serverKey";
    private const string KEY_ALIVE = "aliveHeartbeatMs";   // Промпт 115: «пульс живости» сервиса — для оценки простоя

    private static SharedPreferences prefs(Context context) => SharedPreferences.get(context.applicationContext, PREFS);

    /// <summary>Сохранить ключ успешно подключённого сервера (вызывается из сервиса при running=true).</summary>
    public static void save(Context context, string? serverKey) => prefs(context).edit().putString(KEY, serverKey).apply();

    /// <summary>Ключ последнего успешного сервера или null, если ещё не подключались.</summary>
    public static string? load(Context context) => prefs(context).getString(KEY, null);

    /** Промпт 115: отметить, что сервис ЖИВ сейчас (периодически из опроса трафика). На восстановлении после
     *  убийства простой ≈ now − этот штамп. */
    public static void heartbeat(Context context) => prefs(context).edit().putLong(KEY_ALIVE, JavaSystem.currentTimeMillis()).apply();

    /// <summary>Время последнего «пульса» (мс) или 0, если сервис ещё ни разу не отмечался.</summary>
    public static long lastAlive(Context context) => prefs(context).getLong(KEY_ALIVE, 0L);
}

/**
 * Пр.134.C: состояние НЕНАВЯЗЧИВОГО предложения автозагрузки (на Windows — «запускать при входе в систему»).
 *  • [firstHealthyMs] — когда связь ВПЕРВЫЕ подтвердилась (ставим один раз). Предлагаем автозагрузку не при
 *    первом запуске, а после того как связь уже поработала — прошло ≥ [OFFER_DELAY_MS] с первого OK.
 *  • [offerDismissed] — человек закрыл полосу → больше не предлагаем.
 * Порт 1:1 с service/BootPrefs.kt.
 */
public static class BootPrefs
{
    private const string PREFS = "boot_offer";
    private const string KEY_FIRST_OK = "firstHealthyMs";
    private const string KEY_DISMISSED = "offerDismissed";
    public const long OFFER_DELAY_MS = 10 * 60_000L;   // связь должна проработать хотя бы ~10 минут (не первый запуск)

    private static SharedPreferences p(Context c) => SharedPreferences.get(c.applicationContext, PREFS);

    /// <summary>Отметить первый подтверждённый OK (только если ещё не отмечали).</summary>
    public static void markHealthyOnce(Context c)
    {
        var pr = p(c);
        if (pr.getLong(KEY_FIRST_OK, 0L) == 0L) pr.edit().putLong(KEY_FIRST_OK, JavaSystem.currentTimeMillis()).apply();
    }

    public static long firstHealthyMs(Context c) => p(c).getLong(KEY_FIRST_OK, 0L);
    public static bool offerDismissed(Context c) => p(c).getBoolean(KEY_DISMISSED, false);
    public static void dismissOffer(Context c) => p(c).edit().putBoolean(KEY_DISMISSED, true).apply();

    /// <summary>Пора ли показать предложение: связь была OK ≥10 мин назад, не закрыто, автозагрузка ещё выключена.</summary>
    public static bool shouldOffer(Context c, bool startOnBoot)
    {
        if (startOnBoot || offerDismissed(c)) return false;
        var first = firstHealthyMs(c);
        return first > 0L && JavaSystem.currentTimeMillis() - first >= OFFER_DELAY_MS;
    }
}
