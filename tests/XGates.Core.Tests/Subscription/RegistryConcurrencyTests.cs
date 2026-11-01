using XGates.Core.Platform;
using XGates.Core.Subscription;

namespace XGates.Core.Tests.Subscription;

/// <summary>Цикл монитора и фоновая работа пишут замеры одновременно: результат одного потока не должен затирать другой.</summary>
[Collection("xray")]
public sealed class RegistryConcurrencyTests : IDisposable
{
    private const int SERVERS = 60;
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-registry-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private List<string> Keys()
    {
        var body = string.Join("\n", Enumerable.Range(1, SERVERS).Select(i => $"vless://11111111-1111-1111-1111-111111111111@127.0.0.1:{10_000 + i}?type=tcp&security=none#srv{i}"));
        SubscriptionManager.init(ctx);
        var (_, r) = SubscriptionManager.addLocalFromBody(ctx, body, "local");
        Assert.True(r.ok, r.error);
        return SubscriptionManager.allServers(ctx).Select(SubscriptionManager.serverKey).ToList();
    }

    [Fact]
    public void Parallel_ping_results_are_all_kept()
    {
        var keys = Keys();
        Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = 8 }, k =>
            SubscriptionManager.applyPingResults(ctx, new Dictionary<string, int> { [k] = 20 }));
        Assert.Equal(SERVERS, SubscriptionManager.allServers(ctx).Count(it => it.pingMs == 20));
    }

    [Fact]
    public void Parallel_speed_results_are_all_kept()
    {
        var keys = Keys();
        Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = 8 }, k =>
            SubscriptionManager.applySpeedResults(ctx, new Dictionary<string, double> { [k] = 5.0 }));
        Assert.Equal(SERVERS, SubscriptionManager.allServers(ctx).Count(it => it.speedMbps == 5.0));
    }

    [Fact]
    public void Parallel_tagged_speed_results_are_all_kept()
    {
        var keys = Keys();
        Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = 8 }, k =>
            SubscriptionManager.applySpeedResultsTagged(ctx, new Dictionary<string, (double, bool)> { [k] = (5.0, true) }));
        Assert.Equal(SERVERS, SubscriptionManager.allServers(ctx).Count(it => it.speedMbps == 5.0));
    }
}
