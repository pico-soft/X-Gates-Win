using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XGates.Core.Platform;

namespace XGates.App;

/**
 * «Настроить Firefox» / «Вернуть как было» (решение Elyor 2026-10-30, вариант Б): рабочий браузер — Firefox, настройка —
 * одной кнопкой в его ОБЫЧНОМ профиле. Системный прокси Windows не трогаем (рамки продукта) — меняем только настройки
 * самого Firefox и только по нажатию:
 *  - профиль — по profiles.ini (профиль по умолчанию каждой установки; есть и вариант Firefox из Microsoft Store);
 *  - прокси — блок в user.js (Firefox применяет его при каждом запуске) с тем же набором настроек, что выставляет
 *    политика Proxy самого Mozilla (ProxyPolicies.sys.mjs): ручной режим, SOCKS5 127.0.0.1:порт, DNS через прокси;
 *  - перед первой настройкой запоминаем прежние значения этих настроек из prefs.js — «Вернуть как было» убирает блок и
 *    возвращает их. Возврат — только при ЗАКРЫТОМ Firefox: при выходе он переписывает prefs.js своими значениями.
 * Без запущенного X-Gates такой Firefox сайты не открывает (прокси не отвечает) — это цена варианта Б, о ней говорим.
 * Отвергнуто: PAC с запасным DIRECT (при переключении серверов запросы к заблокированным сайтам уходили бы мимо
 * туннеля); политики в реестре (HKCU\Software\Policies закрыт на запись без прав администратора).
 */
public static partial class FirefoxSetup
{
    public sealed record Profile(string dir, string name);

    /// <param name="restartFirefox">Firefox открыт: настройка применится только после его перезапуска.</param>
    public sealed record Outcome(bool ok, string text, bool restartFirefox = false);

    /// <summary>
    /// Firefox читает user.js только при запуске. Просьба про перезапуск в строке статуса осталась незамеченной
    /// (Elyor 2026-11-01), поэтому окно показывает её отдельно.
    /// </summary>
    public const string RESTART_TEXT = "Закройте все окна Firefox и откройте его заново: пока он открыт, он работает по-старому.";

    /// <summary>Настройки, которыми управляем (порядок — как в блоке).</summary>
    internal static readonly IReadOnlyList<string> MANAGED =
    [
        "network.proxy.type", "network.proxy.socks", "network.proxy.socks_port", "network.proxy.socks_version",
        "network.proxy.socks_remote_dns", "network.proxy.socks5_remote_dns", "network.proxy.share_proxy_settings",
        "network.proxy.http", "network.proxy.http_port", "network.proxy.ssl", "network.proxy.ssl_port",
    ];

    // Маркеры — только ASCII (user.js читает парсер настроек Firefox; комментарии с кириллицей не рискуем).
    internal const string BEGIN = "// >>> X-Gates Win: Firefox proxy via X-Gates (undo: X-Gates Win > Settings > Restore)";
    internal const string END = "// <<< X-Gates Win";
    private const string STATE_FILE = "firefox_proxy.json";

    /// <summary>Каталоги с profiles.ini. Тесты подменяют на временные.</summary>
    public static Func<IReadOnlyList<string>> roots { get; set; } = defaultRoots;

    private static IReadOnlyList<string> defaultRoots()
    {
        var list = new List<string>();
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (roaming.Length > 0) list.Add(Path.Combine(roaming, "Mozilla", "Firefox"));
        // Firefox из Microsoft Store держит профиль в песочнице пакета.
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        try
        {
            if (Directory.Exists(packages))
                foreach (var d in Directory.GetDirectories(packages, "Mozilla.Firefox_*"))
                    list.Add(Path.Combine(d, "LocalCache", "Roaming", "Mozilla", "Firefox"));
        }
        catch (Exception) { }
        return list;
    }

    // ─────────────────────────── профили ───────────────────────────

    /// <summary>Профили по умолчанию каждой установки Firefox (нет секций Install — профиль с Default=1 / единственный).</summary>
    public static IReadOnlyList<Profile> findProfiles()
    {
        var result = new List<Profile>();
        foreach (var root in roots())
        {
            var ini = Path.Combine(root, "profiles.ini");
            if (!File.Exists(ini)) continue;
            Dictionary<string, Dictionary<string, string>> sec;
            try { sec = parseIni(File.ReadAllText(ini)); } catch (Exception) { continue; }
            string resolve(string path, bool relative) =>
                Path.GetFullPath(relative || !Path.IsPathRooted(path) ? Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)) : path);
            var profiles = sec.Where(kv => kv.Key.StartsWith("Profile", StringComparison.Ordinal) && kv.Value.ContainsKey("Path"))
                .Select(kv => (kv.Value, dir: resolve(kv.Value["Path"], kv.Value.GetValueOrDefault("IsRelative") == "1"))).ToList();
            string nameOf(string dir) => profiles.FirstOrDefault(p => p.dir == dir).Value?.GetValueOrDefault("Name") ?? Path.GetFileName(dir);
            var picked = sec.Where(kv => kv.Key.StartsWith("Install", StringComparison.Ordinal) && kv.Value.ContainsKey("Default"))
                .Select(kv => resolve(kv.Value["Default"], relative: false)).Distinct().ToList();
            if (picked.Count == 0)
            {
                var def = profiles.Where(p => p.Value.GetValueOrDefault("Default") == "1").Select(p => p.dir).ToList();
                picked = def.Count > 0 ? def : profiles.Count == 1 ? [profiles[0].dir] : [];
            }
            foreach (var dir in picked.Where(Directory.Exists))
                if (result.All(r => r.dir != dir)) result.Add(new Profile(dir, nameOf(dir)));
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> parseIni(string text)
    {
        var res = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { cur = res[line[1..^1]] = new Dictionary<string, string>(); continue; }
            var eq = line.IndexOf('=');
            if (cur != null && eq > 0) cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return res;
    }

    /// <summary>Профиль открыт Firefox: он держит parent.lock открытым без общего доступа (Windows).</summary>
    public static bool profileInUse(string dir)
    {
        var lockFile = Path.Combine(dir, "parent.lock");
        if (!File.Exists(lockFile)) return false;
        try { using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    // ─────────────────────────── состояние ───────────────────────────

    internal sealed class State
    {
        public int port { get; set; }
        public List<ProfileState> profiles { get; set; } = [];
    }

    internal sealed class ProfileState
    {
        public string dir { get; set; } = "";
        public bool userJsExisted { get; set; }
        public bool addedNewline { get; set; }
        /// <summary>Прежние значения из prefs.js: имя → литерал как в файле; отсутствовавших нет в словаре.</summary>
        public Dictionary<string, string> backup { get; set; } = [];
    }

    private static string statePath(Context ctx) => Path.Combine(ctx.filesDir, STATE_FILE);

    internal static State? loadState(Context ctx)
    {
        try { return File.Exists(statePath(ctx)) ? JsonSerializer.Deserialize<State>(File.ReadAllText(statePath(ctx))) : null; }
        catch (Exception) { return null; }
    }

    private static void saveState(Context ctx, State? s)
    {
        if (s == null || s.profiles.Count == 0) { try { File.Delete(statePath(ctx)); } catch (Exception) { } return; }
        File.WriteAllText(statePath(ctx), JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool isConfigured(Context ctx) => loadState(ctx) is { profiles.Count: > 0 };

    // ─────────────────────────── user.js / prefs.js ───────────────────────────

    [GeneratedRegex("^\\s*user_pref\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*(?<val>.*?)\\s*\\)\\s*;\\s*$")] private static partial Regex PrefLine();

    internal static string block(int socksPort, string nl) => string.Join(nl,
    [
        BEGIN,
        "user_pref(\"network.proxy.type\", 1);",
        "user_pref(\"network.proxy.socks\", \"127.0.0.1\");",
        $"user_pref(\"network.proxy.socks_port\", {socksPort});",
        "user_pref(\"network.proxy.socks_version\", 5);",
        "user_pref(\"network.proxy.socks_remote_dns\", true);",
        "user_pref(\"network.proxy.socks5_remote_dns\", true);",
        "user_pref(\"network.proxy.share_proxy_settings\", false);",
        "user_pref(\"network.proxy.http\", \"\");",
        "user_pref(\"network.proxy.http_port\", 0);",
        "user_pref(\"network.proxy.ssl\", \"\");",
        "user_pref(\"network.proxy.ssl_port\", 0);",
        END,
    ]) + nl;

    private static string newlineOf(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    /// <summary>Убрать наш блок (от BEGIN до END включительно, с переводом строки после END).</summary>
    private static string withoutBlock(string text, bool addedNewline)
    {
        var b = text.IndexOf(BEGIN, StringComparison.Ordinal);
        if (b < 0) return text;
        var e = text.IndexOf(END, b, StringComparison.Ordinal);
        if (e < 0) return text;
        e += END.Length;
        if (e < text.Length && text[e] == '\r') e++;
        if (e < text.Length && text[e] == '\n') e++;
        if (addedNewline && b > 0 && text[b - 1] == '\n') { b--; if (b > 0 && text[b - 1] == '\r') b--; }
        return text[..b] + text[e..];
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    // ─────────────────────────── действия ───────────────────────────

    /// <summary>«Настроить Firefox»: блок прокси в user.js профиля(ей); прежние значения запоминаются один раз.</summary>
    public static Outcome configure(Context ctx, int socksPort)
    {
        var found = findProfiles();
        if (found.Count == 0)
            return new Outcome(false, "Firefox не найден. Установите Firefox и запустите его один раз — потом нажмите «Настроить Firefox».");
        var state = loadState(ctx) ?? new State();
        var running = false;
        foreach (var p in found)
        {
            var userJs = Path.Combine(p.dir, "user.js");
            var ps = state.profiles.FirstOrDefault(x => x.dir == p.dir);
            var text = File.Exists(userJs) ? File.ReadAllText(userJs) : "";
            if (ps == null)
            {
                ps = new ProfileState { dir = p.dir, userJsExisted = File.Exists(userJs) };
                var prefs = Path.Combine(p.dir, "prefs.js");
                if (File.Exists(prefs))
                    foreach (var line in File.ReadAllLines(prefs))
                        if (PrefLine().Match(line) is { Success: true } m && MANAGED.Contains(m.Groups["name"].Value))
                            ps.backup[m.Groups["name"].Value] = m.Groups["val"].Value;
                state.profiles.Add(ps);
            }
            else text = withoutBlock(text, ps.addedNewline);   // уже настроен — обновить блок (напр. новый порт)
            var nl = newlineOf(text);
            ps.addedNewline = text.Length > 0 && !text.EndsWith('\n');
            File.WriteAllText(userJs, text + (ps.addedNewline ? nl : "") + block(socksPort, nl), Utf8NoBom);
            running |= profileInUse(p.dir);
        }
        state.port = socksPort;
        saveState(ctx, state);
        var names = string.Join(", ", found.Select(p => $"«{p.name}»"));
        return new Outcome(true, $"Firefox настроен на X-Gates (SOCKS5 127.0.0.1:{socksPort}, DNS через прокси), профиль {names}. " +
            (running ? RESTART_TEXT : "Откройте Firefox."), running);
    }

    /// <summary>Порт сменили в настройках X-Gates — переписать блок, если Firefox настроен нами.</summary>
    public static Outcome? refreshPort(Context ctx, int socksPort)
    {
        var state = loadState(ctx);
        if (state is not { profiles.Count: > 0 } || state.port == socksPort) return null;
        return configure(ctx, socksPort);
    }

    /// <summary>«Вернуть как было»: убрать блок из user.js и вернуть прежние значения в prefs.js. Firefox должен быть закрыт.</summary>
    public static Outcome restore(Context ctx)
    {
        var state = loadState(ctx);
        if (state is not { profiles.Count: > 0 }) return new Outcome(true, "Firefox не был настроен из X-Gates — возвращать нечего.");
        var busy = state.profiles.Where(p => Directory.Exists(p.dir) && profileInUse(p.dir)).ToList();
        if (busy.Count > 0)
            return new Outcome(false, "Закройте Firefox (все окна) и нажмите «Вернуть как было» ещё раз: открытый Firefox при выходе перезапишет настройки.");
        foreach (var ps in state.profiles.ToList())
        {
            if (Directory.Exists(ps.dir))
            {
                var userJs = Path.Combine(ps.dir, "user.js");
                if (File.Exists(userJs))
                {
                    var rest = withoutBlock(File.ReadAllText(userJs), ps.addedNewline);
                    if (rest.Length == 0 && !ps.userJsExisted) File.Delete(userJs);
                    else File.WriteAllText(userJs, rest, Utf8NoBom);
                }
                var prefs = Path.Combine(ps.dir, "prefs.js");
                if (File.Exists(prefs))
                {
                    var text = File.ReadAllText(prefs);
                    var nl = newlineOf(text);
                    var kept = text.Split('\n').Select(l => l.TrimEnd('\r'))
                        .Where(l => !(PrefLine().Match(l) is { Success: true } m && MANAGED.Contains(m.Groups["name"].Value))).ToList();
                    while (kept.Count > 0 && kept[^1].Length == 0) kept.RemoveAt(kept.Count - 1);
                    kept.AddRange(MANAGED.Where(ps.backup.ContainsKey).Select(n => $"user_pref(\"{n}\", {ps.backup[n]});"));
                    File.WriteAllText(prefs, string.Join(nl, kept) + nl, Utf8NoBom);
                }
            }
            state.profiles.Remove(ps);
        }
        saveState(ctx, state);
        return new Outcome(true, "Готово: прокси в Firefox снят, прежние настройки возвращены. Откройте Firefox.");
    }

    /// <summary>Строка состояния под кнопками.</summary>
    public static string statusText(Context ctx, int socksPort)
    {
        var st = loadState(ctx);
        if (st is { profiles.Count: > 0 })
            return st.port == socksPort
                ? $"Firefox настроен на X-Gates (SOCKS5 127.0.0.1:{socksPort}), настройка действует после перезапуска Firefox. Пока X-Gates не запущен, Firefox сайты не откроет — тогда «Вернуть как было»."
                : $"Firefox настроен на старый порт {st.port} — нажмите «Настроить Firefox» ещё раз.";
        var found = findProfiles();
        return found.Count == 0
            ? "Firefox не найден. Установите Firefox и запустите его один раз."
            : $"Firefox найден (профиль {string.Join(", ", found.Select(p => $"«{p.name}»"))}). Кнопка пропишет прокси X-Gates только в Firefox — системные настройки Windows не меняются. Открытый Firefox после этого нужно закрыть и открыть заново.";
    }
}
