using System.Net;
using System.Net.Sockets;
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
    public int StartVlessServer(string network = "tcp", string path = "/ws")
    {
        int port = FreePort();
        var stream = new JsonObject { ["network"] = network };
        if (network == "ws") stream["wsSettings"] = new JsonObject { ["path"] = path };
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
                        if (target.StartsWith("/up"))
                        {
                            // chunked POST: читаем до терминатора «0\r\n\r\n», отвечаем 200
                            var tailBuf = all;
                            while (!tailBuf.Contains("\r\n0\r\n\r\n") && !tailBuf.StartsWith("0\r\n\r\n"))
                            {
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
