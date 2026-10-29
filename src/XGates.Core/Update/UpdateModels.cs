using System.Text.Json.Serialization;

namespace XGates.Core.Update;

/**
 * Модели проверки обновления (Промпт 70). Порт update/UpdateModels.kt.
 *
 * ГЛАВНОЕ РЕШЕНИЕ: сравниваем версии ТОЛЬКО по [UpdateManifest.versionCode] из update.json, вложенного
 * в релиз. Публичный API GitHub versionCode не отдаёт (только строку tag_name/name), а строку версии
 * сравнивать ненадёжно. update.json — источник ТОЧНЫХ данных (versionCode + имя файла + SHA-256).
 * Имена вложений — КОНТРАКТ с обновлялкой, менять нельзя.
 * Windows: «abi» = платформа сборки ("win-x64"); update.json ПОДПИСАН своим ключом (update.json.sig, см. UpdateSignature).
 */

// ─────────────────────── ответ GitHub API (releases/latest) ───────────────────────

public sealed record GithubAsset
{
    [JsonPropertyName("name")] public string name { get; init; } = "";
    [JsonPropertyName("browser_download_url")] public string browserDownloadUrl { get; init; } = "";
    [JsonPropertyName("size")] public long size { get; init; }
}

public sealed record GithubRelease
{
    [JsonPropertyName("tag_name")] public string tagName { get; init; } = "";
    [JsonPropertyName("name")] public string name { get; init; } = "";
    [JsonPropertyName("body")] public string body { get; init; } = "";           // описание релиза (человекочитаемое, запасное)
    [JsonPropertyName("draft")] public bool draft { get; init; }
    [JsonPropertyName("prerelease")] public bool prerelease { get; init; }
    [JsonPropertyName("assets")] public IReadOnlyList<GithubAsset> assets { get; init; } = [];
}

// ─────────────────────── update.json (вложение релиза, ТОЧНЫЕ данные) ───────────────────────

public sealed record UpdateArtifact
{
    [JsonPropertyName("abi")] public string abi { get; init; } = "";             // Windows: "win-x64"
    [JsonPropertyName("fileName")] public string fileName { get; init; } = "";   // имя вложения релиза — контракт
    [JsonPropertyName("sha256")] public string sha256 { get; init; } = "";       // hex, регистр не важен
}

public sealed record UpdateManifest
{
    [JsonPropertyName("versionCode")] public int versionCode { get; init; }
    [JsonPropertyName("versionName")] public string versionName { get; init; } = "";
    [JsonPropertyName("notes")] public string notes { get; init; } = "";                          // краткое описание изменений
    [JsonPropertyName("artifacts")] public IReadOnlyList<UpdateArtifact> artifacts { get; init; } = [];
    // Промпт 121.C: идентификатор НАШЕГО приложения. Проверяем ДО скачивания, что найденный релиз наш.
    [JsonPropertyName("packageName")] public string packageName { get; init; } = "";
}

// ─────────────────────── типизированные исходы ───────────────────────

/// <summary>Типы ошибок — «никаких общих ‘ошибка’». Текст — готовое сообщение пользователю.</summary>
public enum UpdateErrorKind
{
    NO_TUNNEL, API_UNAVAILABLE, MANIFEST_DOWNLOAD_FAILED, MANIFEST_MISSING, MANIFEST_PARSE, NO_ARTIFACT, ASSET_MISSING,
    FOREIGN_RELEASE, DOWNLOAD_FAILED, CHECKSUM_MISMATCH, SIGNATURE_MISMATCH, SIGNATURE_MISSING, CANCELLED, INSTALL_FAILED,
}

public static class UpdateErrorKindExt
{
    public static string text(this UpdateErrorKind k) => k switch
    {
        // Прямой путь к GitHub заблокирован, а туннель не запущен — это НЕ «GitHub недоступен» (узел жив,
        // недоступен путь). Нормально для приложения, которое туннель и предоставляет: подключить и повторить.
        UpdateErrorKind.NO_TUNNEL => "Проверка обновлений недоступна без туннеля: прямой доступ к GitHub заблокирован, а прокси не запущен. Подключитесь и повторите.",
        UpdateErrorKind.API_UNAVAILABLE => "GitHub API недоступен — не удалось получить сведения о релизах",
        UpdateErrorKind.MANIFEST_DOWNLOAD_FAILED => "сведения получены, но файл update.json не скачался (вложение недоступно)",
        UpdateErrorKind.MANIFEST_MISSING => "в релизе нет файла update.json",
        UpdateErrorKind.MANIFEST_PARSE => "файл update.json повреждён или в неизвестном формате",
        UpdateErrorKind.NO_ARTIFACT => "в update.json нет сборки для Windows x64",
        UpdateErrorKind.ASSET_MISSING => "сборка указана в update.json, но отсутствует среди вложений релиза",
        UpdateErrorKind.FOREIGN_RELEASE => "найден релиз другого приложения (не наш) — пропускаю этот адрес",
        UpdateErrorKind.DOWNLOAD_FAILED => "не удалось скачать файл обновления",
        UpdateErrorKind.CHECKSUM_MISMATCH => "контрольная сумма не совпала — файл повреждён или подменён",
        // Windows: подписи APK нет — вместо неё ПОДПИСЬ update.json нашим ключом (docs/decisions.md §13.6).
        UpdateErrorKind.SIGNATURE_MISMATCH => "подпись сведений об обновлении не совпала — обновление НЕ от разработчика, установка отменена",
        UpdateErrorKind.SIGNATURE_MISSING => "у обновления нет подписи разработчика — установка отменена",
        UpdateErrorKind.CANCELLED => "отменено",
        UpdateErrorKind.INSTALL_FAILED => "не удалось запустить установщик",
        _ => k.ToString(),
    };
}

public abstract record UpdateCheckResult
{
    private UpdateCheckResult() { }

    /// <summary>Релизов ещё нет (API вернул 404) — это НЕ ошибка.</summary>
    public sealed record NoReleases : UpdateCheckResult;

    /// <summary>Установлена последняя версия. [via] — каким путём получены сведения (API / запасной).</summary>
    public sealed record UpToDate(int currentCode, string latestName, string via = "") : UpdateCheckResult;

    /// <summary>Есть обновление с подтверждённым (подписанным) update.json — можно скачать и проверить.</summary>
    public sealed record Available(
        int versionCode,
        string versionName,
        string notes,
        UpdateArtifact artifact,
        // Промпт 121.B: адреса скачивания по порядку (текущий адрес → запасные). Каждый идёт через полный каскад.
        IReadOnlyList<string> downloadUrls,
        long sizeBytes,
        string via = "") : UpdateCheckResult
    {
        public string downloadUrl => downloadUrls[0];
    }

    /**
     * По метке релиза (v0.11) версия НОВЕЕ, но update.json нет → скачать/проверить надёжно нельзя
     * (нет SHA-256 и имени файла). Только сообщаем (менее надёжный путь, запасной).
     */
    public sealed record AvailableUnverified(string tag, string name, string notes) : UpdateCheckResult;

    public sealed record Error(UpdateErrorKind kind, string detail = "") : UpdateCheckResult;
}

/**
 * Итог проверки: результат + ПОЛНАЯ постадийная диагностика (по каждому адресу — какие ступени каскада
 * выполнялись/пропущены и почему, код, размер, исключение, КОНЕЧНЫЙ host, цепочка редиректов). Показывается
 * в UI под «Подробности» (Промпт 77.E), чтобы разбирательство занимало одно нажатие.
 */
public sealed record CheckReport(UpdateCheckResult result, string details);
