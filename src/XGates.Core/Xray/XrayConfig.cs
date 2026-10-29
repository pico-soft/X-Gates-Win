using XGates.Core.Compat;

namespace XGates.Core.Xray;

/// <summary>
/// Порты и общая inbound-часть конфига xray-core.
/// Порты — константы, по коду никаких магических значений.
/// Windows: порты будут настраиваемыми (docs/decisions.md §13.7) — пока константы, как на Android.
/// </summary>
public static class XrayConfig
{
    /// <summary>Локальный SOCKS-inbound.</summary>
    public const int SOCKS_PORT = 10815;

    /// <summary>Локальный HTTP-inbound.</summary>
    public const int HTTP_PORT = 10816;

    /// <summary>Слушаем только петлю — модель локального прокси, без VpnService.</summary>
    public const string LISTEN = "127.0.0.1";

    /// <summary>
    /// Общая inbound-часть: socks на SOCKS_PORT + http на HTTP_PORT, оба на 127.0.0.1.
    /// Переиспользуется билдером (<see cref="XrayConfigBuilder"/>) для любого сервера.
    /// Шаблон — байт в байт Kotlin raw-строка (с её отступами) + <see cref="KotlinText.TrimIndent"/>.
    /// </summary>
    internal static string inbounds() => $$"""
        "inbounds": [
          {
            "tag": "socks-in",
            "listen": "{{LISTEN}}",
            "port": {{SOCKS_PORT}},
            "protocol": "socks",
            "settings": { "auth": "noauth", "udp": true }
          },
          {
            "tag": "http-in",
            "listen": "{{LISTEN}}",
            "port": {{HTTP_PORT}},
            "protocol": "http",
            "settings": {}
          }
        ]
    
""".TrimIndent();
}
