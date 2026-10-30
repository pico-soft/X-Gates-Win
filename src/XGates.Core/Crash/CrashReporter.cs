using System.Text;
using System.Text.RegularExpressions;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;

namespace XGates.Core.Crash;

/**
 * Промпт 93.I — сбор отчётов о падениях ИЗ САМОГО приложения (тестировщики удалённые). Порт crash/CrashReporter.kt.
 *
 * Ставит глобальный обработчик необработанных исключений (весь процесс). При падении: пишет отчёт в каталог данных
 * (трассировка, время, версия, ОС, что происходило, последние строки журнала), вычищает чувствительное (адреса
 * подписок, UUID, пароли), хранит несколько последних, старые удаляет — и ДАЁТ процессу упасть штатно.
 * Windows: AppDomain.UnhandledException (+ UI-поток приложения зовёт [report] из DispatcherUnhandledException).
 */
public static class CrashReporter
{
    private const string TAG = "CrashReporter";
    private const string DIR = "crashes";
    private const int KEEP = 6;                       // сколько последних отчётов хранить
    private const string PREF = "crash_prefs";
    private const string KEY_SEEN = "last_seen";      // имя последнего ПОКАЗАННОГО отчёта (чтобы не нудеть)

    /**
     * Промпт 97: адрес получателя отчётов о сбоях — ОДНО место (константа). Отправка ТОЛЬКО по явному
     * действию человека (открыть письмо и нажать «отправить»); автоматической отправки НЕТ и быть не должно
     * (приложение для обхода блокировок не может тихо слать данные разработчику).
     */
    public const string DEVELOPER_EMAIL = "pico.soft.github@gmail.com";
    private const string APP_NAME = "X-Gates Win";

    private static volatile Context? appContext;
    private static int installed;
    private static string versionName = "?";
    private static int versionCode;

    /// <summary>Тема письма: название, версия, versionCode, ОС (Промпт 97.D).</summary>
    public static string emailSubject() =>
        $"{APP_NAME} {versionName} ({versionCode}) — {Environment.OSVersion.VersionString} — отчёт о сбое";

    /**
     * Тело письма (Промпт 97.D/E): необязательное описание пользователя «что делал перед сбоем» отдельным
     * блоком СВЕРХУ, затем очищенный отчёт как есть. Описание тоже прогоняем через [Sanitizer] — человек мог
     * вписать адрес/учётку, а письмо уходит постороннему.
     */
    public static string emailBody(string description, string report)
    {
        var sb = new StringBuilder();
        var desc = description.Trim();
        if (desc.Length > 0)
        {
            sb.AppendLine("--- Что делал пользователь перед сбоем ---");
            sb.AppendLine(Sanitizer.clean(desc));
            sb.AppendLine();
        }
        sb.Append(report);
        return sb.ToString();
    }

    public static void install(Context context, string appVersionName, int appVersionCode)
    {
        appContext = context.applicationContext;
        versionName = appVersionName;
        versionCode = appVersionCode;
        // Фоновые потоки порта (KThread) процесс НЕ роняют (docs/decisions.md, «потоки»), но отчёт пишем — как на
        // Android, где такое исключение = падение с отчётом. Иначе ошибка в поле осталась бы невидимой.
        KThread.onUncaught = (name, ex) => report(name, ex);
        if (Interlocked.Exchange(ref installed, 1) == 1) return;   // повторный install — обработчик процесса уже стоит
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // .NET после этого события завершит процесс сам — «дать упасть штатно».
            try { if (e.ExceptionObject is Exception ex) writeReport(Thread.CurrentThread.Name ?? "?", ex); }
            catch (Exception t) { try { Log.e(TAG, "не удалось записать отчёт", t); } catch (Exception) { } }
        };
    }

    /// <summary>Записать отчёт без падения (UI-поток WPF: DispatcherUnhandledException; ошибка глушится там же).</summary>
    public static void report(string thread, Exception t)
    {
        try { writeReport(thread, t); } catch (Exception) { }
    }

    private static string dir()
    {
        var d = Path.Combine(appContext!.filesDir, DIR);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Отчёты, свежие первыми (имя содержит сортируемую метку времени).</summary>
    public static List<string> reports()
    {
        try
        {
            return Directory.GetFiles(dir(), "crash-*").Where(File.Exists)
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        }
        catch (Exception) { return []; }
    }

    public static string read(string f)
    {
        try { return File.ReadAllText(f); } catch (Exception) { return "(отчёт недоступен)"; }
    }

    public static void clearAll()
    {
        try { foreach (var f in reports()) File.Delete(f); } catch (Exception) { }
        markLatestSeen();
    }

    public static void delete(string f) { try { File.Delete(f); } catch (Exception) { } markLatestSeen(); }

    /// <summary>Есть НЕпоказанный отчёт (для баннера на главной/в настройках).</summary>
    public static bool hasUnseen()
    {
        var latest = reports().FirstOrDefault();
        if (latest == null) return false;
        return Path.GetFileName(latest) != prefs()?.getString(KEY_SEEN, null);
    }

    /// <summary>Пометить самый свежий отчёт «показанным» — баннер снимается (для следующего падения покажется снова).</summary>
    public static void markLatestSeen()
    {
        var latest = reports().FirstOrDefault();
        prefs()?.edit().putString(KEY_SEEN, latest == null ? null : Path.GetFileName(latest)).apply();
    }

    private static SharedPreferences? prefs() => appContext == null ? null : SharedPreferences.get(appContext, PREF);

    private static void writeReport(string thread, Exception t)
    {
        if (appContext == null) return;
        var sb = new StringBuilder();
        sb.AppendLine("=== X-Gates Win — отчёт о сбое ===");
        try { sb.AppendLine($"Время: {stamp("yyyy-MM-dd HH:mm:ss")}"); } catch (Exception) { }
        sb.AppendLine($"Версия: {versionName} (versionCode {versionCode})");
        sb.AppendLine($"ОС: {Environment.OSVersion.VersionString} · {(Environment.Is64BitOperatingSystem ? "x64" : "x86")} · .NET {Environment.Version}");
        sb.AppendLine($"Поток: {thread}");
        // Что происходило в момент падения (best-effort, каждое поле защищено).
        try { sb.AppendLine($"Состояние: {CrashContext.breadcrumb}"); } catch (Exception) { }
        try { sb.AppendLine($"Полный тест идёт: {(MonitorCoordinator.fullTestRunning ? "true" : "false")}"); } catch (Exception) { }
        try { sb.AppendLine($"Прокси активен: {(ProxyState.state.value.running ? "true" : "false")}"); } catch (Exception) { }
        try { sb.AppendLine($"Монитор включён: {(SettingsStore.current().monitorEnabled ? "true" : "false")}"); } catch (Exception) { }
        try { sb.AppendLine($"Серверов в реестре: {CrashContext.registrySize}"); } catch (Exception) { }
        try
        {
            var usedMb = GC.GetTotalMemory(false) / 1_048_576;
            var wsMb = Environment.WorkingSet / 1_048_576;
            sb.AppendLine($"Память: управляемая {usedMb} МБ, рабочий набор {wsMb} МБ");
        }
        catch (Exception) { }
        sb.AppendLine();
        sb.AppendLine("--- Трассировка ---");
        sb.AppendLine(t.ToString());
        sb.AppendLine("--- Журнал (последние события) ---");
        try
        {
            foreach (var it in MonitorLog.state.value.TakeLast(15))
                sb.AppendLine($"• {it.text}{(string.IsNullOrWhiteSpace(it.detail) ? "" : $" — {it.detail}")}");
        }
        catch (Exception) { }
        var report = Sanitizer.clean(sb.ToString());
        var f = Path.Combine(dir(), $"crash-{stamp("yyyyMMdd-HHmmss")}.txt");
        File.WriteAllText(f, report, new UTF8Encoding(false));
        prune();
        Log.e(TAG, $"отчёт о сбое записан: {Path.GetFileName(f)}");
    }

    private static void prune()
    {
        var all = reports();
        if (all.Count > KEEP) foreach (var f in all.Skip(KEEP)) { try { File.Delete(f); } catch (Exception) { } }
    }

    private static string stamp(string fmt) => DateTime.Now.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
}

/**
 * «Хлебные крошки» для отчёта: что приложение делает прямо сейчас. Ключевые операции обновляют [breadcrumb]
 * (полный тест/фаза, импорт источника, обновление, монитор), а reload обновляет [registrySize].
 */
public static class CrashContext
{
    public static volatile string breadcrumb = "старт";
    public static volatile int registrySize = -1;
    public static void set(string b) => breadcrumb = b;
}

/**
 * Вычистка чувствительного из отчёта (Промпт 93.B): адреса подписок, учётные данные серверов, UUID, пароли,
 * IP — человек пересылает отчёт в мессенджере. Лучше перемаскировать, чем утечь.
 * Регулярки — как в Kotlin (Java-классы \w/\s/\d → явные ASCII-классы; \b — Unicode, как на Android).
 */
public static partial class Sanitizer
{
    [GeneratedRegex("[a-zA-Z][a-zA-Z0-9+.\\-]*://[^ \\t\\n\\x0B\\f\\r\"'<>]+")] private static partial Regex Url();           // http(s)/vless/vmess/trojan/ss://…
    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")] private static partial Regex Uuid();
    [GeneratedRegex("[A-Za-z0-9_.\\-]+:[^@ \\t\\n\\x0B\\f\\r/]+@")] private static partial Regex Creds();                     // user:pass@host
    [GeneratedRegex("\\b[0-9]{1,3}(?:\\.[0-9]{1,3}){3}\\b")] private static partial Regex Ip();
    [GeneratedRegex("[A-Za-z0-9+/=_\\-]{24,}")] private static partial Regex Token();                                        // длинные base64/hex-токены (ключи/uuid-подобное)

    public static string clean(string s)
    {
        s = Url().Replace(s, "<адрес>");
        s = Creds().Replace(s, "<логин>@");
        s = Uuid().Replace(s, "<uuid>");
        s = Ip().Replace(s, "<ip>");
        s = Token().Replace(s, "<токен>");
        return s;
    }
}
