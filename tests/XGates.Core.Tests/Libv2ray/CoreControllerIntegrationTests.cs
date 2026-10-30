using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using XGates.Core.Libv2ray;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;
using L = XGates.Core.Libv2ray.Libv2ray;

namespace XGates.Core.Tests.Libv2ray;

/// <summary>
/// Интеграция с НАСТОЯЩИМ xray (та же версия, что в Android): старт/стоп, порты, трафик через туннель,
/// статистика со сбросом (как queryStats на Android), ошибки старта, замер задержки (measureOutboundDelay Lib v39).
/// </summary>
[Collection("xray")] // по одному: тесты занимают порты и процессы
public sealed class CoreControllerIntegrationTests : IDisposable
{
    private readonly LocalTestNet net = new();

    public void Dispose() => net.Dispose();

    private static bool PortOpen(int port)
    {
        try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); return true; } catch (SocketException) { return false; }
    }

    private static HttpClient ViaSocks(int port) =>
        new(new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{port}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(10) };

    private static HttpClient ViaHttpProxy(int port) =>
        new(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Боевой конфиг XrayConfigBuilder.build с портами 10815/10816, подменёнными на свободные (только в тесте).</summary>
    private static (string cfg, int socks, int http) BuildOnFreePorts(ServerProfile p)
    {
        var cfg = JsonNode.Parse(XrayConfigBuilder.build(p))!.AsObject();
        int socks = LocalTestNet.FreePort(), http = LocalTestNet.FreePort();
        var inb = cfg["inbounds"]!.AsArray();
        inb[0]!["port"] = socks;
        inb[1]!["port"] = http;
        return (cfg.ToJsonString(), socks, http);
    }

    private ServerProfile LocalVless(int serverPort, string query = "type=tcp&security=none") =>
        VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{serverPort}?{query}#local")!;

    [Fact]
    public void Start_tunnel_traffic_stats_with_reset_and_stop()
    {
        int server = net.StartVlessServer();
        var (cfg, socks, http) = BuildOnFreePorts(LocalVless(server));
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(cfg, 0);
        try
        {
            Assert.True(core.isRunning);
            Assert.True(PortOpen(socks), "SOCKS слушает");
            Assert.True(PortOpen(http), "HTTP слушает");

            using (var c = ViaSocks(socks))
                Assert.Equal(300_000, c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=300000").Result.Length);
            using (var c = ViaHttpProxy(http))
                Assert.Equal(1000, c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=1000").Result.Length);

            long down = core.queryStats("proxy", "downlink");
            long up = core.queryStats("proxy", "uplink");
            Assert.InRange(down, 301_000, 400_000);        // тело + заголовки, через outbound "proxy"
            // Факт xray v26.7.28 (и через его собственный `xray api statsquery`): у vless-outbound uplink-счётчик
            // остаётся 0 (у freedom — растёт). То же ядро на Android → паритет; трафик «туннеля» считается по downlink.
            Assert.InRange(up, 0, 10_000);
            Assert.Equal(0, core.queryStats("proxy", "downlink")); // чтение со СБРОСОМ (как counter.Set(0))
            Assert.Equal(0, core.queryStats("nosuchtag", "downlink"));
        }
        finally { core.stopLoop(); }
        Assert.False(core.isRunning);
        Assert.False(PortOpen(socks), "после stopLoop порт свободен");
        core.stopLoop(); // идемпотентно
    }

    [Fact]
    public void Ws_transport_through_builder_config()
    {
        int server = net.StartVlessServer("ws", "/tunnel");
        var p = LocalVless(server, "type=ws&path=%2Ftunnel&host=example.com&security=none");
        int socks = LocalTestNet.FreePort();
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(XrayConfigBuilder.buildForSpeedTest(p, socks), 0);
        try
        {
            using var c = ViaSocks(socks);
            Assert.Equal(5000, c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=5000").Result.Length);
            Assert.Equal(0, core.queryStats("proxy", "downlink")); // в конфиге замера нет "stats" → 0, как в Go
        }
        finally { core.stopLoop(); }
    }

    [Fact]
    public void Non_ascii_config_reaches_core_intact()
    {
        // Сквозная проверка: не-ASCII значение конфига (путь ws кириллицей) доходит до ядра без искажений — клиент и
        // сервер сходятся на одном пути. (До stdin конфиг уходит пересобранным, не-ASCII — как \uXXXX.)
        int server = net.StartVlessServer("ws", "/туннель");
        var p = LocalVless(server, "type=ws&path=%2F%D1%82%D1%83%D0%BD%D0%BD%D0%B5%D0%BB%D1%8C&host=example.com&security=none");
        Assert.Contains("/туннель", XrayConfigBuilder.buildForSpeedTest(p, 1));
        int socks = LocalTestNet.FreePort();
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(XrayConfigBuilder.buildForSpeedTest(p, socks), 0);
        try
        {
            using var c = ViaSocks(socks);
            Assert.Equal(5000, c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=5000").Result.Length);
        }
        finally { core.stopLoop(); }
    }

    [Fact]
    public void Busy_port_fails_start_with_core_output()
    {
        int server = net.StartVlessServer();
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        int port = ((IPEndPoint)busy.LocalEndpoint).Port;
        try
        {
            var core = L.newCoreController(new LocalTestNet.NoopCallback());
            var e = Assert.Throws<XrayCoreException>(() => core.startLoop(XrayConfigBuilder.buildForSpeedTest(LocalVless(server), port), 0));
            Assert.Contains("startup failed", e.Message);
            Assert.Contains("address already in use", e.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(core.isRunning);
        }
        finally { busy.Stop(); }
    }

    [Fact]
    public void Invalid_config_fails()
    {
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        Assert.Throws<XrayCoreException>(() => core.startLoop("{ not json", 0));
        var e = Assert.Throws<XrayCoreException>(() => core.startLoop("{\"outbounds\":[{\"protocol\":\"nosuchproto\"}]}", 0));
        Assert.Contains("startup failed", e.Message);
    }

    [Fact]
    public void MeasureOutboundDelay_alive_and_dead()
    {
        int server = net.StartVlessServer();
        var url = $"http://127.0.0.1:{net.HttpPort}/generate_204";
        long ms = L.measureOutboundDelay(XrayConfigBuilder.build(LocalVless(server)), url);
        Assert.InRange(ms, 0, 2000);

        int dead = LocalTestNet.FreePort(); // никто не слушает
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => L.measureOutboundDelay(XrayConfigBuilder.build(LocalVless(dead)), url));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Process_is_gone_after_stop()
    {
        int server = net.StartVlessServer();
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(XrayConfigBuilder.buildForSpeedTest(LocalVless(server), LocalTestNet.FreePort()), 0);
        int pid = core.pid!.Value;
        core.stopLoop();
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid)); // процесса больше нет — не сирота
    }

    [Fact]
    public void CheckVersionX_reports_pinned_core()
    {
        Assert.Equal("Xray-core v26.7.28", L.checkVersionX());
    }
}

public class XrayStatsClientCodecTests
{
    [Fact]
    public void Decodes_real_xray_GetStats_response()
    {
        // Байты ответа настоящего xray v26.7.28 (без 5-байтового gRPC-заголовка): Stat{name, value=298992}.
        var msg = Convert.FromHexString("0A2B0A256F7574626F756E643E3E3E70726F78793E3E3E747261666669633E3E3E646F776E6C696E6B10F0B012");
        Assert.Equal(0x12 << 14 | 0x30 << 7 | 0x70, XrayStatsClient.DecodeGetStatsResponseValue(msg));
        Assert.Equal(0, XrayStatsClient.DecodeGetStatsResponseValue(Convert.FromHexString("0A050A03616263"))); // value опущен = 0
    }

    [Fact]
    public void Encodes_GetStats_request()
    {
        Assert.Equal("0A036162631001", Convert.ToHexString(XrayStatsClient.EncodeGetStatsRequest("abc", reset: true)));
    }
}
