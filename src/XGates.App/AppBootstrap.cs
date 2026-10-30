using XGates.Core;
using XGates.Core.Crash;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Traffic;
using XGates.Core.Update;

namespace XGates.App;

/// <summary>
/// Запуск приложения: порядок XrayApp.onCreate (обработчик падений — первым) + MainActivity.onCreate (сторы) и
/// реакция на возврат окна (onResume). Без WPF — чтобы тот же порядок проверялся тестом.
/// </summary>
public static class AppBootstrap
{
    /// <summary>Каталог данных: %LOCALAPPDATA%\X-Gates (§13.4: Local — замеры привязаны к сети машины; отдельно от программы).</summary>
    public static string defaultDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Gates");

    /// <summary>Журнал приложения (аналог logcat): файл с ротацией в каталоге данных.</summary>
    public static string logFile(Context context) => Path.Combine(context.filesDir, "logs", "x-gates.log");

    public static Context start(string dataDir, bool logToFile = true)
    {
        var context = new Context(dataDir);
        if (logToFile) Log.Sink = new Log.FileSink(logFile(context));
        // XrayApp.onCreate: обработчик падений МАКСИМАЛЬНО РАНО (до окна и «сервиса»).
        CrashReporter.install(context, AppInfo.VERSION_NAME, AppInfo.VERSION_CODE);
        // MainActivity.onCreate: сторы — до первого замера.
        SettingsStore.init(context);        // настройки
        WinSettingsStore.init(context);     // Windows: порты локального прокси (§13.7)
        BlocklistStore.init(context);       // стоп-лист (первый запуск → засев дефолтом RU/BY)
        MonitorLog.init(context);           // журнал автомониторинга (переживает перезапуск)
        SubscriptionManager.init(context);  // миграция старой подписки в мультиподписки (однократно)
        TrafficTracker.init(context);       // дневные корзины трафика
        UpdateStore.init(context);          // результат последней проверки обновления + время
        // XrayApp: уборка отработавших установщиков обновления (в фоне — не задерживаем запуск).
        KThread.start(() => UpdateInstaller.pruneObsolete(context), "prune-updates");
        // Авто-проверка обновления при холодном старте (не чаще раза в сутки, в фоне, ждёт свой SOCKS).
        UpdateAutoCheck.coldStart(context);
        Log.i("App", $"{AppInfo.APP_NAME} {AppInfo.VERSION_NAME} (code {AppInfo.VERSION_CODE}) · данные: {dataDir}");
        return context;
    }

    /// <summary>MainActivity.onResume: окно снова на экране.</summary>
    public static void onResume(Context context)
    {
        UpdateFlowController.appInForeground = true;
        DefaultSeeder.maybeSeed(context, "resume");   // Пр.136: повтор посева
        EconomyNet.applyNow(context);                 // Пр.139: пересчитать режим по ТЕКУЩЕЙ сети
        if (SettingsStore.current().whiteListAutoEnabled)   // Пр.140: оценить белые списки при открытии (в фоне)
            KThread.start(() => { try { WhiteListDetector.evaluate(context, tunnelForeignOk: false); } catch (Exception) { } }, "white-list-eval");
        MonitorCoordinator.wake();
    }

    /// <summary>MainActivity.onPause: окно свернули/спрятали в трей.</summary>
    public static void onPause() => UpdateFlowController.appInForeground = false;
}
