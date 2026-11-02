using System.Text.Json.Nodes;
using XGates.Core.Libv2ray;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;
using L = XGates.Core.Libv2ray.Libv2ray;

namespace XGates.Core.Tests.Libv2ray;

/// <summary>
/// mKCP на настоящем ядре: конфиг принят, данные прошли туда и обратно. Сервер здесь — то же ядро; связь со старым сервером (ядро 25.12.8)
/// проверена отдельно, его бинарника в репозитории нет (docs/decisions.md).
/// </summary>
[Collection("xray")]
public sealed class MkcpIntegrationTests : IDisposable
{
    private readonly LocalTestNet net = new();

    public void Dispose() => net.Dispose();

    [Fact]
    public void Udp_pings_do_not_start_a_core_each_at_once()
    {
        // Пинг UDP-сервера поднимает настоящее ядро. Считаем настоящие процессы: их на машине может быть и без нас, поэтому от базы.
        static int cores() => System.Diagnostics.Process.GetProcessesByName("xray").Length;
        var servers = Enumerable.Range(0, 8).Select(i => VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{LocalTestNet.FreePort()}?type=kcp&security=none#k{i}")!).ToList();
        var ctx = new XGates.Core.Platform.Context(Path.Combine(Path.GetTempPath(), "xgates-udp-" + Guid.NewGuid().ToString("N")));
        ServerSpeedTester.probesOverrideForTest = [$"http://127.0.0.1:{net.HttpPort}/bytes?n=100000"];
        try
        {
            var before = cores();
            var peak = 0;
            var results = 0;
            using var done = new ManualResetEventSlim();
            ServerTester.testAll(ctx, servers, (_, _) => Interlocked.Increment(ref results), (_, _) => { }, done.Set, concurrency: servers.Count);
            while (!done.IsSet) { peak = Math.Max(peak, cores() - before); Thread.Sleep(10); }
            Assert.InRange(peak, 1, ServerTester.UDP_PING_SLOTS);
            Assert.Equal(8, results);   // сторож батча дождался всех
        }
        finally
        {
            ServerSpeedTester.probesOverrideForTest = null;
            try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Ping_batch_budget_waits_for_the_udp_queue()
    {
        const long perServer = 8_000L;
        Assert.Equal(15_000L + 1 * perServer, ServerTester.batchBudgetMs(30, 0, 48, perServer));   // обычные пинги: как раньше
        Assert.Equal(15_000L + (1 + 10) * perServer, ServerTester.batchBudgetMs(30, 30, 48, perServer));   // 30 UDP по 3 слота: 10 волн
    }

    [Theory]
    [InlineData("")]
    [InlineData("&headerType=none")]
    [InlineData("&headerType=srtp")]
    [InlineData("&headerType=utp")]
    [InlineData("&headerType=wechat-video")]
    [InlineData("&headerType=dtls")]
    [InlineData("&headerType=wireguard")]
    [InlineData("&headerType=dns")]
    [InlineData("&seed=s3cret")]
    [InlineData("&headerType=srtp&seed=s3cret")]
    [InlineData("&headerType=unknown")]
    public void Tunnel_over_mkcp_works(string query)
    {
        // Маски сервера берём из конфига клиента: здесь проверяется ядро и передача, а порядок масок — в TransportBuildTest.
        var draft = VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:1?type=kcp&security=none{query}#k")!;
        var outbounds = JsonNode.Parse(XrayConfigBuilder.build(draft))!["outbounds"]!.AsArray();
        var stream = outbounds.First(o => o!["tag"]!.GetValue<string>() == "proxy")!["streamSettings"]!.DeepClone().AsObject();
        int server = net.StartServer(stream);

        var p = VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{server}?type=kcp&security=none{query}#k")!;
        var (cfg, socks, _) = CoreControllerIntegrationTests.BuildOnFreePorts(p);
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(cfg, 0);
        try
        {
            using var c = CoreControllerIntegrationTests.ViaSocks(socks);
            Assert.Equal(200_000, c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=200000").Result.Length);
        }
        // Сбой был один на сотни полных прогонов и в 6000 отдельных проходов не повторился: пусть следующий объяснит себя сам.
        catch (AggregateException e) { Assert.Fail($"{e.InnerException?.Message}; потоков пула {ThreadPool.ThreadCount}, в очереди {ThreadPool.PendingWorkItemCount}; ядро: {core.recentOutput()}"); }
        finally { core.stopLoop(); }
    }
}
