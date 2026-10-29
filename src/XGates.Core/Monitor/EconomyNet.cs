using XGates.Core.Platform;
using XGates.Core.Settings;

namespace XGates.Core.Monitor;

/**
 * Пр.139 (переустройство Пр.129): вычисляет ЭФФЕКТИВНОЕ состояние экономии [trafficSaveMode] (его читают все
 * потребители) из МАСТЕРА [economyEnabled] + подпункта «только моб.» [trafficSaveAutoByNetwork] + типа сети:
 *   effective = economyEnabled && ( !onlyMobile || isMobile ).
 * Единственный писатель trafficSaveMode — [recompute] (идемпотентен). Порт 1:1 с monitor/EconomyNet.kt.
 * Windows: «мобильная сеть» = Device.isMobileNetwork (фаза 2 — лимитное подключение; пока всегда false →
 * экономия включается только мастером без «только моб.», как и задумано для ПК без модема).
 */
public static class EconomyNet
{
    // Последний известный тип сети (мобильная?) — чтобы реагировать ТОЛЬКО на смену Wi-Fi↔мобильная.
    private static bool? lastMobile;

    /// <summary>Последний известный тип сети: true — мобильная, false — Wi-Fi/Ethernet/неизвестно.</summary>
    public static bool lastKnownMobile => lastMobile ?? false;

    /// <summary>Текущий тип сети (свежее чтение). Для «Только Wi-Fi» (пауза на мобильной) — без ожидания события.</summary>
    public static bool currentMobile(Context app) => Device.isMobileNetwork();

    /// <summary>Событие сети (на Windows — NetworkChange). Пересчитываем ЭФФЕКТИВНОЕ состояние (идемпотентно).</summary>
    public static void onDefaultNetwork(Context app)
    {
        var isMobile = Device.isMobileNetwork();
        lastMobile = isMobile;
        recompute(app.applicationContext, isMobile);
    }

    /**
     * Применить по ТЕКУЩЕЙ активной сети немедленно. Зовётся при старте сервиса, возврате приложения на
     * передний план и переключении мастера/подпункта — ЧИНИТ «игнор уже-подключённой сети» (событие смены
     * не приходит, если сеть подключена ДО подписки на события).
     */
    public static void applyNow(Context app)
    {
        var isMobile = Device.isMobileNetwork();
        lastMobile = isMobile;
        recompute(app.applicationContext, isMobile);
    }

    /**
     * Пр.139: ЕДИНСТВЕННЫЙ писатель эффективного [trafficSaveMode]. Чистая функция входов:
     *   effective = economyEnabled && ( !onlyMobile || isMobile )
     *  • мастер выкл → экономии нет никогда;
     *  • мастер вкл + «только моб.» вкл → экономия лишь на мобильной (на Wi-Fi обычный);
     *  • мастер вкл + «только моб.» выкл → экономия всегда.
     * Пишет только при РЕАЛЬНОЙ смене (идемпотентно) — можно звать сколько угодно.
     */
    private static void recompute(Context app, bool isMobile)
    {
        var s = SettingsStore.current();
        // ТЗ Elyor: «Только Wi-Fi» на БАТАРЕЕ (Wi-Fi/Ethernet) требует ЩАДЯЩИЙ режим. На мобильной туннель всё равно
        // на паузе (trafficSaveMode там неважен). ИЛИ с экономией → единственный писатель trafficSaveMode остаётся здесь.
        var wifiOnlyGentle = s.wifiOnlyEnabled && !isMobile && !NetworkMonitor.poweredByCharger(app);
        var want = s.economyEffective(isMobile) || wifiOnlyGentle;
        if (s.trafficSaveMode == want) return;
        SettingsStore.update(app, s with { trafficSaveMode = want });
        var why =
            wifiOnlyGentle && !s.economyEffective(isMobile) ? "Только Wi-Fi: Wi-Fi на батарее (щадящий)" :
            !s.economyEnabled ? "режим экономии выключен" :
            !s.trafficSaveAutoByNetwork ? "экономия всегда (подпункт «только моб.» выкл)" :
            isMobile ? "мобильная сеть" :
            "Wi-Fi (экономия только на мобильной)";
        MonitorLog.@event(app, "net", want ? "Экономия включена" : "Экономия выключена", why);
        MonitorCoordinator.wake();   // монитор перечитает режим на ближайшем цикле
    }
}

/**
 * Промпт 118 (Android): точный будильник AlarmManager будит цикл монитора в Doze. На Windows Doze нет — цикл
 * монитора живёт на обычных таймерах, а выход из сна будит его событием питания (приложение → MonitorCoordinator.wake).
 * API сохранён ради построчного переноса NetworkMonitor; всё — no-op. Порт monitor/MonitorAlarm.kt.
 */
public static class MonitorAlarm
{
    public static bool canExact(Context context) => true;
    public static bool scheduleNext(Context context, long delayMs) => true;
    public static void cancel(Context context) { }

    /// <summary>Аналог PowerManager.WakeLock: на Windows не нужен (процесс не замораживается).</summary>
    public sealed class WakeLock
    {
        public void acquire(long timeoutMs) { }
        public void release() { }
        public bool isHeld => false;
    }

    public static WakeLock? newWakeLock(Context context) => new();
}
