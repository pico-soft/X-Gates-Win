using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using XGates.Core.Libv2ray;

namespace XGates.Core.Tests.Libv2ray;

/// <summary>
/// Герметичная «сеть» для интеграционных тестов ядра (без интернета): локальный HTTP-сервер (204 и N байт) и
/// «удалённый» xray-сервер vless на петле. Клиентский xray строит НАШ XrayConfigBuilder из обычной vless://-ссылки.
/// Бинарь — third_party/xray/&lt;платформа&gt; (tools/xray/fetch-xray.sh), версия как в Android.
/// </summary>
public sealed class LocalTestNet : IDisposable
{
    public const string Uuid = "0f6b3c1e-8a2d-4c5b-9e7f-1a2b3c4d5e6f";
    private readonly TcpListener http;
    private readonly CancellationTokenSource cts = new();
    private readonly List<CoreController> servers = [];

    public int HttpPort { get; }

    /// <summary>Тело «подписки» по /sub (одна vless-ссылка — чтобы hasSupportedLinks принял ответ).</summary>
    public string SubBody { get; set; } = "vless://11111111-2222-3333-4444-555555555555@example.com:443?type=ws#sub\n";

    /// <summary>Отдать файл по ТОЧНОМУ пути (без query) — «поддельный GitHub» для обновлялки.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Files { get; } = new();

    /// <summary>Сырой ответ целиком (статус+заголовки+тело) по ТОЧНОМУ пути — для проверок разбора ответа прокси.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> RawResponses { get; } = new();

    /// <summary>302 по ТОЧНОМУ пути → Location (как releases/latest/download → releases/download/&lt;тег&gt;).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> Redirects { get; } = new();

    public static string XrayPath
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "XGatesWin.slnx"))) d = d.Parent;
            var plat = OperatingSystem.IsWindows() ? "windows-64" : "linux-64";
            var exe = OperatingSystem.IsWindows() ? "xray.exe" : "xray";
            var p = Path.Combine(d!.FullName, "third_party", "xray", plat, exe);
            if (!File.Exists(p)) throw new InvalidOperationException($"нет {p} — запустите tools/xray/fetch-xray.sh");
            return p;
        }
    }

    public LocalTestNet()
    {
        XGates.Core.Libv2ray.Libv2ray.xrayPath = XrayPath;
        http = new TcpListener(IPAddress.Loopback, 0);
        http.Start();
        HttpPort = ((IPEndPoint)http.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    /// <summary>Поднять «удалённый» vless-сервер (tcp или ws) на петле; вернуть его порт.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CoreController> serverByPort = new();

    /// <summary>«Обрыв»: остановить vless-сервер стенда — порт закрыт, туннель через него мёртв.</summary>
    public void StopVlessServer(int port)
    {
        if (serverByPort.TryRemove(port, out var core)) core.stopLoop();
    }

    public int StartVlessServer(string network = "tcp", string path = "/ws")
    {
        var stream = new JsonObject { ["network"] = network };
        if (network == "ws") stream["wsSettings"] = new JsonObject { ["path"] = path };
        return StartServer(stream);
    }

    /// <summary>Самоподписанный сертификат стенда; SHA-256 от DER — то, что ядро сверяет в pinnedPeerCertSha256.</summary>
    public sealed record TestCert(string CertPem, string KeyPem, string Sha256Hex);

    public static TestCert MakeCert(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return new TestCert(cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), Convert.ToHexStringLower(SHA256.HashData(cert.RawData)));
    }

    /// <summary>vless поверх настоящего TLS; [xhttp] — настройки транспорта xhttp (null — tcp).</summary>
    public int StartVlessTlsServer(TestCert cert, JsonObject? xhttp = null)
    {
        var stream = new JsonObject
        {
            ["network"] = xhttp != null ? "xhttp" : "tcp",
            ["security"] = "tls",
            ["tlsSettings"] = new JsonObject
            {
                ["alpn"] = new JsonArray("h2", "http/1.1"),
                ["certificates"] = new JsonArray(new JsonObject { ["certificate"] = PemLines(cert.CertPem), ["key"] = PemLines(cert.KeyPem) }),
            },
        };
        if (xhttp != null) stream["xhttpSettings"] = xhttp;
        return StartServer(stream);
    }

    private static JsonArray PemLines(string pem) =>
        new(pem.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (JsonNode?)JsonValue.Create(l.TrimEnd('\r'))).ToArray());

    private int StartServer(JsonObject stream)
    {
        int port = FreePort();
        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["listen"] = "127.0.0.1", ["port"] = port, ["protocol"] = "vless",
                ["settings"] = new JsonObject { ["clients"] = new JsonArray(new JsonObject { ["id"] = Uuid }), ["decryption"] = "none" },
                ["streamSettings"] = stream,
            }),
            // Xray v26 freedom по умолчанию НЕ пускает на loopback/частные адреса («blocked target … blackholing»,
            // защита серверов от SSRF) → в тестовом «удалённом» сервере явно разрешаем 127.0.0.0/8.
            ["outbounds"] = new JsonArray(new JsonObject
            {
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject { ["finalRules"] = new JsonArray(new JsonObject { ["action"] = "allow", ["ip"] = new JsonArray("127.0.0.0/8") }) },
            }),
        };
        var core = XGates.Core.Libv2ray.Libv2ray.newCoreController(new NoopCallback());
        core.startLoop(cfg.ToJsonString(), 0);
        servers.Add(core);
        serverByPort[port] = core;
        return port;
    }

    private async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await http.AcceptTcpClientAsync(cts.Token); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    /// <summary>HTTP/1.1 с keep-alive: /generate_204 → 204; /bytes?n=N → N байт.</summary>
    private async Task Serve(TcpClient c)
    {
        using (c)
        {
            var s = c.GetStream();
            var buf = new byte[8192];
            var acc = new StringBuilder();
            try
            {
                while (true)
                {
                    int n = await s.ReadAsync(buf, cts.Token);
                    if (n == 0) return;
                    acc.Append(Encoding.ASCII.GetString(buf, 0, n));
                    string all = acc.ToString();
                    int end;
                    while ((end = all.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        string head = all.Substring(0, end);
                        all = all.Substring(end + 4);
                        string target = head.Split(' ')[1];
                        var exact = target.Split('?')[0];
                        if (Files.TryGetValue(exact, out var file))
                        {
                            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {file.Length}\r\n\r\n"), cts.Token);
                            await s.WriteAsync(file, cts.Token);
                            continue;
                        }
                        if (RawResponses.TryGetValue(exact, out var raw))
                        {
                            await s.WriteAsync(raw, cts.Token);
                            continue;
                        }
                        if (Redirects.TryGetValue(exact, out var loc))
                        {
                            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {loc}\r\nContent-Length: 0\r\n\r\n"), cts.Token);
                            continue;
                        }
                        if (target.StartsWith("/up"))
                        {
                            // chunked POST: читаем до терминатора «0\r\n\r\n», отвечаем 200
                            var tailBuf = all;
                            var slowUp = target.Contains("slow=1");   // медленный приём: на петле отдача иначе даёт «нулевое окно»
                            while (!tailBuf.Contains("\r\n0\r\n\r\n") && !tailBuf.StartsWith("0\r\n\r\n"))
                            {
                                if (slowUp) await Task.Delay(2, cts.Token);
                                int m = await s.ReadAsync(buf, cts.Token);
                                if (m == 0) return;
                                tailBuf = tailBuf.Length > 16 ? tailBuf.Substring(tailBuf.Length - 16) : tailBuf;
                                tailBuf += Encoding.ASCII.GetString(buf, 0, m);
                            }
                            all = "";
                            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"), cts.Token);
                            continue;
                        }
                        if (target.StartsWith("/status/"))
                        {
                            int code = int.Parse(target.Substring(8));
                            var extra = code == 429 ? "Retry-After: 42\r\n" : "";
                            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} X\r\n{extra}Content-Length: 4\r\n\r\nnope"), cts.Token);
                        }
                        else if (target.StartsWith("/redirect?to="))
                        {
                            var to = Uri.UnescapeDataString(target.Substring("/redirect?to=".Length));
                            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {to}\r\nContent-Length: 0\r\n\r\n"), cts.Token);
                        }
                        else if (target.StartsWith("/sub"))
                        {
                            var bodyBytes = Encoding.UTF8.GetBytes(SubBody);
                            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\n\r\n"), cts.Token);
                            await s.WriteAsync(bodyBytes, cts.Token);
                        }
                        else if (target.StartsWith("/bytes"))
                        {
                            int len = int.Parse(target.Split("n=")[1].Split('&')[0]);
                            var slow = target.Contains("slow=1");   // медленная отдача: на петле 100 КБ иначе приходят за <1 мс (окно 0)
                            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {len}\r\nContent-Type: application/octet-stream\r\n\r\n"), cts.Token);
                            var chunk = new byte[Math.Min(len, 65536)];
                            if (slow) chunk = new byte[16 * 1024];
                            for (int sent = 0; sent < len; sent += chunk.Length)
                            {
                                await s.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, len - sent)), cts.Token);
                                if (slow) await Task.Delay(2, cts.Token);
                            }
                        }
                        else
                        {
                            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"), cts.Token);
                        }
                    }
                    acc.Clear().Append(all);
                }
            }
            catch { /* клиент закрыл соединение */ }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        http.Stop();
        foreach (var s in servers) s.stopLoop();
    }

    public sealed class NoopCallback : CoreCallbackHandler
    {
        public long startup() => 0;
        public long shutdown() => 0;
        public long onEmitStatus(long l, string? s) => 0;
    }
}
