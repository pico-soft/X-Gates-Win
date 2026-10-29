using System.Text;
using System.Text.RegularExpressions;
using XGates.Core.Compat;

namespace XGates.Core.Xray.Link;

/// <summary>
/// Shadowsocks. Эталон — v2rayNG ShadowsocksFmt (в Python ss не было). Два формата:
///  - SIP002:  ss://base64url(method:password)@host:port?plugin=..#remark
///             (user-info может быть base64 ИЛИ plain "method:password")
///  - legacy:  ss://base64(method:password@host:port)#remark
///
/// credential = password, method = cipher (aes-256-gcm / chacha20-poly1305 / …).
/// Обычно без tls и транспорта — чистый tcp. plugin=obfs пока не поддержан (отложено, не падаем).
/// </summary>
public static partial class ShadowsocksParser
{
    public static ServerProfile? parse(string uri) =>
        parseSip002(uri) ?? parseLegacy(uri);

    /// <summary>ss://[base64|plain method:password]@host:port#remark</summary>
    private static ServerProfile? parseSip002(string str)
    {
        JavaUri u;
        try { u = new JavaUri(str); } catch (Exception) { return null; }
        var host = u.Host.TakeIfNotBlank();
        if (host == null) return null;
        if (u.Port <= 0) return null;
        var userInfo = u.UserInfo.TakeIfNotBlank();
        if (userInfo == null) return null;

        // user-info: plain "method:password" (есть ':') ИЛИ base64(method:password)
        string[] parts;
        if (userInfo.Contains(':'))
        {
            parts = userInfo.Split(':', 2);
        }
        else
        {
            var decoded = decodeBase64(userInfo);
            if (decoded == null) return null;
            parts = decoded.Split(':', 2);
        }
        if (parts.Length != 2) return null;

        return build(
            remarks: u.Fragment ?? "",
            host: host,
            port: u.Port,
            method: parts[0],
            password: parts[1],
            raw: str);
    }

    // Kotlin/Java: «.» — любой символ, кроме терминаторов строки Java (\n \r U+0085 U+2028 U+2029); «\d» — только [0-9];
    // matchEntire — вся строка.
    [GeneratedRegex("\\A([^\\n\\r\\u0085\\u2028\\u2029]+?):([^\\n\\r\\u0085\\u2028\\u2029]*)@([^\\n\\r\\u0085\\u2028\\u2029]+?):([0-9]+?)/?\\z")]
    private static partial Regex LegacyBody();

    /// <summary>ss://base64(method:password@host:port)#remark</summary>
    private static ServerProfile? parseLegacy(string str)
    {
        var result = str.RemovePrefix("ss://");

        var remarks = "";
        var hashIdx = result.IndexOf('#');
        if (hashIdx >= 0)
        {
            remarks = LinkUtil.urlDecode(result.Substring(hashIdx + 1));
            result = result.Substring(0, hashIdx);
        }

        // Декодируем только левую часть (до @), если @ уже в открытом виде; иначе — всю строку.
        var atIdx = result.IndexOf('@');
        if (atIdx > 0)
        {
            var left = decodeBase64(result.Substring(0, atIdx));
            if (left == null) return null;
            result = left + result.Substring(atIdx);
        }
        else
        {
            var all = decodeBase64(result);
            if (all == null) return null;
            result = all;
        }

        var m = LegacyBody().Match(result);
        if (!m.Success) return null;
        string method = m.Groups[1].Value, password = m.Groups[2].Value, host = m.Groups[3].Value, portStr = m.Groups[4].Value;
        var port = portStr.ToIntOrNull();
        if (port == null) return null;

        return build(remarks, host, port.Value, method.Lowercase(), password, str);
    }

    private static ServerProfile? build(string remarks, string host, int port, string method, string password, string raw)
    {
        // Без cipher/пароля профиль бессмысленен → Invalid на парсинге (лучше, чем "method":"").
        if (method.IsBlank() || password.IsBlank()) return null;
        return new ServerProfile
        {
            protocol = Protocol.SHADOWSOCKS,
            remarks = remarks,
            address = host.RemoveSurrounding("[", "]"),
            port = port,
            credential = password,     // пароль
            method = method,           // cipher
            security = "none",
            network = "tcp",           // чистый tcp; plugin=obfs отложен
            raw = raw,
        };
    }

    /// <summary>base64 c нормализацией url-safe (-_ → +/) и дополнением паддинга; null при ошибке.</summary>
    private static string? decodeBase64(string s)
    {
        try
        {
            var norm = s.Replace('-', '+').Replace('_', '/');
            var padded = norm + "=".Repeat((4 - norm.Length % 4) % 4);
            return Encoding.UTF8.GetString(AndroidBase64.Decode(padded, AndroidBase64.NO_WRAP));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
