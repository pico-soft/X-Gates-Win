using System.Text;
using XGates.Core.Compat;

namespace XGates.Core.Xray.Link;

/// <summary>
/// vmess:// + base64(JSON). Принципиально отличается от vless/trojan — НЕ query-ссылка.
/// Эталон — v2rayNG VmessFmt (в Python vmess не было).
///
/// JSON-поля (VmessQRCode): add, port, id, net, scy, aid, type, host, path, tls, sni, fp, alpn, ps.
/// Маппинг из карты: id→credential(UUID), scy→method(шифр), tls→security, net→network.
/// alterId в конфиге всегда 0 (AEAD) — поле aid из ссылки игнорируем.
///
/// Второй, редкий формат <c>vmess://uuid@host?params</c> (query) — пока НЕ поддержан (TODO),
/// но не падаем: помечаем как невалидный и идём дальше.
/// </summary>
public static class VmessParser
{
    public static ServerProfile? parse(string uri)
    {
        var body = uri.RemovePrefix("vmess://").KTrim();

        // Детект query-формата (vmess-std) по наличию ? и & — откладываем, не крашим.
        if (body.Contains('?') && body.Contains('&')) return null;
        // Находка порта №2: ~1.5% реальных ссылок — `vmess://<base64>=?хвост` (без &): хвост ломал Base64 и сервер
        // отбрасывался. Base64-часть — до «?», хвост не нужен (имя — в "ps" JSON).
        var b64 = body.SubstringBefore('?');

        string json;
        try
        {
            var padded = b64 + "=".Repeat((4 - b64.Length % 4) % 4); // паддинг до кратности 4
            json = Encoding.UTF8.GetString(AndroidBase64.Decode(padded, AndroidBase64.DEFAULT));
        }
        catch (Exception)
        {
            return null;
        }

        JSONObject o;
        try { o = new JSONObject(json); } catch (Exception) { return null; }

        var address = o.optString("add").TakeIfNotBlank();
        if (address == null) return null;
        var id = o.optString("id").TakeIfNotBlank();
        if (id == null) return null;
        var port = JSON.ToString(o.opt("port")).ToIntOrNull();
        if (port == null) return null;

        var net = o.optString("net").IfBlank("tcp");
        var tls = o.optString("tls");                         // "" | "tls" | "reality"
        var path = o.optString("path").IfBlankNull();

        return new ServerProfile
        {
            protocol = Protocol.VMESS,
            remarks = o.optString("ps"),
            address = address,
            port = port.Value,
            credential = id,                                  // UUID
            method = o.optString("scy").IfBlank("auto"),      // шифр vmess
            flow = null,
            security = tls.IsBlank() ? "none" : tls,
            sni = o.optString("sni").IfBlankNull(),
            fingerprint = o.optString("fp").IfBlankNull(),
            alpn = o.optString("alpn").IfBlankNull(),
            allowInsecure = false,
            network = net,
            path = path,
            hostHeader = o.optString("host").IfBlankNull(),
            serviceName = net == "grpc" ? path : null,        // grpc: serviceName лежит в path
            headerType = o.optString("type").IfBlankNull(),
            mode = o.optString("mode").IfBlankNull(),         // xhttp mode (vmess-json)
            raw = uri,
        };
    }
}
