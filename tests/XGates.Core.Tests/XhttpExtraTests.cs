using System.Text.Json;
using System.Text.Json.Nodes;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests;

/// <summary>
/// extra приходит из подписки и попадает в конфиг ядра: проходят только разрешённые поля (XhttpExtra), а JSON из чужой
/// строки не может выйти за пределы своего значения.
/// </summary>
public class XhttpExtraTests
{
    private static JsonObject Clean(string raw) => JsonNode.Parse(XhttpExtra.sanitize(raw)!)!.AsObject();

    private static JsonObject Extra(ServerProfile p) =>
        JsonNode.Parse(XrayConfigBuilder.build(p))!["outbounds"]![0]!["streamSettings"]!["xhttpSettings"]!["extra"]!.AsObject();

    [Fact]
    public void Keeps_padding_and_xmux()
    {
        var c = Clean("""{"xPaddingBytes":"100-1000","noGRPCHeader":false,"xmux":{"maxConcurrency":"16-32","hMaxRequestTimes":"600-900"}}""");
        Assert.Equal("100-1000", c["xPaddingBytes"]!.GetValue<string>());
        Assert.False(c["noGRPCHeader"]!.GetValue<bool>());
        Assert.Equal("16-32", c["xmux"]!["maxConcurrency"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"masterKeyLog":"C:\\x.log"}""")]
    [InlineData("""{"sockopt":{"dialerProxy":"direct"}}""")]
    [InlineData("""{"MasterKeyLog":"/tmp/x"}""")]       // Go сравнивает имена без учёта регистра
    [InlineData("""{"extra":{"xPaddingBytes":"1-2"}}""")]
    [InlineData("""{"host":"a","path":"/b","mode":"auto"}""")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void Nothing_allowed_gives_null(string raw) => Assert.Null(XhttpExtra.sanitize(raw));

    [Fact]
    public void Dangerous_download_settings_are_cut_out()
    {
        var c = Clean("""
            {"downloadSettings":{"address":"d.example.com","port":443,"network":"xhttp","security":"tls",
              "sockopt":{"dialerProxy":"direct","interface":"eth0"},
              "finalmask":{"tcp":[]},
              "tlsSettings":{"serverName":"d.example.com","fingerprint":"chrome","allowInsecure":true,"masterKeyLog":"/tmp/keys",
                "certificates":[{"certificateFile":"/etc/passwd"}],"echSockopt":{"mark":1}},
              "realitySettings":{"publicKey":"pk","shortId":"ab","masterKeyLog":"/tmp/keys"},
              "xhttpSettings":{"path":"/d","mode":"auto","xPaddingBytes":"100-200","downloadSettings":{"address":"x","port":1},"extra":{}}}}
            """);
        var d = c["downloadSettings"]!.AsObject();
        Assert.Equal("d.example.com", d["address"]!.GetValue<string>());
        Assert.Equal(443, d["port"]!.GetValue<int>());
        Assert.False(d.ContainsKey("sockopt"));
        Assert.False(d.ContainsKey("finalmask"));
        var tls = d["tlsSettings"]!.AsObject();
        Assert.Equal(["fingerprint", "serverName"], tls.Select(kv => kv.Key).Order());
        Assert.Equal(["publicKey", "shortId"], d["realitySettings"]!.AsObject().Select(kv => kv.Key).Order());
        Assert.Equal(["mode", "path", "xPaddingBytes"], d["xhttpSettings"]!.AsObject().Select(kv => kv.Key).Order());
    }

    [Theory]
    [InlineData("""{"downloadSettings":{"network":"xhttp"}}""")]
    [InlineData("""{"downloadSettings":{"address":"d.example.com"}}""")]
    [InlineData("""{"downloadSettings":{"address":"","port":443}}""")]
    public void Download_settings_without_address_or_port_are_dropped(string raw) => Assert.Null(XhttpExtra.sanitize(raw));

    [Fact]
    public void Host_header_is_removed()
    {
        var c = Clean("""{"headers":{"Host":"evil","X-Ok":"1","hOsT":"evil"}}""");
        Assert.Equal(["X-Ok"], c["headers"]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void Link_cannot_break_out_of_extra()
    {
        // Хвост после первого объекта разбор отбрасывает, в конфиг идёт только пересобранный JSON.
        var extra = Uri.EscapeDataString("""{"xPaddingBytes":"1-2"}}, "evil": {"y":{""");
        var p = VlessParser.parse($"vless://u@h:443?type=xhttp&security=tls&extra={extra}#n")!;
        var xhttp = JsonNode.Parse(XrayConfigBuilder.build(p))!["outbounds"]![0]!["streamSettings"]!["xhttpSettings"]!.AsObject();
        Assert.Equal(["extra", "host", "path"], xhttp.Select(kv => kv.Key).Order());
        Assert.Equal(["xPaddingBytes"], Extra(p).Select(kv => kv.Key));
    }

    [Fact]
    public void Value_saved_on_disk_is_cleaned_again_when_config_is_built()
    {
        var p = VlessParser.parse("vless://u@h:443?type=xhttp&security=tls#n")!
            with { xhttpExtra = """{"xPaddingBytes":"1-2","masterKeyLog":"/tmp/x"}""" };
        Assert.Equal(["xPaddingBytes"], Extra(p).Select(kv => kv.Key));
    }

    [Fact]
    public void Stream_one_mode_loses_download_settings_because_core_rejects_them()
    {
        var extra = Uri.EscapeDataString("""{"xPaddingBytes":"1-2","downloadSettings":{"address":"d.example.com","port":443}}""");
        var one = VlessParser.parse($"vless://u@h:443?type=xhttp&mode=stream-one&security=tls&extra={extra}#n")!;
        Assert.False(Extra(one).ContainsKey("downloadSettings"));
        var auto = VlessParser.parse($"vless://u@h:443?type=xhttp&security=tls&extra={extra}#n")!;
        Assert.True(Extra(auto).ContainsKey("downloadSettings"));
    }

    [Fact]
    public void Trojan_link_reads_the_same_parameters()
    {
        var extra = Uri.EscapeDataString("""{"xPaddingBytes":"1-2"}""");
        var p = TrojanParser.parse($"trojan://pw@h:443?type=xhttp&security=tls&pcs={new string('a', 64)}&vcn=n.example&extra={extra}#n")!;
        Assert.Equal(new string('a', 64), p.pinnedPeerCertSha256);
        Assert.Equal("n.example", p.verifyPeerCertByName);
        Assert.Equal("1-2", Extra(p)["xPaddingBytes"]!.GetValue<string>());
    }

    [Fact]
    public void Profile_without_new_fields_serializes_as_on_android()
    {
        var p = VlessParser.parse("vless://u@h:443?security=tls#n")!;
        var json = JsonSerializer.SerializeToNode(p)!.AsObject();
        Assert.False(json.ContainsKey("pinnedPeerCertSha256"));
        Assert.False(json.ContainsKey("verifyPeerCertByName"));
        Assert.False(json.ContainsKey("xhttpExtra"));
    }

    [Fact]
    public void Saved_profile_keeps_new_fields_after_reload()
    {
        var extra = Uri.EscapeDataString("""{"xPaddingBytes":"1-2"}""");
        var p = VlessParser.parse($"vless://u@h:443?type=xhttp&security=tls&pcs={new string('a', 64)}&vcn=n.example&extra={extra}#n")!;
        var back = XGates.Core.Platform.KJson.decodeFromString<ServerProfile>(XGates.Core.Platform.KJson.encodeToString(p));
        Assert.Equal(p, back);
    }
}
