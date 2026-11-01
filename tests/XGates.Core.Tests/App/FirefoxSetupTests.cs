using XGates.App;
using XGates.Core.Platform;

namespace XGates.Core.Tests.App;

/// <summary>
/// «Настроить Firefox» / «Вернуть как было» (вариант Б, решение Elyor) на раскладке профиля как у Firefox на Windows:
/// profiles.ini с секцией Install, user.js пользователя (его строки не трогаем), prefs.js с прежними настройками прокси
/// (после «вернуть» — ровно они), профиль открыт Firefox (parent.lock) — возврат отказывается.
/// Настоящий Firefox здесь не запустить (нет GTK) — живая проверка в docs/windows-check.md.
/// </summary>
public sealed class FirefoxSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xgates-ff-" + Guid.NewGuid().ToString("N"));
    private readonly Context ctx;
    private readonly Func<IReadOnlyList<string>> savedRoots = FirefoxSetup.roots;
    private readonly string profile;

    private const string PREFS_BEFORE =
        "// Mozilla User Preferences\r\n\r\n" +
        "user_pref(\"browser.startup.homepage\", \"https://example.org\");\r\n" +
        "user_pref(\"network.proxy.type\", 0);\r\n" +
        "user_pref(\"network.proxy.http\", \"corp.proxy\");\r\n" +
        "user_pref(\"network.proxy.http_port\", 3128);\r\n";
    private const string USERJS_BEFORE = "user_pref(\"privacy.resistFingerprinting\", true);";   // без перевода строки в конце

    public FirefoxSetupTests()
    {
        ctx = new Context(Path.Combine(root, "xgates"));
        var ff = Path.Combine(root, "Mozilla", "Firefox");
        profile = Path.Combine(ff, "Profiles", "abcd1234.default-release");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Path.Combine(ff, "Profiles", "zzzz.default"));
        File.WriteAllText(Path.Combine(ff, "profiles.ini"),
            "[Install308046B0AF4A39CB]\r\nDefault=Profiles/abcd1234.default-release\r\nLocked=1\r\n\r\n" +
            "[Profile1]\r\nName=default\r\nIsRelative=1\r\nPath=Profiles/zzzz.default\r\nDefault=1\r\n\r\n" +
            "[Profile0]\r\nName=default-release\r\nIsRelative=1\r\nPath=Profiles/abcd1234.default-release\r\n\r\n" +
            "[General]\r\nStartWithLastProfile=1\r\nVersion=2\r\n");
        File.WriteAllText(Path.Combine(profile, "prefs.js"), PREFS_BEFORE);
        File.WriteAllText(Path.Combine(profile, "user.js"), USERJS_BEFORE);
        FirefoxSetup.roots = () => [ff];
    }

    public void Dispose()
    {
        FirefoxSetup.roots = savedRoots;
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private string UserJs => File.ReadAllText(Path.Combine(profile, "user.js"));
    private string Prefs => File.ReadAllText(Path.Combine(profile, "prefs.js"));

    [Fact]
    public void Finds_install_default_profile_not_legacy_default()
    {
        var p = Assert.Single(FirefoxSetup.findProfiles());
        Assert.Equal(Path.GetFullPath(profile), p.dir);
        Assert.Equal("default-release", p.name);
        Assert.Contains("«default-release»", FirefoxSetup.statusText(ctx, 10815, 10816));
    }

    [Fact]
    public void Configure_adds_block_keeps_user_lines_and_restore_returns_exact_previous_state()
    {
        var r = FirefoxSetup.configure(ctx, 10815, 10816);
        Assert.True(r.ok, r.text);
        Assert.StartsWith(USERJS_BEFORE + "\n", UserJs);   // строки пользователя целы, блок — с новой строки
        Assert.Contains("user_pref(\"network.proxy.type\", 1);", UserJs);
        Assert.Contains("user_pref(\"network.proxy.socks\", \"127.0.0.1\");", UserJs);
        Assert.Contains("user_pref(\"network.proxy.socks_port\", 10815);", UserJs);
        Assert.Contains("user_pref(\"network.proxy.socks_version\", 5);", UserJs);
        Assert.Contains("user_pref(\"network.proxy.socks5_remote_dns\", true);", UserJs);
        Assert.Contains("user_pref(\"network.proxy.http\", \"127.0.0.1\");", UserJs);
        Assert.Contains("user_pref(\"network.proxy.http_port\", 10816);", UserJs);
        Assert.Contains("user_pref(\"network.proxy.ssl\", \"127.0.0.1\");", UserJs);
        Assert.Contains("user_pref(\"network.proxy.ssl_port\", 10816);", UserJs);
        Assert.True(FirefoxSetup.isConfigured(ctx));
        Assert.Contains("настроен на X-Gates", FirefoxSetup.statusText(ctx, 10815, 10816));

        // Firefox поработал с блоком и при выходе записал его значения в prefs.js (как он делает с user.js).
        File.WriteAllText(Path.Combine(profile, "prefs.js"),
            "// Mozilla User Preferences\n\nuser_pref(\"browser.startup.homepage\", \"https://example.org\");\n" +
            "user_pref(\"network.proxy.http\", \"\");\nuser_pref(\"network.proxy.http_port\", 0);\n" +
            "user_pref(\"network.proxy.socks\", \"127.0.0.1\");\nuser_pref(\"network.proxy.socks_port\", 10815);\n" +
            "user_pref(\"network.proxy.type\", 1);\n");

        // Смена порта в X-Gates → блок переписан, прежние значения (резервная копия) НЕ перезаписаны нашими.
        Assert.NotNull(FirefoxSetup.refreshPort(ctx, 20815, 10816));
        Assert.Contains("user_pref(\"network.proxy.socks_port\", 20815);", UserJs);
        Assert.DoesNotContain("10815", UserJs);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(UserJs, "X-Gates Win: Firefox proxy"));
        Assert.Null(FirefoxSetup.refreshPort(ctx, 20815, 10816));   // те же порты — ничего
        Assert.NotNull(FirefoxSetup.refreshPort(ctx, 20815, 20816));   // сменился только HTTP — тоже переписать
        Assert.Contains("user_pref(\"network.proxy.ssl_port\", 20816);", UserJs);

        var back = FirefoxSetup.restore(ctx);
        Assert.True(back.ok, back.text);
        Assert.Equal(USERJS_BEFORE, UserJs);                                // user.js — байт в байт как был
        Assert.Contains("user_pref(\"network.proxy.type\", 0);", Prefs);   // прежние значения вернулись
        Assert.Contains("user_pref(\"network.proxy.http\", \"corp.proxy\");", Prefs);
        Assert.Contains("user_pref(\"network.proxy.http_port\", 3128);", Prefs);
        Assert.DoesNotContain("network.proxy.socks", Prefs);                // наших значений не осталось
        Assert.Contains("browser.startup.homepage", Prefs);                 // чужое не тронуто
        Assert.False(FirefoxSetup.isConfigured(ctx));
        Assert.Contains("возвращать нечего", FirefoxSetup.restore(ctx).text);
    }

    [Fact]
    public void Restore_refuses_while_firefox_is_open_and_new_userjs_is_removed()
    {
        File.Delete(Path.Combine(profile, "user.js"));
        var closed = FirefoxSetup.configure(ctx, 10815, 10816);
        Assert.True(closed.ok);
        Assert.False(closed.restartFirefox);                               // закрыт — просто открыть
        Assert.DoesNotContain(FirefoxSetup.RESTART_TEXT, closed.text);
        Assert.StartsWith(FirefoxSetup.BEGIN, UserJs);

        using (new FileStream(Path.Combine(profile, "parent.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(FirefoxSetup.profileInUse(profile));
            var open = FirefoxSetup.configure(ctx, 10815, 10816);                  // настроить можно и при открытом
            Assert.True(open.restartFirefox);
            Assert.Contains(FirefoxSetup.RESTART_TEXT, open.text);
            var r = FirefoxSetup.restore(ctx);
            Assert.False(r.ok);
            Assert.Contains("Закройте Firefox", r.text);
            Assert.True(FirefoxSetup.isConfigured(ctx));                    // ничего не тронуто
        }
        Assert.False(FirefoxSetup.profileInUse(profile));                  // Firefox закрыт: замок свободен
        Assert.True(FirefoxSetup.restore(ctx).ok);
        Assert.False(File.Exists(Path.Combine(profile, "user.js")));       // файла не было — мы его и убрали
        Assert.Equal(PREFS_BEFORE.Replace("\r\n", "\n").TrimEnd('\n'), Prefs.Replace("\r\n", "\n").TrimEnd('\n'));
    }

    [Fact]
    public void No_firefox_says_so()
    {
        FirefoxSetup.roots = () => [Path.Combine(root, "nothing")];
        var r = FirefoxSetup.configure(ctx, 10815, 10816);
        Assert.False(r.ok);
        Assert.Contains("Firefox не найден", r.text);
        Assert.False(FirefoxSetup.isConfigured(ctx));
    }
}
