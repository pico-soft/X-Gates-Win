using XGates.Core.Compat;

namespace XGates.Core.Xray.Link;

/// <summary>
/// Диспетчер: строка → <see cref="ParseResult"/>. Определяет протокол по scheme-префиксу.
///
/// SUPPORTED — исполняются нашим xray-core. KNOWN_UNSUPPORTED — распознаём и помечаем
/// (ждут sing-box), но не падаем. Всё прочее → Invalid.
/// </summary>
public static class ServerLinkParser
{
    // Порядок как в Kotlin mapOf (вставки).
    private static readonly (string scheme, LinkParser parser)[] SUPPORTED =
    [
        ("vless://", VlessParser.parse),
        ("trojan://", TrojanParser.parse),
        ("vmess://", VmessParser.parse),
        ("ss://", ShadowsocksParser.parse),
    ];

    private static readonly string[] KNOWN_UNSUPPORTED =
    [
        "hysteria2://", "hy2://", "tuic://", "wireguard://", "socks://",
    ];

    public static ParseResult parse(string line)
    {
        var s = line.KTrim();

        foreach (var (scheme, parser) in SUPPORTED)
        {
            if (!s.StartsWithIgnoreCase(scheme)) continue;
            var profile = parser(s);
            if (profile == null) return new ParseResult.Invalid($"failed to parse {scheme}", s);
            return new ParseResult.Supported(profile);
        }

        foreach (var scheme in KNOWN_UNSUPPORTED)
        {
            if (s.StartsWithIgnoreCase(scheme)) return new ParseResult.Unsupported(scheme.RemoveSuffix("://"), s);
        }

        return new ParseResult.Invalid("unknown scheme", s);
    }
}
