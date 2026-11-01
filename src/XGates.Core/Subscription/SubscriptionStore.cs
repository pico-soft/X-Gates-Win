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
    private const string LEGACY_FILE_NAME = "subscriptions.json";
    private static readonly object Sync = new();

    private static string file(Context context) => Path.Combine(context.filesDir, FILE_NAME);

    /** Текущий файл источников; пустой (migratedLegacy=false), если файла ещё нет. */
    public static SourcesFile load(Context context)
    {
        lock (Sync)
        {
            var f = file(context);
            if (!File.Exists(f)) return new SourcesFile();
            try
            {
                return KJson.decodeFromString<SourcesFile>(File.ReadAllText(f));
            }
            catch (Exception e)
            {
                Log.w(TAG, "load failed, returning empty", e);
                return new SourcesFile();
            }
        }
    }

    /** Атомарная запись (temp→rename). */
    public static void save(Context context, SourcesFile data)
    {
        lock (Sync)
        {
            KJson.atomicWrite(file(context), Path.Combine(context.filesDir, TMP_NAME), KJson.encodeToString(data));
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
