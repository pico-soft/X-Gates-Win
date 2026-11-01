using XGates.Core.Compat;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Subscription;

namespace XGates.App;

/// <summary>Снимок вкладки «Подписки».</summary>
public sealed record SubscriptionsSnapshot(
    IReadOnlyList<SubSource> sources,
    string status,
    bool refreshing,
    SubscriptionManager.SeedStatus seed);

/// <summary>Карточка подписки словами для человека без знаний IT (ТЗ Elyor 2026-11-01). Адрес и текст ошибки — в подсказке.</summary>
public sealed record SourceCard(string servers, string updated, string problem);

/**
 * Вкладка «Подписки» — порт состояния и обработчиков SubscriptionsScreen (MainActivity.kt): добавить URL (и сразу
 * обновить), вставить ссылки текстом, импорт из файла, вкл/выкл, переименовать, удалить, обновить все, повтор посева.
 * Любая правка реестра — ПОСЛЕ остановки идущего полного теста (Промпт 93.E). Тексты статуса — как в Android.
 */
public sealed class SubscriptionsModel : IDisposable
{
    private readonly Context context;
    private readonly IUiThread uiThread;
    private bool disposed;

    private IReadOnlyList<SubSource> sources;
    private string status = "";
    private bool refreshing;

    public event Action? changed;
    /// <summary>Реестр изменён — «Главной» перечитать серверы (в Android вкладка пересоздавалась и перечитывала сама).</summary>
    public event Action? registryChanged;

    public SubscriptionsModel(Context context, IUiThread ui)
    {
        this.context = context;
        uiThread = ui;
        sources = SubscriptionManager.sources(context);
        // Фоновый посев (успех/повтор) меняет статус → перечитать источники, чтобы список появился сам.
        SubscriptionManager.seedStatus.changed += onSeed;
    }

    public void Dispose() { disposed = true; SubscriptionManager.seedStatus.changed -= onSeed; }

    private void onSeed(object? _) => ui(() => { reload(); registryChanged?.Invoke(); });
    private void ui(Action a) { if (!disposed) uiThread.post(() => { a(); try { changed?.Invoke(); } catch (Exception) { } }); }
    private void reload() => sources = SubscriptionManager.sources(context);
    private void reloadAndNotify() { reload(); registryChanged?.Invoke(); }

    public SubscriptionsSnapshot snapshot() => new(sources, status, refreshing, SubscriptionManager.seedStatus.value);

    private static string plural(int n, string one, string few, string many)
    {
        int d = n % 10, h = n % 100;
        return d == 1 && h != 11 ? one : d is >= 2 and <= 4 && h is < 12 or > 14 ? few : many;
    }

    public static string serversText(int n) => $"{n} {plural(n, "сервер", "сервера", "серверов")}";

    private static string whenText(string ts, DateTime now)
    {
        if (KDate.parseMs(ts) is not { } ms) return "";
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        var day = t.Date == now.Date ? "сегодня" : t.Date == now.Date.AddDays(-1) ? "вчера" : t.ToString("dd.MM");
        return $"{day} в {t:HH:mm}";
    }

    public static SourceCard card(SubSource s, DateTime now)
    {
        var updated =
            string.IsNullOrWhiteSpace(s.url) ? "Добавлена вручную" :
            string.IsNullOrWhiteSpace(s.lastOkTs) ? "Ещё не обновлялась" :
            $"Обновлена {whenText(s.lastOkTs, now)}";
        var problem =
            string.IsNullOrWhiteSpace(s.lastError) ? "" :
            s.serverCount > 0 ? "Не удалось обновить. Прежний список серверов работает." :
            "Не удалось загрузить список серверов. Попробуем снова при следующем обновлении.";
        return new SourceCard(serversText(s.serverCount), updated, problem);
    }

    /// <summary>Итог добавления серверов.</summary>
    internal static string addedText(RefreshSummary s)
    {
        var unreadable = s.unsupported + s.invalid;
        return $"Добавлено серверов: {s.added}" +
            (s.duplicates > 0 ? $", уже были в списке: {s.duplicates}" : "") +
            (unreadable > 0 ? $", не удалось распознать: {unreadable}" : "");
    }

    /// <summary>Итог обновления подписок.</summary>
    public static string refreshedText(int ok, int failed) =>
        $"Обновлено подписок: {ok}" + (failed > 0 ? $", не удалось: {failed}" : "");

    public void onImportFile(string path) => KThread.start(() =>
    {
        MonitorCoordinator.abortFullTestAndWait();   // Промпт 93.E: не менять реестр под идущим тестом
        string body;
        try { body = File.ReadAllText(path); } catch (Exception) { body = ""; }
        if (body.IsBlank()) { ui(() => status = "Файл пуст или не читается"); return; }
        var name = Path.GetFileName(path).IfBlank("файл");
        var (_, s) = SubscriptionManager.addLocalFromBody(context, body, name);
        ui(() => { status = addedText(s); reloadAndNotify(); });
    }, "import-file");

    public void onAddUrl(string url, string name)
    {
        if (url.IsBlank()) { ui(() => status = "Вставьте адрес подписки"); return; }
        KThread.start(() =>
        {
            MonitorCoordinator.abortFullTestAndWait();   // Промпт 93.E: сначала остановить тест, потом менять реестр
            var id = SubscriptionManager.addUrl(context, url, name);
            if (id == null) { ui(() => { status = "Такая подписка уже есть"; reloadAndNotify(); }); return; }
            var s = SubscriptionManager.refreshOne(context, id);
            ui(() => { status = s.ok ? $"Подписка добавлена: {serversText(s.added)}" : "Подписка добавлена, но список серверов пока не загрузился"; reloadAndNotify(); });
        }, "add-url");
    }

    public void onAddPaste(string text)
    {
        if (text.IsBlank()) { ui(() => status = "Вставьте ссылки"); return; }
        KThread.start(() =>
        {
            MonitorCoordinator.abortFullTestAndWait();   // Промпт 93.E
            var (_, s) = SubscriptionManager.addLocalFromBody(context, text);
            ui(() => { status = addedText(s); reloadAndNotify(); });
        }, "add-paste");
    }

    public void onRefreshAll() => ui(() =>
    {
        if (refreshing) return;
        if (!sources.Any(it => it.enabled && it.url.IsNotBlank())) { status = "Нет включённых подписок для обновления"; return; }
        refreshing = true; status = "Обновляю…";
        KThread.start(() =>
        {
            MonitorCoordinator.abortFullTestAndWait();   // Промпт 93.E: не обновлять реестр под идущим тестом
            var res = SubscriptionManager.refreshAllEnabled(context, cancelled: () => false, onEach: (_, _) => ui(reload));
            var okN = res.Values.Count(it => it.ok); var failN = res.Values.Count(it => !it.ok);
            ui(() => { refreshing = false; status = refreshedText(okN, failN); reloadAndNotify(); });
        }, "refresh-subs");
    });

    public void onToggle(string id, bool enabled) => KThread.start(() =>
    {
        MonitorCoordinator.abortFullTestAndWait();
        SubscriptionManager.setEnabled(context, id, enabled);
        ui(reloadAndNotify);
    }, "toggle-source");

    public void rename(string id, string name) => ui(() => { SubscriptionManager.rename(context, id, name); reloadAndNotify(); });

    /// <summary>Текст подтверждения удаления (как в диалоге Android).</summary>
    public string deleteConfirmText(SubSource src) =>
        $"«{src.name}»\nИсчезнет серверов: {SubscriptionManager.serversLostOnRemove(context, src.id)} (только те, которых нет в других подписках).";

    public void remove(string id) => KThread.start(() =>
    {
        MonitorCoordinator.abortFullTestAndWait();
        SubscriptionManager.remove(context, id);
        ui(reloadAndNotify);
    }, "remove-source");

    /// <summary>Пр.136: «Повторить» загрузку рекомендуемого списка.</summary>
    public void retrySeed()
    {
        ui(() => status = "Повторяю загрузку рекомендуемого списка…");
        KThread.start(() => DefaultSeeder.maybeSeed(context, "manual", force: true), "seed-manual");
    }
}
