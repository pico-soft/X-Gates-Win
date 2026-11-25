using XGates.Core.Platform;
using XGates.Core.Subscription;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests.Subscription;

/// <summary>Изменение источника и завершившийся замер не должны затирать друг друга устаревшим снимком.</summary>
[Collection("stores")]
public sealed class RegistryWriteTransactionTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "xgates-registry-write-" + Guid.NewGuid().ToString("N"));
    private readonly Context ctx;

    public RegistryWriteTransactionTests() => ctx = new Context(dir);

    public void Dispose()
    {
        SubscriptionManager.beforeRefreshCommitForTest = null;
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static string Body(string remarks = "one") =>
        $"vless://11111111-1111-1111-1111-111111111111@127.0.0.1:1443?type=tcp&security=none#{Uri.EscapeDataString(remarks)}";

    private (string id, string key) Seed()
    {
        var (id, result) = SubscriptionManager.addLocalFromBody(ctx, Body(), "local");
        Assert.True(result.ok, result.error);
        var profile = Assert.Single(SubscriptionManager.allServers(ctx));
        return (id, SubscriptionManager.serverKey(profile));
    }

    private ServerProfile OnlyProfile() => Assert.Single(SubscriptionStore.load(ctx).servers).profile;

    private void MeasureDuringRefreshCommit(string key, int ping)
    {
        SubscriptionManager.beforeRefreshCommitForTest = () =>
        {
            SubscriptionManager.beforeRefreshCommitForTest = null;
            SubscriptionManager.applyPingResults(ctx, new Dictionary<string, int> { [key] = ping });
        };
    }

    [Fact]
    public void Successful_refresh_does_not_overwrite_measurement_from_stale_snapshot()
    {
        var (id, key) = Seed();
        MeasureDuringRefreshCommit(key, 73);

        var result = SubscriptionManager.importInto(ctx, id, Body("fresh"), "test");

        Assert.True(result.ok, result.error);
        Assert.Equal(73, OnlyProfile().pingMs);
    }

    [Fact]
    public void Failed_refresh_status_does_not_overwrite_measurement_from_stale_snapshot()
    {
        var (id, key) = Seed();
        MeasureDuringRefreshCommit(key, 91);

        var result = SubscriptionManager.importInto(ctx, id, "текст без ссылок", "test");

        Assert.False(result.ok);
        Assert.Equal(91, OnlyProfile().pingMs);
    }

    [Fact]
    public void Rebuild_preserves_quick_speed_quality()
    {
        var (id, key) = Seed();
        SubscriptionManager.applySpeedResultsTagged(ctx, new Dictionary<string, (double, bool)> { [key] = (12.5, false) });
        Assert.False(OnlyProfile().speedFull);

        var result = SubscriptionManager.importInto(ctx, id, Body("fresh"), "test");

        Assert.True(result.ok, result.error);
        Assert.Equal(12.5, OnlyProfile().speedMbps);
        Assert.False(OnlyProfile().speedFull);
    }

    [Fact]
    public void Merge_preserves_full_speed_quality()
    {
        var parsed = ServerLinkParser.parse(Body("legacy"));
        var supported = Assert.IsType<ParseResult.Supported>(parsed);
        var existing = supported.profile with { speedMbps = 25.0, speedTestedTs = "2026-11-25 12:00", speedFull = true };
        var registry = new[] { new ServerRecord { profile = existing, sourceIds = ["old"] } };

        var (merged, _) = SubscriptionManager.mergeIntoRegistry(registry, "new", [supported.profile]);

        var profile = Assert.Single(merged).profile;
        Assert.Equal(25.0, profile.speedMbps);
        Assert.True(profile.speedFull);
    }
}
