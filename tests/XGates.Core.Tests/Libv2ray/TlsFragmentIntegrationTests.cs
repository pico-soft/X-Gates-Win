using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;
using L = XGates.Core.Libv2ray.Libv2ray;

namespace XGates.Core.Tests.Libv2ray;

/// <summary>
/// Фрагментация ClientHello на НАСТОЯЩЕМ xray: что приходит на приёмник и не ломается ли туннель. Против реальной
/// блокировки здесь не проверить: у стенда нет ТСПУ.
/// </summary>
[Collection("xray")]
public sealed class TlsFragmentIntegrationTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly LocalTestNet.TestCert cert = LocalTestNet.MakeCert("example.com");
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-fragment-" + Guid.NewGuid().ToString("N")));

    public TlsFragmentIntegrationTests() => Directory.CreateDirectory(ctx.filesDir);

    public void Dispose()
    {
        WinSettingsStore.resetForTest();
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private void Fragment(bool on) => WinSettingsStore.update(ctx, new WinSettings { tlsFragment = on });

    private static ServerProfile Vless(int port, string query) =>
        VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{port}?{query}#f")!;

    /// <summary>Размеры кусков, в которых приёмник получил начало TLS-соединения от клиента.</summary>
    private static List<int> ChunksSeenBy(TcpListener receiver, ServerProfile p)
    {
        var (cfg, socks, _) = CoreControllerIntegrationTests.BuildOnFreePorts(p);
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(cfg, 0);
        try
        {
            using var http = CoreControllerIntegrationTests.ViaSocks(socks);
            http.Timeout = TimeSpan.FromSeconds(3);
            _ = http.GetAsync("http://198.51.100.1/").ContinueWith(t => t.Exception);
            using var c = receiver.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5)).Result;
            c.NoDelay = true;
            var s = c.GetStream();
            s.ReadTimeout = 600;
            var chunks = new List<int>();
            var buf = new byte[65536];
            try
            {
                int total = 0;
                while (total < 1500)
                {
                    int n = s.Read(buf);
                    if (n == 0) break;
                    chunks.Add(n);
                    total += n;
                }
            }
            catch (IOException) { }
            return chunks;
        }
        finally { core.stopLoop(); }
    }

    private static TcpListener Receiver()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return l;
    }

    private (long down, bool ok) Tunnel(ServerProfile p)
    {
        var (cfg, socks, _) = CoreControllerIntegrationTests.BuildOnFreePorts(p);
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(cfg, 0);
        try
        {
            using var c = CoreControllerIntegrationTests.ViaSocks(socks);
            var ok = c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=200000").Result.Length == 200_000;
            return (core.queryStats("proxy", "downlink"), ok);
        }
        finally { core.stopLoop(); }
    }

    /// <summary>Свой приёмник на замер: ядро повторяет подключение, и прошлая попытка осталась бы в его очереди.</summary>
    private static List<int> HelloChunks()
    {
        var receiver = Receiver();
        try { return ChunksSeenBy(receiver, Vless(((IPEndPoint)receiver.LocalEndpoint).Port, "type=tcp&security=tls&sni=example.com&fp=chrome")); }
        finally { receiver.Stop(); }
    }

    [Fact]
    public void With_option_client_hello_arrives_in_pieces_and_without_it_in_one()
    {
        var whole = HelloChunks();
        Assert.True(whole[0] >= 1000, $"без опции первый кусок {whole[0]} байт");
        Fragment(true);
        var pieces = HelloChunks();
        Assert.True(pieces.Count >= 3, $"кусков {pieces.Count}");
        Assert.True(pieces[0] < 300, $"первый кусок {pieces[0]} байт");
    }

    [Fact]
    public void Tunnel_over_tls_works_and_traffic_is_counted()
    {
        Fragment(true);
        int server = net.StartVlessTlsServer(cert);
        var (down, ok) = Tunnel(Vless(server, $"type=tcp&security=tls&sni=example.com&pcs={cert.Sha256Hex}"));
        Assert.True(ok);
        Assert.InRange(down, 200_000, 300_000);
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("xhttp")]
    public void Tunnel_over_reality_works(string network)
    {
        Fragment(true);
        var xhttp = network == "xhttp" ? new JsonObject { ["path"] = "/x" } : null;
        var (server, publicKey, shortId) = net.StartVlessRealityServer(cert, "example.com", xhttp);
        var q = $"type={network}&security=reality&sni=example.com&fp=chrome&pbk={publicKey}&sid={shortId}&path=%2Fx";
        Assert.True(Tunnel(Vless(server, q)).ok);
    }

    [Fact]
    public void Xhttp_download_channel_is_fragmented_too()
    {
        Fragment(true);
        var receiver = Receiver();
        try
        {
            int server = net.StartVlessTlsServer(cert, new JsonObject { ["path"] = "/x" });
            var extra = Uri.EscapeDataString(new JsonObject
            {
                ["downloadSettings"] = new JsonObject
                {
                    ["address"] = "127.0.0.1", ["port"] = ((IPEndPoint)receiver.LocalEndpoint).Port, ["network"] = "xhttp", ["security"] = "tls",
                    ["tlsSettings"] = new JsonObject { ["serverName"] = "example.com", ["fingerprint"] = "chrome" },
                    ["xhttpSettings"] = new JsonObject { ["path"] = "/x" },
                },
            }.ToJsonString());
            var p = Vless(server, $"type=xhttp&mode=packet-up&security=tls&sni=example.com&path=%2Fx&pcs={cert.Sha256Hex}&extra={extra}");
            var pieces = ChunksSeenBy(receiver, p);
            Assert.True(pieces.Count >= 3, $"кусков {pieces.Count}");
            Assert.True(pieces[0] < 300, $"первый кусок {pieces[0]} байт");
        }
        finally { receiver.Stop(); }
    }
}
