using XGates.App;
using XGates.Core.Update;

namespace XGates.Core.Tests.App;

public sealed class UpdateProgressTests
{
    [Fact]
    public void Bar_follows_the_download()
    {
        Assert.Equal(new UpdateProgress.Bar(true, false, 0.25), UpdateProgress.of(new UpdateFlowController.Phase.Downloading(25, 100)));
        Assert.Equal(new UpdateProgress.Bar(true, false, 1.0), UpdateProgress.of(new UpdateFlowController.Phase.Downloading(150, 100)));
    }

    [Fact]
    public void Unknown_size_gives_a_running_bar_not_an_empty_one()
    {
        Assert.Equal(new UpdateProgress.Bar(true, true, 0.0), UpdateProgress.of(new UpdateFlowController.Phase.Downloading(4096, 0)));
    }

    [Fact]
    public void Bar_is_hidden_when_nothing_is_downloading()
    {
        Assert.False(UpdateProgress.of(new UpdateFlowController.Phase.Idle()).visible);
        Assert.False(UpdateProgress.of(new UpdateFlowController.Phase.Checking()).visible);
        Assert.False(UpdateProgress.of(new UpdateFlowController.Phase.Installing()).visible);
        Assert.False(UpdateProgress.of(new UpdateFlowController.Phase.Failed("нет связи")).visible);
    }
}
