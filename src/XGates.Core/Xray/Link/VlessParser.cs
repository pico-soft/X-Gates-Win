using XGates.Core.Compat;

namespace XGates.Core.Xray.Link;

/// <summary>
/// vless://UUID@host:port?encryption=none&amp;security=tls&amp;type=ws&amp;path=..&amp;host=..&amp;sni=..&amp;fp=..&amp;flow=..#remark
///
/// Эталон полей — Python parse_vless (xproxy_lib.py) + v2rayNG VlessFmt/FmtBase.getItemFormQuery.
/// Обязательны: UUID (user-info), host, port. Остальное — query с дефолтами.
/// </summary>
public static class VlessParser
{
    public static ServerProfile? parse(string uri)
    {
        JavaUri u;
        try { u = new JavaUri(uri); } catch (Exception) { return null; }
        var host = u.Host.TakeIfNotBlank();
        if (host == null) return null;
        var uuid = u.UserInfo.TakeIfNotBlank();
        if (uuid == null) return null;
        var port = u.Port > 0 ? u.Port : 443;
        var q = LinkUtil.queryParams(u.RawQuery);

        return new ServerProfile
        {
            protocol = Protocol.VLESS,
            remarks = u.Fragment ?? "",                 // URI уже percent-decode'ит фрагмент (UTF-8)
            address = host.RemoveSurrounding("[", "]"), // IPv6 без скобок
            port = port,
            credential = uuid,                          // UUID
            method = q.Get("encryption") ?? "none",
            flow = q.Get("flow"),
            security = q.Get("security") ?? "none",
            sni = q.Get("sni"),
            fingerprint = q.Get("fp"),
            alpn = q.Get("alpn"),
            allowInsecure = q.Get("allowInsecure") == "1" || q.Get("insecure") == "1",
            network = q.Get("type") ?? "tcp",
            path = q.Get("path"),
            hostHeader = q.Get("host"),
            serviceName = q.Get("serviceName"),
            headerType = q.Get("headerType"),
            mode = q.Get("mode"),
            seed = q.Get("seed"),
            publicKey = q.Get("pbk"),
            shortId = q.Get("sid"),
            spiderX = q.Get("spx"),
            raw = uri,
        };
    }
}
