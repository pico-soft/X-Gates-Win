using XGates.App;
using XGates.Core.Crash;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Tests.Libv2ray;
using XGates.Core.Update;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests.App;

/// <summary>
/// Вкладка «Подписки» (порт SubscriptionsScreen): вставка текстом, URL (сразу обновляется), файл, дубликат, вкл/выкл,
/// переименование, удаление с подсчётом теряемых серверов, обновить все — тексты статуса как в Android. Плюс подписи
/// (порт форматтеров MainActivity) и порядок запуска приложения.
/// </summary>
[Collection("xray")]
public sealed class SubscriptionsAndBootstrapTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-subs-" + Guid.NewGuid().ToString("N")));
    private readonly SerialUiThread ui = new();

    public void Dispose()
    {
        ui.Dispose();
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private static bool WaitUntil(Func<bool> cond, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    private SubscriptionsSnapshot Snap(SubscriptionsModel m) { ui.drain(); return m.snapshot(); }

    [Fact]
    public void Add_paste_url_file_toggle_rename_remove()
    {
        SubscriptionManager.init(ctx);
        using var m = new SubscriptionsModel(ctx, ui);
        var registryChanges = 0;
        m.registryChanged += () => Interlocked.Increment(ref registryChanges);

        m.onAddPaste("  ");
        Assert.True(WaitUntil(() => Snap(m).status == "Вставьте ссылки", 5_000));

        m.onAddPaste($"vless://{LocalTestNet.Uuid}@127.0.0.1:1001?type=tcp#a\nvless://{LocalTestNet.Uuid}@127.0.0.1:1002?type=tcp#b\nfoo://bar");
        Assert.True(WaitUntil(() => Snap(m).status == "Добавлено серверов: 2, не удалось распознать: 1", 10_000), Snap(m).status);
        Assert.True(registryChanges > 0);

        // URL: добавляется и сразу обновляется (локальный «сервер подписок»).
        net.SubBody = $"vless://{LocalTestNet.Uuid}@127.0.0.1:1003?type=tcp#c\n";
        var url = $"http://127.0.0.1:{net.HttpPort}/sub";
        m.onAddUrl(url, "Моя");
        Assert.True(WaitUntil(() => Snap(m).status == "Подписка добавлена: 1 сервер", 20_000), Snap(m).status);
        m.onAddUrl(url, "Моя ещё раз");
        Assert.True(WaitUntil(() => Snap(m).status == "Такая подписка уже есть", 10_000), Snap(m).status);

        // Файл.
        var f = Path.Combine(ctx.filesDir, "list.txt");
        File.WriteAllText(f, $"vless://{LocalTestNet.Uuid}@127.0.0.1:1004?type=tcp#d\n");
        m.onImportFile(f);
        Assert.True(WaitUntil(() => Snap(m).status == "Добавлено серверов: 1", 10_000), Snap(m).status);
        Assert.Contains(Snap(m).sources, s => s.name == "list.txt");

        var sub = Snap(m).sources.Single(s => s.url == url);
        Assert.Equal(4, SubscriptionManager.allServers(ctx).Count);
        m.onToggle(sub.id, false);
        Assert.True(WaitUntil(() => !Snap(m).sources.Single(s => s.id == sub.id).enabled, 10_000));
        Assert.Equal(3, SubscriptionManager.allServers(ctx).Count);   // сервер выключенного источника не в списке
        m.onToggle(sub.id, true);
        Assert.True(WaitUntil(() => Snap(m).sources.Single(s => s.id == sub.id).enabled, 10_000));

        m.rename(sub.id, "Переименована");
        Assert.True(WaitUntil(() => Snap(m).sources.Single(s => s.id == sub.id).name == "Переименована", 5_000));

        Assert.Equal("«Переименована»\nИсчезнет серверов: 1 (только те, которых нет в других подписках).",
            m.deleteConfirmText(Snap(m).sources.Single(s => s.id == sub.id)));
        m.remove(sub.id);
        Assert.True(WaitUntil(() => Snap(m).sources.All(s => s.id != sub.id), 10_000));
        Assert.Equal(3, SubscriptionManager.allServers(ctx).Count);

        m.onRefreshAll();   // подписок с URL не осталось
        Assert.True(WaitUntil(() => Snap(m).status == "Нет включённых подписок для обновления", 5_000), Snap(m).status);
    }

    [Fact]
    public void Source_card_speaks_plain_language()
    {
        var now = new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Local);
        var ok = new SubSource { id = "a", name = "A", url = "https://x/sub", lastOkTs = "2026-11-01 12:30", serverCount = 21 };
        Assert.Equal(new SourceCard("21 сервер", "Обновлена сегодня в 12:30", ""), SubscriptionsModel.card(ok, now));
        Assert.Equal("Обновлена вчера в 12:30", SubscriptionsModel.card(ok with { lastOkTs = "2026-10-31 12:30" }, now).updated);
        Assert.Equal("Обновлена 29.10 в 12:30", SubscriptionsModel.card(ok with { lastOkTs = "2026-10-29 12:30" }, now).updated);

        // Технический текст ошибки в карточку не попадает.
        var broken = ok with { lastError = "HttpRequestException: 502 via socks · host=x" };
        Assert.Equal("Не удалось обновить. Прежний список серверов работает.", SubscriptionsModel.card(broken, now).problem);
        Assert.StartsWith("Не удалось загрузить", SubscriptionsModel.card(broken with { serverCount = 0 }, now).problem);

        Assert.Equal("Добавлена вручную", SubscriptionsModel.card(new SubSource { id = "a", name = "A", url = "" }, now).updated);
        Assert.Equal("Ещё не обновлялась", SubscriptionsModel.card(new SubSource { id = "a", name = "A", url = "https://x/sub" }, now).updated);
        Assert.Equal("2 сервера", SubscriptionsModel.serversText(2));
        Assert.Equal("5 серверов", SubscriptionsModel.serversText(5));
        Assert.Equal("11 серверов", SubscriptionsModel.serversText(11));
        Assert.Equal("112 серверов", SubscriptionsModel.serversText(112));
    }

    [Fact]
    public void Format_helpers_match_kotlin()
    {
        Assert.Equal("не изм.", UiFormat.speedCell(null));
        Assert.Equal("✗", UiFormat.speedCell(0));
        Assert.Equal("✗", UiFormat.speedCell(-1));
        Assert.Equal("4.3 Мбит/с", UiFormat.speedCell(4.26));
        Assert.Equal("13 Мбит/с", UiFormat.speedCell(12.6));
        Assert.Equal("3.0 Мбит/с", UiFormat.speedCell(3.0));     // Kotlin Double.toString: «.0» не теряется
        Assert.Equal("0.0 Мбит/с", UiFormat.speedCell(0.04));
        Assert.Equal("5.0", UiFormat.r1(5));          // Kotlin Double.toString
        Assert.Equal("0.3", UiFormat.r1(0.26));
        Assert.Equal("59 сек", UiFormat.humanAgo(59_999));
        Assert.Equal("2 мин", UiFormat.humanAgo(150_000));
        Assert.Equal("1 ч 5 мин", UiFormat.humanAgo(3_900_000));
        Assert.Equal("0 сек", UiFormat.humanAgo(-5));
        Assert.Equal("0.5 КБ", UiFormat.fmtBytes(512));
        Assert.Equal("1.5 МБ", UiFormat.fmtBytes(1_572_864));
        Assert.Equal("2.0 ГБ", UiFormat.fmtBytes(2L * 1024 * 1024 * 1024));
        Assert.Equal("1:01:05", UiFormat.fmtUptime(3_665_000));
        Assert.Equal("сейчас", UiFormat.speedAgeTextMs(JavaSystem.currentTimeMillis()));
        Assert.Equal("5 мин назад", UiFormat.speedAgeTextMs(JavaSystem.currentTimeMillis() - 5 * 60_000 - 1000));
        Assert.Equal("2 дн назад", UiFormat.speedAgeTextMs(JavaSystem.currentTimeMillis() - 2 * 86_400_000L - 1000));
        Assert.Equal("", UiFormat.speedAgeText("мусор"));
        var ts = DateTime.Now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("5 мин назад", UiFormat.speedAgeText(ts + ":56"));   // нестрогий разбор, как SimpleDateFormat

        // Одинаковые имена различаются подписью; совпала и подпись → адрес:порт; совпал и он → «#» + хэш ключа.
        var a = VlessParser.parse($"vless://{LocalTestNet.Uuid}@1.1.1.1:443?type=ws#Same")!;
        var b = VlessParser.parse($"vless://{LocalTestNet.Uuid}@1.1.1.2:443?type=tcp#Same")!;
        var c = VlessParser.parse($"vless://{LocalTestNet.Uuid}@1.1.1.3:443?type=tcp#Same")!;
        var e1 = VlessParser.parse($"vless://{LocalTestNet.Uuid}@[2001:db8::5]:8443?type=tcp&path=%2Fa#Same")!;
        var e2 = VlessParser.parse($"vless://{LocalTestNet.Uuid}@[2001:db8::5]:8443?type=tcp&path=%2Fb#Same")!;
        var u = VlessParser.parse($"vless://{LocalTestNet.Uuid}@1.1.1.4:443?type=tcp#Unique")!;
        var d = UiFormat.buildDiscriminators([a, b, c, e1, e2, u], new Blocklist());
        Assert.False(d.ContainsKey(SubscriptionManager.serverKey(u)));
        Assert.Equal("VLESS · ws · none · 1.1.1.1:443", d[SubscriptionManager.serverKey(a)]);
        // Находка порта №9 (исправлено): раньше у b и c было одинаковое «VLESS · tcp · none · ||||».
        Assert.Equal("VLESS · tcp · none · 1.1.1.2:443", d[SubscriptionManager.serverKey(b)]);
        Assert.Equal("VLESS · tcp · none · 1.1.1.3:443", d[SubscriptionManager.serverKey(c)]);
        Assert.StartsWith("VLESS · tcp · none · [2001:db8::5]:8443 · #", d[SubscriptionManager.serverKey(e1)]);
        Assert.NotEqual(d[SubscriptionManager.serverKey(e1)], d[SubscriptionManager.serverKey(e2)]);
        Assert.Equal(5, d.Values.Distinct().Count());   // все пять одноимённых подписей разные (u — без подписи)
        // Хэш — Java String.hashCode (одинаково с Android и между запусками): известные значения Java.
        Assert.Equal("0840", UiFormat.keyTag("Aa"));                    // "Aa".hashCode() == 2112
        Assert.Equal("0000", UiFormat.keyTag("polygenelubricants"));    // == Integer.MIN_VALUE (0x80000000)
    }

    [Fact]
    public void Bootstrap_initializes_stores_log_and_crash_reporter()
    {
        var savedSink = Log.Sink;
        var dir = Path.Combine(ctx.filesDir, "data");
        try
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(dir).FullName, "win_settings.json"), "{\"socksPort\":21000,\"httpPort\":21001}");
            Directory.CreateDirectory(Path.Combine(dir, "updates"));
            File.WriteAllText(Path.Combine(dir, "updates", "XGatesWin-0.0-win-x64.msi"), "старый установщик");
            UpdateAutoCheck.skipOnLaunch = true;
            var c = AppBootstrap.start(dir);
            Assert.Equal(dir, c.filesDir);
            Assert.Equal(21000, XGates.Core.Xray.XrayConfig.SOCKS_PORT);   // WinSettingsStore загружен
            Assert.True(File.Exists(AppBootstrap.logFile(c)));               // журнал пишется в файл
            Assert.Contains("X-Gates Win", File.ReadAllText(AppBootstrap.logFile(c)));
            Assert.True(WaitUntil(() => !Directory.EnumerateFiles(Path.Combine(dir, "updates")).Any(), 5_000), "отработавший установщик не убран");
            Assert.EndsWith("X-Gates", AppBootstrap.defaultDataDir());
        }
        finally
        {
            UpdateAutoCheck.skipOnLaunch = false;
            Log.Sink = savedSink;
            WinSettingsStore.resetForTest();
            KThread.onUncaught = null;
        }
    }
}
