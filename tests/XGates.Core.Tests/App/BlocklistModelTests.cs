using XGates.App;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Tests.Libv2ray;

namespace XGates.Core.Tests.App;

/// <summary>
/// «Стоп-лист» в настройках (BlocklistSection Kotlin): счётчик слова по обоим именам, защита «скрыть почти все?»
/// (≥80% и больше одного; списки ≤2 — без вопроса), добавление после подтверждения, удаление слова, разблокировка.
/// </summary>
[Collection("xray")]
public sealed class BlocklistModelTests : IDisposable
{
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-bl-" + Guid.NewGuid().ToString("N")));

    public BlocklistModelTests()
    {
        BlocklistStore.init(ctx);   // свежий каталог → засев дефолтных слов (RU/BY), как на Android
        SubscriptionManager.init(ctx);
    }

    public void Dispose() { try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { } }

    [Fact]
    public void Words_count_confirm_almost_all_and_unblock()
    {
        var u = LocalTestNet.Uuid;
        var body = string.Join("\n", Enumerable.Range(1, 5).Select(i => $"vless://{u}@10.0.2.{i}:443?type=tcp#Provider-NL-{i}"))
                   + $"\nvless://{u}@10.0.2.9:443?type=tcp#Fast-DE";
        SubscriptionManager.addLocalFromBody(ctx, body, "local");
        var m = new BlocklistModel(ctx);
        Assert.Contains(m.words(), w => w.word == "москва" && w.count == 0);   // дефолтные слова на месте

        // «nl» скроет 5 из 6 (≥80%) → сначала вопрос.
        var r = Assert.IsType<AddWordResult.NeedsConfirm>(m.tryAddWord("  NL "));
        Assert.Equal((5, 6), (r.hides, r.total));
        Assert.StartsWith("Слово «NL» скроет 5 из 6 серверов — почти все.", r.text);
        Assert.DoesNotContain(m.words(), w => w.word == "nl");
        Assert.IsType<AddWordResult.Added>(m.tryAddWord("NL", confirmed: true));
        Assert.Contains(m.words(), w => w.word == "nl" && w.count == 5);   // хранится в нижнем регистре

        // Узкое слово — без вопроса; дубль/пусто — игнор.
        Assert.IsType<AddWordResult.Added>(m.tryAddWord("fast"));     // 1 из 6
        Assert.IsType<AddWordResult.Ignored>(m.tryAddWord("FAST"));
        Assert.IsType<AddWordResult.Ignored>(m.tryAddWord("   "));

        // Счётчик учитывает СВОЁ имя: переименованный в «Мой-NL» сервер DE считается и по «nl».
        var de = SubscriptionManager.allServers(ctx).Single(p => p.address == "10.0.2.9");
        BlocklistStore.rename(ctx, [(SubscriptionManager.serverKey(de), "Fast-DE")], "Мой-NL", JavaSystem.currentTimeMillis());
        Assert.Contains(m.words(), w => w.word == "nl" && w.count == 6);

        m.removeWord("nl");
        Assert.DoesNotContain(m.words(), w => w.word == "nl");

        BlocklistStore.blockServer(ctx, SubscriptionManager.serverKey(de), "Мой-NL", JavaSystem.currentTimeMillis());
        Assert.Equal((SubscriptionManager.serverKey(de), "Мой-NL"), Assert.Single(m.blockedServers()));
        m.unblock(SubscriptionManager.serverKey(de));
        Assert.Empty(m.blockedServers());
    }
}
