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

    public void onImportFile(string path) => KThread.start(() =>
    {
        MonitorCoordinator.abortFullTestAndWait();   // Промпт 93.E: не менять реестр под идущим тестом
        string body;
        try { body = File.ReadAllText(path); } catch (Exception) { body = ""; }
        if (body.IsBlank()) { ui(() => status = "файл пуст/не прочитан"); return; }
        var name = Path.GetFileName(path).IfBlank("файл");
        var (_, s) = SubscriptionManager.addLocalFromBody(context, body, name);
        ui(() => { status = $"из файла: +{s.added}  дубли={s.duplicates}  неподдерж={s.unsupported}  ошибок={s.invalid}"; reloadAndNotify(); });
    }, "import-file");

    public void onAddUrl(string url, string name)
    {
        if (url.IsBlank()) { ui(() => status = "введите URL"); return; }
        KThread.start(() =>
        {
            MonitorCoordinator.abortFullTestAndWait();   // Промпт 93.E: сначала остановить тест, потом менять реестр
            var id = SubscriptionManager.addUrl(context, url, name);
            if (id == null) { ui(() => { status = "дубликат URL или пусто"; reloadAndNotify(); }); return; }
            var s = SubscriptionManager.refreshOne(context, id);
            ui(() => { status = s.ok ? $"добавлено: +{s.added}" : $"источник добавлен, обновление: {s.error}"; reloadAndNotify(); });
        }, "add-url");
    }

    public void onAddPaste(string text)
    {
        if (text.IsBlank()) { ui(() => status = "вставьте ссылки"); return; }
        KThread.start(() =>
        {
            MonitorCoordinator.abortFullTestAndWait();   // Промпт 93.E
            var (_, s) = SubscriptionManager.addLocalFromBody(context, text);
            ui(() => { status = $"вставка: +{s.added}  дубли={s.duplicates}  неподдерж={s.unsupported}  ошибок={s.invalid}"; reloadAndNotify(); });
        }, "add-paste");
    }

    public void onRefreshAll() => ui(() =>
    {
        if (refreshing) return;
        if (!sources.Any(it => it.enabled && it.url.IsNotBlank())) { status = "нет включённых подписок с URL"; return; }
        refreshing = true; status = "обновление…";
        KThread.start(() =>
        {
            MonitorCoordinator.abortFullTestAndWait();   // Промпт 93.E: не обновлять реестр под идущим тестом
            var res = SubscriptionManager.refreshAllEnabled(context, cancelled: () => false, onEach: (_, _) => ui(reload));
            var okN = res.Values.Count(it => it.ok); var failN = res.Values.Count(it => !it.ok);
            ui(() => { refreshing = false; status = $"обновлено: {okN} ок, {failN} ошибок"; reloadAndNotify(); });
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
