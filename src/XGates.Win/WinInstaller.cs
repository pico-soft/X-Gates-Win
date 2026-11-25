using System.Diagnostics;
using XGates.Core.Platform;
using XGates.Core.Update;

namespace XGates.Win;

/// <summary>
/// Запуск установщика обновления с последующим перезапуском приложения. Ядро (UpdateInstaller.startInstall) только
/// запускает msiexec; здесь — через отдельный cmd: дождаться конца установки (start /wait) и открыть новую версию из
/// того же места (MajorUpgrade ставит в тот же каталог). При ненулевом коде открывается старая версия с --update-failed.
/// Само приложение после запуска закрывается (exitApp).
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
                // Внешний cmd ждёт msiexec и передаёт его код старой копии при отказе; см. windowsLauncherArguments.
                Arguments = UpdateInstaller.windowsLauncherArguments(msi, exe),
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
