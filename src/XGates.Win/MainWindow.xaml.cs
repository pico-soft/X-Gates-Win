using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XGates.App;
using XGates.Core;
using XGates.Core.Crash;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Monitor;
using XGates.Core.Subscription;
using XGates.Core.Traffic;
using XGates.Core.Update;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;
using static XGates.App.UiFormat;

namespace XGates.Win;

/// <summary>
/// Окно: три вкладки как нижняя панель Android (Главная · Подписки · Настройки). Только отрисовка снимков моделей
/// (XGates.App) и проброс нажатий. Закрытие окна прячет его в трей — прокси работает дальше.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Context ctx;
    private readonly MainScreenModel main;
    private readonly SubscriptionsModel subs;
    private readonly BlocklistModel blocklist;
    private bool promptShowing;
    private readonly DispatcherTimer redraw;
    private bool dirty = true, closing;
    private string? coreVersion;   // версия ядра — один раз (Kotlin: remember { coreVersion() }), не на каждой перерисовке

    public event Action? hiddenToTray;   // закрыли крестиком → в трей
    public event Action? minimized;      // свернули на панель задач
    public event Action? restored;       // развернули обратно → снова «на переднем плане»

    // Списки обновляются НА МЕСТЕ (строки — с уведомлением об изменении): пересоздание строк 4 раза в секунду во время
    // теста уничтожало кнопки под курсором — клик между нажатием и отпусканием терялся (находка ревью).
    private readonly System.Collections.ObjectModel.ObservableCollection<RowView> liveRows = [], prevRows = [], allRows = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<SourceView> sourceRows = [];

    public MainWindow(Context ctx, MainScreenModel main, SubscriptionsModel subs, IUiThread ui)
    {
        this.ctx = ctx; this.main = main; this.subs = subs;
        blocklist = new BlocklistModel(ctx);
        InitializeComponent();
        // Перерисовка не чаще ~4 раз в секунду: тики теста идут десятками в секунду (как отдельный StateFlow бара в Kotlin).
        var ticks = 0;
        redraw = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (dirty) render();
            if (++ticks % 4 == 0 && IsVisible && Tabs.SelectedItem == SettingsTab) renderStats();   // время «N с назад», трафик
        }, Dispatcher);
        main.changed += () => dirty = true;
        subs.changed += () => dirty = true;
        TestProgress.state.changed += _ => dirty = true;
        UpdateStore.record.changed += _ => dirty = true;
        UpdateStore.live.changed += _ => dirty = true;
        UpdateFlowController.phase.changed += _ => dirty = true;
        WinSettingsStore.state.changed += _ => dirty = true;
        BlocklistStore.state.changed += _ => Dispatcher.BeginInvoke(() => { if (Tabs.SelectedItem == SettingsTab) renderBlocklist(); });
        redraw.Start();
        Tabs.SelectionChanged += (_, e) => { if (e.Source == Tabs) { dirty = true; if (Tabs.SelectedItem == SettingsTab) loadSettings(); } };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) minimized?.Invoke(); else restored?.Invoke(); };
        LiveList.ItemsSource = liveRows; PrevList.ItemsSource = prevRows; AllList.ItemsSource = allRows;
        SourcesList.ItemsSource = sourceRows;
        render();
    }

    /// <summary>Закрытие = в трей (прокси работает дальше); настоящий выход — меню трея «Выход».</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closing)
        {
            e.Cancel = true;
            Hide();
            hiddenToTray?.Invoke();
        }
        base.OnClosing(e);
    }

    public void forceClose() { closing = true; redraw.Stop(); Close(); }

    public void openSettings() { Tabs.SelectedItem = SettingsTab; loadSettings(); }

    // ═════════════ отрисовка ═════════════
    private void render()
    {
        dirty = false;
        if (!IsVisible) { dirty = true; return; }   // спрятано в трей — рисуем при показе
        if (Tabs.SelectedIndex == 0) renderMain();
        else if (Tabs.SelectedIndex == 1) renderSubs();
        else renderSettingsLive();
    }

    private static SolidColorBrush B(uint argb) => new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    private static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    private void renderMain()
    {
        var s = main.snapshot();
        var st = s.status;

        // «Нет живых серверов — включить выключенные источники?» (AppRoot Kotlin) — поверх любой вкладки, сами не включаем.
        if (s.enablePrompt is { } ep && !promptShowing)
        {
            promptShowing = true;
            Dispatcher.BeginInvoke(() =>
            {
                var r = MessageBox.Show(this, $"Все включённые серверы перебраны — живых нет. Есть {ep.sources} выключенных источников (~{ep.servers} серверов). Включить их и обновить?",
                    "Нет живых серверов", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes) main.acceptEnablePrompt(); else main.declineEnablePrompt();
                promptShowing = false;
            });
        }
        CrashBanner.Visibility = V(s.showCrashBanner);
        UpdateBanner.Visibility = V(s.updateBanner != null);
        UpdateBannerText.Text = UpdateFlowController.phase.value switch
        {
            UpdateFlowController.Phase.Checking => "Проверяю обновление…",
            UpdateFlowController.Phase.Downloading d => d.total > 0 ? $"Скачиваю {d.done * 100 / d.total}%" : "Скачиваю…",
            UpdateFlowController.Phase.Installing => "Запускаю установку…",
            UpdateFlowController.Phase.Failed f => f.message,
            _ => "Нажмите, чтобы обновить",   // номер версии НЕ пишем (часто уже вышли релизы новее)
        };
        PausedBanner.Visibility = V(st.paused);
        AddSubscriptionHint.Visibility = V(s.sourcesEmpty);

        // Цвет плашки — правило Elyor (StatusBox Kotlin): выкл серый; обрыв тёмно-красный; не подтверждено серый; по скорости.
        var liveFresh = (st.tunnelDownMbps ?? 0.0) > 0.0 && st.liveMeasuredAtMs > 0 && JavaSystem.currentTimeMillis() - st.liveMeasuredAtMs <= MainScreenModel.HEALTH_FRESH_MS;
        var colorSpeed = Math.Max(liveFresh ? st.tunnelDownMbps ?? 0.0 : 0.0, st.speedMbps ?? 0.0);
        (uint bg, uint fg) =
            !st.running ? (0xFF3A3A3Au, 0xFFBDBDBDu) :
            st.problem ? (0xFF7F1D1Du, 0xFFFFCDD2u) :
            !st.verified ? (0xFF5C5C5Cu, 0xFFE8E8E8u) :
            colorSpeed > 0.0
                ? colorSpeed > 5.0 ? (0xFF1B5E20u, 0xFFA5D6A7u)
                : colorSpeed >= 1.0 ? (0xFF37474Fu, 0xFFB0BEC5u)
                : colorSpeed >= 0.2 ? (0xFF5D4037u, 0xFFD7CCC8u)
                : (0xFF5D2A2Au, 0xFFEF9A9Au)
            : (0xFF1B5E20u, 0xFFA5D6A7u);
        StatusBox.Background = B(bg);
        var fgB = B(fg);
        foreach (var tb in StatusPanel.Children.OfType<TextBlock>()) tb.Foreground = fgB;

        StatusTitle.Text = st.running ? "● " + (st.serverNumber > 0 ? $"№{st.serverNumber} · " : "") + (st.serverName ?? "Активен") : "○ Не запущен";
        var tunnelOk = st.running && st.tunnelOk;
        StatusDirect.Visibility = V(tunnelOk);
        StatusTunnel.Visibility = V(tunnelOk);
        if (tunnelOk)
        {
            StatusDirect.Text = $"Напрямую: ↓{(st.directDownMbps is >= 0 ? r1(st.directDownMbps.Value) : "—")} Мбит/с";
            if (st.liveMeasuredAtMs <= 0L) StatusTunnel.Text = "замеряю скорость…";
            else
            {
                var ageMin = (int)((JavaSystem.currentTimeMillis() - st.liveMeasuredAtMs) / 60_000L);
                var age = ageMin <= 0 ? "сейчас" : $"{ageMin} мин назад";
                StatusTunnel.Text = $"Туннель: {(st.tunnelDownMbps is >= 0 ? "↓" + r1(st.tunnelDownMbps.Value) : "↓—")} / {(st.tunnelUpMbps is >= 0 ? "↑" + r1(st.tunnelUpMbps.Value) : "↑—")} Мбит/с · {age}";
            }
        }
        StatusLine.Text = st.running && st.statusLine.Length > 0 ? "⚠ " + st.statusLine : "";
        StatusLine.Visibility = V(StatusLine.Text.Length > 0);
        StatusBlocked.Visibility = V(st.running && st.blocked);
        StatusHidden.Visibility = V(st.running && st.hidden);
        var pingSuffix = st.pingMs is int pm ? pm < 0 ? " · пинг ✗" : $" · пинг {pm} мс" + (st.pingAge.Length > 0 ? $" ({st.pingAge})" : "") : "";
        StatusSubtitle.Text = st.running && st.subtitle != null ? st.subtitle + pingSuffix : "";
        StatusSubtitle.Visibility = V(StatusSubtitle.Text.Length > 0);
        StatusIp.Text = st.running ? "IP: " + (st.ipText.Length == 0 ? "…" : st.ipText) : "";
        StatusIp.Visibility = V(st.running);
        StatusMessage.Text = st.message.Length > 0 && st.message != "idle" ? st.message : "";
        StatusMessage.Visibility = V(StatusMessage.Text.Length > 0);
        CheckButton.Content = s.checking ? "■ Прервать проверку" : "↻ Проверить";
        ExcludeButton.Visibility = V(st.running && !s.checking);
        CheckProgress.Visibility = V(s.checking);
        CheckStatus.Text = s.checkStatus;
        CheckStatus.Visibility = V(s.checkStatus.Length > 0);

        ModeLine.Text = s.modeLine;
        StopWordsLine.Text = s.stopWordsSuspended ? "⚠ Стоп-слова сняты (временно) · вернуть ограничения" : "Снять ограничения по стоп-словам (временно)";
        StopWordsLine.Foreground = s.stopWordsSuspended ? B(0xFFE8710A) : (Brush)FindResource("Gray");
        StopWordsLine.FontWeight = s.stopWordsSuspended ? FontWeights.Bold : FontWeights.Normal;
        WhiteListBanner.Visibility = V(s.whiteListModeActive);

        var busy = s.fullTesting || s.refreshingSubs;
        FastestButton.Content = busy ? (s.refreshingSubs ? "■  Обновление…" : "■  Прервать") : "▶  Самый быстрый";
        StopButton.Visibility = V(st.running);
        var tp = TestProgress.state.value;
        TestBar.Visibility = V(tp.active);
        TestBar.IsIndeterminate = tp.indeterminate;
        TestBar.Value = tp.fraction;
        TestPhase.Text = tp.phase;
        SubStatus.Text = s.subStatus;

        LiveHeader.Text = $"Живые серверы ({s.alive.Count})";
        HiddenNote.Text = s.hiddenCount > 0 ? $"скрыто настройками и стоп-листом: {s.hiddenCount}" : "";
        HiddenNote.Visibility = V(s.hiddenCount > 0);
        NoProtocolsNote.Visibility = V(s.noProtocolsAllowed);
        HiddenLiveNote.Visibility = V(s.hiddenLiveProtocols.Count > 0);
        HiddenLiveNote.Text = s.hiddenLiveProtocols.Count > 0
            ? $"Живые серверы есть среди скрытых протоколов: {string.Join(", ", s.hiddenLiveProtocols)} — включите их в «Настройки → Протоколы»." : "";
        sync(liveRows, s.alive);
        LiveEmpty.Text = s.serversEmpty ? "Список пуст — обновите подписку." : "Запусти тест кнопкой «Самый быстрый».";
        LiveEmpty.Visibility = V(s.alive.Count == 0);
        PrevBox.Visibility = V(s.prevMeasured.Count > 0);
        PrevExpander.Header = $"Ранее измеренные ({s.prevMeasured.Count})";
        if (PrevExpander.IsExpanded) sync(prevRows, s.prevMeasured);
        AllExpander.Header = $"Все серверы ({s.all.Count})";
        if (AllExpander.IsExpanded) sync(allRows, s.all);
    }

    /// <summary>
    /// Привести коллекцию к списку строк: тот же порядок ключей → обновить строки на месте (кнопки живут); иначе —
    /// пересобрать (порядок сменился — например, пришёл новый замер; это редкие события, не каждый тик).
    /// </summary>
    private static void sync(System.Collections.ObjectModel.ObservableCollection<RowView> rows, IReadOnlyList<ServerRowInfo> items)
    {
        if (rows.Count == items.Count && rows.Select(r => r.Info.key).SequenceEqual(items.Select(i => i.key)))
        {
            for (var i = 0; i < items.Count; i++) rows[i].update(items[i]);
            return;
        }
        rows.Clear();
        foreach (var i in items) rows.Add(new RowView(i));
    }

    /// <summary>Строка списка для привязки (ServerRow Kotlin). Обновляется на месте — см. <see cref="sync"/>.</summary>
    public sealed class RowView(ServerRowInfo info) : System.ComponentModel.INotifyPropertyChanged
    {
        private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        private ServerRowInfo r = info;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void update(ServerRowInfo next)
        {
            if (next == r) return;   // record: равенство по значению — без лишней перерисовки
            r = next;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));   // все свойства
        }
        public ServerRowInfo Info => r;
        public Brush RowBg => r.isActive ? new SolidColorBrush(Color.FromRgb(0xD6, 0xE4, 0xFF)) : Brushes.Transparent;
        public string Title => (r.isActive ? "● " : "") + (r.number > 0 ? $"{r.number}. " : "") + r.name;
        public FontWeight TitleWeight => r.isActive ? FontWeights.Bold : FontWeights.Normal;
        public Brush TitleBrush => r.paused ? Gray : Brushes.Black;
        public string Caption => r.caption;
        public Visibility CaptionVisibility => V(r.caption.Length > 0);
        public string PingText => r.pingMs is int p ? p < 0 ? "пинг ✗" : $"пинг {p} мс" : "";
        public Visibility PingVisibility => V(!r.paused && r.pingMs != null);
        public string PingAge => r.pingAge;
        public Visibility PingAgeVisibility => V(!r.paused && r.pingMs != null && r.pingAge.Length > 0);
        // Глаз = ЧТО СДЕЛАЕТ нажатие: включённый → перечёркнутый (выключить); исключённый → открытый (вернуть).
        public string EyeGlyph => r.paused ? "" : "";
        public string EyeTip => r.paused ? "Использовать снова" : "Не использовать";
        public Brush EyeBrush => r.paused ? Gray : new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
        private bool quick => r.speedFull == false && r.speedMbps is > 0.0;
        public string SpeedText => (quick ? "≈" : "") + speedCell(r.speedMbps);
        public FontWeight SpeedWeight => r.speedFull == false ? FontWeights.Normal : FontWeights.Bold;
        public Brush SpeedBrush => r.speedMbps switch
        {
            null => Gray,
            <= 0 => new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)),
            < 1 => new SolidColorBrush(Color.FromRgb(0xBF, 0x36, 0x0C)),
            < 5 => new SolidColorBrush(Color.FromRgb(0xF5, 0x7C, 0x00)),
            _ => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
        };
        public string SpeedAge => r.speedAge;
        public Visibility SpeedAgeVisibility => V(r.speedAge.Length > 0 && r.speedMbps is > 0.0);
        public string ConnectGlyph => r.isActive ? "●" : "▶";
    }

    // ═════════════ Главная: действия ═════════════
    private static RowView? rowOf(object sender) => (sender as FrameworkElement)?.Tag as RowView;
    private void OnRowDetails(object sender, MouseButtonEventArgs e)
    {
        if (rowOf(sender) is { } r) new ServerDetailsWindow(this, main, r.Info.profile).ShowDialog();
    }
    private void OnConnect(object sender, RoutedEventArgs e) { if (rowOf(sender) is { } r) main.connectServer(r.Info.profile, "ручной выбор"); }
    private void OnToggleUse(object sender, RoutedEventArgs e) { if (rowOf(sender) is { } r) main.toggleUse(r.Info.profile); }
    private void OnFastest(object sender, RoutedEventArgs e)
    {
        var s = main.snapshot();
        if (s.fullTesting || s.refreshingSubs) main.onCancel(); else main.onFastest();
    }
    private void OnStop(object sender, RoutedEventArgs e) => main.onStop();
    private void OnCheck(object sender, RoutedEventArgs e) { if (main.snapshot().checking) main.onCancelCheck(); else main.onCheck(); }
    private void OnExclude(object sender, RoutedEventArgs e) => main.onRemoveCurrent();
    private void OnRefreshIp(object sender, MouseButtonEventArgs e) => main.refreshIp();
    private void OnModeLine(object sender, MouseButtonEventArgs e) => openSettings();
    private void OnStopWords(object sender, MouseButtonEventArgs e) => main.toggleStopWordsSuspended();
    private void OnWhiteListNormal(object sender, RoutedEventArgs e) => main.forceNormalMode();
    private void OnAllExpanded(object sender, RoutedEventArgs e) => dirty = true;
    private void OnAllCollapsed(object sender, RoutedEventArgs e) => allRows.Clear();   // тысячи строк не держим свёрнутыми
    private void OnUpdateBannerClick(object sender, MouseButtonEventArgs e) => UpdateFlowController.oneTap(ctx);
    private void OnUpdateDismiss(object sender, MouseButtonEventArgs e) { e.Handled = true; main.dismissUpdateBanner(); }
    private void OnCrashDismiss(object sender, RoutedEventArgs e) => main.dismissCrashBanner();
    private void OnCrashCopy(object sender, RoutedEventArgs e)
    {
        var latest = CrashReporter.reports().FirstOrDefault();
        if (latest != null) copy(CrashReporter.read(latest));
    }
    private void OnCrashSend(object sender, RoutedEventArgs e)
    {
        // Отправка — только руками человека (Промпт 97): отчёт кладём в буфер обмена и открываем письмо — вставить и отправить.
        var latest = CrashReporter.reports().FirstOrDefault();
        if (latest == null) return;
        copy(CrashReporter.emailBody("", CrashReporter.read(latest)));
        openUrl($"mailto:{CrashReporter.DEVELOPER_EMAIL}?subject={Uri.EscapeDataString(CrashReporter.emailSubject())}&body={Uri.EscapeDataString("Отчёт скопирован в буфер обмена — вставьте его сюда (Ctrl+V).")}");
    }

    // ═════════════ Подписки ═════════════
    public sealed class SourceView(SubSource s0) : System.ComponentModel.INotifyPropertyChanged
    {
        private SubSource s = s0;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void update(SubSource next)
        {
            if (next == s) return;
            s = next;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
        }
        public SubSource Source => s;
        public bool Enabled => s.enabled;
        public string Name => s.name;
        public string Url => s.url;
        public string CountLine => $"{s.serverCount} серв" + (string.IsNullOrWhiteSpace(s.url) ? " · локальная" : "");
        public string TimesLine => $"удача: {s.lastOkTs ?? "—"} · попытка: {s.lastRefreshTs ?? "не обновлялась"}";
        public string Error => s.lastError ?? "";
        public Visibility ErrorVisibility => V(!string.IsNullOrWhiteSpace(s.lastError));
    }

    private void renderSubs()
    {
        var s = subs.snapshot();
        SubsHeader.Text = $"Подписки ({s.sources.Count})";
        SubsRefresh.IsEnabled = !s.refreshing;
        SubsRefresh.Content = s.refreshing ? "Обновление…" : "↻  Обновить";
        SubsStatus.Text = s.status;
        if (sourceRows.Count == s.sources.Count && sourceRows.Select(v => v.Source.id).SequenceEqual(s.sources.Select(x => x.id)))
            for (var i = 0; i < s.sources.Count; i++) sourceRows[i].update(s.sources[i]);
        else { sourceRows.Clear(); foreach (var x in s.sources) sourceRows.Add(new SourceView(x)); }
        NoSources.Visibility = V(s.sources.Count == 0);
        SeedCard.Visibility = V(s.sources.Count == 0 && s.seed.error.Trim().Length > 0);
        SeedError.Text = $"Причина: {s.seed.error}. Это бывает, когда сеть закрыта или адрес заблокирован напрямую — на свежей установке туннеля ещё нет, обойти нечем.";
    }

    private static SourceView? srcOf(object sender) => (sender as FrameworkElement)?.Tag as SourceView;
    private void OnSubsRefresh(object sender, RoutedEventArgs e) => subs.onRefreshAll();
    private void OnSeedRetry(object sender, RoutedEventArgs e) => subs.retrySeed();
    private void OnSourceToggle(object sender, RoutedEventArgs e) { if (srcOf(sender) is { } v) subs.onToggle(v.Source.id, !v.Source.enabled); }
    private void OnSourceRename(object sender, RoutedEventArgs e)
    {
        if (srcOf(sender) is not { } v) return;
        var name = Prompt.ask(this, "Переименовать источник", "Название:", v.Source.name);
        if (!string.IsNullOrWhiteSpace(name)) subs.rename(v.Source.id, name.Trim());
    }
    private void OnSourceDelete(object sender, RoutedEventArgs e)
    {
        if (srcOf(sender) is not { } v) return;
        if (MessageBox.Show(this, subs.deleteConfirmText(v.Source), "Удалить источник?", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            subs.remove(v.Source.id);
    }
    private void OnAddUrl(object sender, RoutedEventArgs e) { subs.onAddUrl(NewUrl.Text.Trim(), NewName.Text.Trim()); NewUrl.Text = ""; NewName.Text = ""; }
    private void OnAddPaste(object sender, RoutedEventArgs e) { subs.onAddPaste(Paste.Text); Paste.Text = ""; }
    private void OnImportFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Импорт подписки из файла", Filter = "Все файлы|*.*|Текст|*.txt" };
        if (dlg.ShowDialog(this) == true) subs.onImportFile(dlg.FileName);
    }

    // ═════════════ Настройки ═════════════
    private void loadSettings()
    {
        var w = WinSettingsStore.current();
        SocksPort.Text = w.socksPort.ToString();
        HttpPort.Text = w.httpPort.ToString();
        HowToText.Text = howTo(w.socksPort, w.httpPort);
        var s = SettingsStore.current();
        try { LaunchAtLogin.IsChecked = Autostart.isEnabled(); } catch (Exception) { LaunchAtLogin.IsChecked = false; }
        ProtocolsPanel.Children.Clear();
        foreach (var p in Enum.GetValues<Protocol>())
        {
            var cb = new CheckBox { Content = p.ToString(), IsChecked = s.allowedProtocols.Contains(p), Margin = new Thickness(0, 2, 14, 2), Tag = p };
            cb.Click += OnProtocolToggle;
            ProtocolsPanel.Children.Add(cb);
        }
        buildParams();
        renderSettingsLive();
        renderBlocklist();
        renderStats();
    }

    /// <summary>Строки «Параметров» (SettingsRows): ввод сохраняется сразу, если значение годится (как в Android).</summary>
    private void buildParams()
    {
        ParamsPanel.Children.Clear();
        string? group = null;
        foreach (var row in SettingsRows.rows)
        {
            if (row.group != group)
            {
                group = row.group;
                ParamsPanel.Children.Add(new TextBlock { Text = group, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) });
            }
            var s = SettingsStore.current();
            var hint = new TextBlock { Text = $"деф. {row.defaultText}", Foreground = (Brush)FindResource("Gray"), FontSize = 11, Visibility = V(row.changed(s)) };
            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = row.title, TextWrapping = TextWrapping.Wrap });
            left.Children.Add(hint);
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            FrameworkElement control;
            if (row.kind == SettingKind.BOOL)
            {
                var cb = new CheckBox { IsChecked = row.read(s) == "true", VerticalAlignment = VerticalAlignment.Center };
                cb.Click += (_, _) =>
                {
                    if (row.commit(SettingsStore.current(), cb.IsChecked == true ? "true" : "false") is { } n) SettingsStore.update(ctx, n);
                    hint.Visibility = V(row.changed(SettingsStore.current()));
                };
                control = cb;
            }
            else
            {
                var tb = new TextBox { Text = row.read(s), Width = row.kind == SettingKind.TEXT ? double.NaN : 110, VerticalAlignment = VerticalAlignment.Center };
                var normal = tb.BorderBrush;
                tb.TextChanged += (_, _) =>
                {
                    var n = row.commit(SettingsStore.current(), tb.Text);
                    tb.BorderBrush = n == null ? Brushes.Firebrick : normal;
                    if (n != null && n != SettingsStore.current()) SettingsStore.update(ctx, n);
                    hint.Visibility = V(row.changed(SettingsStore.current()));
                };
                control = tb;
            }
            if (row.kind == SettingKind.TEXT)
            {
                var col = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
                col.Children.Add(left); col.Children.Add(control);
                ParamsPanel.Children.Add(col);
                continue;
            }
            DockPanel.SetDock(control, Dock.Right);
            line.Children.Add(control);
            line.Children.Add(left);
            ParamsPanel.Children.Add(line);
        }
    }

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Вернуть все настройки к значениям по умолчанию? Подписки, стоп-лист и порты не затрагиваются.", "Сброс настроек",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SettingsStore.resetToDefaults(ctx);
        loadSettings();
    }

    /// <summary>Признак жизни, журнал (последние 100) и трафик — обновляются раз в секунду, пока открыты «Настройки».</summary>
    private void renderStats()
    {
        var hb = StatsModel.heartbeatLine(SettingsStore.current(), MonitorStatus.state.value, JavaSystem.currentTimeMillis());
        HeartbeatLine.Text = hb;
        HeartbeatLine.Visibility = V(hb.Length > 0);
        JournalTitle.Text = StatsModel.journalTitle();
        var j = StatsModel.journal();
        if (JournalPanel.Tag as int? != MonitorLog.state.value.Count)   // перестраиваем только при новых событиях
        {
            JournalPanel.Tag = MonitorLog.state.value.Count;
            JournalPanel.Children.Clear();
            if (j.Count == 0) JournalPanel.Children.Add(new TextBlock { Text = StatsModel.JOURNAL_EMPTY, Foreground = (Brush)FindResource("Gray"), TextWrapping = TextWrapping.Wrap });
            foreach (var (kind, line, detail) in j.Take(100))
            {
                var color = kind switch { "switch" => B(0xFF1565C0), "error" => B(0xFFD32F2F), "net" => B(0xFFF9A825), _ => Brushes.Black };
                JournalPanel.Children.Add(new TextBlock { Text = line, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                if (detail.Length > 0) JournalPanel.Children.Add(new TextBlock { Text = detail, Foreground = (Brush)FindResource("Gray"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
            }
        }
        TrafficPanel.Children.Clear();
        foreach (var block in StatsModel.traffic(TrafficTracker.state.value, StatsModel.nowElapsed()))
        {
            TrafficPanel.Children.Add(new TextBlock { Text = block.title, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 2) });
            foreach (var (label, value, bold) in block.rows)
            {
                var d = new DockPanel();
                var v = new TextBlock { Text = value, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
                DockPanel.SetDock(v, Dock.Right);
                d.Children.Add(v);
                d.Children.Add(new TextBlock { Text = label, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal });
                TrafficPanel.Children.Add(d);
            }
        }
    }

    private void OnClearJournal(object sender, RoutedEventArgs e) { MonitorLog.clear(ctx); renderStats(); }
    private void OnResetTrafficSession(object sender, RoutedEventArgs e) { TrafficTracker.resetSession(); renderStats(); }
    private void OnResetTrafficToday(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, StatsModel.resetTodayText(TrafficTracker.state.value), "Сбросить за сутки?", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
        { TrafficTracker.resetToday(); renderStats(); }
    }
    private void OnResetTrafficAll(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, StatsModel.resetAllText(TrafficTracker.state.value), "Стереть всю историю?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
        { TrafficTracker.resetAll(); renderStats(); }
    }

    /// <summary>Инструкции (текст можно выделить и скопировать). Порты — текущие.</summary>
    private static string howTo(int socks, int http) =>
        $"""
        Firefox: Настройки → Основные → Параметры сети → «Настроить…» → «Ручная настройка прокси»:
          узел SOCKS 127.0.0.1, порт {socks}, SOCKS 5, ☑ «Отправлять DNS-запросы через прокси при использовании SOCKS 5».
          Остальные поля пустые. Проверка: откройте 2ip.ru — адрес и страна должны быть зарубежными.

        Chrome / Edge: сами берут только СИСТЕМНЫЙ прокси, а приложение его не меняет. Варианты:
          • расширение-переключатель прокси (SOCKS5 127.0.0.1:{socks});
          • отдельный ярлык браузера с параметром  --proxy-server="socks5://127.0.0.1:{socks}"
            (все окна браузера нужно закрыть перед запуском такого ярлыка).

        Telegram Desktop: Настройки → Продвинутые настройки → Тип соединения → «Использовать собственный прокси» →
          SOCKS5, сервер 127.0.0.1, порт {socks}, логин и пароль пустые.

        Программы с HTTP-прокси: 127.0.0.1:{http}.
        """;

    private void renderBlocklist()
    {
        WordsPanel.Children.Clear();
        foreach (var w in blocklist.words())
        {
            var chip = new Border { BorderBrush = (Brush)FindResource("Gray"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 1, 4, 1), Margin = new Thickness(0, 0, 6, 6) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = $"{w.word} · {w.count}", VerticalAlignment = VerticalAlignment.Center });
            var x = new Button { Content = "✕", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Tag = w.word, ToolTip = "Убрать слово" };
            x.Click += (_, _) => blocklist.removeWord(w.word);
            sp.Children.Add(x);
            chip.Child = sp;
            WordsPanel.Children.Add(chip);
        }
        BlockedPanel.Children.Clear();
        var blocked = blocklist.blockedServers();
        if (blocked.Count == 0)
            BlockedPanel.Children.Add(new TextBlock { Text = "Пусто. Заблокировать конкретный сервер: щелчок по строке → «В стоп-лист».", Foreground = (Brush)FindResource("Gray"), TextWrapping = TextWrapping.Wrap });
        foreach (var (key, name) in blocked)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var b = new Button { Content = "Разблокировать", Padding = new Thickness(8, 1, 8, 1) };
            b.Click += (_, _) => blocklist.unblock(key);
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            BlockedPanel.Children.Add(row);
        }
    }

    private void OnAddWord(object sender, RoutedEventArgs e)
    {
        var r = blocklist.tryAddWord(NewWord.Text);
        if (r is AddWordResult.NeedsConfirm c)
        {
            if (MessageBox.Show(this, c.text, c.title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            blocklist.tryAddWord(c.word, confirmed: true);
        }
        NewWord.Text = "";
    }

    private void renderSettingsLive()
    {
        var rec = UpdateStore.record.value;
        UpdateSummary.Text = rec.checkedAtMs > 0
            ? $"{rec.summary} · {DateTimeOffset.FromUnixTimeMilliseconds(rec.checkedAtMs).ToLocalTime():dd.MM.yyyy HH:mm}"
            : "Ещё не проверялось";
        UpdateInstallButton.Visibility = V(UpdateStore.live.value is UpdateCheckResult.Available);
        UpdatePhase.Text = UpdateFlowController.phase.value switch
        {
            UpdateFlowController.Phase.Checking => "Проверяю обновление…",
            UpdateFlowController.Phase.Downloading d => d.total > 0 ? $"Скачиваю {d.done * 100 / d.total}%" : $"Скачиваю… {fmtBytes(d.done)}",
            UpdateFlowController.Phase.Installing => "Запускаю установку — приложение закроется",
            UpdateFlowController.Phase.Failed f => f.message,
            _ => "",
        };
        var n = CrashReporter.reports().Count;
        CrashSummary.Text = n == 0 ? "Отчётов нет." : $"Сохранено отчётов: {n}.";
        if (coreVersion == null)
        {
            coreVersion = "…";
            KThread.start(() => { var v = UpdateStore.coreVersion() ?? "недоступно"; Dispatcher.BeginInvoke(() => { coreVersion = v; dirty = true; }); }, "core-version");
        }
        AboutText.Text = $"{AppInfo.APP_NAME} — {UpdateStore.appVersion()}\nЯдро: {coreVersion}\nДанные: {ctx.filesDir}";
    }

    private void OnApplyPorts(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SocksPort.Text.Trim(), out var socks) || !int.TryParse(HttpPort.Text.Trim(), out var http))
        { PortsStatus.Text = "Порты — целые числа"; PortsStatus.Foreground = Brushes.Firebrick; return; }
        applyPorts(socks, http);
    }

    private void OnDefaultPorts(object sender, RoutedEventArgs e)
    {
        SocksPort.Text = XrayConfig.DEFAULT_SOCKS_PORT.ToString(); HttpPort.Text = XrayConfig.DEFAULT_HTTP_PORT.ToString();
        applyPorts(XrayConfig.DEFAULT_SOCKS_PORT, XrayConfig.DEFAULT_HTTP_PORT);
    }

    private void applyPorts(int socks, int http)
    {
        var err = XrayConfig.portsError(socks, http);
        if (err != null) { PortsStatus.Text = err; PortsStatus.Foreground = Brushes.Firebrick; return; }
        WinSettingsStore.update(ctx, new WinSettings { socksPort = socks, httpPort = http });
        PortsStatus.Foreground = Brushes.DarkGreen;
        PortsStatus.Text = $"Сохранено: SOCKS5 127.0.0.1:{socks}, HTTP 127.0.0.1:{http}";
        // Работающий туннель слушает старые порты → переподнять тот же сервер на новых.
        var p = ProxyState.state.value;
        if (p.running && p.serverKey != null && SubscriptionManager.allServers(ctx).FirstOrDefault(x => SubscriptionManager.serverKey(x) == p.serverKey) is { } cur)
        {
            main.connectServer(cur, "смена портов");
            PortsStatus.Text += " · прокси перезапущен на новых портах";
        }
    }

    private void OnCopySocks(object sender, RoutedEventArgs e) => copy($"127.0.0.1:{WinSettingsStore.current().socksPort}");
    private void OnCopyHttp(object sender, RoutedEventArgs e) => copy($"127.0.0.1:{WinSettingsStore.current().httpPort}");

    private void OnLaunchAtLogin(object sender, RoutedEventArgs e)
    {
        try { Autostart.set(LaunchAtLogin.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show(this, $"Не удалось изменить автозапуск: {ex.Message}", "X-Gates Win"); }
        try { LaunchAtLogin.IsChecked = Autostart.isEnabled(); } catch (Exception) { }
        SettingsStore.update(ctx, SettingsStore.current() with { startOnBoot = LaunchAtLogin.IsChecked == true });
    }
    private void OnProtocolToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Protocol p } cb) return;
        var set = new HashSet<Protocol>(SettingsStore.current().allowedProtocols);
        if (cb.IsChecked == true) set.Add(p); else set.Remove(p);
        SettingsStore.update(ctx, SettingsStore.current() with { allowedProtocols = set });
    }

    private void OnUpdateCheck(object sender, RoutedEventArgs e)
    {
        UpdateCheckButton.IsEnabled = false;
        UpdateSummary.Text = "Проверяю…";
        KThread.start(() =>
        {
            try { UpdateStore.apply(ctx, UpdateChecker.check(ctx, manual: true), JavaSystem.currentTimeMillis()); }
            catch (Exception ex) { Log.w("Settings", "проверка обновления", ex); }
            Dispatcher.BeginInvoke(() => { UpdateCheckButton.IsEnabled = true; renderSettingsLive(); });
        }, "update-manual");
    }
    private void OnUpdateInstall(object sender, RoutedEventArgs e) => UpdateFlowController.oneTap(ctx);
    private void OnUpdateDetails(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, UpdateStore.record.value.details.Length > 0 ? UpdateStore.record.value.details : "Подробностей пока нет — проверка не выполнялась.", "Проверка обновления");

    private void OnOpenCrashDir(object sender, RoutedEventArgs e) => openFolder(Path.Combine(ctx.filesDir, "crashes"));
    private void OnClearCrashes(object sender, RoutedEventArgs e) { CrashReporter.clearAll(); renderSettingsLive(); main.dismissCrashBanner(); }
    private void OnOpenDataDir(object sender, RoutedEventArgs e) => openFolder(ctx.filesDir);
    private void OnOpenLog(object sender, RoutedEventArgs e) { var f = AppBootstrap.logFile(ctx); if (File.Exists(f)) openUrl(f); }

    /// <summary>Буфер обмена бывает занят другой программой (CLIPBRD_E_CANT_OPEN) — не роняем окно из-за этого.</summary>
    private static void copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.w("MainWindow", "буфер обмена недоступен", ex); }
    }

    private static void openFolder(string dir) { Directory.CreateDirectory(dir); openUrl(dir); }
    private static void openUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.w("MainWindow", $"не открылось: {target}", ex); }
    }
}
