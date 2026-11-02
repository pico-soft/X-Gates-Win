using XGates.App;
using XGates.Core.Update;
using static XGates.Core.Update.UpdateFlowController;

namespace XGates.Core.Tests.App;

public sealed class UpdateButtonTests
{
    private static readonly UpdateCheckResult available = new UpdateCheckResult.Available(7, "0.7 beta", "", default!, ["https://example.com/a.msi"], 1L);
    private static readonly UpdateCheckResult upToDate = new UpdateCheckResult.UpToDate(6, "0.6 beta");

    [Fact]
    public void New_release_replaces_check_with_download_and_update()
    {
        var b = UpdateButton.of(new Phase.Idle(), available, checking: false);
        Assert.Equal(new UpdateButton.View("Загрузить и обновить", UpdateButton.Action.INSTALL, true), b);
    }

    [Fact]
    public void Without_a_release_the_button_checks()
    {
        Assert.Equal(new UpdateButton.View("Проверить обновление", UpdateButton.Action.CHECK, true), UpdateButton.of(new Phase.Idle(), upToDate, false));
        Assert.Equal(UpdateButton.Action.CHECK, UpdateButton.of(new Phase.Idle(), null, false).action);
    }

    [Fact]
    public void Failed_download_can_be_retried_with_the_same_button() =>
        Assert.Equal(UpdateButton.Action.INSTALL, UpdateButton.of(new Phase.Failed("нет связи"), available, false).action);

    [Fact]
    public void Download_can_be_cancelled_from_the_same_place() =>
        Assert.Equal(new UpdateButton.View("Отменить", UpdateButton.Action.CANCEL, true), UpdateButton.of(new Phase.Downloading(1, 2), available, false));

    [Fact]
    public void Button_waits_while_something_is_running()
    {
        Assert.False(UpdateButton.of(new Phase.Idle(), upToDate, checking: true).enabled);
        Assert.False(UpdateButton.of(new Phase.Idle(), available, checking: true).enabled);
        Assert.False(UpdateButton.of(new Phase.Checking(), available, false).enabled);
        Assert.False(UpdateButton.of(new Phase.Installing(), available, false).enabled);
    }
}
