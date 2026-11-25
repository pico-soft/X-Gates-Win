using XGates.Core.Platform;
using XGates.Core.Subscription;

namespace XGates.Core.Tests.Subscription;

/// <summary>Повреждённый реестр сохраняется для расследования, а последняя валидная копия остаётся доступной.</summary>
[Collection("stores")]
public sealed class StoreRecoveryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "xgates-store-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly Context ctx;
    private string Primary => Path.Combine(dir, "sources.json");
    private string Backup => Path.Combine(dir, "sources.json.bak");

    public StoreRecoveryTests() => ctx = new Context(dir);

    public void Dispose()
    {
        SubscriptionStore.beforeQuarantineForTest = null;
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static SourcesFile State(string id) => new()
    {
        sources = [new SubSource { id = id, name = "source-" + id }],
    };

    private string[] Quarantines() => Directory.GetFiles(dir, "sources.json.corrupt-*");

    [Fact]
    public void Save_creates_and_rotates_valid_backup()
    {
        SubscriptionStore.save(ctx, State("old"));
        Assert.Equal("old", Assert.Single(SubscriptionStore.load(ctx).sources).id);
        Assert.Equal("old", Assert.Single(KJson.decodeFromString<SourcesFile>(File.ReadAllText(Backup)).sources).id);

        SubscriptionStore.save(ctx, State("new"));

        Assert.Equal("new", Assert.Single(SubscriptionStore.load(ctx).sources).id);
        Assert.Equal("old", Assert.Single(KJson.decodeFromString<SourcesFile>(File.ReadAllText(Backup)).sources).id);
    }

    [Fact]
    public void Corrupt_primary_is_quarantined_and_backup_is_loaded()
    {
        SubscriptionStore.save(ctx, State("recoverable"));
        var backup = File.ReadAllText(Backup);
        const string broken = "{ незаконченный json";
        File.WriteAllText(Primary, broken);

        var restored = SubscriptionStore.load(ctx);

        Assert.Equal("recoverable", Assert.Single(restored.sources).id);
        Assert.False(File.Exists(Primary));
        Assert.Equal(backup, File.ReadAllText(Backup));
        Assert.Equal(broken, File.ReadAllText(Assert.Single(Quarantines())));
    }

    [Fact]
    public void Corrupt_primary_without_backup_cannot_be_overwritten_by_update()
    {
        const string broken = "{ незаконченный json";
        File.WriteAllText(Primary, broken);

        Assert.Throws<InvalidDataException>(() => SubscriptionStore.update(ctx, f => f with { migratedLegacy = true }));

        Assert.False(File.Exists(Primary));
        Assert.Equal(broken, File.ReadAllText(Assert.Single(Quarantines())));
    }

    [Fact]
    public void Failed_quarantine_preserves_original_and_blocks_save()
    {
        SubscriptionStore.save(ctx, State("old"));
        var backup = File.ReadAllText(Backup);
        const string broken = "{ незаконченный json";
        File.WriteAllText(Primary, broken);
        SubscriptionStore.beforeQuarantineForTest = (_, _) => throw new IOException("injected quarantine failure");

        try
        {
            Assert.Throws<IOException>(() => SubscriptionStore.save(ctx, State("replacement")));
        }
        finally
        {
            SubscriptionStore.beforeQuarantineForTest = null;
        }

        Assert.Equal(broken, File.ReadAllText(Primary));
        Assert.Equal(backup, File.ReadAllText(Backup));
        Assert.Empty(Quarantines());
    }

    [Fact]
    public void Missing_primary_uses_backup_without_quarantine()
    {
        SubscriptionStore.save(ctx, State("from-backup"));
        File.Delete(Primary);

        var restored = SubscriptionStore.load(ctx);

        Assert.Equal("from-backup", Assert.Single(restored.sources).id);
        Assert.Empty(Quarantines());
        Assert.False(File.Exists(Primary));
    }
}
