using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XGates.Core.Platform;
using XGates.Core.Subscription;

namespace XGates.Core.Tests.Subscription;

/**
 * Паритет SubscriptionManager с Android: сценарий (импорт трёх тел, отказ HTML, замеры, переимпорт, выкл/вкл,
 * переименование, удаление) даёт ТОТ ЖЕ sources.json после каждого шага, что настоящий Kotlin
 * (tools/golden/kotlin/GoldenSubscriptionScenarioTest.kt) — с точностью до UUID и меток времени.
 */
[Collection("stores")]
public class SubscriptionScenarioTests
{
    private static string Golden => Path.Combine(AppContext.BaseDirectory, "Golden");
    private static string CorpusDir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "XGatesWin.slnx"))) d = d.Parent;
            return Path.Combine(d!.FullName, "tools", "golden", "corpus");
        }
    }

    private static bool Same(JsonNode? a, JsonNode? b) => StoreCompatTests.Same(a, b);

    private static readonly Regex Uuid = new("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}");
    private static readonly Regex Ts = new("\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}");

    /// <summary>sources.json → без случайных UUID (по порядку появления → S1, S2…) и меток времени.</summary>
    private static JsonNode? Normalize(string json)
    {
        if (json == "null") return null;
        var map = new Dictionary<string, string>();
        var s = Uuid.Replace(json, m => map.TryGetValue(m.Value, out var v) ? v : map[m.Value] = "S" + (map.Count + 1));
        s = Ts.Replace(s, "TS");
        return JsonNode.Parse(s);
    }

    [Fact]
    public void SubscriptionManager_scenario_matches_kotlin()
    {
        var steps = File.ReadAllLines(Path.Combine(Golden, "subscription-scenario.jsonl"), Encoding.UTF8)
            .Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        var dir = Path.Combine(Path.GetTempPath(), "xgates-scn-" + Guid.NewGuid().ToString("N"));
        var ctx = new Context(dir);
        try
        {
            string body(string name) => File.ReadAllText(Path.Combine(CorpusDir, "bodies", name), new UTF8Encoding(false));
            var got = new List<(string step, string sources)>();
            void snap(string step)
            {
                var f = Path.Combine(dir, "sources.json");
                got.Add((step, File.Exists(f) ? File.ReadAllText(f) : "null"));
            }
            string b(bool v) => v ? "true" : "false";
            SubscriptionManager.init(ctx); snap("init");
            var (a, ra) = SubscriptionManager.addLocalFromBody(ctx, body("plain.txt"), "A"); snap($"addA {b(ra.ok)} {ra.added} {ra.invalid}");
            var (bb, rb) = SubscriptionManager.addLocalFromBody(ctx, body("b64-urlsafe-nopad.txt"), "B"); snap($"addB {b(rb.ok)} {rb.added}");
            var (c, rc) = SubscriptionManager.addLocalFromBody(ctx, body("json-array.json"), "C"); snap($"addC {b(rc.ok)} {rc.added}");
            var (_, rd) = SubscriptionManager.addLocalFromBody(ctx, body("html-error.txt"), "D"); snap($"addD {b(rd.ok)} {rd.error}");
            var keys = SubscriptionStore.load(ctx).servers.Select(it => SubscriptionManager.serverKey(it.profile)).ToList();
            SubscriptionManager.applyPingResults(ctx, new Dictionary<string, int> { [keys[0]] = 120, [keys[1]] = -1 }); snap("ping");
            SubscriptionManager.applySpeedResultsTagged(ctx, new Dictionary<string, (double, bool)> { [keys[0]] = (12.5, true), [keys[2]] = (-1.0, false) }); snap("speed");
            SubscriptionManager.applySpeedResults(ctx, new Dictionary<string, double> { [keys[0]] = -1.0, [keys[3]] = 3.0 }); snap("speed2");
            var r2 = SubscriptionManager.importInto(ctx, a, body("plain-with-comments.txt"), "re"); snap($"reimportA {b(r2.ok)} {r2.added}");
            SubscriptionManager.setEnabled(ctx, bb, false); snap("disableB");
            SubscriptionManager.rename(ctx, c, "  Новое имя  "); snap("renameC");
            SubscriptionManager.remove(ctx, a); snap("removeA");
            SubscriptionManager.setEnabled(ctx, bb, true); snap("enableB");
            got.Add(("allServers", string.Join("\n", SubscriptionManager.allServers(ctx).Select(SubscriptionManager.serverKey))));

            Assert.Equal(steps.Count, got.Count);
            for (int i = 0; i < steps.Count; i++)
            {
                var ktStep = steps[i]["step"]!.GetValue<string>();
                var ktSources = steps[i]["sources"]!.GetValue<string>();
                Assert.Equal(ktStep, got[i].step);
                if (ktStep == "allServers") { Assert.Equal(ktSources, got[i].sources); continue; }
                Assert.True(Same(Normalize(ktSources), Normalize(got[i].sources)),
                    $"шаг «{ktStep}»: sources.json отличается\n--kt--\n{ktSources}\n--cs--\n{got[i].sources}");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
