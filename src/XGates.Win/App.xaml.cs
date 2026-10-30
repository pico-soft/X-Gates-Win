using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using XGates.App;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Update;

namespace XGates.Win;

/// <summary>
/// Процесс приложения: один экземпляр на пользователя, трей, события Windows → «сервис» (как BroadcastReceiver'ы
/// Android), окно по требованию. Закрытие окна = в трей (прокси работает дальше, как сервис Android); «Выход» в
/// меню трея — остановка ядра и выход.
/// </summary>
public partial class App : Application
{
    private const string MUTEX = @"Local\X-Gates-Win";
    private const string SHOW_EVENT = @"Local\X-Gates-Win.show";

    private Mutex? single;
    private EventWaitHandle? showEvent;
    private Tray? tray;
    private Context? ctx;
    private MainWindow? window;
    private MainScreenModel? main;
    private SubscriptionsModel? subs;
    private DispatcherUi? ui;
    private bool trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Событие «покажи окно» — ДО мьютекса и в обоих экземплярах: иначе второй запуск, попавший между созданием
        // мьютекса и события первым экземпляром, не нашёл бы событие и молча вышел.
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SHOW_EVENT, out _);
        single = new Mutex(initiallyOwned: true, MUTEX, out var first);
        if (!first)
        {
            // Уже запущено → попросить ту копию показать окно и выйти (второй трей-значок не нужен). Разрешаем ей
            // вывести окно на передний план (иначе Windows не даст фоновому процессу забрать фокус — лишь мигнёт кнопка).
            AllowSetForegroundWindow(ASFW_ANY);
            showEvent.Set();
            Shutdown();
            return;
        }
        new Thread(() => { while (showEvent.WaitOne()) Dispatcher.BeginInvoke(showWindow); }) { IsBackground = true, Name = "show-signal" }.Start();

        ctx = AppBootstrap.start(AppBootstrap.defaultDataDir());
        ui = new DispatcherUi(Dispatcher);
        tray = new Tray();
        NotificationHelper.Sink = new UiSink(tray, Dispatcher);
        NotificationHelper.clearStatus();
        tray.openRequested += showWindow;
        tray.fastestRequested += () => main?.onFastest();
        tray.stopRequested += () => main?.onStop();
        tray.exitRequested += exitApp;
        tray.balloonClicked += kind => { showWindow(); if (kind == NotificationHelper.Kind.UPDATE) window?.openSettings(); };
        UpdateFlowController.exitApp = () => Dispatcher.BeginInvoke(exitApp);
        UpdateFlowController.launchInstaller = WinInstaller.start;   // установить и открыть новую версию

        main = new MainScreenModel(ctx, ui);
        subs = new SubscriptionsModel(ctx, ui);
        subs.registryChanged += main.reloadAll;

        // События Windows → «сервис» (аналог NetworkCallback / ACTION_POWER_* / ACTION_SCREEN_ON Android).
        NetworkChange.NetworkAddressChanged += (_, _) => { XrayProxyService.onNetworkChanged(); main.onNetworkAvailable(); };
        NetworkChange.NetworkAvailabilityChanged += (_, a) => { XrayProxyService.onNetworkChanged(); if (a.IsAvailable) main.onNetworkAvailable(); };
        SystemEvents.PowerModeChanged += (_, a) =>
        {
            XrayProxyService.onPowerChanged();
            if (a.Mode == PowerModes.Resume) XrayProxyService.onNetworkChanged();   // после сна сеть «новая»
        };
        SystemEvents.SessionSwitch += (_, a) => { if (a.Reason == SessionSwitchReason.SessionUnlock) XrayProxyService.onScreenOn(); };

        var fromLogin = e.Args.Contains(Autostart.ARG);
        if (fromLogin)
        {
            // Вход в Windows (аналог BootReceiver): поднять последний сервер без полного теста, окно не показывать.
            XrayProxyService.startFromBoot(ctx);
            tray.info("X-Gates Win", "Работает в трее. Щёлкните значок, чтобы открыть.");
        }
        else showWindow();
    }

    private void showWindow()
    {
        if (ctx == null || main == null || subs == null) return;
        if (window == null)
        {
            window = new MainWindow(ctx, main, subs, ui!);
            window.hiddenToTray += () =>
            {
                AppBootstrap.onPause();
                if (!trayHintShown) { trayHintShown = true; tray?.info("X-Gates Win", "Окно скрыто — приложение работает в трее. Выход — правой кнопкой по значку."); }
            };
            window.minimized += AppBootstrap.onPause;
            window.restored += () => { AppBootstrap.onResume(ctx); main.onResume(); };
            window.Show();
            main.autoStart();   // как LaunchedEffect(Unit) экрана: один раз за процесс, при первом показе
        }
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        AppBootstrap.onResume(ctx);
        main.onResume();
    }

    private void exitApp()
    {
        try { XrayProxyService.shutdown(); } catch (Exception ex) { Log.w("App", "shutdown", ex); }
        main?.Dispose(); subs?.Dispose();
        tray?.Dispose(); tray = null;
        window?.forceClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        try { single?.ReleaseMutex(); } catch (Exception) { }
        base.OnExit(e);
    }

    private const int ASFW_ANY = -1;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>«Главный поток» моделей = поток окна (Dispatcher).</summary>
    public sealed class DispatcherUi(Dispatcher d) : IUiThread
    {
        public void post(Action action) => d.BeginInvoke(action);
    }

    /// <summary>Уведомления приходят из фоновых потоков ядра → значок трогаем только из потока окна.</summary>
    private sealed class UiSink(Tray tray, Dispatcher d) : NotificationHelper.INotificationSink
    {
        public void show(NotificationHelper.Kind kind, string title, string text) => d.BeginInvoke(() => tray.show(kind, title, text));
        public void cancel(NotificationHelper.Kind kind) => d.BeginInvoke(() => tray.cancel(kind));
    }
}
