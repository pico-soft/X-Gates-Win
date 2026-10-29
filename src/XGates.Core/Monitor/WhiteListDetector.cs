using System.Net;
using System.Net.Sockets;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Xray;

namespace XGates.Core.Monitor;

/**
 * Пр.140: РЕЖИМ БЕЛЫХ СПИСКОВ РФ. Часть сетей пускает только «белый список» (домены из реестра) — тогда
 * yandex.ru открыт, а google.com нет, и обычные (зарубежные) серверы не поднимаются. Детектор это распознаёт
 * и переводит приложение на источники белого списка; раз в полчаса проверяет возврат google → обычный режим.
 *
 * Живость/пинг это НЕ трогает (Пр.139). Эффективный список серверов сужается в [SubscriptionManager.allServers]
 * по флагу [whiteListModeActive] — enabled-флаги пользователя НЕ меняем. Порт 1:1 с monitor/WhiteListDetector.kt.
 */
public static class WhiteListDetector
{
    /// <summary>Итог оценки — ЧИСТАЯ функция входов (для юнит-тестов; без сети/настроек).</summary>
    public sealed record Decision(bool active, bool manualNormal, string? @event);

    /**
     * @param auto фича включена (whiteListAutoEnabled)
     * @param manualNormal пользователь выбрал «Обычный» на главной (держим обычный, пока google не вернётся)
     * @param active сейчас активен режим белых списков
     * @param googleOk google доступен (через туннель ИЛИ напрямую) — зарубеж работает
     * @param yandexOk yandex доступен напрямую — домашний сегмент жив
     */
    public static Decision decide(bool auto, bool manualNormal, bool active, bool googleOk, bool yandexOk)
    {
        if (!auto) return new Decision(active: false, manualNormal: false, @event: active ? "Режим белых списков выключен в настройках — обычный режим" : null);
        if (googleOk) return new Decision(active: false, manualNormal: false,
            @event: active ? "Google снова доступен — возврат в обычный режим" : null);
        if (yandexOk && !manualNormal) return new Decision(active: true, manualNormal: false,
            @event: !active ? "Google недоступен, Yandex открыт — переход в режим белых списков" : null);
        if (yandexOk && manualNormal) return new Decision(active: false, manualNormal: true, @event: null);   // юзер держит обычный
        return new Decision(active: active, manualNormal: manualNormal, @event: null);                       // и то и то мертво = общий обрыв, не трогаем
    }

    private const int PROBE_TIMEOUT_MS = 4000;
    private static readonly (string h, int p)[] GOOGLE_HOSTS = [("www.google.com", 443), ("google.com", 443)];
    private static readonly (string h, int p)[] YANDEX_HOSTS = [("ya.ru", 443), ("yandex.ru", 443), ("77.88.8.8", 53)];

    /// <summary>Прямой TCP-коннект (приложение работает через ЛОКАЛЬНЫЙ SOCKS, не VPN → маршрут ОС = физическая сеть).</summary>
    private static bool tcpOk(string host, int port)
    {
        try
        {
            using var s = new TcpClient();
            return s.ConnectAsync(host, port).Wait(PROBE_TIMEOUT_MS) && s.Connected;
        }
        catch (Exception) { return false; }
    }

    private static bool anyOk((string h, int p)[] hosts) => hosts.Any(t => tcpOk(t.h, t.p));

    /**
     * Оценить сеть и применить решение. [tunnelForeignOk] — зарубеж уже подтверждён через туннель в этом цикле
     * (ExternalIpChecker вернул IP); тогда google считаем доступным без отдельной пробы. БЛОКИРУЮЩАЯ (фон).
     * Возвращает true, если режим ПЕРЕКЛЮЧИЛСЯ (вызывающему стоит пересобрать выбор сервера).
     */
    public static bool evaluate(Context context, bool tunnelForeignOk)
    {
        var app = context.applicationContext;
        var s = SettingsStore.current();
        if (!s.whiteListAutoEnabled)
        {
            if (s.whiteListModeActive || s.whiteListManualNormal)
                SettingsStore.update(app, s with { whiteListModeActive = false, whiteListManualNormal = false });
            return false;
        }
        var googleOk = tunnelForeignOk || anyOk(GOOGLE_HOSTS) ||
            (CascadeFetch.isOwnProxyUp() && ExternalIpChecker.fetch() != null);
        var yandexOk = anyOk(YANDEX_HOSTS);
        var d = decide(s.whiteListAutoEnabled, s.whiteListManualNormal, s.whiteListModeActive, googleOk, yandexOk);
        var changed = d.active != s.whiteListModeActive;
        if (d.active != s.whiteListModeActive || d.manualNormal != s.whiteListManualNormal)
            SettingsStore.update(app, s with { whiteListModeActive = d.active, whiteListManualNormal = d.manualNormal });
        if (d.@event != null) MonitorLog.@event(app, "net", d.@event, $"google={(googleOk ? "ок" : "нет")}, yandex={(yandexOk ? "ок" : "нет")}");
        if (changed) MonitorCoordinator.wake();
        return changed;
    }

    /// <summary>Пользователь на главной нажал «Обычный режим» при активном белом. Держим обычный до возврата google/смены сети.</summary>
    public static void userForceNormal(Context context)
    {
        var app = context.applicationContext;
        var s = SettingsStore.current();
        SettingsStore.update(app, s with { whiteListModeActive = false, whiteListManualNormal = true });
        MonitorLog.@event(app, "net", "Пользователь выбрал обычный режим (из белого)", "");
        MonitorCoordinator.wake();
    }

    /// <summary>Смена сети → свежая оценка: снять ручной «держать обычный», чтобы новая сеть переоценилась с нуля.</summary>
    public static void onNetworkChange(Context context)
    {
        var s = SettingsStore.current();
        if (s.whiteListManualNormal) SettingsStore.update(context.applicationContext, s with { whiteListManualNormal = false });
    }
}
