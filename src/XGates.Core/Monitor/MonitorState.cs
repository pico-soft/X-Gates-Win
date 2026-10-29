using XGates.Core.Compat;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Xray.Link;

namespace XGates.Core.Monitor;

// Порт 1:1 мелких файлов monitor/: MonitorStatus.kt, MonitorPrompt.kt, TunnelHealth.kt, TunnelSpeed.kt, ServerLabels.kt.

/**
 * Признак жизни монитора для ШАПКИ секции (не журнал!): время последней проверки, текущее состояние,
 * сколько циклов прошло. Обновляется на месте каждый цикл, в журнал НЕ пишется (иначе забьёт рутиной).
 * В памяти (не persist) — это индикатор «жив сейчас», после перезапуска обнуляется, и это правильно.
 */
public sealed record MonitorHeartbeat(bool enabled = false, string state = "—", long lastCheckMs = 0L, int cycles = 0);

public static class MonitorStatus
{
    private static readonly MutableStateFlow<MonitorHeartbeat> _state = new(new MonitorHeartbeat());
    public static StateFlow<MonitorHeartbeat> state => _state.asStateFlow();

    public static void update(bool enabled, string state, long lastCheckMs, int cycles) =>
        _state.value = new MonitorHeartbeat(enabled, state, lastCheckMs, cycles);

    public static void setEnabled(bool enabled) => _state.update(s => s with { enabled = enabled });
}

/// <summary>Предложение пользователю включить выключенные источники (пункт E). НЕ включаем сами — решение за ним.</summary>
public sealed record EnableSourcesPrompt(int sources, int servers);

/**
 * Состояние предложения «включить выключенные источники». Монитор ([NetworkMonitor]) выставляет запрос,
 * UI показывает диалог; сервис дублирует уведомлением (если свёрнуто). [declined] —
 * пользователь отказался; повторно не спрашиваем до следующего явного провала (сбрасывается при
 * восстановлении/успешном переключении).
 */
public static class MonitorPrompt
{
    private static readonly MutableStateFlow<EnableSourcesPrompt?> _state = new(null);
    public static StateFlow<EnableSourcesPrompt?> state => _state.asStateFlow();
    private static volatile bool _declined;
    public static bool declined => _declined;
    public static bool pending => _state.value != null;
    public static void request(int sources, int servers) => _state.value = new EnableSourcesPrompt(sources, servers);
    public static void clear() => _state.value = null;
    public static void decline() { _state.value = null; _declined = true; }
    public static void resetDeclined() => _declined = false;
}

/**
 * Промпт 95 — ЕДИНЫЙ ФАКТ-СТАТУС ТУННЕЛЯ. ОСНОВНОЕ ТРЕБОВАНИЕ приложения: либо держим рабочую связь, либо
 * прямо говорим, что рабочих серверов нет. Третьего состояния (молчаливое «соединён, но не работает») НЕТ.
 *
 * ЖИВОСТЬ = ФАКТ ПРОХОЖДЕНИЯ ТРАФИКА (реальный запрос через SOCKS вернул внешний IP), а НЕ флаг/слушающий порт/
 * «ядро запущено». Это состояние — единственный источник, из которого UI решает «зелёный/не зелёный», и по
 * которому запускается восстановление. Обновляется и по расписанию, и по событиям (см. NetworkMonitor).
 */
public static class TunnelHealth
{
    public enum Phase
    {
        UNKNOWN,      // ещё не проверяли (прокси только поднялся)
        VERIFYING,    // идёт проверка связи
        OK,           // ФАКТ: запрос прошёл (есть внешний IP) — ТОЛЬКО это = зелёный
        RECOVERING,   // связи нет — идёт лестница восстановления (перезапуск/перебор/обновление)
        NO_SERVERS,   // лестница пройдена, рабочих серверов нет
        NO_INTERNET,  // нет интернета вообще (прямой канал недоступен) — туннель не виноват
    }

    public sealed record State(
        Phase phase = Phase.UNKNOWN,
        string ip = "",           // внешний IP последней УСПЕШНОЙ проверки (факт)
        long lastOkMs = 0L,       // когда связь в последний раз подтверждена фактом
        long lastCheckMs = 0L,    // когда последний раз проверяли
        string detail = "",       // что происходит сейчас (перебор 3/10, обновляю подписки…)
        // Промпт 123.B/C: serverKey сервера, для которого подтверждён IP. Внешний адрес и «зелёный»
        // ПРИНАДЛЕЖАТ этому серверу: сменился активный serverKey — прежнее подтверждение (ip/OK) НЕ его,
        // UI не показывает ни зелёный, ни чужой адрес. Пустой = ещё не подтверждали ни для кого.
        string serverKey = "");

    private static readonly MutableStateFlow<State> _state = new(new State());
    public static StateFlow<State> state => _state.asStateFlow();

    /// <summary>ФАКТ подтверждён: запрос прошёл, есть внешний IP — для сервера [serverKey]. Единственный путь к «зелёному».</summary>
    public static void ok(string ip, long nowMs, string serverKey) => _state.value = new State(Phase.OK, ip, nowMs, nowMs, "", serverKey);

    /**
     * Промпт 123.C: подтверждена ли связь ИМЕННО для [activeKey] И не позже [freshMs] назад. Зелёный только тут:
     * ФАКТ прохождения запроса через туннель ЭТОГО сервера, свежий. Устарело/сменился сервер → не зелёный.
     */
    public static bool isConfirmedFor(string? activeKey, long nowMs, long freshMs)
    {
        var s = _state.value;
        return s.phase == Phase.OK && activeKey != null && s.serverKey == activeKey && (nowMs - s.lastOkMs) <= freshMs;
    }

    /// <summary>Сменить фазу (проверка/восстановление/нет серверов/нет интернета), сохранив последний ОК-IP/время.</summary>
    public static void setPhase(Phase p, long nowMs, string detail = "") =>
        _state.update(s => s with { phase = p, lastCheckMs = nowMs, detail = detail });

    /// <summary>Прокси остановлен — статус неизвестен (не зелёный).</summary>
    public static void reset() => _state.value = new State();

    public static State snapshot() => _state.value;
}

/**
 * ЖИВАЯ скорость интернета для цветной плашки (НЕ «скорость, с которой соединился при подборе»). Две группы:
 *  • НАПРЯМУЮ — скорость прямого канала (без туннеля), только скачивание ↓.
 *  • В ТУННЕЛЕ — скорость через активный прокси: скачивание ↓ и отдача ↑.
 *
 * ГИБРИД (решение Elyor): когда идёт реальный трафик пользователя — ТУННЕЛЬНУЮ скорость берём БЕСПЛАТНО из
 * счётчиков ядра (дельта туннельных байт) → source=LIVE (прямую при этом сохраняем от последней пробы). Когда
 * простой — раз в несколько минут одна активная проба меряет ВСЁ: прямой канал + туннель ↓/↑ → source=PROBE.
 * Заполняется из [NetworkMonitor]. UI берёт ТОЛЬКО отсюда + время замера.
 *
 * Единицы — Мбит/с ВЕЗДЕ (без смешения КБ/МБ). measuredAtMs — wall-clock (System.currentTimeMillis) для «N мин назад».
 */
public static class TunnelSpeed
{
    public enum Source
    {
        NONE,   // ещё не замеряли (только подключились)
        LIVE,   // туннель из реального трафика пользователя — бесплатно
        PROBE,  // активная проба в простое — стоит трафика
    }

    public sealed record Speed(
        double? directDownMbps = null,   // НАПРЯМУЮ ↓ (прямой канал, без туннеля); null = не замерено
        double? tunnelDownMbps = null,   // В ТУННЕЛЕ ↓; null = не замерено
        double? tunnelUpMbps = null,     // В ТУННЕЛЕ ↑; null = не замерено
        long measuredAtMs = 0L,          // wall-clock последнего замера (0 = не было)
        Source source = Source.NONE,
        // Промпт 123.B: serverKey сервера, чья это скорость. Сменился активный — числа НЕ его, UI их не показывает.
        string serverKey = "",
        // ЖИВОЙ пинг активного (мс; -1/✗; null неизвестно) — монитор ставит КАЖДЫЙ цикл (через активный туннель). Плашка
        // берёт пинг ОТСЮДА, а не из сессионного pingResults (тот затеняет свежий монитора старым -1 из полного теста).
        int? pingMs = null,
        long pingAtMs = 0L);             // wall-clock живого пинга (для «N сек/мин назад» на плашке)

    private static readonly MutableStateFlow<Speed> _state = new(new Speed());
    public static StateFlow<Speed> state => _state.asStateFlow();

    /// <summary>Активная проба: свежие прямой канал + туннель ↓/↑ для сервера [serverKey]. Живой пинг сохраняем (если тот же сервер).</summary>
    public static void setProbe(double? directDownMbps, double? tunnelDownMbps, double? tunnelUpMbps, long nowMs, string serverKey) =>
        _state.update(s =>
        {
            var sameSrv = s.serverKey == serverKey;
            return new Speed(directDownMbps, tunnelDownMbps, tunnelUpMbps, nowMs, Source.PROBE, serverKey,
                sameSrv ? s.pingMs : null, sameSrv ? s.pingAtMs : 0L);
        });

    /// <summary>Живой пинг активного (мс; -1 ✗; null неизвестно) + время — монитор ставит каждый цикл. Смена сервера сбрасывает прочие поля.</summary>
    public static void setPing(int? pingMs, long nowMs, string serverKey) =>
        _state.update(s => s.serverKey == serverKey || s.serverKey.Length == 0
            ? s with { pingMs = pingMs, pingAtMs = nowMs, serverKey = serverKey }
            : new Speed(pingMs: pingMs, pingAtMs: nowMs, serverKey: serverKey));

    /// <summary>Живой трафик: обновляем только ТУННЕЛЬ для сервера [serverKey], прямую сохраняем от прошлой пробы.</summary>
    public static void setLive(double? tunnelDownMbps, double? tunnelUpMbps, long nowMs, string serverKey) =>
        _state.update(s => s with { tunnelDownMbps = tunnelDownMbps, tunnelUpMbps = tunnelUpMbps, measuredAtMs = nowMs, source = Source.LIVE, serverKey = serverKey });

    /// <summary>Прокси остановлен / статус сброшен — живой скорости нет.</summary>
    public static void clear() => _state.value = new Speed();

    public static Speed snapshot() => _state.value;
}

/**
 * Отображаемое имя сервера для ФОНОВЫХ модулей (журнал, монитор, полный тест) — читает оверрайд из
 * [BlocklistStore.current]. UI использует собранный StateFlow ради обновления; здесь снимок на момент вызова.
 * Единая семантика: пользовательское имя или провайдера.
 */
public static class ServerLabels
{
    public static string display(ServerProfile p) =>
        BlocklistStore.current().customName(SubscriptionManager.serverKey(p)) ?? p.remarks.IfBlank(p.address);

    /// <summary>Имя по serverKey (ищем профиль в реестре). null — сервера уже нет / ключ null.</summary>
    public static string? displayForKey(Context context, string? key)
    {
        if (key == null) return null;
        var p = SubscriptionManager.allServers(context).FirstOrDefault(it => SubscriptionManager.serverKey(it) == key);
        return p == null ? null : display(p);
    }

    /// <summary>Verbose-метка для уведомления/трея: «proto · имя · addr:port».</summary>
    public static string full(ServerProfile p) => $"{p.protocol}  ·  {display(p)}  ·  {p.address}:{p.port}";
}
