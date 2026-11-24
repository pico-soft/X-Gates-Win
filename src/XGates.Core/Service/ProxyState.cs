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
    /// <summary>Владелец результата пробы: и сервер, и конкретный запуск ядра. Рестарт того же сервера — новое поколение.</summary>
    public sealed record ProbeToken(string serverKey, long generation);

    private static readonly MutableStateFlow<ProxyStatus> _state = new(new ProxyStatus());
    private static readonly object ProbeSync = new();
    private static long tunnelGeneration;
    public static StateFlow<ProxyStatus> state => _state.asStateFlow();

    /** serverKey пишется АТОМАРНО вместе с running (одним ProxyStatus) — без промежуточных состояний. */
    public static void update(bool running, string? label, string? serverKey, string message,
        bool socksOk = false, bool httpOk = false, bool paused = false)
    {
        _state.value = new ProxyStatus(running, label, serverKey, message, socksOk, httpOk, paused);
    }

    /// <summary>Новый запрос старта/стопа ядра делает все незавершённые пробы прежнего туннеля устаревшими.</summary>
    internal static long advanceTunnelGeneration()
    {
        lock (ProbeSync) return ++tunnelGeneration;
    }

    /// <summary>Токен действителен только для работающего туннеля с именованным сервером.</summary>
    public static ProbeToken? probeToken()
    {
        lock (ProbeSync)
        {
            var s = _state.value;
            return s.running && s.serverKey != null ? new ProbeToken(s.serverKey, tunnelGeneration) : null;
        }
    }

    public static bool isCurrent(ProbeToken? token)
    {
        if (token == null) return true;   // прямые вызовы тестов/временного SOCKS без активного ProxyState
        lock (ProbeSync)
        {
            var s = _state.value;
            return s.running && s.serverKey == token.serverKey && tunnelGeneration == token.generation;
        }
    }

    /// <summary>Атомарно с инвалидацией поколения: опоздавший сетевой ответ не меняет статус нового туннеля.</summary>
    public static bool ifCurrent(ProbeToken? token, Action action)
    {
        if (token == null) { action(); return true; }
        lock (ProbeSync)
        {
            var s = _state.value;
            if (!s.running || s.serverKey != token.serverKey || tunnelGeneration != token.generation) return false;
            action();
            return true;
        }
    }
}
