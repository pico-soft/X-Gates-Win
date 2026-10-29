using System.Text.Json.Serialization;
using XGates.Core.Xray.Link;

namespace XGates.Core.Subscription;

/**
 * Подписка со вложенными серверами — зеркало Python (subscriptions.json + SOURCE).
 * Хранится в subscriptions.json (filesDir) через [SubscriptionStore].
 * (Легаси-формат: читается только для однократной миграции.)
 *
 * @param url          адрес подписки (уникальный ключ; дубли не добавляем)
 * @param name         человекочитаемое имя (по умолчанию — из URL, см. Manager)
 * @param lastUpdateOk результат последнего фетча: true/false/null (ещё не обновлялась)
 * @param lastUpdateTs метка времени последнего фетча ("yyyy-MM-dd HH:mm")
 * @param servers      распарсенные серверы этой подписки; при ре-импорте заменяются целиком
 */
public sealed record Subscription
{
    [JsonPropertyName("url")] public required string url { get; init; }
    [JsonPropertyName("name")] public required string name { get; init; }
    [JsonPropertyName("lastUpdateOk")] public bool? lastUpdateOk { get; init; } = null;
    [JsonPropertyName("lastUpdateTs")] public string? lastUpdateTs { get; init; } = null;
    [JsonPropertyName("servers")] public IReadOnlyList<ServerProfile> servers { get; init; } = [];
}
