using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XGates.Core.Subscription;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests;

/// <summary>
/// Паритет с Android: тот же корпус (tools/golden/corpus), прогнанный через НАСТОЯЩИЙ Kotlin-код
/// (tools/golden/run-golden.sh → Golden/*.jsonl), должен дать в C# ровно те же профили, конфиги xray (байт в байт)
/// и строки подписок. Корпус генерирует tools/golden/make-corpus.py.
/// </summary>
public class GoldenParityTests
{
    // Переопределение — для разовой сверки на РЕАЛЬНЫХ подписках (в репо не кладутся: в них живые пароли серверов):
    //   tools/golden/run-golden.sh <корпус> <выход>; XGATES_GOLDEN_DIR=<выход> XGATES_CORPUS_DIR=<корпус> dotnet test
    private static string GoldenDir => Environment.GetEnvironmentVariable("XGATES_GOLDEN_DIR") ?? Path.Combine(AppContext.BaseDirectory, "Golden");
    private static string CorpusDir => Environment.GetEnvironmentVariable("XGATES_CORPUS_DIR") ?? Path.Combine(FindRepoRoot(), "tools", "golden", "corpus");

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "XGatesWin.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static JsonNode? ProfileNode(ServerProfile p) => JsonSerializer.SerializeToNode(p);

    /// <summary>Kotlin пишет ошибку как «ERROR: IllegalArgumentException: msg»; в C# — ArgumentException(msg).</summary>
    private static string Attempt(Func<string> f)
    {
        try { return f(); }
        catch (ArgumentException e) { return "ERROR: IllegalArgumentException: " + e.Message; }
        catch (Exception e) { return "ERROR: " + e.GetType().Name + ": " + e.Message; }
    }

    public static IEnumerable<object[]> LinkFiles() =>
        Directory.GetFiles(GoldenDir, "links-*.jsonl")
            .Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(LinkFiles))]
    public void Links_match_kotlin(string file)
    {
        var errors = new List<string>();
        int n = 0;
        foreach (var line in File.ReadLines(Path.Combine(GoldenDir, file), Encoding.UTF8))
        {
            var g = JsonNode.Parse(line)!.AsObject();
            var input = g["in"]!.GetValue<string>();
            var kind = g["kind"]!.GetValue<string>();
            var r = ServerLinkParser.parse(input);
            n++;
            string Where() => $"[{file}#{n}] {input}";
            switch (r)
            {
                case ParseResult.Supported s:
                    if (kind != "Supported") { errors.Add($"{Where()}: C# Supported, Kotlin {kind} ({g["reason"]})"); break; }
                    var cs = ProfileNode(s.profile);
                    if (!JsonNode.DeepEquals(cs, g["profile"]))
                        errors.Add($"{Where()}: profile\n  kt: {g["profile"]!.ToJsonString()}\n  cs: {cs!.ToJsonString()}");
                    var cfg = Attempt(() => XrayConfigBuilder.build(s.profile));
                    if (cfg != g["config"]!.GetValue<string>()) errors.Add($"{Where()}: config differs\n--kt--\n{g["config"]}\n--cs--\n{cfg}");
                    var sp = Attempt(() => XrayConfigBuilder.buildForSpeedTest(s.profile, 23456));
                    if (sp != g["speedConfig"]!.GetValue<string>()) errors.Add($"{Where()}: speedConfig differs\n--kt--\n{g["speedConfig"]}\n--cs--\n{sp}");
                    break;
                case ParseResult.Unsupported u:
                    if (kind != "Unsupported" || u.scheme != g["scheme"]!.GetValue<string>() || u.raw != g["raw"]!.GetValue<string>())
                        errors.Add($"{Where()}: C# Unsupported({u.scheme}), Kotlin {kind} {g["scheme"]}");
                    break;
                case ParseResult.Invalid iv:
                    if (kind != "Invalid" || iv.reason != g["reason"]!.GetValue<string>() || iv.raw != g["raw"]!.GetValue<string>())
                        errors.Add($"{Where()}: C# Invalid({iv.reason}), Kotlin {kind} {g["reason"]}");
                    break;
            }
        }
        Assert.True(n > 0, "golden file is empty");
        Assert.True(errors.Count == 0, $"{errors.Count} расхождений из {n}:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void Bodies_match_kotlin()
    {
        var errors = new List<string>();
        int n = 0;
        foreach (var line in File.ReadLines(Path.Combine(GoldenDir, "bodies.jsonl"), Encoding.UTF8))
        {
            var g = JsonNode.Parse(line)!.AsObject();
            var file = g["file"]!.GetValue<string>();
            var body = File.ReadAllText(Path.Combine(CorpusDir, "bodies", file), new UTF8Encoding(false));
            n++;
            var decoded = new JsonArray(SubscriptionDecoder.decode(body).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            if (!JsonNode.DeepEquals(decoded, g["decoded"]))
                errors.Add($"[{file}] decoded\n  kt: {g["decoded"]!.ToJsonString()}\n  cs: {decoded.ToJsonString()}");
            var configs = new JsonArray(JsonConfigParser.parse(body).Select(ProfileNode).ToArray());
            if (!JsonNode.DeepEquals(configs, g["jsonConfigs"]))
                errors.Add($"[{file}] jsonConfigs\n  kt: {g["jsonConfigs"]!.ToJsonString()}\n  cs: {configs.ToJsonString()}");
        }
        Assert.True(n > 0, "golden file is empty");
        Assert.True(errors.Count == 0, $"{errors.Count} расхождений из {n}:\n" + string.Join("\n", errors));
    }
}
