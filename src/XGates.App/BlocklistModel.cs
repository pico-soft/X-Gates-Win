using XGates.Core.Compat;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using static XGates.App.UiFormat;

namespace XGates.App;

/// <summary>Слово-правило и сколько серверов оно скрывает (WordChip Kotlin).</summary>
public sealed record WordInfo(string word, int count);

/// <summary>Итог попытки добавить слово: добавлено / нужно подтверждение «скрыть почти все?» / нечего добавлять.</summary>
public abstract record AddWordResult
{
    private AddWordResult() { }
    public sealed record Added : AddWordResult;
    public sealed record Ignored : AddWordResult;
    /// <summary>Слово скроет почти все серверы — спросить (текст — как в диалоге Android).</summary>
    public sealed record NeedsConfirm(string word, int hides, int total) : AddWordResult
    {
        public string title => "Скрыть почти все?";
        public string text => $"Слово «{word}» скроет {hides} из {total} серверов — почти все. Похоже, провайдер называет их все так. " +
                              "Чтобы скрыть только часть, используйте более узкое слово или точечно (тап по строке → «В стоп-лист»).";
    }
}

/**
 * Раздел «Стоп-лист» вкладки «Настройки» — порт BlocklistSection (MainActivity.kt): слова-правила со счётчиком
 * скрываемых серверов (по ОБОИМ именам: исходному и своему), защита от «скрылось всё», персонально заблокированные
 * с разблокировкой. Состояние — в BlocklistStore (как в Android), модель лишь считает и вызывает.
 */
public sealed class BlocklistModel(Context context)
{
    /// <summary>Пары (исходное имя, своё) всех серверов — счётчик учитывает оба (D3), иначе разойдётся с фактом.</summary>
    private List<(string o, string? c)> serverNames()
    {
        var bl = BlocklistStore.current();
        return SubscriptionManager.allServers(context).Select(p => (providerName(p), bl.customName(SubscriptionManager.serverKey(p)))).ToList();
    }

    public IReadOnlyList<WordInfo> words()
    {
        var names = serverNames();
        var bl = BlocklistStore.current();
        return bl.words.Select(w => new WordInfo(w, bl.countForWord(w, names))).ToList();
    }

    /// <summary>Персонально заблокированные: (ключ, что показать).</summary>
    public IReadOnlyList<(string key, string name)> blockedServers() =>
        BlocklistStore.current().servers.Select(s => (s.serverKey, s.name.IfBlank(s.serverKey))).ToList();

    /// <summary>
    /// Добавить слово. «Почти все» (≥80% и больше одного; при списке ≤2 не мешаем) — сначала подтверждение
    /// ([confirmed] = true после «Всё равно добавить»).
    /// </summary>
    public AddWordResult tryAddWord(string input, bool confirmed = false)
    {
        var w = input.KTrim();
        if (w.IsBlank()) return new AddWordResult.Ignored();
        if (!confirmed)
        {
            var names = serverNames();
            var total = names.Count;
            var n = BlocklistStore.current().countForWord(w, names);
            if (total > 2 && n >= 2 && n >= (total * 4 + 4) / 5) return new AddWordResult.NeedsConfirm(w, n, total);
        }
        return BlocklistStore.addWord(context, w) ? new AddWordResult.Added() : new AddWordResult.Ignored();
    }

    public void removeWord(string word) => BlocklistStore.removeWord(context, word);

    public void unblock(string serverKey) => BlocklistStore.unblockServer(context, serverKey);
}
