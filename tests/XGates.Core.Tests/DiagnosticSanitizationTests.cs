using XGates.Core.Platform;
using XGates.Core.Subscription;

namespace XGates.Core.Tests.Crash;

/// <summary>Приватный адрес подписки не должен уходить ни в обычный лог, ни в сохраняемую диагностику.</summary>
public sealed class DiagnosticSanitizationTests : IDisposable
{
    private const string PRIVATE_URL = "https://panel.example.test/subscriptions/user-42?access=sw0rdFish-1234567890";
    private readonly string root = Path.Combine(Path.GetTempPath(), "xgates-diagnostics-" + Guid.NewGuid().ToString("N"));
    private readonly Context ctx;
    private readonly Log.ILogSink savedSink = Log.Sink;

    public DiagnosticSanitizationTests()
    {
        ctx = new Context(root);
        Directory.CreateDirectory(ctx.filesDir);
    }

    private sealed class Capture : Log.ILogSink
    {
        public readonly List<string> lines = [];
        public void Write(int priority, string tag, string msg, Exception? tr) => lines.Add(Log.Format(priority, tag, msg, tr));
    }

    [Fact]
    public void Ordinary_log_and_exception_are_sanitized_for_every_sink()
    {
        var capture = new Capture();
        Log.Sink = capture;

        Log.i("Test", $"refresh url={PRIVATE_URL}");
        Log.w("Test", "download failed", new IOException($"GET {PRIVATE_URL} returned 403"));

        Assert.Equal(2, capture.lines.Count);
        Assert.All(capture.lines, line =>
        {
            Assert.DoesNotContain(PRIVATE_URL, line);
            Assert.DoesNotContain("user-42", line);
            Assert.DoesNotContain("sw0rdFish", line);
            Assert.Contains("<адрес>", line);
        });
    }

    [Fact]
    public void Saved_detail_masks_url_but_source_configuration_is_preserved()
    {
        var id = SubscriptionManager.addUrl(ctx, PRIVATE_URL, "private");
        Assert.NotNull(id);

        SubscriptionManager.importInto(ctx, id, " ", $"Загружено: прямо\nURL: {PRIVATE_URL}\nHTTP: 200");

        var source = SubscriptionStore.load(ctx).sources.Single(s => s.id == id);
        Assert.Equal(PRIVATE_URL, source.url);
        Assert.NotNull(source.lastDetail);
        Assert.DoesNotContain(PRIVATE_URL, source.lastDetail);
        Assert.DoesNotContain("user-42", source.lastDetail);
        Assert.Contains("<адрес>", source.lastDetail);
    }

    public void Dispose()
    {
        Log.Sink = savedSink;
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
