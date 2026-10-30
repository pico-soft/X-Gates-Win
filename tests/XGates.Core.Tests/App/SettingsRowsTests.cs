using XGates.App;
using XGates.Core.Settings;

namespace XGates.Core.Tests.App;

/// <summary>Раздел «Настройки» (SettingsSection Kotlin): границы, запятая в дробях, Java-вид чисел, URL со схемой, минуты↔секунды.</summary>
public class SettingsRowsTests
{
    private static SettingRow Row(string label) => SettingsRows.rows.Single(r => r.label == label);
    private static readonly AppSettings D = SettingsStore.DEFAULTS;

    [Fact]
    public void Rows_validate_and_apply_like_kotlin()
    {
        var warm = Row("Прогрев перед замером");
        Assert.Equal("Прогрев перед замером, с", warm.title);
        Assert.Equal("0.5", warm.read(D));               // Java Double.toString
        Assert.Equal(1.5, warm.commit(D, "1,5")!.speedWarmupSec);   // запятая = точка
        Assert.Null(warm.commit(D, "61"));               // вне 0..60
        Assert.Null(warm.commit(D, "NaN"));
        Assert.Null(warm.commit(D, "abc"));

        var window = Row("Окно замера");
        Assert.Equal("2.0", window.read(D));
        Assert.Equal("2.0", window.defaultText);

        var ping = Row("Таймаут пинга");
        Assert.Equal(1000, ping.commit(D, "1000")!.pingTimeoutMs);
        Assert.Null(ping.commit(D, "499"));
        Assert.Null(ping.commit(D, "1000.5"));

        var degr = Row("Проверка деградации не чаще");   // в UI минуты, в настройках секунды
        Assert.Equal("30", degr.read(D));
        Assert.Equal(600, degr.commit(D, "10")!.degradationCheckSec);

        var url = Row("URL пробника скорости");
        Assert.Null(url.commit(D, "ftp://x"));
        Assert.Equal("https://x/1GB", url.commit(D, "  https://x/1GB ")!.speedProbeUrl);
        Assert.Equal("curl/8", Row("User-Agent загрузки").commit(D, "curl/8")!.subUserAgent);   // UA: схема не нужна
        Assert.Null(Row("User-Agent загрузки").commit(D, "   "));

        var auto = Row("Автозапуск при старте");
        Assert.Equal("вкл", auto.defaultText);
        Assert.False(auto.commit(D, "false")!.autoStartOnLaunch);
        Assert.True(auto.changed(D with { autoStartOnLaunch = false }));
        Assert.False(auto.changed(D));

        Assert.DoesNotContain(SettingsRows.rows, r => r.label.Contains("VPN"));   // обход VPN — фаза 2 Windows
        Assert.All(SettingsRows.rows, r => Assert.NotNull(r.commit(D, r.read(D))));   // дефолты сами валидны
    }
}
