using System.Runtime.InteropServices;

namespace XGates.Core.Platform;

/// <summary>
/// Состояние устройства, которое на Android монитор берёт из BatteryManager/PowerManager/ConnectivityManager.
/// Windows: питание — GetSystemPowerStatus (ноутбук на шнуре/батарее; стационарный ПК без батареи = «на шнуре»);
/// «экран включён» — приложение выставляет по блокировке сеанса (SessionSwitch); тип сети «мобильная» — фаза 2
/// (лимитное подключение, HANDOFF §6), пока false. Делегаты подменяемы (тесты/приложение).
/// </summary>
public static class Device
{
    public static Func<bool> isCharging { get; set; } = DefaultIsCharging;
    /// <summary>Заряд батареи, % (100 — нет батареи / неизвестно).</summary>
    public static Func<int> batteryLevel { get; set; } = DefaultBatteryLevel;
    public static volatile bool screenOn = true;
    public static Func<bool> isMobileNetwork { get; set; } = () => false;
    /// <summary>Активная сеть платная/лимитная? (Android isActiveNetworkMetered; Windows — фаза 2, пока false.)</summary>
    public static Func<bool> isMeteredNetwork { get; set; } = () => false;

    private static bool DefaultIsCharging()
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (!GetSystemPowerStatus(out var s)) return true;
        if (s.BatteryFlag == 128) return true;          // нет батареи — стационарный ПК
        return s.ACLineStatus == 1;
    }

    private static int DefaultBatteryLevel()
    {
        if (!OperatingSystem.IsWindows()) return 100;
        if (!GetSystemPowerStatus(out var s) || s.BatteryLifePercent == 255 || s.BatteryFlag == 128) return 100;
        return s.BatteryLifePercent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
