using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using XGates.Core.Platform;
using XGates.Core.Update;

namespace XGates.Core.Tests.Update;

/// <summary>
/// Подпись update.json (§13.6): верная подпись принимается, любая правка манифеста/подписи/чужой ключ — нет;
/// формат совместим с openssl (tools/release/make-update.py подписывает именно им).
/// </summary>
[Collection("xray")]   // publicKeyOverrideForTest — общее состояние процесса (его же меняют тесты проверки обновления)
public sealed class UpdateSignatureTests : IDisposable
{
    public void Dispose() => UpdateSignature.publicKeyOverrideForTest = null;

    internal static string Sign(ECDsa key, string manifest) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    [Fact]
    public void Valid_signature_accepted_tampered_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        UpdateSignature.publicKeyOverrideForTest = key.ExportSubjectPublicKeyInfoPem();
        const string manifest = "{\"versionCode\": 2, \"artifacts\": []}\n";
        var sig = Sign(key, manifest);

        Assert.True(UpdateSignature.verify(manifest, sig));
        Assert.True(UpdateSignature.verify(manifest, sig + "\n"));                     // перевод строки в .sig не мешает
        Assert.False(UpdateSignature.verify(manifest.Replace("2", "3"), sig));        // правка манифеста
        Assert.False(UpdateSignature.verify(manifest.TrimEnd('\n'), sig));             // даже один байт
        Assert.False(UpdateSignature.verify(manifest, "не base64 !!!"));
        Assert.False(UpdateSignature.verify(manifest, ""));
        var bad = Convert.FromBase64String(sig); bad[^1] ^= 1;
        Assert.False(UpdateSignature.verify(manifest, Convert.ToBase64String(bad)));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateSignature.verify(manifest, Sign(other, manifest)));       // чужой ключ
    }

    [Fact]
    public void Embedded_developer_key_rejects_foreign_signature()
    {
        // Без подмены — вшитый ключ разработчика: подпись любым другим ключом не проходит.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateSignature.verify("{}", Sign(other, "{}")));
    }

    [Fact]
    public void Release_tool_output_verifies_in_app()
    {
        // tools/release/make-update.py (openssl) → update.json + .sig → проверка тем же кодом, что в приложении.
        if (!Has("python3") || !Has("openssl")) return;   // нет инструментов (напр. чистый Windows-агент) — пропуск
        var dir = Directory.CreateTempSubdirectory("xgates-sign-").FullName;
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(Path.Combine(dir, "k.pem"), key.ExportECPrivateKeyPem());
            File.WriteAllText(Path.Combine(dir, "pub.pem"), key.ExportSubjectPublicKeyInfoPem());
            File.WriteAllBytes(Path.Combine(dir, "XGatesWin-9.9-win-x64.msi"), RandomNumberGenerator.GetBytes(4096));
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "Исправления\nвторая строка");
            var (rc, output) = Run("python3", Path.Combine(RepoRoot(), "tools", "release", "make-update.py"),
                "--msi", Path.Combine(dir, "XGatesWin-9.9-win-x64.msi"), "--notes-file", Path.Combine(dir, "notes.txt"),
                "--key", Path.Combine(dir, "k.pem"), "--verify-pubkey", Path.Combine(dir, "pub.pem"));
            Assert.True(rc == 0, output);

            var manifestBytes = File.ReadAllBytes(Path.Combine(dir, "update.json"));
            Assert.NotEqual(0xEF, manifestBytes[0]);   // без BOM
            var manifest = Encoding.UTF8.GetString(manifestBytes);
            var sig = File.ReadAllText(Path.Combine(dir, "update.json.sig"));
            UpdateSignature.publicKeyOverrideForTest = key.ExportSubjectPublicKeyInfoPem();
            Assert.True(UpdateSignature.verify(manifest, sig));

            var m = KJson.decodeFromString<UpdateManifest>(manifest, KJson.Compact);
            Assert.Equal(AppInfo.APPLICATION_ID, m.packageName);
            Assert.Equal(AppInfo.VERSION_CODE, m.versionCode);
            Assert.Equal(AppInfo.VERSION_NAME, m.versionName);
            Assert.Equal("Исправления\nвторая строка", m.notes);
            var art = Assert.Single(m.artifacts);
            Assert.Equal(UpdateChecker.PLATFORM, art.abi);
            Assert.Equal("XGatesWin-9.9-win-x64.msi", art.fileName);
            Assert.Equal(UpdateInstaller.sha256(Path.Combine(dir, "XGatesWin-9.9-win-x64.msi")), art.sha256);

            // Ключ подписи не совпал с ключом самопроверки → отказ, неподписанный манифест не оставлен.
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(Path.Combine(dir, "other.pem"), other.ExportSubjectPublicKeyInfoPem());
            File.Delete(Path.Combine(dir, "update.json")); File.Delete(Path.Combine(dir, "update.json.sig"));
            (rc, output) = Run("python3", Path.Combine(RepoRoot(), "tools", "release", "make-update.py"),
                "--msi", Path.Combine(dir, "XGatesWin-9.9-win-x64.msi"),
                "--key", Path.Combine(dir, "k.pem"), "--verify-pubkey", Path.Combine(dir, "other.pem"));
            Assert.NotEqual(0, rc);
            Assert.False(File.Exists(Path.Combine(dir, "update.json")));
            Assert.False(File.Exists(Path.Combine(dir, "update.json.sig")));
        }
        finally { Directory.Delete(dir, true); }
    }

    internal static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "XGatesWin.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("не нашёл корень репозитория");
    }

    private static bool Has(string tool)
    {
        try { return Run(tool, "--version").rc == 0 || tool == "openssl" && Run(tool, "version").rc == 0; }
        catch (Exception) { return false; }
    }

    private static (int rc, string output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit(60_000);
        return (p.ExitCode, o.Result + e.Result);
    }
}
