using XGates.Core.Update;

namespace XGates.App;

/// <summary>Полоса скачивания обновления. Размер файла неизвестен — полоса бегущая: заполнять её нечем.</summary>
public static class UpdateProgress
{
    public sealed record Bar(bool visible, bool indeterminate, double fraction);

    public static Bar of(UpdateFlowController.Phase phase) => phase is UpdateFlowController.Phase.Downloading d
        ? new Bar(true, d.total <= 0, d.total > 0 ? Math.Clamp((double)d.done / d.total, 0.0, 1.0) : 0.0)
        : new Bar(false, false, 0.0);
}
