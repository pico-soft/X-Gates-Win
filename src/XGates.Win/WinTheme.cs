using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XGates.Win;

/// <summary>Тема Windows: настройку только читаем, в системе ничего не меняем. Заголовок окна красим у своего окна, это не системная настройка.</summary>
internal static class WinTheme
{
    public const string ICON = "pack://application:,,,/Assets/app.ico";
    public const string ICON_ON_DARK = "pack://application:,,,/Assets/app-on-dark.ico";

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>true — система приняла. 20 — номер настройки с Windows 10 2004, 19 — раньше.</summary>
    public static bool setDarkTitleBar(IntPtr hwnd, bool dark)
    {
        var on = dark ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) == 0 || DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)) == 0;
    }
}
