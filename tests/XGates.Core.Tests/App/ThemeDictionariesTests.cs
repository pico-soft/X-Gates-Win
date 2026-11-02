using System.Text.RegularExpressions;
using System.Xml.Linq;
using XGates.Core.Platform;
using XGates.Core.Settings;

namespace XGates.Core.Tests.App;

/// <summary>
/// Окно здесь не запускается, поэтому палитры проверяются как текст: ключ, которого нет в одной из них, на Windows дал бы пустой цвет
/// или падение FindResource только в этой теме. Сам вид проверяют скриншоты windows.yml.
/// </summary>
public class ThemeDictionariesTests
{
    private static string WinDir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "XGatesWin.slnx"))) d = d.Parent;
            return Path.Combine(d!.FullName, "src", "XGates.Win");
        }
    }

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static List<string> palette(string name) =>
        [.. XDocument.Load(Path.Combine(WinDir, "Themes", name)).Root!.Elements()
            .Select(e => e.Attribute(X + "Key")!.Value)
            .Where(k => !k.StartsWith('{'))   // системные цвета подменяет только тёмная
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void Both_palettes_define_the_same_colors() => Assert.Equal(palette("Light.xaml"), palette("Dark.xaml"));

    [Fact]
    public void Every_color_the_window_asks_for_is_in_the_palette()
    {
        var used = new HashSet<string>();
        foreach (var f in Directory.EnumerateFiles(WinDir, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\{DynamicResource (\w+)\}")) used.Add(m.Groups[1].Value);
        foreach (var f in Directory.EnumerateFiles(WinDir, "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\b(?:R|FindResource)\(""(\w+)""\)")) used.Add(m.Groups[1].Value);
        used.Remove("Primary");   // стиль кнопки, а не цвет: лежит в Styles.xaml и DarkStyles.xaml
        Assert.Contains("RowActive", used);   // поиск и правда что-то находит
        Assert.Empty(used.Except(palette("Light.xaml")));
    }

    [Fact]
    public void Dark_switch_is_kept_and_an_old_file_means_off()
    {
        var ctx = new Context(Path.Combine(Path.GetTempPath(), "xgates-theme-" + Guid.NewGuid().ToString("N")));
        try
        {
            Assert.True(WinSettingsStore.update(ctx, new WinSettings { darkTheme = true }));
            WinSettingsStore.update(ctx, new WinSettings());
            Assert.False(WinSettingsStore.current().darkTheme);

            Assert.True(WinSettingsStore.update(ctx, new WinSettings { darkTheme = true }));
            Assert.Contains("\"darkTheme\":true", File.ReadAllText(Path.Combine(ctx.filesDir, "win_settings.json")).Replace(" ", ""));
            WinSettingsStore.init(ctx);
            Assert.True(WinSettingsStore.current().darkTheme);

            File.WriteAllText(Path.Combine(ctx.filesDir, "win_settings.json"), """{"socksPort":10815,"httpPort":10816,"tlsFragment":false}""");
            WinSettingsStore.init(ctx);
            Assert.False(WinSettingsStore.current().darkTheme);
        }
        finally
        {
            WinSettingsStore.update(ctx, new WinSettings());
            try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
        }
    }
}
