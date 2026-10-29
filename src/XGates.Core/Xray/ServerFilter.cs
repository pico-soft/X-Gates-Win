using XGates.Core.Compat;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Xray.Link;

namespace XGates.Core.Xray;

/**
 * ЕДИНАЯ точка всех отсевов сервера: живость (пинг), минимальная скорость, протокол и стоп-лист.
 * Через неё обязаны идти список «Живые», секция «Все», счётчики N, автоподключение и апгрейд —
 * ни одного условия «по месту».
 *
 * РАЗНИЦА двух отсевов настроек:
 *  - протокол выключают ВРЕМЕННО → числа должны быть готовы к обратному включению → замер идёт по всем;
 *  - блокировка = «не нужен вовсе» → заблокированных НЕ мерят (см. FullTestRunner, фильтр ДО ping/speed),
 *    иначе тратили бы трафик и время прогона впустую.
 * Порт 1:1 с xray/ServerFilter.kt.
 */
public static class ServerFilter
{
    /** Протокол разрешён настройками. */
    public static bool protocolAllowed(ServerProfile p, AppSettings s) => s.allowedProtocols.Contains(p.protocol);

    /**
     * ЧЕТВЁРТЫЙ отсев — стоп-лист: заблокирован по слову в ИМЕНИ или точечно по serverKey.
     * Слово ищем И в исходном имени провайдера, И в пользовательском (D2): иначе переименование
     * молча снимало бы блокировку.
     */
    public static bool isBlocked(ServerProfile p, Blocklist b)
    {
        var key = SubscriptionManager.serverKey(p);
        // Пр.147: временно сняты стоп-СЛОВА → блокировка только по точечному serverKey (слова игнорируем).
        if (BlocklistStore.stopWordsSuspended.value) return b.isServerBlocked(key);
        return b.isBlocked(p.remarks.IfBlank(p.address), b.customName(key), key);
    }

    /** ПЯТЫЙ отсев — «на паузе» (Промпт 91): отдельная причина от стоп-листа. Скрыт из «Живых» и автовыбора. */
    public static bool isPaused(ServerProfile p, Blocklist b) => b.isPaused(SubscriptionManager.serverKey(p));

    /**
     * Виден в «Живые» = РЕАЛЬНО доступен СЕЙЧАС: не заблокирован + не на паузе + протокол разрешён + ЖИВОЙ ПИНГ
     * (pingMs>=0). ТЗ Elyor 2026-10-14: ПИНГ ОБЯЗАТЕЛЕН — сервер без живого пинга в «Живых» НЕ показываем. Прежний
     * эскейп «жив по свежей скорости ≤2ч даже без пинга» (Пр.148) УБРАН: он давал «призраков» с высокой скоростью,
     * но ПУСТЫМ пингом, которые всплывали в списке и путали («сверху быстрые без пинга, ниже медленные с пингом»).
     * Не пингуется → уходит из «Живых» (фоновый пере-пинг раз в 15 мин ставит pingMs=-1). Топ строим по РЕАЛЬНОЙ
     * замеренной скорости (сортировка в MainActivity), а живость гарантирует пинг.
     * ИСКЛЮЧЕНИЕ — АКТИВНЫЙ подключённый сервер: его живость доказана туннелем (в MainActivity добавляется отдельно).
     */
    public static bool isVisible(ServerProfile p, int? pingMs, double? speedMbps, AppSettings s, Blocklist b) =>
        !isBlocked(p, b) && !isPaused(p, b) && protocolAllowed(p, s) && (pingMs != null && pingMs >= 0);

    /** Пригоден для автоподключения/апгрейда: не заблокирован + не на паузе + протокол разрешён + скорость ≥ порога. */
    public static bool isSelectable(ServerProfile p, double? speedMbps, AppSettings s, Blocklist b) =>
        !isBlocked(p, b) && !isPaused(p, b) && protocolAllowed(p, s) && speedMbps != null && speedMbps >= s.minUsableMbps;

    // ═══ ДВА ВИДИМЫХ СПИСКА (ТЗ Elyor 2026-10-15) ═══════════════════════════════════════════════════════════

    /** Момент последнего замера скорости в эпоха-мс из persisted-метки; null — не тестировался/метки нет. */
    public static long? speedMeasuredAtMs(ServerProfile p) =>
        p.speedTestedTs.TakeIfNotBlank() is { } ts ? KDate.parseMs(ts) : null;

    /** Замер считается СВЕЖИМ (список «Живые»), если он сделан не старее окна [freshSec]. null-метка = не свежий. */
    public static bool isSpeedFresh(long? atMs, long nowMs, int freshSec) =>
        atMs != null && atMs <= nowMs && nowMs - atMs <= freshSec.coerceAtLeast(60) * 1000L;

    /** Классификация сервера для видимых списков. */
    public enum Tier { LIVE, PREVIOUS, NONE }

    /**
     * Куда сервер идёт на экране (ТЗ Elyor 2026-10-15 «после полного замера перестраивай видимый топ»):
     *  LIVE     — «Живые серверы»: доступен (не блок/пауза/протокол ок) + ЖИВОЙ ПИНГ (≥0) + измеренная СКОРОСТЬ>0 +
     *             замер СВЕЖИЙ (не старше окна свежести). Это «отозвался и померился».
     *  PREVIOUS — «Ранее измеренные»: то же, но замер СТАРЫЙ (мерили раньше, пинг ещё жив, не переподтвердили).
     *  NONE     — ни в одном списке (только во «Все»): нет живого пинга ЛИБО пинг есть, но скорость не измерена/0
     *             («при последнем замере не показали пинг / показали пинг, но не отдали скорость» — Elyor).
     * [speedFreshAtMs] — момент последнего замера в мс (сессионный или из persisted-метки); null → трактуем как старый.
     * АКТИВНЫЙ подключённый сервер — исключение (его живость доказана туннелем), добавляется в «Живые» вызывающим.
     */
    public static Tier tier(ServerProfile p, int? pingMs, double? speedMbps, long? speedFreshAtMs, long nowMs, AppSettings s, Blocklist b)
    {
        if (isBlocked(p, b) || isPaused(p, b) || !protocolAllowed(p, s)) return Tier.NONE;
        if (pingMs == null || pingMs < 0) return Tier.NONE;                 // нет пинга → только «Все»
        if ((speedMbps ?? 0.0) <= 0.0) return Tier.NONE;                    // пинг-без-скорости → только «Все»
        return isSpeedFresh(speedFreshAtMs, nowMs, s.liveFreshSec) ? Tier.LIVE : Tier.PREVIOUS;
    }
}
