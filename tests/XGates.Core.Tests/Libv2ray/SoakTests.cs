using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using XGates.Core.Libv2ray;
using XGates.Core.Monitor;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
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
        XrayProxyService.shutdown();
        ServerSpeedTester.probesOverrideForTest = null;
        NetworkMonitor.directAliveOverrideForTest = null;
        ExternalIpChecker.onceOverrideForTest = null;
        WinSettingsStore.resetForTest();
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    /// <summary>
    /// Исходящие соединения, как у браузера: на Windows они занимали порт временного ядра, пока тот запускался. Темп обычный: цикл без пауз за секунды
    /// исчерпывает порты Windows (каждый закрытый ещё две минуты занят), и тогда не работает ничего, прогон 41525975146.
    /// </summary>
    private Task churn(CancellationToken stop) => Task.Run(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, net.HttpPort); }
            catch (SocketException) { }
            Thread.Sleep(50);
        }
    }, stop);

    private ServerSpeedTester.Measurement shortMeasure(ServerProfile p) =>
        ServerSpeedTester.measureSpeedDetailed(ctx, p, warmupMs: 400, measureMs: 1200, warmupBudgetOverride: 512 * 1024L, measureBudgetOverride: 1_200_000L);

    private static void report(List<string> fails, int total, Stopwatch time)
    {
        var byReason = fails.GroupBy(f => f[(f.IndexOf(':') + 1)..].Trim()).OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Count()} × {g.First()}");
        Assert.True(fails.Count == 0, $"сбоев {fails.Count} из {total} за {time.Elapsed.TotalSeconds:F0} с:\n{string.Join("\n", byReason)}");
    }

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
            var m = shortMeasure(p);
            if (!m.ok) fails.Add($"#{i} {t.ElapsedMilliseconds} мс: {m.reason}");
        }
        stop.Cancel();
        report(fails, rounds, total);
    }

    /// <summary>Как в прогоне 41455944519: туннель только что поднялся, и сразу идёт первый замер этого же сервера временным ядром.</summary>
    [Fact]
    public void Short_measure_right_after_the_tunnel_starts()
    {
        if (rounds == 0) return;
        NetworkMonitor.directAliveOverrideForTest = () => true;
        ExternalIpChecker.onceOverrideForTest = _ => "198.51.100.1";
        WinSettingsStore.update(ctx, new WinSettings { socksPort = LocalTestNet.FreePort(), httpPort = LocalTestNet.FreePort() });
        SubscriptionManager.init(ctx);
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx, $"vless://{LocalTestNet.Uuid}@127.0.0.1:{net.StartVlessServer()}?type=tcp&security=none#soak", "local");
        Assert.True(r.ok, r.error);
        var p = SubscriptionManager.allServers(ctx).Single();
        var key = SubscriptionManager.serverKey(p);
        var fails = new List<string>();
        var total = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            XrayProxyService.start(ctx, XrayConfigBuilder.build(p), "soak", key);
            var end = Environment.TickCount64 + 10_000;
            while (!ProxyState.state.value.running && Environment.TickCount64 < end) Thread.Sleep(20);
            Assert.True(ProxyState.state.value.running, ProxyState.state.value.message);
            var t = Stopwatch.StartNew();
            var m = shortMeasure(p);
            if (!m.ok) fails.Add($"#{i} {t.ElapsedMilliseconds} мс: {m.reason}");
            XrayProxyService.stop(ctx);
            end = Environment.TickCount64 + 10_000;
            while (ProxyState.state.value.running && Environment.TickCount64 < end) Thread.Sleep(20);
        }
        report(fails, rounds, total);
    }

    [Fact]
    public void Mkcp_tunnel()
    {
        if (rounds == 0) return;
        string[] queries = ["", "&headerType=srtp", "&headerType=utp", "&headerType=wechat-video", "&headerType=dtls", "&headerType=wireguard", "&headerType=dns", "&seed=s3cret", "&headerType=srtp&seed=s3cret"];
        var fails = new List<string>();
        var total = Stopwatch.StartNew();
        var mkcpRounds = rounds * 5;   // проход 0,3 с, а сбой был один на сотни проходов
        for (var i = 0; i < mkcpRounds; i++)
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
        report(fails, mkcpRounds, total);
    }
}
