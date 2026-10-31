using System.Diagnostics;
using System.Security.Cryptography;
using XGates.Core.Compat;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Traffic;

namespace XGates.Core.Update;

/**
 * Скачивание и запуск установщика обновления — Windows-замена update/UpdateInstaller.kt (там PackageInstaller/
 * FileProvider и проверка подписи APK). Здесь: установщик MSI качается тем же каскадом (адреса по очереди), сверяется
 * SHA-256 из ПОДПИСАННОГО update.json, затем запускается msiexec (per-user MSI — без прав администратора, §13.5), а
 * приложение закрывается, чтобы установщик заменил файлы.
 */
public static class UpdateInstaller
{
    private const string TAG = "UpdateInstaller";
    private const string DIR = "updates";

    public abstract record DownloadOutcome
    {
        private DownloadOutcome() { }
        public sealed record Ok(string file) : DownloadOutcome;
        public sealed record Fail(UpdateErrorKind kind, string detail = "") : DownloadOutcome;
    }

    // Как на Android: 10 минут на файл (MSI ~40 МБ; медленный туннель — не повод висеть бесконечно).
    private const int DOWNLOAD_TOTAL_TIMEOUT_MS = 10 * 60 * 1000;

    /// <summary>Скачать установщик [available] (адреса по очереди, каждый — полным каскадом) и сверить SHA-256.</summary>
    public static DownloadOutcome download(Context context, UpdateCheckResult.Available available, Func<bool>? isCancelled = null, Action<long, long>? onProgress = null)
    {
        isCancelled ??= () => false;
        onProgress ??= (_, _) => { };
        var dir = Path.Combine(context.filesDir, DIR);
        Directory.CreateDirectory(dir);
        // Имя из манифеста — только имя файла (без каталогов), даже при подписанном манифесте.
        var dest = Path.Combine(dir, Path.GetFileName(available.artifact.fileName));
        // Проверенный установщик ИМЕННО этой сборки уже лежит на диске — НЕ качаем повторно (Android, Пункт 1).
        // Сумму сверяем заново — доверяем только полностью проверенному файлу.
        if (File.Exists(dest) && sha256(dest).Equals(available.artifact.sha256, StringComparison.OrdinalIgnoreCase)) return new DownloadOutcome.Ok(dest);
        foreach (var old in Directory.GetFiles(dir)) tryDelete(old);   // чистим устаревшее/битое/чужие сборки

        var s = SettingsStore.current();
        var directT = s.subTimeoutSec * 1000;
        var proxyT = s.subTimeoutSec * 1000 + 10_000;

        // Промпт 121.B: адреса по порядку (текущий → запасные), каждый через полный каскад. Провал скачивания или
        // несовпадение суммы — пробуем следующий адрес; успех с верной суммой — выходим.
        DownloadOutcome.Fail? lastFail = null, checksumFail = null;
        foreach (var url in available.downloadUrls)
        {
            if (isCancelled()) { tryDelete(dest); return new DownloadOutcome.Fail(UpdateErrorKind.CANCELLED); }
            var res = CascadeFetch.download(context, url, UpdateChecker.UA, dest,
                directTimeoutMs: directT, proxyTimeoutMs: proxyT, totalTimeoutMs: DOWNLOAD_TOTAL_TIMEOUT_MS,
                expectedSize: available.sizeBytes, isCancelled: isCancelled, onProgress: onProgress);
            if (res.bytes > 0) TrafficTracker.addTest(res.bytes);   // трафик скачивания — в поток «Тест»
            if (res.cancelled) { tryDelete(dest); return new DownloadOutcome.Fail(UpdateErrorKind.CANCELLED); }
            if (!res.ok || !File.Exists(dest))
            {
                tryDelete(dest);
                var detail = string.Join("; ", res.attempts.Where(it => !it.skipped).Select(it => $"{it.stage.label()}: {it.note}"));
                lastFail = new DownloadOutcome.Fail(UpdateErrorKind.DOWNLOAD_FAILED, $"{hostOf(url)}: {detail}");
                continue;
            }
            // Контрольная сумма из ПОДПИСАННОГО update.json — единственная (и достаточная) проверка подлинности файла
            // на Windows (на Android дальше шла сверка подписи APK). Не совпало — следующий адрес.
            var actual = sha256(dest);
            if (!actual.Equals(available.artifact.sha256, StringComparison.OrdinalIgnoreCase))
            {
                tryDelete(dest);
                lastFail = checksumFail = new DownloadOutcome.Fail(UpdateErrorKind.CHECKSUM_MISMATCH,
                    $"{hostOf(url)}: ожидали {available.artifact.sha256[..Math.Min(16, available.artifact.sha256.Length)]}…, получили {actual[..16]}…");
                continue;
            }
            return new DownloadOutcome.Ok(dest);
        }
        // Пр.135: сам установщик через одноразовый temp-инстанс НЕ тянем (дорого). Прямой путь заблокирован, а туннеля
        // нет — честно направляем поднять прокси (сведения об обновлении могли прийти через temp-инстанс).
        // Находка порта №7: файл ДОШЁЛ, но сумма не совпала → путь не заблокирован, подсказка про прокси была бы ложной.
        if (checksumFail != null) return checksumFail;
        if (!CascadeFetch.isOwnProxyUp())
            return new DownloadOutcome.Fail(UpdateErrorKind.DOWNLOAD_FAILED,
                "Прямой доступ к GitHub заблокирован, а прокси не запущен. Запустите прокси на вкладке «Главная» и повторите — файл скачается через туннель.");
        return lastFail ?? new DownloadOutcome.Fail(UpdateErrorKind.DOWNLOAD_FAILED);
    }

    private static string hostOf(string url)
    {
        try { return new Uri(url).Host; } catch (Exception) { return url; }
    }

    /**
     * Уборка на старте (аналог pruneObsoleteApks): если более новой версии, чем установлена, не ожидается — всё в
     * каталоге обновлений отработало (установщик уже поставил эту версию) → удаляем. Ждёт новая — не трогаем:
     * проверенный файл переиспользуется без повторной загрузки.
     */
    public static void pruneObsolete(Context context)
    {
        var dir = Path.Combine(context.filesDir, DIR);
        if (!Directory.Exists(dir)) return;
        if (UpdateStore.record.value.availCode > UpdateChecker.currentVersionCode()) return;
        foreach (var f in Directory.GetFiles(dir)) tryDelete(f);
    }

    public static string sha256(string file)
    {
        using var f = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>Запустить установщик (MSI per-user, окно прогресса без вопросов). true — процесс установщика стартовал.</summary>
    public static bool startInstall(string file)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(file);
            psi.ArgumentList.Add("/passive");
            psi.ArgumentList.Add("/norestart");
            Process.Start(psi);
            return true;
        }
        catch (Exception e)
        {
            Log.w(TAG, "msiexec не запустился", e);
            return false;
        }
    }

    private static void tryDelete(string f) { try { File.Delete(f); } catch (Exception) { } }
}

/**
 * ОДИН ТАП по баннеру «новая версия» = перепроверить latest → скачать → СВЕРИТЬ подпись и контрольную сумму →
 * запустить установщик → закрыть приложение. Порт update/UpdateFlowController.kt (без Android-шагов разрешения
 * установки и уведомлений BAL). Прогресс — в [phase].
 */
public static class UpdateFlowController
{
    public abstract record Phase
    {
        private Phase() { }
        public sealed record Idle : Phase;
        public sealed record Checking : Phase;
        public sealed record Downloading(long done, long total) : Phase;
        public sealed record Installing : Phase;               // установщик запущен — приложение сейчас закроется
        public sealed record Failed(string message) : Phase;
    }

    private static readonly MutableStateFlow<Phase> _phase = new(new Phase.Idle());
    public static StateFlow<Phase> phase => _phase.asStateFlow();

    /// <summary>Как закрыть приложение после запуска установщика (ставит UI: выход из трея с остановкой ядра).</summary>
    public static Action exitApp { get; set; } = () => { };

    // Пункт 2 Android: приложение на переднем плане? (окно на экране; ставит приложение в onResume/onPause.)
    // На Windows установщик запускается и из трея — флаг нужен «Главной»: IP освежаем, только когда смотрят.
    public static volatile bool appInForeground;

    private static volatile bool running;
    private static volatile bool cancelFlag;
    // Уже скачанный+проверенный файл (если установщик не запустился — повторный тап ставит без повторной загрузки).
    private static volatile string? readyFile;
    private static volatile int readyForCode;

    /// <summary>Запуск установщика. Приложение ставит свой (установить и открыть новую версию); тесты — подмену.</summary>
    public static Func<string, bool> launchInstaller { get; set; } = UpdateInstaller.startInstall;

    public static void cancel() => cancelFlag = true;

    /// <summary>Идёт ли сейчас поток «одной кнопки» (тесты ждут его завершения, чтобы он не пережил свой тест).</summary>
    internal static bool isRunning => running;

    internal static void resetForTest() { _phase.value = new Phase.Idle(); readyFile = null; readyForCode = 0; }

    /// <summary>Тап по баннеру/кнопке. Идемпотентно: пока идёт — повторный тап игнорируется.</summary>
    public static void oneTap(Context context)
    {
        if (running) return;
        running = true; cancelFlag = false;
        var app = context.applicationContext;
        KThread.start(() =>
        {
            try
            {
                // ТЗ Elyor: ВСЕГДА перепроверяем latest перед скачиванием. Баннер мог зафиксировать ПРОМЕЖУТОЧНУЮ
                // версию (фоновая проверка прошла ДО выхода более свежего релиза). Качаем ИМЕННО текущий latest.
                _phase.value = new Phase.Checking();
                // Перепроверка не удалась (сеть) — UpdateStore.live держит прежний Available, используем его.
                try { UpdateStore.apply(app, UpdateChecker.check(app, manual: true), JavaSystem.currentTimeMillis()); } catch (Exception) { }
                var live = UpdateStore.live.value;
                if (live is not UpdateCheckResult.Available avail)
                {
                    // Windows-добавка: отказ по ПОДПИСИ (есть только здесь) называем прямо — «попробуйте позже» тут неверно,
                    // повтор не поможет. Остальное — как на Android.
                    _phase.value = live switch
                    {
                        UpdateCheckResult.UpToDate => new Phase.Failed("Новее версии не найдено — обновление не требуется"),
                        UpdateCheckResult.Error { kind: UpdateErrorKind.SIGNATURE_MISMATCH or UpdateErrorKind.SIGNATURE_MISSING } e => new Phase.Failed(e.kind.text()),
                        _ => new Phase.Failed("Обновление недоступно — попробуйте позже"),
                    };
                    return;
                }
                // Уже скачан ИМЕННО этот (текущий latest) код → установить без повторной загрузки.
                if (readyFile is { } rf && File.Exists(rf) && readyForCode == avail.versionCode)
                {
                    install(app, rf);
                    return;
                }
                _phase.value = new Phase.Downloading(0L, avail.sizeBytes);
                var lastShown = 0L;
                var outcome = UpdateInstaller.download(app, avail, () => cancelFlag, (d, t) =>
                {
                    if (d == t || d - lastShown >= 512 * 1024) { lastShown = d; _phase.value = new Phase.Downloading(d, t); }
                });
                switch (outcome)
                {
                    case UpdateInstaller.DownloadOutcome.Ok ok:
                        readyFile = ok.file; readyForCode = avail.versionCode;
                        install(app, ok.file);
                        break;
                    case UpdateInstaller.DownloadOutcome.Fail f:
                        _phase.value = f.kind == UpdateErrorKind.CANCELLED ? new Phase.Idle() : new Phase.Failed(f.detail.IfBlank(f.kind.text()));
                        break;
                }
            }
            catch (Exception e)
            {
                _phase.value = new Phase.Failed($"Ошибка обновления: {(string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message)}");
            }
            finally { running = false; }
        }, "update-flow");
    }

    /// <summary>Запустить установщик и закрыть приложение. Полосу/уведомление снимаем только если установщик РЕАЛЬНО стартовал.</summary>
    private static void install(Context app, string file)
    {
        bool started;
        try { started = launchInstaller(file); } catch (Exception) { started = false; }
        if (!started) { _phase.value = new Phase.Failed(UpdateErrorKind.INSTALL_FAILED.text()); return; }
        Service.NotificationHelper.cancelUpdate(app); Service.NotificationHelper.cancelInstall(app);
        UpdateStore.markDismissed(app);
        _phase.value = new Phase.Installing();
        exitApp();
    }
}
