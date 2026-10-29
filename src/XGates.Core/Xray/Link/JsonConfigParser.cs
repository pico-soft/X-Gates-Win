using XGates.Core.Compat;

namespace XGates.Core.Xray.Link;

/// <summary>
/// Подписки формата «готовый JSON-конфиг(и) Xray / экспорт v2rayN(G)» — тело не ссылки vless://…, а JSON:
/// либо МАССИВ полных конфигов, либо один конфиг. Полевой случай: платный Furk отдаёт по URL именно такой JSON
/// (массив из ~30 конфигов, у каждого свой <c>outbounds</c>→proxy). Наш <see cref="ServerLinkParser"/> читает только ссылки, поэтому
/// из таких подписок импортировалось НОЛЬ серверов (в логе «ссылок не найдено (JSON)»), хотя серверы живые.
///
/// Достаём из каждого конфига outbound с tag=="proxy" (иначе первый proxy-протокол) и мапим в <see cref="ServerProfile"/>
/// ТЕМИ ЖЕ полями, что <see cref="VlessParser"/>/<see cref="VmessParser"/> (чтобы serverKey и XrayConfigBuilder работали одинаково).
/// Конфиги-плейсхолдеры (address 0.0.0.0 / пусто — напр. запись «Автовыбор») пропускаем.
/// </summary>
public static class JsonConfigParser
{
    /// <summary>Распарсить тело подписки как JSON-конфиг(и). Пусто — тело не JSON-конфиг (откат к ссылочному парсеру).</summary>
    public static List<ServerProfile> parse(string body)
    {
        var t = body.KTrim();
        if (t.Length == 0 || (t[0] != '[' && t[0] != '{')) return [];   // быстрый отказ для не-JSON
        List<JSONObject> configs;
        try
        {
            if (t[0] == '[')
            {
                var a = new JSONArray(t);
                configs = [];
                for (int i = 0; i < a.length(); i++) if (a.optJSONObject(i) is { } o) configs.Add(o);
            }
            else configs = [new JSONObject(t)];
        }
        catch (Exception) { return []; }
        var @out = new List<ServerProfile>();
        foreach (var cfg in configs) if (profileFromConfig(cfg) is { } p) @out.Add(p);
        return @out;
    }

    private static readonly HashSet<string> PROXY_PROTOCOLS = ["vless", "vmess", "trojan", "shadowsocks"];

    private static ServerProfile? profileFromConfig(JSONObject cfg)
    {
        var outs = cfg.optJSONArray("outbounds");
        if (outs == null) return null;
        // Предпочитаем tag=="proxy"; иначе первый outbound с нашим протоколом (не freedom/blackhole).
        JSONObject? proxy = null;
        for (int i = 0; i < outs.length(); i++)
        {
            var o = outs.optJSONObject(i);
            if (o == null) continue;
            if (o.optString("tag") == "proxy" && PROXY_PROTOCOLS.Contains(o.optString("protocol"))) { proxy = o; break; }
        }
        if (proxy == null)
            for (int i = 0; i < outs.length(); i++)
            {
                var o = outs.optJSONObject(i);
                if (o == null) continue;
                if (PROXY_PROTOCOLS.Contains(o.optString("protocol"))) { proxy = o; break; }
            }
        var p = proxy;
        if (p == null) return null;
        Protocol protocol;
        switch (p.optString("protocol"))
        {
            case "vless": protocol = Protocol.VLESS; break;
            case "vmess": protocol = Protocol.VMESS; break;
            case "trojan": protocol = Protocol.TROJAN; break;
            case "shadowsocks": protocol = Protocol.SHADOWSOCKS; break;
            default: return null;
        }
        var settings = p.optJSONObject("settings");
        if (settings == null) return null;

        string address; int port; string credential; string? method; string? flow = null;
        switch (protocol)
        {
            case Protocol.VLESS:
            case Protocol.VMESS:
            {
                var v = settings.optJSONArray("vnext")?.optJSONObject(0);
                if (v == null) return null;
                var user = v.optJSONArray("users")?.optJSONObject(0);
                if (user == null) return null;
                address = v.optString("address"); port = v.optInt("port");
                credential = user.optString("id");
                method = protocol == Protocol.VMESS ? user.optString("security", "auto").IfBlank("auto")
                                                    : user.optString("encryption", "none").IfBlank("none");
                flow = user.optString("flow").IfBlankNull();
                break;
            }
            default:
            {
                var s = settings.optJSONArray("servers")?.optJSONObject(0);
                if (s == null) return null;
                address = s.optString("address"); port = s.optInt("port");
                credential = s.optString("password");
                method = protocol == Protocol.SHADOWSOCKS ? s.optString("method").IfBlankNull() : null;
                break;
            }
        }
        // Плейсхолдер/битый конфиг (напр. запись «Автовыбор» с 0.0.0.0) — пропускаем.
        if (address.IsBlank() || address == "0.0.0.0" || port <= 0 || credential.IsBlank()) return null;

        var st = p.optJSONObject("streamSettings") ?? new JSONObject();
        var network = st.optString("network", "tcp").IfBlank("tcp");
        var security = st.optString("security", "none").IfBlank("none");
        var tls = st.optJSONObject("tlsSettings");
        var reality = st.optJSONObject("realitySettings");
        var sec = reality ?? tls;   // reality и tls несут serverName/fingerprint в одном месте
        var sni = sec?.optString("serverName").IfBlankNull();
        var fingerprint = sec?.optString("fingerprint").IfBlankNull();
        string? alpn = null;
        if (tls?.optJSONArray("alpn") is { } alpnArr)
        {
            var parts = new List<string>();
            for (int i = 0; i < alpnArr.length(); i++) parts.Add(alpnArr.optString(i));
            alpn = string.Join(",", parts).IfBlankNull();
        }
        var allowInsecure = tls?.optBoolean("allowInsecure", false) ?? false;
        var publicKey = reality?.optString("publicKey").IfBlankNull();
        var shortId = reality?.optString("shortId").IfBlankNull();
        var spiderX = reality?.optString("spiderX").IfBlankNull();

        string? path = null, hostHeader = null, serviceName = null, headerType = null, mode = null, seed = null;
        switch (network)
        {
            case "ws":
                if (st.optJSONObject("wsSettings") is { } w)
                {
                    path = w.optString("path").IfBlankNull();
                    hostHeader = w.optJSONObject("headers")?.optString("Host").IfBlankNull() ?? w.optString("host").IfBlankNull();
                }
                break;
            case "httpupgrade":
                if (st.optJSONObject("httpupgradeSettings") is { } h)
                {
                    path = h.optString("path").IfBlankNull(); hostHeader = h.optString("host").IfBlankNull();
                }
                break;
            case "xhttp":
            case "splithttp":
                if ((st.optJSONObject("xhttpSettings") ?? st.optJSONObject("splithttpSettings")) is { } x)
                {
                    path = x.optString("path").IfBlankNull(); hostHeader = x.optString("host").IfBlankNull();
                    mode = x.optString("mode").IfBlankNull();
                }
                break;
            case "grpc":
                if (st.optJSONObject("grpcSettings") is { } g) serviceName = g.optString("serviceName").IfBlankNull();
                break;
            case "kcp":
            case "mkcp":
                if (st.optJSONObject("kcpSettings") is { } k)
                {
                    headerType = k.optJSONObject("header")?.optString("type").IfBlankNull();
                    seed = k.optString("seed").IfBlankNull();
                }
                break;
            case "tcp":
            case "raw":
                if ((st.optJSONObject("tcpSettings") ?? st.optJSONObject("rawSettings")) is { } tcp)
                {
                    var hdr = tcp.optJSONObject("header");
                    headerType = hdr?.optString("type").IfBlankNull();
                    if (hdr?.optJSONObject("request") is { } req)
                    {
                        path = req.optJSONArray("path")?.optString(0).IfBlankNull();
                        hostHeader = req.optJSONObject("headers")?.optJSONArray("Host")?.optString(0).IfBlankNull();
                    }
                }
                break;
        }

        return new ServerProfile
        {
            protocol = protocol,
            remarks = cfg.optString("remarks").IfBlank(address),
            address = address.RemoveSurrounding("[", "]"),
            port = port,
            credential = credential,
            method = method,
            flow = flow,
            security = security,
            sni = sni,
            fingerprint = fingerprint,
            alpn = alpn,
            allowInsecure = allowInsecure,
            network = network,
            path = path,
            hostHeader = hostHeader,
            serviceName = serviceName,
            headerType = headerType,
            mode = mode,
            seed = seed,
            publicKey = publicKey,
            shortId = shortId,
            spiderX = spiderX,
            raw = "json-config",
        };
    }
}
