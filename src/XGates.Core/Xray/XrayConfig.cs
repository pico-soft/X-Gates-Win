using XGates.Core.Compat;

namespace XGates.Core.Xray;

/// <summary>
/// Порты и общая inbound-часть конфига xray-core.
/// Порты — без магических значений по коду. Windows: порты настраиваемые (docs/decisions.md §13.7) —
/// читаются из <see cref="Settings.WinSettingsStore"/> на момент использования; по умолчанию — как на Android.
/// </summary>
public static class XrayConfig
{
    public const int DEFAULT_SOCKS_PORT = 10815;
    public const int DEFAULT_HTTP_PORT = 10816;

    /// <summary>Локальный SOCKS-inbound.</summary>
    public static int SOCKS_PORT => Settings.WinSettingsStore.current().socksPort;

    /// <summary>Локальный HTTP-inbound.</summary>
    public static int HTTP_PORT => Settings.WinSettingsStore.current().httpPort;

    /// <summary>Нижняя граница порта: ниже 1024 — системные, Windows их тоже резервирует под службы.</summary>
    public const int MIN_PORT = 1024;

    /// <summary>Ошибка пары портов (текст для пользователя) или null — пара годится.</summary>
    public static string? portsError(int socks, int http)
    {
        if (socks < MIN_PORT || socks > 65535) return $"Порт SOCKS должен быть от {MIN_PORT} до 65535";
        if (http < MIN_PORT || http > 65535) return $"Порт HTTP должен быть от {MIN_PORT} до 65535";
        if (socks == http) return "Порты SOCKS и HTTP должны различаться";
        return null;
    }

    /// <summary>
    /// Свободен ли порт на петле для НАШЕГО прокси (пробная привязка — как это сделает ядро). false — его держит
    /// другая программа: xray не поднимется, а пользователю нужна понятная причина, а не «ядро не запустилось».
    /// </summary>
    public static bool portFree(int port)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

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
