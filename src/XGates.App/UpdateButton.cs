using XGates.Core.Update;

namespace XGates.App;

/// <summary>Единая кнопка обновления на одном месте, как на Android: что показывает и что делает. Слова — Elyor 2026-11-02.</summary>
public static class UpdateButton
{
    public enum Action { CHECK, INSTALL, CANCEL }

    public sealed record View(string label, Action action, bool enabled);

    public static View of(UpdateFlowController.Phase phase, UpdateCheckResult? live, bool checking) => phase switch
    {
        UpdateFlowController.Phase.Downloading => new View("Отменить", Action.CANCEL, true),
        UpdateFlowController.Phase.Installing => new View("Запускаю установку…", Action.CHECK, false),
        UpdateFlowController.Phase.Checking => new View("Проверяю…", Action.CHECK, false),
        _ when checking => new View("Проверяю…", Action.CHECK, false),
        _ when live is UpdateCheckResult.Available => new View("Загрузить и обновить", Action.INSTALL, true),
        _ => new View("Проверить обновление", Action.CHECK, true),
    };
}
