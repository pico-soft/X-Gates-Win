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
        finally { core.stopLoop(); }
    }
}
