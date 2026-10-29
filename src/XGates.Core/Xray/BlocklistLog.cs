using XGates.Core.Compat;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Xray.Link;

namespace XGates.Core.Xray;

/**
 * ПРИБОРЫ по стоп-листу (Промпт 73.C): подробный дамп КАЖДОГО пересчёта фильтра под тегом "Blocklist",
 * гейт — «Подробные логи» (settings.verboseLogs). Цель — поймать ИНВЕРСИЮ (скрываются серверы, у которых
 * слова НЕТ), которую чтение кода не объясняет. Приборы важнее объяснений (Промпт 73.B): пока причина
 * не найдена — вопрос ОТКРЫТ ([[overrides-and-blocklist-spec]]). Порт 1:1 с xray/BlocklistLog.kt.
 *
 * Предикаты ВЫЧИСЛЯЕМ ровно как [ServerFilter]/[Blocklist] — не своя копия логики блокировки, а те же
 * функции (matchesWord/isServerBlocked/protocolAllowed); отличие только в том, что здесь ещё и печатаем,
 * КАКОЕ слово совпало (в проде это не нужно).
 */
public static class BlocklistLog
{
    private const string TAG = "Blocklist";

    public static void dump(
        Context context,
        IReadOnlyList<ServerProfile> filterServers,   // ровно тот список, что фильтрует главный экран (`servers`)
        AppSettings settings,
        Blocklist blocklist,
        Func<ServerProfile, int?> effPing,
        Func<ServerProfile, double?> effSpeed,
        string cause)
    {
        if (!settings.verboseLogs) return;
        var chipServers = SubscriptionManager.allServers(context);   // источник чипа «слово (N)»

        Log.i(TAG, $"──────── пересчёт фильтра ({cause}) ────────");
        var same = filterServers.Count == chipServers.Count;
        Log.i(TAG, $"выборка ФИЛЬТРА (BootScreen.servers)={filterServers.Count} · выборка ЧИПА (allServers)={chipServers.Count}" +
            (same ? " (размеры совпали)" : "  ⚠ РАЗНЫЕ РАЗМЕРЫ"));
        var fKeys = filterServers.Select(SubscriptionManager.serverKey).ToHashSet();
        var cKeys = chipServers.Select(SubscriptionManager.serverKey).ToHashSet();
        var onlyFilter = fKeys.Except(cKeys).Count(); var onlyChip = cKeys.Except(fKeys).Count();
        if (onlyFilter > 0 || onlyChip > 0)
            Log.i(TAG, $"  ⚠ РАСХОЖДЕНИЕ выборок: только в фильтре={onlyFilter}, только в чипе={onlyChip}");
        Log.i(TAG, $"слова=[{string.Join(", ", blocklist.words)}] · точечных={blocklist.servers.Count} · протоколы=[{string.Join(", ", settings.allowedProtocols)}] · minMbps={settings.minUsableMbps}");

        int byWord = 0, byPoint = 0, byProto = 0, noPing = 0, bySpeed = 0, visibleAlive = 0;
        var wordHits = new List<(string w, int n)>();   // LinkedHashMap: порядок первого появления

        foreach (var p in filterServers)
        {
            var key = SubscriptionManager.serverKey(p);
            var nameForMatch = p.remarks.IfBlank(p.address);     // ровно то, что матчит ServerFilter.isBlocked
            var custom = blocklist.customName(key);
            var display = custom ?? nameForMatch;                // ровно displayName()
            var point = blocklist.isServerBlocked(key);
            var word = blocklist.words.FirstOrDefault(w =>
                w.IsNotBlank() && (nameForMatch.Lowercase().Contains(w, StringComparison.Ordinal) || (custom != null && custom.Lowercase().Contains(w, StringComparison.Ordinal))));
            var protoOk = ServerFilter.protocolAllowed(p, settings);
            var ping = effPing(p); var speed = effSpeed(p);

            string verdict, reason;
            if (point) { byPoint++; verdict = "СКРЫТ"; reason = "точечно (serverKey)"; }
            else if (word != null)
            {
                byWord++;
                var i = wordHits.FindIndex(t => t.w == word);
                if (i < 0) wordHits.Add((word, 1)); else wordHits[i] = (word, wordHits[i].n + 1);
                verdict = "СКРЫТ"; reason = $"слово «{word}»";
            }
            else if (!protoOk) { byProto++; verdict = "СКРЫТ"; reason = $"протокол {p.protocol}"; }
            else if (ping == null || ping < 0) { noPing++; verdict = "НЕ В ЖИВЫХ"; reason = "нет пинга (в «Все» присутствует)"; }
            else if (speed != null && speed < settings.minUsableMbps) { bySpeed++; verdict = "СКРЫТ-живые"; reason = $"скорость {speed} < {settings.minUsableMbps}"; }
            else { visibleAlive++; verdict = "ВИДЕН"; reason = "—"; }
            Log.i(TAG, $"  [{verdict}] remarks=\"{p.remarks}\" display=\"{display}\" ключ=…{(key.Length > 8 ? key.Substring(key.Length - 8) : key)} → {reason}");
        }

        // «Все» = не скрыт словом/точечно/протоколом (пинг НЕ требуется); «Живые» = ещё пинг есть и скорость ок.
        var shownAll = filterServers.Count(it =>
        {
            var k = SubscriptionManager.serverKey(it);
            return !blocklist.isBlocked(it.remarks.IfBlank(it.address), blocklist.customName(k), k) && ServerFilter.protocolAllowed(it, settings);
        });
        var hits = wordHits.Count > 0 ? "{" + string.Join(", ", wordHits.Select(t => $"{t.w}={t.n}")) + "}" : "{}";
        Log.i(TAG, $"ИТОГ: скрыто словом={byWord} {hits} · точечно={byPoint} · протокол={byProto} · без-пинга(не в Живых)={noPing} · скорость={bySpeed} || ВИДНЫ: Живые={visibleAlive}, «Все»={shownAll}");
    }
}
