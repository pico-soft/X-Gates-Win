using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using XGates.Core.Platform;
using XGates.Core.Settings;

namespace XGates.Win;

/// <summary>
/// Тема Windows: настройку только читаем, в системе ничего не меняем. Заголовок окна красим у своего окна, это не системная настройка.
/// Содержимое окна тёмным делает только свой переключатель в Настройках, от темы Windows оно не зависит.
/// </summary>
internal static class WinTheme
{
    public const string ICON = "pack://application:,,,/Assets/app.ico";
    public const string ICON_ON_DARK = "pack://application:,,,/Assets/app-on-dark.ico";

    private const string THEMES = "pack://application:,,,/Themes/";

    private const string KEY = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static int? read(string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(KEY); return k?.GetValue(name) as int?; }
        catch (Exception) { return null; }
    }

    /// <summary>Панель задач и трей. До Windows 10 1903 значения нет, а панель там всегда тёмная.</summary>
    public static bool taskbarDark => read("SystemUsesLightTheme") != 1;

    /// <summary>Окна приложений. Значения нет — светлые.</summary>
    public static bool appsDark => read("AppsUseLightTheme") == 0;

    /// <summary>Заголовок и значок окна тёмные: включён свой переключатель или тёмные окна в Windows.</summary>
    public static bool windowsDark => WinSettingsStore.current().darkTheme || appsDark;

    private static bool darkApplied;

    /// <summary>Поставить палитру и стили по переключателю. Словари описаны в App.xaml: первый палитра, второй стили.</summary>
    public static void apply()
    {
        var dark = WinSettingsStore.current().darkTheme;
        if (dark == darkApplied) return;
        darkApplied = dark;
        var merged = Application.Current.Resources.MergedDictionaries;
        merged[0] = new ResourceDictionary { Source = new Uri(THEMES + (dark ? "Dark.xaml" : "Light.xaml")) };
        merged[1] = new ResourceDictionary { Source = new Uri(THEMES + (dark ? "DarkStyles.xaml" : "Styles.xaml")) };
    }

    /// <summary>Диалоги создаются на месте: заголовок им красим здесь, одним обработчиком на все окна приложения.</summary>
    public static void watchDialogs() => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
        new RoutedEventHandler((w, _) => setDarkTitleBar(new WindowInteropHelper((Window)w).Handle, windowsDark)));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private const uint SWP_REDRAW_FRAME = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020;   // NOSIZE, NOMOVE, NOZORDER, NOACTIVATE, FRAMECHANGED
    private const int WM_SETICON = 0x0080;
    private static readonly IntPtr ICON_BIG = 1;

    /// <summary>true — система приняла. 20 — номер настройки с Windows 10 2004, 19 — раньше. Без перерисовки рамки уже открытое окно меняет цвет только после переключения на другое.</summary>
    public static bool setDarkTitleBar(IntPtr hwnd, bool dark)
    {
        var on = dark ? 1 : 0;
        var ok = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) == 0 || DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)) == 0;
        if (ok) SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_REDRAW_FRAME);
        return ok;
    }

    private static System.Drawing.Icon? taskbarIcon;   // дескриптор живёт, пока окно его показывает

    /// <summary>Большой значок окна: его берёт панель задач, а заголовок — малый. Поэтому панель может следовать своей теме, отдельной от темы окон.</summary>
    public static void setTaskbarIcon(IntPtr hwnd)
    {
        try
        {
            var sri = Application.GetResourceStream(new Uri(taskbarDark ? ICON_ON_DARK : ICON));
            var icon = new System.Drawing.Icon(sri!.Stream, System.Windows.Forms.SystemInformation.IconSize);
            SendMessage(hwnd, WM_SETICON, ICON_BIG, icon.Handle);
            taskbarIcon?.Dispose();
            taskbarIcon = icon;
        }
        catch (Exception e) { Log.w("WinTheme", "значок панели задач не поставлен", e); }
    }
}
