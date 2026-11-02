using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using XGates.Core.Libv2ray;
using XGates.Core.Platform;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;
using L = XGates.Core.Libv2ray.Libv2ray;

namespace XGates.Core.Tests.Libv2ray;

/// <summary>
/// Серии повторов для случайных сбоев: один проход на тысячу в обычном прогоне не разобрать. Без XGATES_SOAK=&lt;число повторов&gt; ничего не делают
/// (`.github/workflows/soak.yml`). Падают со списком причин, чтобы текст каждого сбоя был виден в журнале прогона.
/// </summary>
[Collection("xray")]
public sealed class SoakTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-soak-" + Guid.NewGuid().ToString("N")));
    private static int rounds => int.TryParse(Environment.GetEnvironmentVariable("XGATES_SOAK"), out var n) ? n : 0;

    public SoakTests() => ServerSpeedTester.probesOverrideForTest = [$"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1"];

    public void Dispose()
    {
        ServerSpeedTester.probesOverrideForTest = null;
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    /// <summary>Исходящие соединения, как у браузера: на Windows они занимали порт временного ядра, пока тот запускался.</summary>
    private Task churn(CancellationToken stop) => Task.Run(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, net.HttpPort); }
            catch (SocketException) { }
        }
    }, stop);

    [Fact]
    public void Short_measure_of_a_live_server()
    {
        if (rounds == 0) return;
        var p = VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{net.StartVlessServer()}?type=tcp&security=none#soak")!;
        var fails = new List<string>();
        using var stop = new CancellationTokenSource();
        var background = churn(stop.Token);
        var total = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            var t = Stopwatch.StartNew();
            var m = ServerSpeedTester.measureSpeedDetailed(ctx, p, warmupMs: 400, measureMs: 1200, warmupBudgetOverride: 512 * 1024L, measureBudgetOverride: 1_200_000L);
            if (!m.ok) fails.Add($"#{i} {t.ElapsedMilliseconds} мс: {m.reason}");
        }
        stop.Cancel();
        Assert.True(fails.Count == 0, $"сбоев {fails.Count} из {rounds} за {total.Elapsed.TotalSeconds:F0} с:\n{string.Join("\n", fails)}");
    }

    [Fact]
    public void Mkcp_tunnel()
    {
        if (rounds == 0) return;
        string[] queries = ["", "&headerType=srtp", "&headerType=utp", "&headerType=wechat-video", "&headerType=dtls", "&headerType=wireguard", "&headerType=dns", "&seed=s3cret", "&headerType=srtp&seed=s3cret"];
        var fails = new List<string>();
        var total = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            var query = queries[i % queries.Length];
            var draft = VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:1?type=kcp&security=none{query}#k")!;
            var outbounds = JsonNode.Parse(XrayConfigBuilder.build(draft))!["outbounds"]!.AsArray();
            var stream = outbounds.First(o => o!["tag"]!.GetValue<string>() == "proxy")!["streamSettings"]!.DeepClone().AsObject();
            int server = net.StartServer(stream);
            var p = VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{server}?type=kcp&security=none{query}#k")!;
            var (cfg, socks, _) = CoreControllerIntegrationTests.BuildOnFreePorts(p);
            var core = L.newCoreController(new LocalTestNet.NoopCallback());
            core.startLoop(cfg, 0);
            var t = Stopwatch.StartNew();
            try
            {
                using var c = CoreControllerIntegrationTests.ViaSocks(socks);
                _ = c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=200000").Result;
            }
            catch (AggregateException e)
            {
                var first = t.ElapsedMilliseconds;
                t.Restart();
                string again;   // тот же туннель ещё раз: завис он насовсем или пропал один запрос
                try
                {
                    using var c = CoreControllerIntegrationTests.ViaSocks(socks);
                    again = $"повтор ok {c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=200000").Result.Length} за {t.ElapsedMilliseconds} мс";
                }
                catch (AggregateException e2) { again = $"повтор сбой {t.ElapsedMilliseconds} мс: {e2.InnerException?.Message}"; }
                fails.Add($"#{i} «{query}» порт {server}: {first} мс {e.InnerException?.Message}; {again}; ядро: {core.recentOutput()}");
            }
            finally { core.stopLoop(); }
            net.StopVlessServer(server);
        }
        Assert.True(fails.Count == 0, $"сбоев {fails.Count} из {rounds} за {total.Elapsed.TotalSeconds:F0} с:\n{string.Join("\n", fails)}");
    }
}
