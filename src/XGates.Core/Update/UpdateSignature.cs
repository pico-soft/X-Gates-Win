using System.Security.Cryptography;
using System.Text;

namespace XGates.Core.Update;

/**
 * Подпись сведений об обновлении (docs/decisions.md §13.6). На Android подлинность обновления гарантирует ОС (подпись
 * APK должна совпасть с установленной); на Windows без платного Authenticode такой проверки нет, поэтому update.json
 * подписан НАШИМ ключом: ECDSA P-256 + SHA-256 (встроено в .NET, без зависимостей). В update.json — sha256 установщика,
 * так что подпись манифеста закрывает и сам файл. Подпись — вложение update.json.sig (base64 DER, как openssl dgst).
 * Закрытый ключ — ТОЛЬКО на сервере сборки (вне репозитория); здесь — открытый.
 */
public static class UpdateSignature
{
    public const string SIG_SUFFIX = ".sig";

    /// <summary>Открытый ключ разработчика (SPKI PEM). Подписывает tools/release/sign-update.sh.</summary>
    internal const string PUBLIC_KEY_PEM = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjeuaoH0wFECgRfvx0vYAg2bHHg+d
ktLyL3cmSEQOPIwvzT7+oZ63oIgfDOsLoa1wqLrUzol6GsriOnSgpvEUDA==
-----END PUBLIC KEY-----
""";

    /// <summary>Подмена ключа (только тесты).</summary>
    internal static string? publicKeyOverrideForTest;

    /// <summary>true — подпись [signatureBase64] над ТОЧНЫМИ байтами [manifestUtf8] верна.</summary>
    public static bool verify(string manifestUtf8, string signatureBase64)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }
        using var ec = ECDsa.Create();
        ec.ImportFromPem(publicKeyOverrideForTest ?? PUBLIC_KEY_PEM);
        var data = Encoding.UTF8.GetBytes(manifestUtf8);
        try { return ec.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); }
        catch (CryptographicException) { return false; }
    }
}
