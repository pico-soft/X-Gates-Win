using System.Reflection;

namespace XGates.Core;

/// <summary>
/// Аналог BuildConfig Android: версия и идентификатор приложения. Источник — Directory.Build.props
/// (VersionCode/VersionNameDisplay → AssemblyMetadata), одно место на весь репозиторий.
/// </summary>
public static class AppInfo
{
    /// <summary>Идентификатор Windows-версии для update.json (как packageName Android-версии): обновлялка проверяет, что релиз наш.</summary>
    public const string APPLICATION_ID = "com.picosoft.xgates.win";

    public const string APP_NAME = "X-Gates Win";

    private static string? Meta(string key) =>
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    public static int VERSION_CODE { get; } = int.TryParse(Meta("VersionCode"), out var c) ? c : 0;

    public static string VERSION_NAME { get; } = Meta("VersionNameDisplay") ?? "0";
}
