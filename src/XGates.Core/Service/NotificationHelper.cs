namespace XGates.Core.Service;

/// <summary>
/// Уведомления (аналог service/NotificationHelper.kt). На Android — системные уведомления; на Windows их показывает
/// приложение (значок в трее, всплывающие подсказки) через <see cref="Sink"/>. Тексты — как в Android-версии.
/// По умолчанию — молча (тесты, ядро без UI).
/// </summary>
public static class NotificationHelper
{
    public enum Kind { STATUS, UPDATE, INSTALL, ENABLE_SOURCES, NO_SERVERS, ADD_SUBSCRIPTION, WHITE_LIST }

    public interface INotificationSink
    {
        void show(Kind kind, string title, string text);
        void cancel(Kind kind);
    }

    public static INotificationSink Sink { get; set; } = new Silent();

    private sealed class Silent : INotificationSink
    {
        public void show(Kind kind, string title, string text) { }
        public void cancel(Kind kind) { }
    }

    public static void notifyUpdate(XGates.Core.Platform.Context context, string firstLine) =>
        Sink.show(Kind.UPDATE, "Доступна новая версия", string.IsNullOrWhiteSpace(firstLine) ? "Откройте приложение, чтобы обновить" : firstLine);

    public static void cancelUpdate(XGates.Core.Platform.Context context) => Sink.cancel(Kind.UPDATE);

    public static void notifyInstallReady(XGates.Core.Platform.Context context) =>
        Sink.show(Kind.INSTALL, "Обновление скачано", "Нажмите, чтобы установить новую версию");

    public static void cancelInstall(XGates.Core.Platform.Context context) => Sink.cancel(Kind.INSTALL);

    public static void notifyEnableSources(XGates.Core.Platform.Context context, int sources, int servers) =>
        Sink.show(Kind.ENABLE_SOURCES, "Нет живых серверов", $"Включить {sources} выключенных источников (~{servers} серверов)? Откройте приложение.");

    public static void cancelEnableSources(XGates.Core.Platform.Context context) => Sink.cancel(Kind.ENABLE_SOURCES);

    public static void notifyNoServers(XGates.Core.Platform.Context context) =>
        Sink.show(Kind.NO_SERVERS, "Нет рабочих серверов", "Связь через туннель сейчас недоступна — приложение перебрало все варианты. Откройте, чтобы обновить подписки.");

    public static void cancelNoServers(XGates.Core.Platform.Context context) => Sink.cancel(Kind.NO_SERVERS);

    public static void notifyAddSubscription(XGates.Core.Platform.Context context) =>
        Sink.show(Kind.ADD_SUBSCRIPTION, "Нет рабочих серверов", "Добавьте подписку: откройте «Подписки» и вставьте ссылку/URL или конфигурацию текстом.");

    public static void notifyWhiteListEnabled(XGates.Core.Platform.Context context) =>
        Sink.show(Kind.WHITE_LIST, "Включены «Белые списки»", "Рабочих серверов не было — включил серверы белого списка, чтобы восстановить связь.");

    /// <summary>Аналог foreground-уведомления «Прокси активен» — на Windows подпись значка в трее.</summary>
    public static void updateStatus(string? label) => Sink.show(Kind.STATUS, "Прокси активен", label ?? "X-Gates работает");

    public static void clearStatus() => Sink.cancel(Kind.STATUS);
}
