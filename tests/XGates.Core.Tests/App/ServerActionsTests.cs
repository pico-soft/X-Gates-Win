using XGates.App;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Tests.Libv2ray;
using XGates.Core.Xray;

namespace XGates.Core.Tests.App;

/// <summary>
/// Действия над сервером из деталей (ServerDetailDialog/RenameServerDialog Kotlin): строки деталей, стоп-лист скрывает
/// отовсюду и снимается, переименование одному и «всем с тем же именем», возврат имени, перемер одного сервера
/// настоящим temp-инстансом; диалог «Нет живых серверов — включить выключенные источники?».
/// </summary>
[Collection("xray")]
public sealed class ServerActionsTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-act-" + Guid.NewGuid().ToString("N")));
    private readonly SerialUiThread ui = new();
    private readonly MainScreenModel model;

    public ServerActionsTests()
    {
        ServerSpeedTester.probesOverrideForTest = [$"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1"];
        SubscriptionManager.init(ctx);
        model = new MainScreenModel(ctx, ui);
    }

    public void Dispose()
    {
        model.Dispose();
        ui.Dispose();
        MonitorPrompt.clear(); MonitorPrompt.resetDeclined();
        ServerSpeedTester.probesOverrideForTest = null;
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private MainSnapshot Snap() { ui.drain(); MainSnapshot? s = null; using var e = new ManualResetEventSlim(); ui.post(() => { s = model.snapshot(); e.Set(); }); e.Wait(10_000); return s!; }

    private static bool WaitUntil(Func<bool> cond, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    private List<XGates.Core.Xray.Link.ServerProfile> Import(string body, string name = "local")
    {
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx, body, name);
        Assert.True(r.ok, r.error);
        model.reloadAll();
        return SubscriptionManager.allServers(ctx);
    }

    [Fact]
    public void Details_block_rename_and_reset()
    {
        var u = LocalTestNet.Uuid;
        var servers = Import($"vless://{u}@10.0.0.1:443?type=ws&security=tls&sni=a.example&fp=chrome#Same\nvless://{u}@10.0.0.2:443?type=tcp#Same\nvless://{u}@10.0.0.3:443?type=tcp#Other");
        var a = servers.Single(s => s.address == "10.0.0.1");

        var d = model.details(a);
        Assert.Equal("Same", d.name);
        Assert.False(d.hasCustomName);
        Assert.Contains(("Адрес", "10.0.0.1:443"), d.rows);
        Assert.Contains(("Транспорт", "ws"), d.rows);
        Assert.Contains(("SNI", "a.example"), d.rows);
        Assert.Contains(("Flow", "—"), d.rows);
        Assert.Equal("Стоп-лист: скрыть ОТОВСЮДУ (возврат через Настройки → Стоп-лист).", d.blockHint);

        // Стоп-лист: скрыт отовсюду (даже из «Все серверы»), снятие возвращает.
        model.toggleBlock(a);
        Assert.True(WaitUntil(() => BlocklistStore.current().isServerBlocked(SubscriptionManager.serverKey(a)), 5_000));
        Assert.Equal(2, Snap().all.Count);
        Assert.True(model.details(a).blockedByKey);
        model.toggleBlock(a);
        Assert.True(WaitUntil(() => Snap().all.Count == 3, 5_000));

        // Переименование: одному / всем с тем же исходным именем; пустое имя = вернуть имя от сервера.
        Assert.Equal(2, model.sameOriginalCount(a));
        model.renameServer(a, "Мой", applyAll: false);
        Assert.True(WaitUntil(() => Snap().all.Count(r => r.name == "Мой") == 1, 5_000));
        Assert.True(model.details(a).hasCustomName);
        Assert.Contains(("Имя от сервера", "Same"), model.details(a).rows);
        model.renameServer(a, "Общий", applyAll: true);
        Assert.True(WaitUntil(() => Snap().all.Count(r => r.name == "Общий") == 2, 5_000));
        model.resetName(a);
        Assert.True(WaitUntil(() => Snap().all.Count(r => r.name == "Same") == 1, 5_000));
        model.renameServer(servers.Single(s => s.address == "10.0.0.2"), "", applyAll: false);
        Assert.True(WaitUntil(() => Snap().all.Count(r => r.name == "Same") == 2, 5_000));
    }

    [Fact]
    public void Remeasure_one_server_through_real_temp_instance()
    {
        var p = Import($"vless://{LocalTestNet.Uuid}@127.0.0.1:{net.StartVlessServer()}?type=tcp&security=none#srv")[0];
        model.onRemeasure(p);
        Assert.True(WaitUntil(() => Snap().remeasuring, 5_000) || Snap().remeasureStatus.StartsWith("результат"));
        Assert.True(WaitUntil(() => !Snap().remeasuring, 60_000), Snap().remeasureStatus);
        Assert.StartsWith("результат: ", Snap().remeasureStatus);
        var row = Assert.Single(Snap().all);
        Assert.True(row.speedMbps > 0);
        Assert.True(SubscriptionManager.allServers(ctx)[0].speedMbps > 0);   // сохранено в реестр
    }

    [Fact]
    public void Enable_disabled_sources_prompt_accept_and_decline()
    {
        Import($"vless://{LocalTestNet.Uuid}@10.0.1.1:443?type=tcp#a\nvless://{LocalTestNet.Uuid}@10.0.1.2:443?type=tcp#b", "выключенный");
        var src = SubscriptionManager.sources(ctx).Single(s => s.name == "выключенный");
        SubscriptionManager.setEnabled(ctx, src.id, false);
        model.reloadAll();
        Assert.True(Snap().serversEmpty);

        MonitorPrompt.request(1, 2);
        Assert.True(WaitUntil(() => Snap().enablePrompt is { sources: 1, servers: 2 }, 5_000));
        model.declineEnablePrompt();
        Assert.True(WaitUntil(() => Snap().enablePrompt == null, 5_000));
        Assert.True(MonitorPrompt.declined);
        Assert.False(SubscriptionManager.sources(ctx).Single(s => s.id == src.id).enabled);   // сами НЕ включаем

        MonitorPrompt.request(1, 2);
        model.acceptEnablePrompt();
        Assert.True(WaitUntil(() => SubscriptionManager.sources(ctx).Single(s => s.id == src.id).enabled, 10_000));
        Assert.True(WaitUntil(() => Snap().all.Count == 2, 10_000));
        Assert.Null(Snap().enablePrompt);
        Assert.False(MonitorPrompt.declined);
    }
}
