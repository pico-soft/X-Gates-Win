using System.Text.Json;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;
using static XGates.Core.Xray.Link.ServerProfileKt;

namespace XGates.Core.Tests;

/// <summary>
/// Порт app/src/test/…/TransportBuildTest.kt. Пр.138: транспорты, добавленные в XrayConfigBuilder (raw/xhttp/httpupgrade/mkcp).
/// Гоняем РЕАЛЬНЫЕ VlessParser + XrayConfigBuilder — раньше build() бросал "unsupported transport".
/// Проверяем: (1) build НЕ падает, (2) сеть/блок настроек в конфиге верные, (3) JSON валиден
/// (в C# — настоящий разбор System.Text.Json вместо грубой проверки скобок из Kotlin-теста).
/// </summary>
public class TransportBuildTest
{
    private static bool balanced(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; } catch (JsonException) { return false; }
    }

    private static ServerProfile vless(string link) => VlessParser.parse(link) ?? throw new InvalidOperationException("parse failed: " + link);

    [Fact]
    public void xhttp_reality_builds()
    {
        var p = vless(
            "vless://11111111-1111-1111-1111-111111111111@example.com:443" +
            "?encryption=none&mode=packet-up&path=%2Fassets%2Fv2&security=reality" +
            "&pbk=SOMEKEY&sid=ab12&sni=s.example.com&type=xhttp&fp=chrome#node");
        Assert.Equal("xhttp", p.network);
        Assert.Equal("packet-up", p.mode);
        Assert.Equal("/assets/v2", p.path);
        var cfg = XrayConfigBuilder.build(p);   // раньше бросал unsupported transport
        Assert.Contains("\"network\": \"xhttp\"", cfg);
        Assert.Contains("\"xhttpSettings\"", cfg);
        Assert.Contains("\"mode\": \"packet-up\"", cfg);
        Assert.Contains("\"path\": \"/assets/v2\"", cfg);
        Assert.Contains("\"realitySettings\"", cfg);
        Assert.True(balanced(cfg), "json balanced");
    }

    [Fact]
    public void raw_normalizes_to_tcp()
    {
        var p = vless("vless://11111111-1111-1111-1111-111111111111@h.example.com:443?type=raw&security=none#n");
        Assert.Equal("raw", p.network);
        var cfg = XrayConfigBuilder.build(p);   // не должно бросать
        Assert.Contains("\"network\": \"tcp\"", cfg);
        Assert.True(balanced(cfg), "json balanced");
    }

    [Fact]
    public void httpupgrade_builds()
    {
        var p = vless(
            "vless://11111111-1111-1111-1111-111111111111@h.example.com:443" +
            "?type=httpupgrade&path=%2Fhu&host=cdn.example.com&security=tls&sni=cdn.example.com#n");
        Assert.Equal("httpupgrade", p.network);
        var cfg = XrayConfigBuilder.build(p);
        Assert.Contains("\"network\": \"httpupgrade\"", cfg);
        Assert.Contains("\"httpupgradeSettings\"", cfg);
        Assert.Contains("\"path\": \"/hu\"", cfg);
        Assert.Contains("\"host\": \"cdn.example.com\"", cfg);
        Assert.True(balanced(cfg), "json balanced");
    }

    [Fact]
    public void mkcp_builds_with_header_and_seed()
    {
        var p = vless(
            "vless://11111111-1111-1111-1111-111111111111@h.example.com:2408" +
            "?type=kcp&headerType=srtp&seed=mySeed&security=none#n");
        Assert.Equal("kcp", p.network);
        Assert.Equal("srtp", p.headerType);
        Assert.Equal("mySeed", p.seed);
        var cfg = XrayConfigBuilder.build(p);
        Assert.Contains("\"network\": \"mkcp\"", cfg);
        Assert.Contains("\"kcpSettings\"", cfg);
        Assert.Contains("\"type\": \"srtp\"", cfg);
        Assert.Contains("\"seed\": \"mySeed\"", cfg);
        Assert.True(balanced(cfg), "json balanced");
    }

    // --- фикс: мусор в security (false/auto/…) больше не валит config load «Unknown security» ---

    [Fact]
    public void normalizeStreamSecurity_maps_garbage_to_none()
    {
        Assert.Equal("tls", normalizeStreamSecurity("tls"));
        Assert.Equal("tls", normalizeStreamSecurity("TLS"));
        Assert.Equal("reality", normalizeStreamSecurity("reality"));
        Assert.Equal("none", normalizeStreamSecurity("none"));
        Assert.Equal("none", normalizeStreamSecurity("false"));  // подписка: «без TLS»
        Assert.Equal("none", normalizeStreamSecurity("auto"));   // утёкший шифр vmess
        Assert.Equal("none", normalizeStreamSecurity("0"));
        Assert.Equal("none", normalizeStreamSecurity(""));
    }

    [Fact]
    public void security_false_builds_as_none_no_crash()
    {
        var p = vless("vless://11111111-1111-1111-1111-111111111111@h.example.com:80?type=ws&path=%2Fw&security=false#n");
        Assert.Equal("false", p.security);                        // сырьё в профиле НЕ трогаем (serverKey стабилен)
        var cfg = XrayConfigBuilder.build(p);                     // раньше ядро падало «Unknown security "false"»
        Assert.DoesNotContain("\"security\": \"false\"", cfg);
        Assert.False(cfg.Contains("\"tlsSettings\"") || cfg.Contains("\"realitySettings\""), "нет tls/reality-блока");
        Assert.Contains("\"wsSettings\"", cfg);
        Assert.True(balanced(cfg), "json balanced");
    }

    [Fact]
    public void security_auto_builds_as_none_no_crash()
    {
        var p = vless("vless://11111111-1111-1111-1111-111111111111@h.example.com:443?type=tcp&security=auto#n");
        var cfg = XrayConfigBuilder.build(p);
        Assert.DoesNotContain("\"security\": \"auto\"", cfg);
        Assert.True(balanced(cfg), "json balanced");
    }

    [Fact]
    public void splithttp_normalizes_to_xhttp()
    {
        // Находка порта №1: splithttp (старое имя xhttp) раньше падал «unsupported transport» — и из ссылки, и из JSON.
        var link = vless("vless://11111111-1111-1111-1111-111111111111@h.example.com:443?type=splithttp&path=%2Fsp&host=cdn.example.com&security=tls&sni=cdn.example.com#n");
        var json = JsonConfigParser.parse("""
            [{"remarks":"j","outbounds":[{"protocol":"vless","settings":{"vnext":[{"address":"h.example.com","port":443,
              "users":[{"id":"11111111-1111-1111-1111-111111111111"}]}]},
              "streamSettings":{"network":"splithttp","security":"none","splithttpSettings":{"path":"/sp","host":"cdn.example.com"}}}]}]
            """).Single();
        foreach (var p in new[] { link, json })
        {
            Assert.Equal("splithttp", p.network);                 // сырьё в профиле НЕ трогаем (serverKey стабилен)
            var cfg = XrayConfigBuilder.build(p);
            Assert.Contains("\"network\": \"xhttp\"", cfg);
            Assert.Contains("\"xhttpSettings\"", cfg);
            Assert.Contains("\"path\": \"/sp\"", cfg);
            Assert.True(balanced(cfg), "json balanced");
        }
    }

    [Fact]
    public void still_supports_ws_grpc()
    {
        var ws = XrayConfigBuilder.build(vless("vless://11111111-1111-1111-1111-111111111111@h:443?type=ws&path=%2Fw&host=x.com&security=tls&sni=x.com#n"));
        Assert.Contains("\"wsSettings\"", ws);
        var grpc = XrayConfigBuilder.build(vless("vless://11111111-1111-1111-1111-111111111111@h:443?type=grpc&serviceName=gs&security=tls&sni=x.com#n"));
        Assert.Contains("\"grpcSettings\"", grpc);
    }
}
