namespace XGates.Core.Platform;

/// <summary>
/// Аналог <c>android.content.Context</c> в той мере, в какой его использует ядро: каталог данных приложения.
/// Android: <c>filesDir</c> = /data/data/…/files; Windows: <c>%LOCALAPPDATA%\X-Gates</c> (docs/decisions.md §13.4).
/// Портированный код принимает <c>Context context</c>, как в Kotlin, — сигнатуры не меняются.
/// </summary>
public sealed class Context
{
    public string filesDir { get; }

    public Context(string filesDir)
    {
        Directory.CreateDirectory(filesDir);
        this.filesDir = filesDir;
    }

    /// <summary>На Android — контекст приложения; здесь он один.</summary>
    public Context applicationContext => this;

    /// <summary>Каталог данных Windows-версии по умолчанию.</summary>
    public static string DefaultDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Gates");
}
