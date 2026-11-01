using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XGates.App;
using XGates.Core.Platform;

namespace XGates.Core.Tests.App;

/// <summary>
/// «Настроить Firefox» / «Вернуть как было» на НАСТОЯЩЕМ Firefox раннера Windows: его профиль, его разбор user.js, его
/// сетевой код. Приёмник SOCKS5 записывает, куда Firefox просится; настоящий туннель для этого не нужен.
/// Запускается только с XGATES_FIREFOX_E2E=1 (шаг в windows.yml): тест правит обычный профиль Firefox и убивает все его
/// процессы, на машине разработчика такому делать нечего. Подробности (profiles.ini, user.js, настройки прокси) —
/// в файле XGATES_FIREFOX_OUT/firefox.log.
/// </summary>
public sealed class FirefoxRealBrowserTests
{
    private const string PROBE = "probe.xgates.example";
    private static readonly TimeSpan WAIT = TimeSpan.FromSeconds(90);
    private static readonly string LOG =
        Path.Combine(Environment.GetEnvironmentVariable("XGATES_FIREFOX_OUT") ?? Path.GetTempPath(), "firefox.log");

    private sealed class SocksRecorder : IDisposable
    {
        private readonly TcpListener lsn = new(IPAddress.Loopback, 0);
        public readonly ConcurrentQueue<string> targets = new();
        public int port => ((IPEndPoint)lsn.LocalEndpoint).Port;

        public SocksRecorder()
        {
            lsn.Start();
            _ = Task.Run(acceptLoop);
        }

        private async Task acceptLoop()
        {
            try { while (true) { var c = await lsn.AcceptTcpClientAsync(); _ = Task.Run(() => serve(c)); } }
            catch (Exception) { }
        }

        /// <summary>Рукопожатие SOCKS5, запись цели, отказ: сам запрос никуда не идёт.</summary>
        private async Task serve(TcpClient c)
        {
            using (c)
                try
                {
                    var s = c.GetStream();
                    var head = new byte[2];
                    await s.ReadExactlyAsync(head);
                    await s.ReadExactlyAsync(new byte[head[1]]);
                    await s.WriteAsync(new byte[] { 5, 0 });
                    var req = new byte[4];
                    await s.ReadExactlyAsync(req);
                    string target;
                    if (req[3] == 3)
                    {
                        var len = new byte[1];
                        await s.ReadExactlyAsync(len);
                        var name = new byte[len[0]];
                        await s.ReadExactlyAsync(name);
                        target = Encoding.ASCII.GetString(name);
                    }
                    else
                    {
                        var ip = new byte[req[3] == 1 ? 4 : 16];
                        await s.ReadExactlyAsync(ip);
                        target = new IPAddress(ip).ToString();
                    }
                    await s.ReadExactlyAsync(new byte[2]);
                    targets.Enqueue(target);
                    await s.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 });
                }
                catch (Exception) { }
        }

        public void Dispose() => lsn.Stop();
    }

    private static string firefoxExe() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe");

    private static void log(string text) => File.AppendAllText(LOG, text + Environment.NewLine);

    private static void start(string url)
    {
        var psi = new ProcessStartInfo(firefoxExe()) { UseShellExecute = false };
        foreach (var a in new[] { "--headless", "--no-remote", url }) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    private static async Task killAll()
    {
        foreach (var p in Process.GetProcessesByName("firefox"))
            try { p.Kill(true); p.WaitForExit(10_000); } catch (Exception) { }
        await Task.Delay(1500);
    }

    private static async Task<bool> waitFor(Func<bool> done, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout) { if (done()) return true; await Task.Delay(250); }
        return done();
    }

    private static void dump(string title, SocksRecorder socks)
    {
        log($"=== {title}");
        log("цели SOCKS: " + string.Join(", ", socks.targets.Distinct()));
        foreach (var p in FirefoxSetup.findProfiles())
        {
            log($"профиль {p.name}: {p.dir}");
            var prefs = Path.Combine(p.dir, "prefs.js");
            var user = Path.Combine(p.dir, "user.js");
            log("user.js: " + (File.Exists(user) ? File.ReadAllText(user) : "(нет)"));
            if (File.Exists(prefs))
                foreach (var l in File.ReadLines(prefs).Where(l => l.Contains("network.proxy."))) log("prefs.js: " + l);
        }
    }

    [Fact]
    public async Task Button_routes_real_firefox_through_socks_and_restore_takes_it_back()
    {
        if (Environment.GetEnvironmentVariable("XGATES_FIREFOX_E2E") != "1") return;
        Directory.CreateDirectory(Path.GetDirectoryName(LOG)!);
        Assert.True(File.Exists(firefoxExe()), "на раннере нет Firefox");
        log("Firefox " + FileVersionInfo.GetVersionInfo(firefoxExe()).ProductVersion);

        var ctx = new Context(Path.Combine(Path.GetTempPath(), "xgates-ff-real-" + Guid.NewGuid().ToString("N")));
        using var socks = new SocksRecorder();
        bool reached() => socks.targets.Contains(PROBE);
        try
        {
            // Первый запуск создаёт профиль, как у человека, который открыл браузер после установки.
            start("about:blank");
            Assert.True(await waitFor(() => FirefoxSetup.findProfiles().Count > 0, WAIT), "Firefox не создал профиль");
            await Task.Delay(5000);
            await killAll();
            foreach (var root in FirefoxSetup.roots())
                if (File.Exists(Path.Combine(root, "profiles.ini")))
                    log($"--- {root}\\profiles.ini{Environment.NewLine}{File.ReadAllText(Path.Combine(root, "profiles.ini"))}");

            // Firefox закрыт: нажали кнопку, открыли браузер (пункт 18а).
            var closed = FirefoxSetup.configure(ctx, socks.port);
            log($"кнопка, Firefox закрыт: ok={closed.ok} «{closed.text}»");
            Assert.True(closed.ok, closed.text);
            start($"http://{PROBE}/");
            var viaClosed = await waitFor(reached, WAIT);
            dump("после кнопки при закрытом Firefox", socks);
            Assert.True(viaClosed, $"Firefox не пошёл в SOCKS; цели: {string.Join(", ", socks.targets.Distinct())}");
            await killAll();

            Assert.True(FirefoxSetup.restore(ctx).ok);
            while (socks.targets.TryDequeue(out _)) { }

            // Firefox открыт: нажали кнопку, перезапустили браузер (пункт 18б).
            start("about:blank");
            await waitFor(() => FirefoxSetup.findProfiles().Any(p => FirefoxSetup.profileInUse(p.dir)), WAIT);
            var open = FirefoxSetup.configure(ctx, socks.port);
            log($"кнопка, Firefox открыт: ok={open.ok} «{open.text}»");
            Assert.Contains("Перезапустите Firefox", open.text);
            await killAll();
            start($"http://{PROBE}/");
            var viaOpen = await waitFor(reached, WAIT);
            dump("после кнопки при открытом Firefox и перезапуска", socks);
            Assert.True(viaOpen, $"Firefox после перезапуска не пошёл в SOCKS; цели: {string.Join(", ", socks.targets.Distinct())}");
            await killAll();

            // «Вернуть как было»: Firefox снова мимо нашего порта (пункт 18в).
            Assert.True(FirefoxSetup.restore(ctx).ok);
            while (socks.targets.TryDequeue(out _)) { }
            start($"http://{PROBE}/");
            await Task.Delay(TimeSpan.FromSeconds(20));
            dump("после «Вернуть как было»", socks);
            Assert.Empty(socks.targets);
        }
        finally
        {
            await killAll();
            try { FirefoxSetup.restore(ctx); } catch (Exception) { }
        }
    }
}
