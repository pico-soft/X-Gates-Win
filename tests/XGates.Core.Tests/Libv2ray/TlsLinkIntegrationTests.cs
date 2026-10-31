using System.Text.Json.Nodes;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;
using L = XGates.Core.Libv2ray.Libv2ray;

namespace XGates.Core.Tests.Libv2ray;

/// <summary>
/// Параметры TLS и XHTTP из ссылок на НАСТОЯЩЕМ xray: ядро v26 убрало allowInsecure (конфиг с true не грузится),
/// вместо него pcs/vcn; extra несёт паддинг, XMUX и downloadSettings. Сервер стенда — vless поверх TLS на петле.
/// </summary>
[Collection("xray")]
public sealed class TlsLinkIntegrationTests : IDisposable
{
    private const string OTHER_PIN = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private readonly LocalTestNet net = new();
    private readonly LocalTestNet.TestCert cert = LocalTestNet.MakeCert("example.com");

    public void Dispose() => net.Dispose();

    private static ServerProfile Vless(int port, string query) =>
        VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{port}?{query}#tls")!;

    /// <summary>Прошёл ли трафик через туннель. Ядро обязано запуститься: иначе startLoop бросит и тест упадёт.</summary>
    private bool Tunnel(ServerProfile p)
    {
        var (cfg, socks, _) = CoreControllerIntegrationTests.BuildOnFreePorts(p);
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(cfg, 0);
        try
        {
            using var c = CoreControllerIntegrationTests.ViaSocks(socks);
            try { return c.GetByteArrayAsync($"http://127.0.0.1:{net.HttpPort}/bytes?n=5000").Result.Length == 5000; }
            catch (AggregateException) { return false; }
        }
        finally { core.stopLoop(); }
    }

    [Fact]
    public void Allow_insecure_link_starts_core_and_certificate_is_still_verified()
    {
        int server = net.StartVlessTlsServer(cert);
        var p = Vless(server, "type=tcp&security=tls&sni=example.com&allowInsecure=1");
        Assert.True(p.allowInsecure);
        Assert.DoesNotContain("allowInsecure", XrayConfigBuilder.build(p));
        Assert.False(Tunnel(p), "самоподписанный сертификат без pin принят — проверка отключилась");
    }

    [Fact]
    public void Pinned_certificate_from_link_opens_tunnel_and_wrong_pin_does_not()
    {
        int server = net.StartVlessTlsServer(cert);
        Assert.True(Tunnel(Vless(server, $"type=tcp&security=tls&sni=example.com&pcs={cert.Sha256Hex}")));
        Assert.False(Tunnel(Vless(server, $"type=tcp&security=tls&sni=example.com&pcs={OTHER_PIN}")));
    }

    [Fact]
    public void Pin_in_openssl_format_and_second_pin_in_list_work()
    {
        int server = net.StartVlessTlsServer(cert);
        var colons = string.Join(":", Convert.FromHexString(cert.Sha256Hex).Select(b => b.ToString("X2")));
        Assert.True(Tunnel(Vless(server, $"type=tcp&security=tls&sni=example.com&pcs={OTHER_PIN},{colons}")));
    }

    [Fact]
    public void Broken_pin_is_dropped_so_core_starts_and_certificate_is_verified_as_usual()
    {
        int server = net.StartVlessTlsServer(cert);
        var p = Vless(server, "type=tcp&security=tls&sni=example.com&pcs=not-hex");
        Assert.DoesNotContain("pinnedPeerCertSha256", XrayConfigBuilder.build(p));
        Assert.False(Tunnel(p));
    }

    [Fact]
    public void Verify_by_name_from_link_reaches_config_and_core_accepts_it()
    {
        int server = net.StartVlessTlsServer(cert);
        var p = Vless(server, "type=tcp&security=tls&sni=front.example&vcn=example.com");
        Assert.Contains("\"verifyPeerCertByName\": \"example.com\"", XrayConfigBuilder.build(p));
        Assert.False(Tunnel(p));   // имя совпало, но цепочка самоподписанная — доверия нет
    }

    [Fact]
    public void Xhttp_extra_from_link_reaches_core()
    {
        // Сервер требует нестандартный паддинг: без extra ядро клиента шлёт 100–1000 и получает отказ.
        int server = net.StartVlessTlsServer(cert, new JsonObject { ["path"] = "/x", ["xPaddingBytes"] = "1500-2000" });
        var q = $"type=xhttp&security=tls&sni=example.com&path=%2Fx&pcs={cert.Sha256Hex}";
        Assert.False(Tunnel(Vless(server, q)));
        var extra = Uri.EscapeDataString("""{"xPaddingBytes":"1500-2000","xmux":{"maxConcurrency":"4-8"}}""");
        Assert.True(Tunnel(Vless(server, q + "&extra=" + extra)));
    }

    [Fact]
    public void Xhttp_download_settings_from_link_carry_traffic()
    {
        int server = net.StartVlessTlsServer(cert, new JsonObject { ["path"] = "/x" });
        var extra = Uri.EscapeDataString(new JsonObject
        {
            ["downloadSettings"] = new JsonObject
            {
                ["address"] = "127.0.0.1", ["port"] = server, ["network"] = "xhttp", ["security"] = "tls",
                ["tlsSettings"] = new JsonObject { ["serverName"] = "example.com", ["pinnedPeerCertSha256"] = cert.Sha256Hex },
                ["xhttpSettings"] = new JsonObject { ["path"] = "/x" },
            },
        }.ToJsonString());
        var p = Vless(server, $"type=xhttp&mode=packet-up&security=tls&sni=example.com&path=%2Fx&pcs={cert.Sha256Hex}&extra={extra}");
        Assert.Contains("downloadSettings", XrayConfigBuilder.build(p));
        Assert.True(Tunnel(p));
    }

    [Fact]
    public void Xhttp_extra_from_json_config_reaches_core()
    {
        int server = net.StartVlessTlsServer(cert, new JsonObject { ["path"] = "/x", ["xPaddingBytes"] = "1500-2000" });
        var body = $$"""
            [{ "remarks": "json", "outbounds": [{ "protocol": "vless",
              "settings": { "vnext": [{ "address": "127.0.0.1", "port": {{server}}, "users": [{ "id": "{{LocalTestNet.Uuid}}", "encryption": "none" }] }] },
              "streamSettings": { "network": "xhttp", "security": "tls",
                "tlsSettings": { "serverName": "example.com", "pinnedPeerCertSha256": "{{cert.Sha256Hex}}" },
                "xhttpSettings": { "path": "/x", "xPaddingBytes": "1500-2000" } } }] }]
            """;
        Assert.True(Tunnel(JsonConfigParser.parse(body).Single()));
    }
}
