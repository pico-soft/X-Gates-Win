using Microsoft.Win32;

namespace XGates.Win;

/// <summary>
/// «Запускать при входе в Windows» (аналог startOnBoot Android). ТОЛЬКО по флажку в приложении (CLAUDE.md: системные
/// настройки не трогаем сверх разрешённого): значение в HKCU\Software\Microsoft\Windows\CurrentVersion\Run — своё,
/// пользовательское, без прав администратора. Источник правды — сам реестр (флажок не расходится с фактом, если
/// запись убрали через «Автозагрузку» диспетчера задач). Путь в записи сам НЕ переписываем при запуске: иначе любая
/// запущенная копия (напр. проверочная сборка из «Загрузок») перехватила бы автозапуск. Известное: при удалении
/// программы запись остаётся, если флажок был включён (MSI не трогает чужое значение Run) — снять флажок до удаления.
/// </summary>
public static class Autostart
{
    private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string VALUE = "X-Gates Win";
    public const string ARG = "--autostart";

    private static string command() => $"\"{Environment.ProcessPath}\" {ARG}";

    public static bool isEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RUN_KEY);
        return k?.GetValue(VALUE) is string v && v.Length > 0;
    }

    public static void set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RUN_KEY, writable: true);
        if (enabled) k.SetValue(VALUE, command(), RegistryValueKind.String);
        else if (k.GetValue(VALUE) != null) k.DeleteValue(VALUE, throwOnMissingValue: false);
    }
}
