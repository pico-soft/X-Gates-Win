using XGates.Core.Service;
using WinForms = System.Windows.Forms;

namespace XGates.Win;

/// <summary>
/// Значок в трее — Windows-замена уведомлений Android (NotificationHelper.Sink): статус «Прокси активен» = подсказка
/// значка, прочие уведомления = всплывающие подсказки. Меню: открыть, «Самый быстрый», стоп, выход.
/// </summary>
public sealed class Tray : NotificationHelper.INotificationSink, IDisposable
{
    private readonly WinForms.NotifyIcon icon;
    private NotificationHelper.Kind? lastBalloon;

    public event Action? openRequested;
    public event Action? fastestRequested;
    public event Action? stopRequested;
    public event Action? exitRequested;
    /// <summary>Щелчок по всплывающей подсказке: какое уведомление (UPDATE → вкладка «Настройки», как OPEN_UPDATE Android).</summary>
    public event Action<NotificationHelper.Kind>? balloonClicked;

    public Tray()
    {
        icon = new WinForms.NotifyIcon
        {
            Icon = loadIcon(),
            Text = "X-Gates Win",
            Visible = true,
            ContextMenuStrip = new WinForms.ContextMenuStrip(),
        };
        icon.ContextMenuStrip.Items.Add("Открыть", null, (_, _) => openRequested?.Invoke());
        icon.ContextMenuStrip.Items.Add("Самый быстрый", null, (_, _) => fastestRequested?.Invoke());
        icon.ContextMenuStrip.Items.Add("Остановить прокси", null, (_, _) => stopRequested?.Invoke());
        icon.ContextMenuStrip.Items.Add(new WinForms.ToolStripSeparator());
        icon.ContextMenuStrip.Items.Add("Выход", null, (_, _) => exitRequested?.Invoke());
        icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) openRequested?.Invoke(); };
        icon.BalloonTipClicked += (_, _) => { if (lastBalloon is { } k) balloonClicked?.Invoke(k); else openRequested?.Invoke(); };
    }

    /// <summary>Наш значок (кадр 16/24 px — резко в трее); не прочитался — стандартный: без значка трея приложения нет вовсе.</summary>
    private static System.Drawing.Icon loadIcon()
    {
        try
        {
            var sri = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            return new System.Drawing.Icon(sri!.Stream, WinForms.SystemInformation.SmallIconSize);
        }
        catch (Exception e)
        {
            XGates.Core.Platform.Log.w("Tray", "значок не прочитан — стандартный", e);
            return System.Drawing.SystemIcons.Application;
        }
    }

    public void show(NotificationHelper.Kind kind, string title, string text)
    {
        if (kind == NotificationHelper.Kind.STATUS)
        {
            // Подсказка значка ограничена 127 символами (NotifyIcon бросает на длинной).
            var t = $"X-Gates Win — {text}";
            icon.Text = t.Length > 127 ? t[..127] : t;
            return;
        }
        lastBalloon = kind;
        icon.ShowBalloonTip(10_000, title, text, kind is NotificationHelper.Kind.NO_SERVERS ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
    }

    public void cancel(NotificationHelper.Kind kind)
    {
        if (kind == NotificationHelper.Kind.STATUS) icon.Text = "X-Gates Win — прокси не запущен";
    }

    public void info(string title, string text) { lastBalloon = null; icon.ShowBalloonTip(5_000, title, text, WinForms.ToolTipIcon.Info); }

    public void Dispose() { icon.Visible = false; icon.Dispose(); }
}
