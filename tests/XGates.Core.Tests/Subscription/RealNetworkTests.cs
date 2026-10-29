using XGates.Core.Platform;
using XGates.Core.Subscription;

namespace XGates.Core.Tests.Subscription;

/// <summary>
/// Реальная сеть (не герметично): посев и обновление ДЕФОЛТНЫХ подписок как на свежей установке.
/// По умолчанию пропускается (CI/офлайн); запуск: XGATES_NET_TESTS=1 dotnet test --filter RealNetwork
/// </summary>
[Collection("stores")]
public class RealNetworkTests
{
    [Fact]
    public void RealNetwork_seed_and_refresh_defaults()
    {
        if (Environment.GetEnvironmentVariable("XGATES_NET_TESTS") != "1") return; // офлайн-режим
        var dir = Path.Combine(Path.GetTempPath(), "xgates-real-" + Guid.NewGuid().ToString("N"));
        var ctx = new Context(dir);
        try
        {
            SubscriptionManager.init(ctx);
            Assert.True(SubscriptionManager.trySeedDefaultSource(ctx), SubscriptionManager.seedStatus.value.error);
            var res = SubscriptionManager.refreshAllEnabled(ctx);
            foreach (var src in SubscriptionManager.sources(ctx))
                Console.WriteLine($"{src.name}: enabled={src.enabled} servers={src.serverCount} outcome={src.lastOutcome} err={src.lastError}");
            Assert.All(res.Values, s => Assert.True(s.ok, s.error));
            Assert.True(SubscriptionManager.allServers(ctx).Count > 100);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
