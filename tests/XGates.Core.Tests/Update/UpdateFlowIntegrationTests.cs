using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Tests.Libv2ray;
using XGates.Core.Update;

namespace XGates.Core.Tests.Update;

/// <summary>
/// Обновлялка целиком против локального «поддельного GitHub» (API releases/latest, вложения, редирект
/// latest/download → download/&lt;тег&gt;): путь через API и запасной, подпись update.json (верная/подменённая/нет),
/// «не новее», чужой релиз, нет релизов; скачивание с проверкой SHA-256 и переходом на запасной адрес; одна кнопка
/// «обновить» до запуска установщика; полоса/уведомление «одно на версию».
/// </summary>
[Collection("xray")]
public sealed class UpdateFlowIntegrationTests : IDisposable
{
    private const string Owner = "pico-soft", RepoName = "X-Gates-Win";
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-upd-" + Guid.NewGuid().ToString("N")));
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Func<int> savedPort = CascadeFetch.ownSocksPort;
    private readonly Func<string, System.Net.IWebProxy?> savedSys = CascadeFetch.systemProxyFor;
    private readonly IReadOnlyList<UpdateChecker.Repo> savedRepos = UpdateChecker.REPOS;
    private readonly Func<int> savedCode = UpdateChecker.currentVersionCode;
    private readonly NotificationHelper.INotificationSink savedSink = NotificationHelper.Sink;
    private readonly Func<string, bool> savedLaunch = UpdateFlowController.launchInstaller;

    public UpdateFlowIntegrationTests()
    {
        CascadeFetch.ownSocksPort = () => LocalTestNet.FreePort();   // «нашего прокси нет» — только прямая ступень
        CascadeFetch.systemProxyFor = _ => null;
        UpdateSignature.publicKeyOverrideForTest = key.ExportSubjectPublicKeyInfoPem();
        UpdateChecker.REPOS = [new UpdateChecker.Repo(Owner, RepoName, web: U("/web"), api: U("/api"))];
        UpdateChecker.currentVersionCode = () => 5;
        UpdateStore.resetForTest();
        UpdateFlowController.resetForTest();
        Directory.CreateDirectory(ctx.filesDir);
    }

    /// <summary>Тап и ожидание КОНЦА потока (не фазы: поток не должен пережить тест и писать в чужие).</summary>
    private void TapAndWait()
    {
        UpdateFlowController.oneTap(ctx);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (UpdateFlowController.isRunning && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.False(UpdateFlowController.isRunning, "поток обновления не завершился за 30 с");
    }

    public void Dispose()
    {
        for (var i = 0; i < 1500 && UpdateFlowController.isRunning; i++) Thread.Sleep(20);
        UpdateFlowController.resetForTest();
        CascadeFetch.ownSocksPort = savedPort;
        CascadeFetch.systemProxyFor = savedSys;
        UpdateSignature.publicKeyOverrideForTest = null;
        UpdateChecker.REPOS = savedRepos;
        UpdateChecker.currentVersionCode = savedCode;
        NotificationHelper.Sink = savedSink;
        UpdateFlowController.launchInstaller = savedLaunch;
        UpdateFlowController.exitApp = () => { };
        UpdateAutoCheck.proxyWaitMs = UpdateChecker.AUTO_CHECK_PROXY_WAIT_MS;
        UpdateStore.resetForTest();
        key.Dispose();
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private string U(string path) => $"http://127.0.0.1:{net.HttpPort}{path}";
    private static string Dl(string tag) => $"/web/{Owner}/{RepoName}/releases/download/{tag}";
    private const string Latest = $"/web/{Owner}/{RepoName}/releases/latest/download";
    private const string Api = $"/api/repos/{Owner}/{RepoName}/releases/latest";

    private sealed record Release(string tag, string msiName, byte[] msi, string manifest);

    /// <summary>Выложить релиз: update.json (+.sig), MSI, API-ответ и редиректы latest/download → download/&lt;тег&gt;.</summary>
    private Release Publish(string tag, int code, bool api = true, bool sig = true, bool tamper = false,
        string? packageName = null, byte[]? servedMsi = null)
    {
        var msiName = $"XGatesWin-{tag.TrimStart('v')}-win-x64.msi";
        var msi = RandomNumberGenerator.GetBytes(300_000);
        var manifest = new JsonObject
        {
            ["versionCode"] = code, ["versionName"] = tag.TrimStart('v') + " beta", ["packageName"] = packageName ?? AppInfo.APPLICATION_ID,
            ["notes"] = "\n  Исправлено: всё\nвторая строка",
            ["artifacts"] = new JsonArray(new JsonObject { ["abi"] = "win-x64", ["fileName"] = msiName, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(msi)) }),
        }.ToJsonString() + "\n";
        var signature = UpdateSignatureTests.Sign(key, manifest);
        // Подмена: злоумышленник правит манифест (versionCode), подпись остаётся от исходного.
        var served = tamper ? manifest.Replace($"\"versionCode\":{code}", $"\"versionCode\":{code + 1}") : manifest;
        Assert.True(!tamper || served != manifest);

        net.Files[$"{Dl(tag)}/update.json"] = Encoding.UTF8.GetBytes(served);
        if (sig) net.Files[$"{Dl(tag)}/update.json.sig"] = Encoding.ASCII.GetBytes(signature + "\n");
        net.Files[$"{Dl(tag)}/{msiName}"] = servedMsi ?? msi;
        foreach (var f in new[] { "update.json", "update.json.sig", msiName }) net.Redirects[$"{Latest}/{f}"] = U($"{Dl(tag)}/{f}");
        if (api)
        {
            var assets = new JsonArray(
                new JsonObject { ["name"] = "update.json", ["browser_download_url"] = U($"{Dl(tag)}/update.json"), ["size"] = served.Length },
                new JsonObject { ["name"] = msiName, ["browser_download_url"] = U($"{Dl(tag)}/{msiName}"), ["size"] = msi.Length });
            if (sig) assets.Add(new JsonObject { ["name"] = "update.json.sig", ["browser_download_url"] = U($"{Dl(tag)}/update.json.sig"), ["size"] = 100 });
            net.Files[Api] = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["tag_name"] = tag, ["name"] = $"X-Gates Win {tag}", ["body"] = "описание", ["draft"] = false, ["prerelease"] = false, ["assets"] = assets,
            }.ToJsonString());
        }
        return new Release(tag, msiName, msi, manifest);
    }

    private static T Is<T>(UpdateCheckResult r, string details) where T : UpdateCheckResult =>
        r as T ?? throw new Xunit.Sdk.XunitException($"ожидали {typeof(T).Name}, получили {r}\n{details}");

    [Fact]
    public void Api_path_signed_manifest_gives_available()
    {
        var rel = Publish("v0.7", code: 7);
        var rep = UpdateChecker.check(ctx);
        var a = Is<UpdateCheckResult.Available>(rep.result, rep.details);
        Assert.Equal(7, a.versionCode);
        Assert.Equal("0.7 beta", a.versionName);
        Assert.Equal(U($"{Dl("v0.7")}/{rel.msiName}"), a.downloadUrl);
        Assert.Equal(U($"{Latest}/{rel.msiName}"), a.downloadUrls[1]);   // запасной адрес
        Assert.Equal(rel.msi.Length, a.sizeBytes);
        Assert.Contains("через API", a.via);
        Assert.Contains("Подпись update.json: верна", rep.details);
    }

    [Fact]
    public void Fallback_without_api_uses_tag_from_redirect()
    {
        var rel = Publish("v0.8", code: 8, api: false);   // API молчит (204, пустое тело) → запасной адрес
        var rep = UpdateChecker.check(ctx);
        var a = Is<UpdateCheckResult.Available>(rep.result, rep.details);
        Assert.Equal(8, a.versionCode);
        Assert.Equal(U($"{Dl("v0.8")}/{rel.msiName}"), a.downloadUrl);   // по тегу, а не latest/download/<файл>
        Assert.Equal(-1, a.sizeBytes);
        Assert.Contains("через запасной адрес", a.via);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tampered_manifest_rejected(bool api)
    {
        Publish("v0.9", code: 9, api: api, tamper: true);
        var rep = UpdateChecker.check(ctx);
        Assert.Equal(UpdateErrorKind.SIGNATURE_MISMATCH, Is<UpdateCheckResult.Error>(rep.result, rep.details).kind);
        Assert.Null(UpdateStore.live.value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missing_signature_rejected(bool api)
    {
        Publish("v0.9", code: 9, api: api, sig: false);
        var rep = UpdateChecker.check(ctx);
        Assert.Equal(UpdateErrorKind.SIGNATURE_MISSING, Is<UpdateCheckResult.Error>(rep.result, rep.details).kind);
    }

    [Fact]
    public void Not_newer_is_up_to_date_and_foreign_release_rejected()
    {
        Publish("v0.5", code: 5);   // строго БОЛЬШЕ установленного — равный не предлагаем
        var rep = UpdateChecker.check(ctx);
        Is<UpdateCheckResult.UpToDate>(rep.result, rep.details);

        Publish("v0.9", code: 9, packageName: "com.picosoft.xrayproxydroid");   // Android-релиз — не наш
        rep = UpdateChecker.check(ctx);
        Assert.Equal(UpdateErrorKind.FOREIGN_RELEASE, Is<UpdateCheckResult.Error>(rep.result, rep.details).kind);
    }

    [Fact]
    public void No_releases_on_api_404()
    {
        net.Redirects[Api] = U("/status/404");
        var rep = UpdateChecker.check(ctx);
        Is<UpdateCheckResult.NoReleases>(rep.result, rep.details);
    }

    [Fact]
    public void Download_verifies_sha_falls_back_to_next_url_and_reuses_verified_file()
    {
        var rel = Publish("v0.7", code: 7, servedMsi: RandomNumberGenerator.GetBytes(300_000));   // по тегу — подменённый файл
        net.Redirects[$"{Latest}/{rel.msiName}"] = U($"/good/{rel.msiName}");                     // запасной адрес — верный
        net.Files[$"/good/{rel.msiName}"] = rel.msi;
        var a = Is<UpdateCheckResult.Available>(UpdateChecker.check(ctx).result, "");

        var progress = 0L;
        var r = UpdateInstaller.download(ctx, a, onProgress: (d, _) => progress = d);
        var ok = Assert.IsType<UpdateInstaller.DownloadOutcome.Ok>(r);
        Assert.Equal(Path.Combine(ctx.filesDir, "updates", rel.msiName), ok.file);
        Assert.Equal(rel.msi, File.ReadAllBytes(ok.file));
        Assert.Equal(rel.msi.Length, progress);

        // Проверенный файл уже на диске → повторно не качаем (сеть убрана — всё равно Ok).
        net.Files.Clear(); net.Redirects.Clear();
        Assert.IsType<UpdateInstaller.DownloadOutcome.Ok>(UpdateInstaller.download(ctx, a));

        // Оба адреса отдают не тот файл → отказ, файла нет. Текст — подсказка «прокси не запущен», как на Android
        // (паритет; неточность записана в docs/decisions.md, «Найдено в Android-коде» №7).
        File.Delete(ok.file);
        net.Files[$"{Dl("v0.7")}/{rel.msiName}"] = RandomNumberGenerator.GetBytes(1000);
        net.Redirects[$"{Latest}/{rel.msiName}"] = U($"{Dl("v0.7")}/{rel.msiName}");
        var fail = Assert.IsType<UpdateInstaller.DownloadOutcome.Fail>(UpdateInstaller.download(ctx, a));
        Assert.Equal(UpdateErrorKind.DOWNLOAD_FAILED, fail.kind);
        Assert.Contains("прокси не запущен", fail.detail);
        Assert.False(File.Exists(ok.file));
        Assert.Empty(Directory.GetFiles(Path.Combine(ctx.filesDir, "updates")));
    }

    [Fact]
    public void One_tap_checks_downloads_verifies_and_launches_installer()
    {
        var rel = Publish("v0.7", code: 7);
        string? launched = null; var exits = 0;
        UpdateFlowController.launchInstaller = f => { launched = f; return true; };
        UpdateFlowController.exitApp = () => exits++;
        var sink = new RecordingSink(); NotificationHelper.Sink = sink;

        TapAndWait();

        Assert.IsType<UpdateFlowController.Phase.Installing>(UpdateFlowController.phase.value);
        Assert.Equal(rel.msi, File.ReadAllBytes(launched!));
        Assert.Equal(1, exits);
        Assert.Equal(7, UpdateStore.record.value.dismissedCode);   // полоса снята для этой версии
        Assert.Null(UpdateStore.bannerVersion(notifyOn: true));
        Assert.Contains(NotificationHelper.Kind.UPDATE, sink.cancelled);
    }

    [Fact]
    public void One_tap_on_bad_signature_names_the_reason_and_downloads_nothing()
    {
        Publish("v0.7", code: 7, tamper: true);
        var launched = false; UpdateFlowController.launchInstaller = _ => launched = true;

        TapAndWait();

        var f = Assert.IsType<UpdateFlowController.Phase.Failed>(UpdateFlowController.phase.value);
        Assert.Equal(UpdateErrorKind.SIGNATURE_MISMATCH.text(), f.message);
        Assert.False(launched);
        Assert.False(Directory.Exists(Path.Combine(ctx.filesDir, "updates")) && Directory.GetFiles(Path.Combine(ctx.filesDir, "updates")).Length > 0);
    }

    [Fact]
    public void Installer_launch_failure_is_reported_and_banner_kept()
    {
        Publish("v0.7", code: 7);
        UpdateFlowController.launchInstaller = _ => false;
        var exits = 0; UpdateFlowController.exitApp = () => exits++;

        TapAndWait();

        var f = Assert.IsType<UpdateFlowController.Phase.Failed>(UpdateFlowController.phase.value);
        Assert.Equal(UpdateErrorKind.INSTALL_FAILED.text(), f.message);
        Assert.Equal(0, exits);
        Assert.Equal((7, "0.7 beta"), UpdateStore.bannerVersion(notifyOn: true));   // не гасим полосу авансом
    }

    [Fact]
    public void Banner_and_notification_once_per_version_survive_restart()
    {
        Publish("v0.7", code: 7);
        var sink = new RecordingSink(); NotificationHelper.Sink = sink;
        UpdateStore.apply(ctx, UpdateChecker.check(ctx), nowMs: 1_000);

        Assert.Equal((7, "0.7 beta"), UpdateStore.bannerVersion(notifyOn: true));
        Assert.Null(UpdateStore.bannerVersion(notifyOn: false));
        Assert.Equal("Исправлено: всё", UpdateStore.availNotesFirstLine());
        Assert.False(UpdateStore.dueForAutoCheck(1_000 + 60_000));
        Assert.True(UpdateStore.dueForAutoCheck(1_000 + 24L * 3600 * 1000));

        UpdateNotifier.maybeNotify(ctx);
        UpdateNotifier.maybeNotify(ctx);
        Assert.Equal(["Исправлено: всё"], sink.shown.Where(s => s.kind == NotificationHelper.Kind.UPDATE).Select(s => s.text));

        // «Перезапуск»: состояние читается из файла — повторно не уведомляем, полоса на месте.
        UpdateStore.resetForTest();
        UpdateStore.init(ctx);
        Assert.Equal(7, UpdateStore.record.value.notifiedCode);
        Assert.False(UpdateStore.shouldNotify(true));
        Assert.Equal((7, "0.7 beta"), UpdateStore.bannerVersion(notifyOn: true));
        Assert.StartsWith("Доступно обновление", UpdateStore.record.value.summary);

        UpdateStore.markDismissed(ctx);
        Assert.Null(UpdateStore.bannerVersion(notifyOn: true));

        // Вышла версия ещё новее → снова одна полоса и одно уведомление.
        Publish("v0.8", code: 8);
        UpdateStore.apply(ctx, UpdateChecker.check(ctx), nowMs: 2_000);
        Assert.Equal((8, "0.8 beta"), UpdateStore.bannerVersion(notifyOn: true));
        Assert.True(UpdateStore.shouldNotify(true));
    }

    [Fact]
    public void Periodic_check_only_while_proxy_runs_and_once_a_day()
    {
        Publish("v0.7", code: 7);
        var sink = new RecordingSink(); NotificationHelper.Sink = sink;

        Assert.False(UpdateAutoCheck.periodicTick(ctx, proxyRunning: false));
        Assert.Equal(0, UpdateStore.record.value.checkedAtMs);

        Assert.True(UpdateAutoCheck.periodicTick(ctx, proxyRunning: true));
        Assert.Equal((7, "0.7 beta"), UpdateStore.bannerVersion(notifyOn: true));
        Assert.Single(sink.shown, s => s.kind == NotificationHelper.Kind.UPDATE);

        Assert.False(UpdateAutoCheck.periodicTick(ctx, proxyRunning: true));   // сутки не прошли
    }

    [Fact]
    public void Cold_start_check_waits_for_own_proxy_and_stays_silent_without_it()
    {
        Publish("v0.7", code: 7);
        UpdateAutoCheck.proxyWaitMs = 0;

        // Своего SOCKS нет → молча выходим, ничего не записав (ручная кнопка объяснит).
        Assert.True(UpdateAutoCheck.coldStart(ctx)!.Join(10_000));
        Assert.Equal(0, UpdateStore.record.value.checkedAtMs);

        // «Свой SOCKS» слушает (заглушка рвёт соединения — каскад переходит на прямую ступень) → проверка записана.
        using var fake = new TcpListener(IPAddress.Loopback, 0);
        fake.Start();
        var port = ((IPEndPoint)fake.LocalEndpoint).Port;
        var stop = false;
        var acceptor = new Thread(() => { while (!stop) { try { fake.AcceptTcpClient().Dispose(); } catch (Exception) { return; } } }) { IsBackground = true };
        acceptor.Start();
        CascadeFetch.ownSocksPort = () => port;
        try
        {
            Assert.True(UpdateAutoCheck.coldStart(ctx)!.Join(30_000));
            Assert.NotEqual(0, UpdateStore.record.value.checkedAtMs);
            Assert.Equal((7, "0.7 beta"), UpdateStore.bannerVersion(notifyOn: true));
            Assert.Null(UpdateAutoCheck.coldStart(ctx));   // сутки не прошли — потока нет
        }
        finally { stop = true; fake.Stop(); }
    }

    private sealed class RecordingSink : NotificationHelper.INotificationSink
    {
        public readonly List<(NotificationHelper.Kind kind, string title, string text)> shown = [];
        public readonly List<NotificationHelper.Kind> cancelled = [];
        public void show(NotificationHelper.Kind kind, string title, string text) { lock (shown) shown.Add((kind, title, text)); }
        public void cancel(NotificationHelper.Kind kind) { lock (cancelled) cancelled.Add(kind); }
    }
}
