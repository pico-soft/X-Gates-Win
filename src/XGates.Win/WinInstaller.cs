using System.Diagnostics;
using XGates.Core.Platform;

namespace XGates.Win;

/// <summary>
/// Запуск установщика обновления с последующим перезапуском приложения. Ядро (UpdateInstaller.startInstall) только
/// запускает msiexec; здесь — через отдельный cmd: дождаться конца установки (start /wait) и открыть новую версию из
/// того же места (MajorUpgrade ставит в тот же каталог). Само приложение после запуска закрывается (exitApp).
/// </summary>
public static class WinInstaller
{
    public static bool start(string msi)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("нет пути к exe");
            var psi = new ProcessStartInfo("cmd.exe")
            {
                // cmd /c: строка начинается не с кавычки — cmd оставляет кавычки как есть.
                Arguments = $"/c start \"\" /wait msiexec /i \"{msi}\" /passive /norestart & start \"\" \"{exe}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception e)
        {
            Log.w("WinInstaller", "установщик не запустился", e);
            return false;
        }
    }
}
