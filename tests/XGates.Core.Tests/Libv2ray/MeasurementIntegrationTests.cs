using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using XGates.Core.Platform;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;
using L = XGates.Core.Libv2ray.Libv2ray;

namespace XGates.Core.Tests.Libv2ray;

/// <summary>
/// Замеры на НАСТОЯЩЕМ xray и герметичной локальной сети: tcping и его батч со сторожем, замер скорости через
/// temp-инстанс (полный/минимальный/достаточность), probeAlive, мёртвый сервер = провал (не «маленькая скорость»),
/// отдача (POST chunked), внешний IP через TLS ВНУТРИ туннеля (свежий сокет, Пр.144), задержка активного туннеля.
/// </summary>
[Collection("xray")]
public sealed class MeasurementIntegrationTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-meas-" + Guid.NewGuid().ToString("N")));

    public MeasurementIntegrationTests()
    {
        // slow=1: на петле 100 КБ иначе приходят быстрее 1 мс → «нулевое окно» и редкие ложные провалы замера.
        ServerSpeedTester.probesOverrideForTest = [$"http://127.0.0.1:{net.HttpPort}/bytes?n=30000000&slow=1"];
    }

    public void Dispose()
    {
        ServerSpeedTester.probesOverrideForTest = null;
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private static ServerProfile Vless(int port, string q = "type=tcp&security=none") =>
        VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{port}?{q}#srv{port}")!;

    [Fact]
    public void Tcping_alive_dead_blank()
    {
        Assert.InRange(ServerTester.ping(ctx, Vless(net.HttpPort)), 1, 2000);
        Assert.Equal(-1, ServerTester.ping(ctx, Vless(LocalTestNet.FreePort())));
        Assert.Equal(-1, ServerTester.ping(ctx, Vless(net.HttpPort) with { address = " " }));
    }

    [Fact]
    public void Ping_batch_reports_all_and_finishes_once()
    {
        var alive = Enumerable.Range(0, 3).Select(_ => Vless(net.HttpPort)).ToList();
        var dead = Enumerable.Range(0, 2).Select(_ => Vless(LocalTestNet.FreePort())).ToList();
        var results = new System.Collections.Concurrent.ConcurrentBag<long>();
        int lastDone = 0, finished = 0;
        using var fin = new ManualResetEventSlim();
        ServerTester.testAll(ctx, alive.Concat(dead).ToList(),
            onResult: (_, ms) => results.Add(ms),
            onProgress: (d, _) => Interlocked.Exchange(ref lastDone, d),
            onFinish: () => { Interlocked.Increment(ref finished); fin.Set(); },
            concurrency: 4);
        Assert.True(fin.Wait(20_000), "батч пинга не завершился");
        Thread.Sleep(2500); // сторож не должен вызвать onFinish второй раз
        Assert.Equal(1, finished);
        Assert.Equal(5, lastDone);
        Assert.Equal(3, results.Count(r => r > 0));
        Assert.Equal(2, results.Count(r => r == -1));
    }

    [Fact]
    public void Speed_through_real_tunnel_full_minimal_sufficiency()
    {
        var p = Vless(net.StartVlessServer());
        var m = ServerSpeedTester.measureSpeedDetailed(ctx, p, warmupMs: 200, measureMs: 600);
        Assert.True(m.ok, m.reason);
        Assert.True(m.mbps > 1, $"mbps={m.mbps}");
        Assert.True(m.bytes > 16 * 1024);
        // Минимальный замер (100 КБ) на петле укладывается в <1 мс → окно 0 мс → по логике Kotlin это «нулевое окно»
        // (в реальной сети 100 КБ дольше миллисекунды). Проверяем, что исход — валиден ЛИБО ровно эта причина.
        var min = ServerSpeedTester.measureSpeedDetailed(ctx, p, warmupMs: 100, measureMs: 800,
            warmupBudgetOverride: 16 * 1024L, measureBudgetOverride: 100 * 1024L, maxProbes: 1, connectTimeoutMs: 3_000);
        Assert.True(min.ok || min.reason == "нулевое окно измерения", min.reason);
        Assert.True(ServerSpeedTester.measureSufficiency(ctx, p, 800.0) > 0);   // бюджет 4 МБ — окно ненулевое и на петле
        Assert.True(ServerSpeedTester.probeAlive(ctx, p));
    }

    [Fact]
    public void Dead_server_is_failure_not_small_speed()
    {
        var dead = Vless(LocalTestNet.FreePort());
        var m = ServerSpeedTester.measureSpeedDetailed(ctx, dead, warmupMs: 100, measureMs: 300, connectTimeoutMs: 3000);
        Assert.False(m.ok);
        Assert.Equal(-1.0, m.mbps);
        Assert.Equal(-1.0, ServerSpeedTester.measureSpeedMinimal(ctx, dead));
        Assert.False(ServerSpeedTester.probeAlive(ctx, dead));
        var broken = Vless(1) with { network = "quic" }; // транспорт, который билдер не собирает
        Assert.StartsWith("ошибка конфига", ServerSpeedTester.measureSpeedDetailed(ctx, broken).reason);
    }

    [Fact]
    public void Speed_batch_progress_and_finish()
    {
        var p = Vless(net.StartVlessServer());
        var got = new System.Collections.Concurrent.ConcurrentBag<double>();
        using var fin = new ManualResetEventSlim();
        ServerSpeedTester.testAll(ctx, [p, Vless(LocalTestNet.FreePort())],
            onResult: (_, mbps) => got.Add(mbps), onProgress: (_, _) => { }, onFinish: fin.Set,
            warmupMs: 100, measureMs: 300, pool: 1);
        Assert.True(fin.Wait(60_000));
        Assert.Equal(2, got.Count);
        Assert.Contains(got, v => v > 0);
        Assert.Contains(got, v => v == -1.0);
    }

    [Fact]
    public void Upload_streams_through_tunnel()
    {
        int server = net.StartVlessServer();
        int socks = LocalTestNet.FreePort();
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(XrayConfigBuilder.buildForSpeedTest(Vless(server), socks), 0);
        try
        {
            var m = ServerSpeedTester.uploadMbps($"http://127.0.0.1:{net.HttpPort}/up", socks, 100, 400, 2 * 1024 * 1024, "тест↑", true);
            // На петле отдача «бесконечно» быстрая → честные исходы: валидно / отсечено как буфер-фейк (>500 Мбит/с) /
            // «нулевое окно» (2 МБ записались быстрее 1 мс). Главное — байты реально ушли через туннель.
            Assert.True(m.ok || m.reason.Contains("нереально") || m.reason == "нулевое окно отдачи", m.reason);
            Assert.True(m.bytes >= 64 * 1024, $"bytes={m.bytes}");
        }
        finally { core.stopLoop(); }
    }

    [Fact]
    public void External_ip_via_tls_inside_tunnel_and_latency()
    {
        int server = net.StartVlessServer();
        int socks = LocalTestNet.FreePort();
        using var tls = new TlsIpServer("203.0.113.7");
        var core = L.newCoreController(new LocalTestNet.NoopCallback());
        core.startLoop(XrayConfigBuilder.buildForSpeedTest(Vless(server), socks), 0);
        ExternalIpChecker.endpointsOverrideForTest = [("127.0.0.1", "/")];
        ExternalIpChecker.tlsPortForTest = tls.Port;
        ExternalIpChecker.certValidationForTest = (_, _, _, _) => true;
        try
        {
            Assert.Equal("203.0.113.7", ExternalIpChecker.fetch(socks, 5000));
            Assert.True(ExternalIpChecker.probePass(socks, 4000));
            Assert.InRange(ExternalIpChecker.latencyMs(socks, "127.0.0.1", "/generate_204", 4000, net.HttpPort), 1, 3000);
            Assert.Null(ExternalIpChecker.fetch(LocalTestNet.FreePort(), 1500)); // наш SOCKS не слушает → null
        }
        finally
        {
            ExternalIpChecker.endpointsOverrideForTest = null;
            ExternalIpChecker.tlsPortForTest = 443;
            ExternalIpChecker.certValidationForTest = null;
            core.stopLoop();
        }
    }

    /// <summary>Мини-HTTPS-сервер с самоподписанным сертификатом: на любой запрос — 200 и IP в теле.</summary>
    private sealed class TlsIpServer : IDisposable
    {
        private readonly TcpListener l = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 cert;
        public int Port { get; }

        public TlsIpServer(string ip)
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var tmp = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
            cert = X509CertificateLoader.LoadPkcs12(tmp.Export(X509ContentType.Pfx), null);
            l.Start();
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await l.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        using (var ssl = new SslStream(c.GetStream()))
                        {
                            try
                            {
                                await ssl.AuthenticateAsServerAsync(cert);
                                var buf = new byte[4096];
                                await ssl.ReadAsync(buf);
                                await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n{ip}"));
                            }
                            catch { }
                        }
                    });
                }
            });
        }

        public void Dispose() { l.Stop(); cert.Dispose(); }
    }
}

/// <summary>Порт app/src/test/…/SingleFlightTest.kt (Промпт 101.A): ОДИН запрос внешнего адреса за раз.</summary>
[Collection("xray")]
public class SingleFlightTest
{
    [Fact]
    public void concurrent_callers_collapse_to_one_in_flight_request()
    {
        ExternalIpChecker.resetTestCounters();
        // Подменяем реальный сетевой запрос медленной «работой».
        ExternalIpChecker.onceOverrideForTest = _ => { Thread.Sleep(200); return "1.2.3.4"; };
        try
        {
            const int n = 8;
            using var start = new ManualResetEventSlim();
            using var done = new CountdownEvent(n);
            var results = new System.Collections.Concurrent.ConcurrentBag<string?>();
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            for (int i = 0; i < n; i++)
                new Thread(() =>
                {
                    try { start.Wait(); results.Add(ExternalIpChecker.fetch(socksPort: 10815)); }
                    catch (Exception t) { errors.Add(t); }
                    finally { done.Signal(); }
                }).Start();
            start.Set();          // все стартуют одновременно
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "потоки не завершились за 10с (дедлок single-flight?)");
            Assert.Empty(errors);

            // Один запрос за раз: параллельность реальной работы не превысила 1.
            Assert.Equal(1, ExternalIpChecker.maxConcurrentSeen);
            // Coalesce: фактическая работа выполнилась 1 раз, хотя вызвали 8.
            Assert.Equal(1, ExternalIpChecker.onceCount);
            // Никого не потеряли: все 8 получили результат.
            Assert.Equal(n, results.Count);
            Assert.All(results, r => Assert.Equal("1.2.3.4", r));
        }
        finally
        {
            ExternalIpChecker.onceOverrideForTest = null;
            ExternalIpChecker.resetTestCounters();
        }
    }
}
