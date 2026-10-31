using XGates.Core.Compat;
using System.Text.Json.Serialization;

namespace XGates.Core.Xray.Link;

/// <summary>Протоколы, которые исполняет наш xray-core. Hysteria2/TUIC сюда НЕ входят (sing-box, будущий этап).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Protocol>))]
public enum Protocol { VLESS, VMESS, TROJAN, SHADOWSOCKS }

/// <summary>Верхнеуровневые функции Kotlin-файла ServerProfile.kt (на JVM — класс ServerProfileKt).
/// Вызывать через <c>using static XGates.Core.Xray.Link.ServerProfileKt;</c> — как в Kotlin, без префикса.</summary>
public static class ServerProfileKt
{
    /// <summary>
    /// streamSettings.security ядра принимает ТОЛЬКО none/tls/reality (наш билдер поддерживает этот набор).
    /// Подписки иногда присылают мусор в этом поле: <c>security=false</c>/<c>0</c> (значит «без TLS»), <c>security=auto</c>
    /// (утёкший шифр vmess) и т.п. → ядро падало «Unknown security "false"/"auto"», сервер не собирался,
    /// не подключался и не мерился. Всё незнакомое трактуем как отсутствие TLS-слоя ("none").
    /// Единый источник правды для билдера конфига и UI. НЕ меняем сохранённый ServerProfile.security
    /// (иначе сдвинется serverKey → dedup/стоп-лист) — нормализуем только на выходе (в конфиг/на экран).
    /// </summary>
    public static string normalizeStreamSecurity(string raw) => raw.KTrim().Lowercase() switch
    {
        "tls" => "tls",
        "reality" => "reality",
        _ => "none",
    };

    /// <summary>
    /// pcs для tlsSettings.pinnedPeerCertSha256: SHA-256 через запятую, можно в формате OpenSSL (AA:BB:…).
    /// Значение не той длины или не hex ядро отвергает вместе с конфигом, поэтому такие выбрасываем: без pin
    /// сертификат проверяется как обычно, а сервер не пропадает из-за опечатки в подписке. null — годных нет.
    /// </summary>
    public static string? normalizePinnedCerts(string? raw)
    {
        if (raw == null) return null;
        var pins = raw.Split(',').Select(it => it.KTrim()).Where(isSha256Hex).ToList();
        return pins.Count > 0 ? string.Join(",", pins) : null;
    }

    private static bool isSha256Hex(string pin)
    {
        var hex = pin.Replace(":", "");
        return hex.Length == 64 && hex.All(Uri.IsHexDigit);
    }
}

/// <summary>
/// Нормализованный сервер: результат разбора одной ссылки, вход для XrayConfigBuilder.
/// Поля — общий знаменатель ss/vless/vmess/trojan; лишние для конкретного протокола остаются null.
/// Сериализуется внутри подписки в subscriptions.json (имена полей — как в Kotlin-версии).
/// </summary>
public sealed record ServerProfile
{
    [JsonPropertyName("protocol")] public required Protocol protocol { get; init; }
    [JsonPropertyName("remarks")] public required string remarks { get; init; }
    [JsonPropertyName("address")] public required string address { get; init; }
    [JsonPropertyName("port")] public required int port { get; init; }

    /// <summary>
    /// Единый секрет сервера. Смысл зависит от протокола:
    ///  - VLESS / VMESS        → UUID пользователя (→ settings.vnext[].users[].id)
    ///  - TROJAN / SHADOWSOCKS → пароль           (→ settings.servers[].password)
    /// Имя намеренно нейтральное: чтобы UUID никогда не лежал в поле с именем «password».
    /// </summary>
    [JsonPropertyName("credential")] public required string credential { get; init; }

    /// <summary>VLESS: "none"; VMESS: шифр (scy, дефолт auto); SHADOWSOCKS: cipher; TROJAN: не используется.</summary>
    [JsonPropertyName("method")] public string? method { get; init; }

    /// <summary>Только VLESS (xtls-vision и т.п.).</summary>
    [JsonPropertyName("flow")] public string? flow { get; init; }

    // --- security ---
    [JsonPropertyName("security")] public string security { get; init; } = "none";     // none | tls | reality
    [JsonPropertyName("sni")] public string? sni { get; init; }
    [JsonPropertyName("fingerprint")] public string? fingerprint { get; init; }
    [JsonPropertyName("alpn")] public string? alpn { get; init; }
    [JsonPropertyName("allowInsecure")] public bool allowInsecure { get; init; }  // только из ссылки: в конфиг не идёт, ядро v26 его убрало
    // Замена allowInsecure в ядре v26: ключи pcs/vcn ссылки. Пустые не сериализуем: профиль и golden остаются как у Android.
    [JsonPropertyName("pinnedPeerCertSha256"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? pinnedPeerCertSha256 { get; init; }
    [JsonPropertyName("verifyPeerCertByName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? verifyPeerCertByName { get; init; }

    // --- транспорт ---
    [JsonPropertyName("network")] public string network { get; init; } = "tcp";       // tcp | raw(=tcp) | ws | grpc | xhttp | httpupgrade | kcp/mkcp
    [JsonPropertyName("path")] public string? path { get; init; }
    [JsonPropertyName("hostHeader")] public string? hostHeader { get; init; }    // ws/xhttp/httpupgrade Host; если пусто — билдер берёт sni (как Python-эталон)
    [JsonPropertyName("serviceName")] public string? serviceName { get; init; }   // grpc
    [JsonPropertyName("headerType")] public string? headerType { get; init; }    // tcp http-header; mkcp тип обфускации заголовка (none/srtp/utp/…)
    [JsonPropertyName("mode")] public string? mode { get; init; }          // xhttp mode (auto/packet-up/stream-up/stream-one); пусто → ядро auto
    [JsonPropertyName("xhttpExtra"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? xhttpExtra { get; init; }   // xhttpSettings.extra, см. XhttpExtra
    [JsonPropertyName("seed")] public string? seed { get; init; }          // mkcp seed (обфускация)

    // --- reality ---
    [JsonPropertyName("publicKey")] public string? publicKey { get; init; }
    [JsonPropertyName("shortId")] public string? shortId { get; init; }
    [JsonPropertyName("spiderX")] public string? spiderX { get; init; }

    /// <summary>Исходная ссылка — для отладки/переэкспорта.</summary>
    [JsonPropertyName("raw")] public string raw { get; init; } = "";

    // --- результаты теста задержки (динамика; backward-compat: дефолты + ignoreUnknownKeys) ---
    /// <summary>Задержка real ping в мс; ≥0 — жив, -1 — мёртвый, null — не тестировался.</summary>
    [JsonPropertyName("pingMs")] public int? pingMs { get; init; }
    /// <summary>Метка времени последнего пинг-замера ("yyyy-MM-dd HH:mm"); null — не тестировался.</summary>
    [JsonPropertyName("lastTestedTs")] public string? lastTestedTs { get; init; }

    /// <summary>Пропускная способность туннеля в Mbps; &gt;0 — скорость, 0 — нет throughput, -1 — ошибка, null — не тестировался.</summary>
    [JsonPropertyName("speedMbps")] public double? speedMbps { get; init; }
    /// <summary>Метка времени последнего замера скорости; null — не тестировался.</summary>
    [JsonPropertyName("speedTestedTs")] public string? speedTestedTs { get; init; }
    /// <summary>Качество последнего замера скорости: true — ПОЛНЫЙ (точный), false — МИНИМАЛЬНЫЙ (быстрый, «≈»),
    /// null — неизвестно/легаси (в UI трактуем как полный).</summary>
    [JsonPropertyName("speedFull")] public bool? speedFull { get; init; }
}
