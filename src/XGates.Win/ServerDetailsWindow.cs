using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XGates.App;
using XGates.Core.Xray.Link;

namespace XGates.Win;

/// <summary>Детали сервера (ServerDetailDialog Kotlin): параметры и действия — пауза, имя, стоп-лист, перемер.</summary>
public sealed class ServerDetailsWindow : Window
{
    private readonly MainScreenModel main;
    private readonly ServerProfile p;
    private readonly StackPanel body = new() { Margin = new Thickness(16) };
    private readonly TextBlock remeasure = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.Bold };
    private readonly DispatcherTimer timer;

    public ServerDetailsWindow(Window owner, MainScreenModel main, ServerProfile p)
    {
        this.main = main; this.p = p;
        Owner = owner; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 700 };
        main.clearRemeasureStatus();
        build();
        // Статус перемера меняется в фоне — показываем живым, пока окно открыто.
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            var s = main.snapshot();
            remeasure.Text = s.remeasureStatus;
        }, Dispatcher);
        Closed += (_, _) => timer.Stop();
    }

    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

    private void build()
    {
        var d = main.details(p);
        Title = d.name;
        body.Children.Clear();
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (k, v) in d.rows)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var r = grid.RowDefinitions.Count - 1;
            var kt = new TextBlock { Text = k, Foreground = Gray, Margin = new Thickness(0, 2, 12, 2) };
            var vt = new TextBox { Text = v, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(kt, r); Grid.SetRow(vt, r); Grid.SetColumn(vt, 1);
            grid.Children.Add(kt); grid.Children.Add(vt);
        }
        body.Children.Add(grid);

        body.Children.Add(button(d.paused ? "Использовать снова" : "Не использовать", () => { main.toggleUse(p); Close(); }));
        body.Children.Add(hint(d.pauseHint));
        body.Children.Add(button("Переименовать…", rename));
        if (d.hasCustomName) body.Children.Add(button("Вернуть имя от сервера", () => { main.resetName(p); Close(); }));
        body.Children.Add(button(d.blockedByKey ? "Убрать из стоп-листа" : "В стоп-лист", () => { main.toggleBlock(p); Close(); }));
        body.Children.Add(hint(d.blockHint));
        if (d.blockedByWord && !d.blockedByKey) body.Children.Add(hint("Скрыт правилом-словом стоп-листа. Снять — Настройки → Стоп-лист."));
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(button("Перемерить этот сервер", () => main.onRemeasure(p)));
        row.Children.Add(button("Закрыть", Close));
        body.Children.Add(row);
        body.Children.Add(remeasure);
    }

    private static Button button(string text, Action onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 4, 8, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static TextBlock hint(string text) => new() { Text = text, Foreground = Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };

    private void rename()
    {
        var d = main.details(p);
        var same = main.sameOriginalCount(p);
        var box = new TextBox { Text = d.name, Margin = new Thickness(0, 6, 0, 6), MinWidth = 320 };
        var all = new CheckBox { Content = $"Применить ко всем с таким же именем ({same})", Visibility = same > 1 ? Visibility.Visible : Visibility.Collapsed };
        var ok = new Button { Content = "Сохранить", IsDefault = true, Margin = new Thickness(0, 10, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = $"Имя от сервера: {d.rows.FirstOrDefault(r => r.key == "Имя от сервера").value ?? d.name}", Foreground = Gray, FontSize = 11 });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "Пустое имя = вернуть имя от сервера.", Foreground = Gray, FontSize = 11 });
        panel.Children.Add(all);
        panel.Children.Add(buttons);
        var w = new Window { Title = "Переименовать сервер", Owner = this, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (w.ShowDialog() == true) { main.renameServer(p, box.Text.Trim(), all.IsChecked == true); Close(); }
    }
}
