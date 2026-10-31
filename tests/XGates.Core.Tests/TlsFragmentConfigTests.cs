using System.Text.Json.Nodes;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests;

/// <summary>Опция фрагментации: выключена по умолчанию, конфиг без неё не меняется; включается только там, где есть TLS поверх TCP.</summary>
[Collection("xray")] // общий WinSettingsStore
public sealed class TlsFragmentConfigTests : IDisposable
{
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-fragcfg-" + Guid.NewGuid().ToString("N")));

    public TlsFragmentConfigTests()
    {
        Directory.CreateDirectory(ctx.filesDir);
        WinSettingsStore.resetForTest();
    }

    public void Dispose()
    {
        WinSettingsStore.resetForTest();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private void On() => Assert.True(WinSettingsStore.update(ctx, new WinSettings { tlsFragment = true }));

    private static ServerProfile P(string query) => VlessParser.parse($"vless://u@h:443?{query}#n")!;

    private static bool Applied(string config)
    {
        var c = JsonNode.Parse(config)!;
        var hasOutbound = c["outbounds"]!.AsArray().Any(o => o!["tag"]!.GetValue<string>() == "fragment");
        var sock = c["outbounds"]![0]!["streamSettings"]!["sockopt"];
        Assert.Equal(hasOutbound, sock != null);
        if (sock != null) Assert.Equal("fragment", sock["dialerProxy"]!.GetValue<string>());
        return hasOutbound;
    }

    [Fact]
    public void Off_by_default() => Assert.False(Applied(XrayConfigBuilder.build(P("security=tls"))));

    [Theory]
    [InlineData("type=tcp&security=tls")]
    [InlineData("type=ws&security=tls&path=%2Fw")]
    [InlineData("type=grpc&security=tls&serviceName=s")]
    [InlineData("type=xhttp&security=reality&pbk=k&sid=ab")]
    [InlineData("type=httpupgrade&security=tls")]
    public void On_for_tls_and_reality_in_both_configs(string query)
    {
        On();
        Assert.True(Applied(XrayConfigBuilder.build(P(query))));
        Assert.True(Applied(XrayConfigBuilder.buildForSpeedTest(P(query), 23456)));
    }

    [Theory]
    [InlineData("type=tcp&security=none")]
    [InlineData("type=ws&security=false&path=%2Fw")]
    [InlineData("type=kcp&security=tls")]
    public void Not_applied_where_there_is_no_tls_over_tcp(string query)
    {
        On();
        Assert.False(Applied(XrayConfigBuilder.build(P(query))));
    }

    [Fact]
    public void Download_channel_gets_dialer_proxy_only_with_option()
    {
        var extra = Uri.EscapeDataString("""{"downloadSettings":{"address":"d.example.com","port":443}}""");
        var p = P($"type=xhttp&security=tls&extra={extra}");
        JsonNode? Sock() => JsonNode.Parse(XrayConfigBuilder.build(p))!["outbounds"]![0]!["streamSettings"]!["xhttpSettings"]!["extra"]!["downloadSettings"]!["sockopt"];
        Assert.Null(Sock());
        On();
        Assert.Equal("fragment", Sock()!["dialerProxy"]!.GetValue<string>());
    }

    [Fact]
    public void Option_survives_restart_and_old_settings_file_means_off()
    {
        On();
        WinSettingsStore.resetForTest();
        WinSettingsStore.init(ctx);
        Assert.True(XrayConfig.TLS_FRAGMENT);

        File.WriteAllText(Path.Combine(ctx.filesDir, "win_settings.json"), """{ "socksPort": 20815, "httpPort": 20816 }""");
        WinSettingsStore.resetForTest();
        WinSettingsStore.init(ctx);
        Assert.False(XrayConfig.TLS_FRAGMENT);
        Assert.Equal(20815, XrayConfig.SOCKS_PORT);
    }
}
