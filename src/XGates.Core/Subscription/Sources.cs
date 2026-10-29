using System.Text.Json.Serialization;
using XGates.Core.Xray.Link;

namespace XGates.Core.Subscription;

// Порт 1:1 с subscription/Sources.kt (data class — tools/port/kt2cs_dataclass.py).

/**
 * Исход последней ПОПЫТКИ обновления источника (Промпт 81.A/C). Не «ok: Boolean» — 429 и отложенность
 * это НЕ поломка, и красным их показывать неверно: панель жива, просто ограничивает частоту.
 */
[JsonConverter(typeof(JsonStringEnumConverter<SourceOutcome>))]
public enum SourceOutcome
{
    OK,            // успешно загружено
    ERROR,         // реальная неудача (сеть/парсинг/40x-кроме-429)
    RATE_LIMITED,  // HTTP 429 — панель ограничивает частоту, не поломка; смена маршрута не помогает
}

/**
 * Источник серверов (подписка по URL или локальная вставка). ТОЛЬКО метаданные — серверы лежат
 * в общем реестре [ServerRecord] (склеены по serverKey между источниками), не вложены сюда.
 *
 * @param id            стабильный уникальный id (UUID) — членство сервера ссылается на него
 * @param url           адрес подписки; ПУСТОЙ для локальных источников (вставка/файл)
 * @param enabled       выключенный источник не даёт серверов и не обновляется
 * @param lastRefreshTs время ПОСЛЕДНЕЙ ПОПЫТКИ обновления ("yyyy-MM-dd HH:mm")
 * @param lastOkTs      время последней УСПЕШНОЙ загрузки (Промпт 81.C — отдельно от попытки)
 * @param lastOk        итог последней попытки: true/false/null (ни разу) — оставлен для совместимости
 * @param lastOutcome   типизированный исход последней попытки (Промпт 81) — для цвета точки и текста
 * @param lastError     текст ошибки последней НЕУДАЧНОЙ попытки; ОЧИЩАЕТСЯ при первой успешной (81.C)
 * @param retryAfterSec для RATE_LIMITED — через сколько секунд можно повторить (заголовок Retry-After)
 * @param serverCount   сколько серверов принадлежит этому источнику (денормализовано, пересчёт при записи)
 */
public sealed record SubSource
{
    [JsonPropertyName("id")] public required string id { get; init; }
    [JsonPropertyName("name")] public required string name { get; init; }
    [JsonPropertyName("url")] public string url { get; init; } = "";
    [JsonPropertyName("enabled")] public bool enabled { get; init; } = true;
    [JsonPropertyName("lastRefreshTs")] public string? lastRefreshTs { get; init; } = null;
    [JsonPropertyName("lastOkTs")] public string? lastOkTs { get; init; } = null;
    [JsonPropertyName("lastOk")] public bool? lastOk { get; init; } = null;
    [JsonPropertyName("lastOutcome")] public SourceOutcome? lastOutcome { get; init; } = null;
    [JsonPropertyName("lastError")] public string? lastError { get; init; } = null;  // суть ошибки последней НЕУДАЧИ (очищается при успехе)
    [JsonPropertyName("lastDetail")] public string? lastDetail { get; init; } = null;  // диагностика под тапом (URL/код/байты/тело-200/исключение)
    [JsonPropertyName("retryAfterSec")] public int? retryAfterSec { get; init; } = null;  // для RATE_LIMITED (сколько ждать)
    [JsonPropertyName("serverCount")] public int serverCount { get; init; } = 0;
    // Пр.140: источник «белого списка РФ» (Vless-Reality-White-Lists…). Такие держим в общем списке ВЫКЛ по
    // умолчанию, авто-обновляем раз в полчаса и используем ТОЛЬКО в режиме белых списков (когда сеть пускает
    // только домены из белого списка: yandex открыт, google — нет). Помечаем, чтобы отличать от обычных подписок.
    [JsonPropertyName("whiteList")] public bool whiteList { get; init; } = false;
}

/**
 * Запись сервера в общем реестре. Один serverKey = одна запись, даже если сервер пришёл из
 * нескольких источников. [sourceIds] — СПИСОК источников (не одно поле): при удалении одной
 * подписки сервер, присутствующий и в другой, не исчезает. Измерения (pingMs/speedMbps/ts) живут
 * в [profile] и СОХРАНЯЮТСЯ при склейке/переимпорте, а не обнуляются.
 */
public sealed record ServerRecord
{
    [JsonPropertyName("profile")] public required ServerProfile profile { get; init; }
    [JsonPropertyName("sourceIds")] public required IReadOnlyList<string> sourceIds { get; init; }
}

/**
 * Файл источников (`sources.json` в filesDir). Единая точка хранения мультиподписок.
 * @param migratedLegacy true после однократной миграции старого `subscriptions.json`.
 * @param seededDefaultRuBypass true после ОДНОКРАТНОГО посева дефолтной подписки «Обход ограничений в РФ»
 *        (и на свежих, и на существующих установках). Флаг не даёт ей «воскреснуть» после удаления юзером.
 */
public sealed record SourcesFile
{
    [JsonPropertyName("migratedLegacy")] public bool migratedLegacy { get; init; } = false;
    [JsonPropertyName("seededDefaultRuBypass")] public bool seededDefaultRuBypass { get; init; } = false;
    // Дефолтную подписку «Yzewe» (ВКЛ) добавляем разово каждому — на свежую установку и существующим при
    // обновлении. Флаг не даёт ей вернуться после удаления пользователем (как seededDefaultRuBypass выше).
    [JsonPropertyName("seededYzewe")] public bool seededYzewe { get; init; } = false;
    // Разовая починка бага посева (0.52–0.59): ensureYzewe добавлял Yzewe в ПУСТОЙ список РАНЬШЕ, чем
    // trySeedDefaultSource успевал засеять дефолт → посев igareck-дефолта (BLACK mobile) + белых ПРОПУСКАЛСЯ
    // (список уже «не пуст» из-за Yzewe) → на свежей установке оставался только Yzewe. Флаг гейтит разовый
    // back-add BLACK-дефолта (healMissingBlackDefault); после — не повторяем (уважаем удаление юзером).
    [JsonPropertyName("blackDefaultHealed")] public bool blackDefaultHealed { get; init; } = false;
    // ВТОРОЙ BLACK-дефолт (igareck BLACK_SS+All, trojan-пул на 443) добавлен разово — свежим (в посеве) и
    // существующим при обновлении (ensureBlackSs). Флаг не даёт ему вернуться после удаления пользователем.
    [JsonPropertyName("seededBlackSs")] public bool seededBlackSs { get; init; } = false;
    [JsonPropertyName("sources")] public IReadOnlyList<SubSource> sources { get; init; } = [];
    [JsonPropertyName("servers")] public IReadOnlyList<ServerRecord> servers { get; init; } = [];
    // Промпт 85: СЫРОЙ ответ каждого источника (sourceId → тело). Позволяет ПЕРЕСОБРАТЬ реестр целиком из
    // оставшихся источников при удалении/выключении/включении/обновлении — вместо вычитания записей на месте
    // (вычитание оставляло осиротевшие профили: запись числится за источником, но данные потеряны). Реестр
    // [servers] — производный КЭШ (несёт измерения); источник истины для пересборки — эти тела.
    [JsonPropertyName("rawBodies")] public IReadOnlyDictionary<string, string> rawBodies { get; init; } = new Dictionary<string, string>();
    // Пр.136: результат ПОСЛЕДНЕЙ попытки посева дефолтной подписки на пустом списке. Нужен, чтобы показать
    // человеку «рекомендуемый список не загрузился, причина …» вместо пустого экрана. seedLastError пуст =
    // либо ещё не пробовали, либо успех (тогда список уже не пуст). Флаг УСПЕХА — seededDefaultRuBypass выше.
    [JsonPropertyName("seedLastError")] public string seedLastError { get; init; } = "";
    [JsonPropertyName("seedLastAttemptTs")] public string seedLastAttemptTs { get; init; } = "";
}
