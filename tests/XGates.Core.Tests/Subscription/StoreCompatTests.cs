using System.Text;
using System.Text.Json.Nodes;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Traffic;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests.Subscription;

/// <summary>Сторы — глобальные синглтоны (как object в Kotlin): тесты, трогающие их, — последовательно.</summary>
[CollectionDefinition("stores", DisableParallelization = true)]
public class StoresCollection { }

/**
 * Паритет хранилищ с Android: C# читает JSON, записанный Kotlin-кодом (tools/golden/kotlin/GoldenStoresTest.kt),
 * и пишет его обратно эквивалентно — данные переносимы между платформами.
 */
[Collection("stores")]
public class StoreCompatTests
{
    private static string Golden => Path.Combine(AppContext.BaseDirectory, "Golden");
    /// <summary>Сравнение JSON по смыслу: числа — по значению (Kotlin пишет 2.0, System.Text.Json — 2).</summary>
    internal static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.TryGetValue<double>(out var da) && vb.TryGetValue<double>(out var db)) return da == db;
            return JsonNode.DeepEquals(a, b);
        }
        if (a is JsonArray aa && b is JsonArray ab) return aa.Count == ab.Count && aa.Zip(ab).All(t => Same(t.First, t.Second));
        if (a is JsonObject oa && b is JsonObject ob)
            return oa.Count == ob.Count && oa.All(kv => ob.TryGetPropertyValue(kv.Key, out var v) && Same(kv.Value, v));
        return false;
    }

    [Theory]
    [InlineData("settings-default.json")]
    [InlineData("settings-custom.json")]
    public void AppSettings_reads_and_writes_android_json(string file)
    {
        var kt = File.ReadAllText(Path.Combine(Golden, "stores", file));
        var s = KJson.decodeFromString<AppSettings>(kt);
        if (file == "settings-default.json") Assert.Equal(new AppSettings(), s);
        else
        {
            Assert.Equal(1.5, s.speedWarmupSec);
            Assert.Equal(1500, s.speedWarmupMs);
            Assert.Equal(3, s.pingPool);
            Assert.True(s.allowedProtocols.SetEquals([Protocol.TROJAN, Protocol.VLESS]));
            Assert.Equal("https://example.com/x?y=1&z=«»", s.directProbeUrl);
        }
        Assert.True(Same(JsonNode.Parse(kt), JsonNode.Parse(KJson.encodeToString(s))), "C# пишет тот же JSON, что Kotlin");
    }

    [Fact]
    public void Blocklist_and_traffic_read_android_json()
    {
        var bl = File.ReadAllText(Path.Combine(Golden, "stores", "blocklist.json"));
        var b = KJson.decodeFromString<Blocklist>(bl);
        Assert.Equal(BlocklistStore.DEFAULT_WORDS, b.words);
        Assert.True(b.isServerBlocked("k1"));
        Assert.Equal("Моё", b.customName("k2"));
        Assert.True(b.isPaused("k3"));
        Assert.True(b.matchesWord("Сервер Москва-1"));
        Assert.True(Same(JsonNode.Parse(bl), JsonNode.Parse(KJson.encodeToString(b))));

        var tr = File.ReadAllText(Path.Combine(Golden, "stores", "traffic.json"));
        var t = KJson.decodeFromString<TrafficFile>(tr, KJson.Compact);
        Assert.Equal(30, t.days["2026-10-29"].test);
        Assert.Equal(new DayBucket(4, 5, 6), t.today);
        Assert.True(Same(JsonNode.Parse(tr), JsonNode.Parse(KJson.encodeToString(t, KJson.Compact))));
    }
}
