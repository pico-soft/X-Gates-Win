using XGates.App;
using XGates.Core.Monitor;

namespace XGates.Core.Tests.Monitor;

/// <summary>Решение Elyor 2026-11-02: что качает браузер — не скорость туннеля. Поток показываем отдельно, замер он не заменяет.</summary>
[Collection("xray")]
public sealed class TunnelSpeedFlowTests : IDisposable
{
    public TunnelSpeedFlowTests() => TunnelSpeed.clear();

    public void Dispose() => TunnelSpeed.clear();

    [Fact]
    public void Browser_traffic_does_not_replace_measured_speed()
    {
        TunnelSpeed.setProbe(30.0, 15.0, 5.0, 1000L, "a");
        TunnelSpeed.setFlow(0.7, 0.1, 2000L, "a");
        var s = TunnelSpeed.snapshot();
        Assert.Equal(15.0, s.tunnelDownMbps);
        Assert.Equal(1000L, s.measuredAtMs);
        Assert.Equal(0.7, s.flowDownMbps);
    }

    [Fact]
    public void Flow_above_the_measure_raises_it()
    {
        TunnelSpeed.setProbe(null, 2.0, 1.0, 1000L, "a");
        TunnelSpeed.setFlow(9.0, 0.1, 2000L, "a");
        var s = TunnelSpeed.snapshot();
        Assert.Equal(9.0, s.tunnelDownMbps);
        Assert.Equal(1.0, s.tunnelUpMbps);
        Assert.Equal(2000L, s.measuredAtMs);
    }

    [Fact]
    public void Without_a_measure_speed_is_not_invented()
    {
        TunnelSpeed.setFlow(3.0, 0.5, 2000L, "a");
        var s = TunnelSpeed.snapshot();
        Assert.Null(s.tunnelDownMbps);
        Assert.Equal(0L, s.measuredAtMs);
        Assert.Equal(3.0, s.flowDownMbps);
    }

    [Fact]
    public void Silence_removes_the_flow_and_keeps_the_measure()
    {
        TunnelSpeed.setProbe(null, 15.0, 5.0, 1000L, "a");
        TunnelSpeed.setFlow(0.7, 0.1, 2000L, "a");
        TunnelSpeed.clearFlow("a");
        var s = TunnelSpeed.snapshot();
        Assert.Null(s.flowDownMbps);
        Assert.Equal(15.0, s.tunnelDownMbps);
    }

    [Fact]
    public void New_probe_keeps_the_flow_of_the_same_server_only()
    {
        TunnelSpeed.setFlow(0.7, 0.1, 2000L, "a");
        TunnelSpeed.setProbe(null, 15.0, 5.0, 3000L, "a");
        Assert.Equal(0.7, TunnelSpeed.snapshot().flowDownMbps);
        TunnelSpeed.setProbe(null, 15.0, 5.0, 4000L, "b");
        Assert.Null(TunnelSpeed.snapshot().flowDownMbps);
    }

    [Fact]
    public void Stale_numbers_do_not_count()
    {
        Assert.Equal(5.0, MainScreenModel.freshMbps(5.0, 1_000L, 1_000L + MainScreenModel.HEALTH_FRESH_MS));
        Assert.Equal(0.0, MainScreenModel.freshMbps(5.0, 1_000L, 1_001L + MainScreenModel.HEALTH_FRESH_MS));
        Assert.Equal(0.0, MainScreenModel.freshMbps(null, 1_000L, 2_000L));
        Assert.Equal(0.0, MainScreenModel.freshMbps(5.0, 0L, 2_000L));
    }
}
