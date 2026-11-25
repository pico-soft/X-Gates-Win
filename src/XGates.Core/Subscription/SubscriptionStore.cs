using System.Text.Json;
using XGates.Core.Platform;

namespace XGates.Core.Subscription;

/**
 * Единственная точка чтения/записи `sources.json` (мультиподписки) в filesDir.
 * Схема — [SourcesFile] (метаданные источников + общий реестр серверов). Запись атомарная.
 * Миграция старого `subscriptions.json` — в [SubscriptionManager.init] (бизнес-логика), здесь только
 * персистентность + сырое чтение легаси-файла.
 */
public static class SubscriptionStore
{
    private const string TAG = "SubscriptionStore";
    private const string FILE_NAME = "sources.json";
    private const string TMP_NAME = "sources.json.tmp";
    private const string BACKUP_NAME = "sources.json.bak";
    private const string BACKUP_TMP_NAME = "sources.json.bak.tmp";
    private const string LEGACY_FILE_NAME = "subscriptions.json";
    private static readonly object Sync = new();

    internal static Action<string, string>? beforeQuarantineForTest;

    private static string file(Context context) => Path.Combine(context.filesDir, FILE_NAME);
    private static string backupFile(Context context) => Path.Combine(context.filesDir, BACKUP_NAME);

    private static SourcesFile decode(string json) => KJson.decodeFromString<SourcesFile>(json);

    /** Сохранить повреждённый оригинал под уникальным именем; при неудаче НЕ продолжать запись поверх него. */
    private static void quarantine(Context context)
    {
        var f = file(context);
        var q = Path.Combine(context.filesDir,
            $"{FILE_NAME}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        beforeQuarantineForTest?.Invoke(f, q);
        File.Move(f, q);
    }

    private static SourcesFile recoverBackup(Context context, Exception primaryError)
    {
        var b = backupFile(context);
        if (!File.Exists(b))
            throw new InvalidDataException("Файл источников повреждён, резервной копии нет; оригинал сохранён в quarantine", primaryError);
        try
        {
            return decode(File.ReadAllText(b));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Файл источников и его резервная копия повреждены; оба файла сохранены", e);
        }
    }

    /**
     * Текущий файл источников; пустой (migratedLegacy=false), только если нет ни primary, ни backup.
     * Повреждение НЕ эквивалентно пустому реестру: primary сохраняется в quarantine, затем читается валидный backup.
     * Если backup отсутствует или тоже повреждён, выбрасывается [InvalidDataException] — load→modify→save не сможет
     * молча записать пустое состояние. Ошибки I/O не маскируются под повреждение и не перемещают файл.
     */
    public static SourcesFile load(Context context)
    {
        lock (Sync)
        {
            var f = file(context);
            if (File.Exists(f))
            {
                try
                {
                    return decode(File.ReadAllText(f));
                }
                catch (JsonException e)
                {
                    Log.w(TAG, "primary is corrupt, preserving it and trying backup", e);
                    quarantine(context);
                    return recoverBackup(context, e);
                }
            }

            var b = backupFile(context);
            if (!File.Exists(b)) return new SourcesFile();
            try
            {
                return decode(File.ReadAllText(b));
            }
            catch (JsonException e)
            {
                throw new InvalidDataException("Файл источников отсутствует, а резервная копия повреждена; копия сохранена", e);
            }
        }
    }

    /**
     * Атомарная запись (temp→rename). Перед заменой валидный primary копируется в `.bak`; повреждённый — только
     * в quarantine и никогда поверх исправной копии. При первой записи backup создаётся из нового валидного состояния.
     */
    public static void save(Context context, SourcesFile data)
    {
        lock (Sync)
        {
            var f = file(context);
            string? previous = null;
            if (File.Exists(f))
            {
                try
                {
                    previous = File.ReadAllText(f);
                    decode(previous);
                }
                catch (JsonException e)
                {
                    Log.w(TAG, "refusing to back up corrupt primary", e);
                    quarantine(context);
                }
            }

            // Backup готовим ДО замены primary: сбой здесь оставляет текущее состояние нетронутым.
            if (previous != null)
                KJson.atomicWrite(backupFile(context), Path.Combine(context.filesDir, BACKUP_TMP_NAME), previous);

            var json = KJson.encodeToString(data);
            KJson.atomicWrite(f, Path.Combine(context.filesDir, TMP_NAME), json);

            // После самой первой записи прежнего primary ещё не было — recovery-копия нужна сразу.
            if (previous == null && !File.Exists(backupFile(context)))
                KJson.atomicWrite(backupFile(context), Path.Combine(context.filesDir, BACKUP_TMP_NAME), json);
        }
    }

    /** Чтение и запись одним шагом: замеры пишут из нескольких потоков, и между load и save чужой результат терялся. */
    public static void update(Context context, Func<SourcesFile, SourcesFile> change)
    {
        lock (Sync)
        {
            save(context, change(load(context)));
        }
    }

    /** Сырое чтение СТАРОГО формата (`subscriptions.json` = список [Subscription] со вложенными
     *  серверами) — только для однократной миграции. Пусто, если файла нет или он не в старой схеме. */
    public static List<Subscription> readLegacy(Context context)
    {
        var f = Path.Combine(context.filesDir, LEGACY_FILE_NAME);
        if (!File.Exists(f)) return [];
        try
        {
            return KJson.decodeFromString<List<Subscription>>(File.ReadAllText(f));
        }
        catch (Exception e)
        {
            Log.w(TAG, "legacy read failed", e);
            return [];
        }
    }
}
