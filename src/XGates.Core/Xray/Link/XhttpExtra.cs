using XGates.Core.Compat;

namespace XGates.Core.Xray.Link;

/// <summary>
/// xhttpSettings.extra (XMUX, паддинг, downloadSettings) из ссылки <c>extra=</c> или JSON-конфига подписки.
/// Подписка — чужие данные, а в конфиг ядра это идёт JSON-ом, поэтому берём только перечисленные поля и только после
/// разбора. Весь StreamConfig из downloadSettings пускать нельзя: masterKeyLog пишет файл, certificates читает файлы,
/// sockopt меняет маршрут. Список — по SplitHTTPConfig ядра v26.7.28 (при обновлении ядра сверить).
/// </summary>
internal static class XhttpExtra
{
    private static readonly HashSet<string> XHTTP_FIELDS =
    [
        "headers", "xPaddingBytes", "xPaddingObfsMode", "xPaddingKey", "xPaddingHeader", "xPaddingPlacement", "xPaddingMethod",
        "uplinkHTTPMethod", "sessionIDPlacement", "sessionIDKey", "sessionIDTable", "sessionIDLength", "seqPlacement", "seqKey",
        "uplinkDataPlacement", "uplinkDataKey", "uplinkChunkSize", "noGRPCHeader", "noSSEHeader", "scMaxEachPostBytes",
        "scMinPostsIntervalMs", "scMaxBufferedPosts", "scStreamUpServerSecs", "xmux",
    ];
    private static readonly HashSet<string> DOWNLOAD_FIELDS = ["address", "port", "network", "security"];
    private static readonly HashSet<string> TLS_FIELDS = ["serverName", "alpn", "fingerprint", "pinnedPeerCertSha256", "verifyPeerCertByName"];
    private static readonly HashSet<string> REALITY_FIELDS = ["serverName", "fingerprint", "publicKey", "password", "shortId", "spiderX", "mldsa65Verify"];
    private static readonly HashSet<string> DOWNLOAD_XHTTP_FIELDS = [.. XHTTP_FIELDS, "path", "host", "mode"];

    /// <summary>Разрешённое подмножество как JSON-объект; null — не JSON-объект или брать нечего.</summary>
    public static string? sanitize(string? raw)
    {
        if (raw.IsBlank()) return null;
        JSONObject src;
        try { src = new JSONObject(raw!); } catch (Exception) { return null; }
        var res = pick(src, XHTTP_FIELDS);
        if (res.has("headers")) res.put("headers", withoutHost(res.optJSONObject("headers")));
        if (src.optJSONObject("downloadSettings") is { } d && download(d) is { } safe) res.put("downloadSettings", safe);
        return res.length() > 0 ? res.ToString() : null;
    }

    /// <summary>Значение extra для конфига. stream-one с downloadSettings ядро отвергает, поэтому такой блок убираем.</summary>
    public static string? forConfig(string? saved, string? mode)
    {
        if (sanitize(saved) is not { } safe) return null;
        if (mode != "stream-one") return safe;
        var o = new JSONObject(safe);
        var res = pick(o, XHTTP_FIELDS);
        return res.length() > 0 ? res.ToString() : null;
    }

    private static JSONObject pick(JSONObject src, HashSet<string> allowed)
    {
        var res = new JSONObject();
        foreach (var k in src.keys())
            if (allowed.Contains(k)) res.put(k, src.opt(k));
        return res;
    }

    /// <summary>Заголовок Host ядро в headers не принимает: конфиг не загрузится.</summary>
    private static JSONObject withoutHost(JSONObject? headers)
    {
        var res = new JSONObject();
        if (headers == null) return res;
        foreach (var k in headers.keys())
            if (!k.Equals("host", StringComparison.OrdinalIgnoreCase)) res.put(k, headers.opt(k));
        return res;
    }

    /// <summary>downloadSettings без адреса и порта отбрасываем: конфиг ядро примет, а на первом соединении упадёт (panic).</summary>
    private static JSONObject? download(JSONObject d)
    {
        if (d.optString("address").IsBlank() || d.optInt("port") <= 0) return null;
        var res = pick(d, DOWNLOAD_FIELDS);
        if (d.optJSONObject("tlsSettings") is { } tls) res.put("tlsSettings", pick(tls, TLS_FIELDS));
        if (d.optJSONObject("realitySettings") is { } reality) res.put("realitySettings", pick(reality, REALITY_FIELDS));
        if ((d.optJSONObject("xhttpSettings") ?? d.optJSONObject("splithttpSettings")) is { } x)
        {
            var xhttp = pick(x, DOWNLOAD_XHTTP_FIELDS);
            if (xhttp.has("headers")) xhttp.put("headers", withoutHost(xhttp.optJSONObject("headers")));
            res.put("xhttpSettings", xhttp);
        }
        return res;
    }
}
