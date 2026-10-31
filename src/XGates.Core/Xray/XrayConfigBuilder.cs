using XGates.Core.Compat;
using XGates.Core.Xray.Link;
using static XGates.Core.Xray.Link.ServerProfileKt;

namespace XGates.Core.Xray;

/// <summary>
/// ServerProfile → configJson (на Android — строка для XrayController.startLoop; на Windows — config.json для xray.exe).
///
/// Переиспользует общий inbound из <see cref="XrayConfig"/> (socks 10815 + http 10816) и добавляет
/// outbound по протоколу + streamSettings. Второй outbound freedom(direct) — как в Python-эталоне.
///
/// ПОРТ 1:1 с Kotlin (xray/XrayConfigBuilder.kt): шаблоны — те же raw-строки с теми же отступами + Kotlin trimIndent(),
/// поэтому вывод совпадает с Android байт в байт (проверяется golden-тестом, tools/golden/).
/// </summary>
public static class XrayConfigBuilder
{
    // Транспорты, которые собираем И которые умеет наше ядро (проверено по бинарю AAR, xray-core v26.x:
    // rawSettings/xhttpSettings/httpupgradeSettings/kcpSettings присутствуют). quic ядром НЕ поддержан (нет пакета).
    //  • raw   — новое имя tcp (нормализуем raw→tcp);
    //  • xhttp — новое имя splithttp (нормализуем splithttp→xhttp: иначе xhttpSettings не пишутся и path теряется);
    //  • kcp/mkcp — одно и то же (нормализуем kcp→mkcp).
    private static readonly HashSet<string> SUPPORTED_NETWORKS = ["tcp", "raw", "ws", "grpc", "xhttp", "splithttp", "httpupgrade", "kcp", "mkcp"];

    /// <summary>Боевой конфиг: общий inbound (socks 10815 + http 10816) + outbound сервера.</summary>
    public static string build(ServerProfile p) => $$"""
        {
          "log": { "loglevel": "warning" },
          "stats": {},
          "policy": { "system": { "statsOutboundUplink": true, "statsOutboundDownlink": true } },
          {{XrayConfig.inbounds()}},
          "outbounds": [
            {{outboundFor(p)}},
            { "tag": "direct", "protocol": "freedom" }
          ]
        }
    
""".TrimIndent();

    /// <summary>
    /// Конфиг для throughput-теста: ЕДИНСТВЕННЫЙ socks-inbound на переданном ЭФЕМЕРНОМ порту
    /// (НЕ 10815/10816 — чтобы temp-инстанс не конфликтовал с активным прокси) + outbound сервера.
    /// На Android поднимается локальным CoreController; на Windows — отдельным процессом xray.exe.
    /// </summary>
    public static string buildForSpeedTest(ServerProfile p, int socksPort) => $$"""
        {
          "log": { "loglevel": "warning" },
          "inbounds": [
            {
              "tag": "socks-in",
              "listen": "127.0.0.1",
              "port": {{socksPort}},
              "protocol": "socks",
              "settings": { "auth": "noauth", "udp": true }
            }
          ],
          "outbounds": [
            {{outboundFor(p)}},
            { "tag": "direct", "protocol": "freedom" }
          ]
        }
    
""".TrimIndent();

    /// <summary>Outbound сервера по протоколу (единый для боевого и speed-test конфигов).</summary>
    private static string outboundFor(ServerProfile p)
    {
        if (!SUPPORTED_NETWORKS.Contains(p.network)) throw new ArgumentException($"unsupported transport: {p.network}");
        return p.protocol switch
        {
            Protocol.VLESS => vlessOutbound(p),
            Protocol.TROJAN => trojanOutbound(p),
            Protocol.VMESS => vmessOutbound(p),
            Protocol.SHADOWSOCKS => shadowsocksOutbound(p),
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        };
    }

    // --- outbound по протоколу (единый vnext/users для vless+vmess) ---

    private static string vlessOutbound(ServerProfile p)
    {
        var flowLine = p.flow.IsNotBlank() ? ", \"flow\": " + j(p.flow!) : "";
        return $$"""
            {
              "tag": "proxy",
              "protocol": "vless",
              "settings": {
                "vnext": [
                  {
                    "address": {{j(p.address)}},
                    "port": {{p.port}},
                    "users": [
                      { "id": {{j(p.credential)}}, "encryption": {{j(p.method ?? "none")}}{{flowLine}}, "level": 0 }
                    ]
                  }
                ]
              },
              "streamSettings": {{streamSettings(p)}}
            }
        
""".TrimIndent();
    }

    private static string trojanOutbound(ServerProfile p)
    {
        return $$"""
            {
              "tag": "proxy",
              "protocol": "trojan",
              "settings": {
                "servers": [
                  { "address": {{j(p.address)}}, "port": {{p.port}}, "password": {{j(p.credential)}}, "level": 0 }
                ]
              },
              "streamSettings": {{streamSettings(p)}}
            }
        
""".TrimIndent();
    }

    private static string vmessOutbound(ServerProfile p)
    {
        // Единый vnext/users (как vless), но с alterId:0 (AEAD) и security = шифр (scy).
        return $$"""
            {
              "tag": "proxy",
              "protocol": "vmess",
              "settings": {
                "vnext": [
                  {
                    "address": {{j(p.address)}},
                    "port": {{p.port}},
                    "users": [
                      { "id": {{j(p.credential)}}, "alterId": 0, "security": {{j(p.method ?? "auto")}}, "level": 0 }
                    ]
                  }
                ]
              },
              "streamSettings": {{streamSettings(p)}}
            }
        
""".TrimIndent();
    }

    private static string shadowsocksOutbound(ServerProfile p)
    {
        // settings.servers с method+password; транспорт — чистый tcp.
        return $$"""
            {
              "tag": "proxy",
              "protocol": "shadowsocks",
              "settings": {
                "servers": [
                  { "address": {{j(p.address)}}, "port": {{p.port}}, "method": {{j(p.method ?? "")}}, "password": {{j(p.credential)}}, "level": 0 }
                ]
              },
              "streamSettings": {{streamSettings(p)}}
            }
        
""".TrimIndent();
    }

    // --- streamSettings (общий для vless/vmess/trojan) ---

    private static string streamSettings(ServerProfile p)
    {
        // Нормализация имён-синонимов транспорта: raw→tcp (raw = переименованный tcp), splithttp→xhttp, kcp→mkcp.
        var net = p.network switch
        {
            "raw" => "tcp",
            "splithttp" => "xhttp",
            "kcp" => "mkcp",
            _ => p.network,
        };
        // security нормализуем: ядро знает только none/tls/reality; мусор из подписок (false/auto/…)
        // иначе валит config load «Unknown security» → сервер не поднимается. См. normalizeStreamSecurity.
        var sec = normalizeStreamSecurity(p.security);
        var parts = new List<string>();
        parts.Add("\"network\": " + j(net));
        if (sec != "none") parts.Add("\"security\": " + j(sec));

        switch (sec)
        {
            case "tls": parts.Add("\"tlsSettings\": " + tlsBlock(p)); break;
            case "reality": parts.Add("\"realitySettings\": " + realityBlock(p)); break;
        }
        switch (net)
        {
            case "ws": parts.Add("\"wsSettings\": " + wsBlock(p)); break;
            case "grpc": parts.Add("\"grpcSettings\": { \"serviceName\": " + j(p.serviceName ?? "") + ", \"multiMode\": true }"); break;
            case "tcp": if (p.headerType == "http") parts.Add("\"tcpSettings\": { \"header\": { \"type\": \"http\" } }"); break;
            case "xhttp": parts.Add("\"xhttpSettings\": " + xhttpBlock(p)); break;
            case "httpupgrade": parts.Add("\"httpupgradeSettings\": " + httpUpgradeBlock(p)); break;
            case "mkcp": parts.Add("\"kcpSettings\": " + kcpBlock(p)); break;
        }
        return "{ " + string.Join(", ", parts) + " }";
    }

    // xhttp (бывш. splithttp): path + host (как ws) + необязательный mode (пусто → ядро выбирает auto).
    private static string xhttpBlock(ServerProfile p)
    {
        var host = p.hostHeader.TakeIfNotBlank() ?? p.sni ?? "";
        var fields = new List<string>
        {
            "\"path\": " + j(p.path ?? "/"),
            "\"host\": " + j(host),
        };
        if (p.mode.IsNotBlank()) fields.Add("\"mode\": " + j(p.mode!));
        return "{ " + string.Join(", ", fields) + " }";
    }

    // httpupgrade: path + host (та же схема, что ws).
    private static string httpUpgradeBlock(ServerProfile p)
    {
        var host = p.hostHeader.TakeIfNotBlank() ?? p.sni ?? "";
        return $$"""{ "path": {{j(p.path ?? "/")}}, "host": {{j(host)}} }""";
    }

    // mkcp: тип обфускации заголовка (headerType, деф. none) + необязательный seed.
    private static string kcpBlock(ServerProfile p)
    {
        var fields = new List<string>
        {
            "\"header\": { \"type\": " + j(p.headerType.TakeIfNotBlank() ?? "none") + " }",
        };
        if (p.seed.IsNotBlank()) fields.Add("\"seed\": " + j(p.seed!));
        return "{ " + string.Join(", ", fields) + " }";
    }

    private static string tlsBlock(ServerProfile p)
    {
        // allowInsecure не пишем: ядро v26 отвергает конфиг с true. Такой сервер запускается и проверяет сертификат как
        // обычно; пропустить проверку больше нельзя, самоподписанному нужен pcs (его даёт ссылка).
        var fields = new List<string>
        {
            "\"serverName\": " + j(p.sni ?? p.address),
        };
        if (p.fingerprint.IsNotBlank()) fields.Add("\"fingerprint\": " + j(p.fingerprint!));
        if (p.alpn.IsNotBlank())
        {
            var alpnArr = string.Join(", ", p.alpn!.Split(',').Select(it => j(it.KTrim())));
            fields.Add("\"alpn\": [" + alpnArr + "]");
        }
        if (normalizePinnedCerts(p.pinnedPeerCertSha256) is { } pins) fields.Add("\"pinnedPeerCertSha256\": " + j(pins));
        if (p.verifyPeerCertByName.IsNotBlank()) fields.Add("\"verifyPeerCertByName\": " + j(p.verifyPeerCertByName!));
        return "{ " + string.Join(", ", fields) + " }";
    }

    private static string realityBlock(ServerProfile p)
    {
        var fields = new List<string>
        {
            "\"serverName\": " + j(p.sni ?? ""),
            "\"fingerprint\": " + j(p.fingerprint ?? "chrome"),
            "\"publicKey\": " + j(p.publicKey ?? ""),
            "\"shortId\": " + j(p.shortId ?? ""),
        };
        if (p.spiderX.IsNotBlank()) fields.Add("\"spiderX\": " + j(p.spiderX!));
        return "{ " + string.Join(", ", fields) + " }";
    }

    private static string wsBlock(ServerProfile p)
    {
        // ws Host: явный host из ссылки, иначе sni (как Python generate_config).
        var host = p.hostHeader.TakeIfNotBlank() ?? p.sni ?? "";
        return $$"""{ "path": {{j(p.path ?? "/")}}, "host": {{j(host)}} }""";
    }

    /// <summary>JSON-строковой литерал с экранированием — защита от кавычек/бэкслэшей в значениях ссылки.</summary>
    private static string j(string s)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in s)
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        return sb.Append('"').ToString();
    }
}
