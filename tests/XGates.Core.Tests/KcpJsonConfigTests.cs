using System.Text.Json.Nodes;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests;

/// <summary>mKCP из JSON-подписок: старый формат (kcpSettings) и новый (finalmask) дают один и тот же конфиг ядра.</summary>
public sealed class KcpJsonConfigTests
{
    private static string Body(string stream) => $$"""
        { "remarks": "n", "outbounds": [ { "tag": "proxy", "protocol": "vless",
          "settings": { "vnext": [ { "address": "h.example.com", "port": 2408,
            "users": [ { "id": "11111111-1111-1111-1111-111111111111", "encryption": "none" } ] } ] },
          "streamSettings": {{stream}} } ] }
        """;

    private static JsonArray Masks(string stream) =>
        JsonNode.Parse(XrayConfigBuilder.build(JsonConfigParser.parse(Body(stream)).Single()))!["outbounds"]![0]!["streamSettings"]!["finalmask"]!["udp"]!.AsArray();

    [Fact]
    public void Old_and_new_format_give_same_masks()
    {
        var old = Masks("""{ "network": "kcp", "kcpSettings": { "header": { "type": "wechat-video" }, "seed": "s3" } }""");
        var fresh = Masks("""
            { "network": "kcp", "kcpSettings": {}, "finalmask": { "udp": [
              { "type": "mkcp-legacy", "settings": { "value": "s3" } },
              { "type": "mkcp-legacy", "settings": { "header": "wechat" } } ] } }
            """);
        Assert.Equal(old.ToJsonString(), fresh.ToJsonString());
    }

    [Fact]
    public void Dns_domain_is_kept_and_foreign_masks_are_ignored()
    {
        var m = Masks("""
            { "network": "mkcp", "finalmask": { "udp": [ { "type": "xicmp", "settings": { "value": "evil" } },
              { "type": "mkcp-legacy" }, { "type": "mkcp-legacy", "settings": { "header": "dns", "value": "a.example.com" } } ] } }
            """);
        Assert.Equal(2, m.Count);
        Assert.Equal("dns", m[1]!["settings"]!["header"]!.GetValue<string>());
        Assert.Equal("a.example.com", m[1]!["settings"]!["value"]!.GetValue<string>());
    }
}
