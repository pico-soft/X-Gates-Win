using XGates.Core.Compat;

namespace XGates.Core.Xray.Link;

/// <summary>
/// trojan://password@host:port?security=tls&amp;type=ws&amp;path=..&amp;host=..&amp;sni=..&amp;fp=..#remark
///
/// Эталон — Python parse_trojan (xproxy_lib.py) + v2rayNG TrojanFmt.
/// Отличия от vless: credential = password (не UUID); security по умолчанию = tls (не none);
/// нет encryption/flow; outbound через settings.servers (см. XrayConfigBuilder).
/// </summary>
public static class TrojanParser
{
    public static ServerProfile? parse(string uri)
    {
        JavaUri u;
        try { u = new JavaUri(uri); } catch (Exception) { return null; }
        var host = u.Host.TakeIfNotBlank();
        if (host == null) return null;
        var password = u.UserInfo.TakeIfNotBlank();
        if (password == null) return null;
        var port = u.Port > 0 ? u.Port : 443;
        var q = LinkUtil.queryParams(u.RawQuery);

        return new ServerProfile
        {
            protocol = Protocol.TROJAN,
            remarks = u.Fragment ?? "",
            address = host.RemoveSurrounding("[", "]"),
            port = port,
            credential = password,                      // пароль, НЕ UUID
            method = null,                              // trojan не использует
            flow = null,
            security = q.Get("security") ?? "tls",      // ← по умолчанию tls, не none
            sni = q.Get("sni"),
            fingerprint = q.Get("fp"),
            alpn = q.Get("alpn"),
            allowInsecure = q.Get("allowInsecure") == "1" || q.Get("insecure") == "1",
            pinnedPeerCertSha256 = q.Get("pcs").IfBlankNull(),
            verifyPeerCertByName = q.Get("vcn").IfBlankNull(),
            network = q.Get("type") ?? "tcp",
            path = q.Get("path"),
            hostHeader = q.Get("host"),
            serviceName = q.Get("serviceName"),
            headerType = q.Get("headerType"),
            mode = q.Get("mode"),
            xhttpExtra = XhttpExtra.sanitize(q.Get("extra")),
            seed = q.Get("seed"),
            publicKey = q.Get("pbk"),
            shortId = q.Get("sid"),
            spiderX = q.Get("spx"),
            raw = uri,
        };
    }
}
