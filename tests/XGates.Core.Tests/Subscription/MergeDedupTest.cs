using XGates.Core.Subscription;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests.Subscription;

/**
 * Порт app/src/test/…/MergeDedupTest.kt. Факт-проверка склейки серверов МЕЖДУ источниками по serverKey.
 * Источник A = {s1,s2,s3}, источник B = {s3,s4,s5} → пересечение по s3.
 * Ожидаем: incoming=3, mergedExisting=1, newInRegistry=2, registryTotal=5 (s3 НЕ задваивается).
 */
public class MergeDedupTest
{
    private static ServerProfile p(int id) => new()
    {
        protocol = Protocol.VLESS,
        remarks = $"srv{id}",
        address = $"10.0.0.{id}",
        port = 443,
        credential = $"uuid-{id}",
    };

    [Fact]
    public void crossSource_mergesByServerKey_noDuplicates()
    {
        ServerProfile s1 = p(1), s2 = p(2), s3 = p(3), s4 = p(4), s5 = p(5);

        // Реестр после источника A (s1,s2,s3) — по одному источнику "A".
        var regA = new[] { s1, s2, s3 }.Select(it => new ServerRecord { profile = it, sourceIds = ["A"] }).ToList();

        // Вливаем источник B (s3,s4,s5). s3 пересекается.
        var stats = SubscriptionManager.mergeStats(regA, "B", [s3, s4, s5]);

        Assert.Equal(3, stats.incoming);        // входящих
        Assert.Equal(1, stats.mergedExisting);  // склейка (пересечение по s3)
        Assert.Equal(2, stats.newInRegistry);   // новых (s4,s5)
        Assert.Equal(5, stats.registryTotal);   // итог реестра (s3 не задвоен)
    }
}
