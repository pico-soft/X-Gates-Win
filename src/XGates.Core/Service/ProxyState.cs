using XGates.Core.Platform;

namespace XGates.Core.Service;

/// <summary>Снимок состояния прокси для UI. Порт 1:1 с service/ProxyState.kt.</summary>
public sealed record ProxyStatus(
    bool running = false,
    string? label = null,      // remark/адрес активного сервера — ТОЛЬКО для показа
    string? serverKey = null,  // полная идентичность активного профиля — для подсветки (не label!)
    string message = "idle",   // человекочитаемый статус (ошибка/этап); порты — отдельно
    bool socksOk = false,      // локальный SOCKS-сокет слушает
    bool httpOk = false,       // локальный HTTP-сокет слушает
    // ТЗ Elyor «Только Wi-Fi»: туннель НАМЕРЕННО остановлен на мобильной сети и сам поднимется на Wi-Fi.
    // Отличается от «остановлено» (пользователь нажал Стоп) — сервис ЖИВ и ждёт Wi-Fi. running=false при этом.
    bool paused = false);

/**
 * Общее состояние прокси между сервисом (на Windows — фоновый контроллер приложения) и UI.
 * Один процесс → обычный singleton со StateFlow, без binding/broadcast.
 */
public static class ProxyState
{
    private static readonly MutableStateFlow<ProxyStatus> _state = new(new ProxyStatus());
    public static StateFlow<ProxyStatus> state => _state.asStateFlow();

    /** serverKey пишется АТОМАРНО вместе с running (одним ProxyStatus) — без промежуточных состояний. */
    public static void update(bool running, string? label, string? serverKey, string message,
        bool socksOk = false, bool httpOk = false, bool paused = false)
    {
        _state.value = new ProxyStatus(running, label, serverKey, message, socksOk, httpOk, paused);
    }
}
