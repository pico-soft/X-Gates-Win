using XGates.Core.Platform;
using XGates.Core.Settings;

namespace XGates.Core.Tests.Settings;

/// <summary>Отказ temp→rename не должен выглядеть сохранением и менять живое состояние.</summary>
public sealed class SettingsStoreFailureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xgates-settings-failure-" + Guid.NewGuid().ToString("N"));
    private readonly Context ctx;
    private readonly AppSettings saved = SettingsStore.current();
    private readonly WinSettings savedWin = WinSettingsStore.current();

    public SettingsStoreFailureTests()
    {
        ctx = new Context(root);
        Directory.CreateDirectory(ctx.filesDir);
    }

    [Fact]
    public void Failed_settings_write_keeps_memory_and_previous_file()
    {
        SettingsStore.update(ctx, SettingsStore.DEFAULTS);
        var before = SettingsStore.current();
        var target = Path.Combine(ctx.filesDir, "settings.json");
        var beforeJson = File.ReadAllText(target);
        Directory.CreateDirectory(target + ".tmp");   // File.WriteAllText(temp) гарантированно откажет

        var result = SettingsStore.update(ctx, before with { subTimeoutSec = before.subTimeoutSec + 1 });

        Assert.False(result);
        Assert.Equal(before, SettingsStore.current());
        Assert.Equal(beforeJson, File.ReadAllText(target));
    }

    [Fact]
    public void Failed_windows_settings_write_returns_false_and_keeps_previous_state()
    {
        var before = new WinSettings();
        Assert.True(WinSettingsStore.update(ctx, before));
        var target = Path.Combine(ctx.filesDir, "win_settings.json");
        var beforeJson = File.ReadAllText(target);
        Directory.CreateDirectory(target + ".tmp");

        var result = WinSettingsStore.update(ctx, before with { darkTheme = !before.darkTheme });

        Assert.False(result);
        Assert.Equal(before, WinSettingsStore.current());
        Assert.Equal(beforeJson, File.ReadAllText(target));
    }

    public void Dispose()
    {
        foreach (var name in new[] { "settings.json.tmp", "win_settings.json.tmp" })
        {
            var path = Path.Combine(ctx.filesDir, name);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        SettingsStore.update(ctx, saved);
        WinSettingsStore.update(ctx, savedWin);
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
