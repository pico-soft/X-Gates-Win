using XGates.Core.Compat;
using XGates.Core.Crash;

namespace XGates.Core.Tests.Crash;

/**
 * Порт app/src/test/…/SanitizerTest.kt (Промпт 97.D) — повторная проверка очистки отчёта перед отправкой постороннему:
 * ни адресов подписок, ни учётных данных, ни UUID, ни паролей, ни внешних IP не должно остаться в очищенном тексте.
 */
public class SanitizerTest
{
    [Fact]
    public void strips_subscription_urls_credentials_uuid_ip_and_tokens()
    {
        var raw = """
            === XrayProxyDroid — отчёт о сбое ===
            Устройство: samsung SM-S908E · Android 14 (SDK 34)
            Подписка: https://sub.example.com/link/abcDEF123456?token=SECRETVALUE
            Сервер: vless://3f2504e0-4f89-41d3-9a0c-0305e82c3301@203.0.113.77:443?type=tcp#Node1
            Trojan: trojan://myS3cretPass@198.51.100.12:8443#T
            Shadowsocks: ss://YWVzLTI1Ni1nY206c2VjcmV0cGFzc3dvcmQ@192.0.2.9:8388#SS
            Логин: admin:hunter2password@server.host
            UUID: 550e8400-e29b-41d4-a716-446655440000
            Внешний IP: 45.132.98.210
            """;

        var clean = Sanitizer.clean(raw);

        // Ничего чувствительного не осталось.
        Assert.DoesNotContain("sub.example.com", clean);
        Assert.DoesNotContain("vless://", clean);
        Assert.DoesNotContain("trojan://", clean);
        Assert.DoesNotContain("ss://", clean);
        Assert.DoesNotContain("550e8400-e29b-41d4-a716-446655440000", clean);
        Assert.DoesNotContain("3f2504e0-4f89-41d3-9a0c-0305e82c3301", clean);
        Assert.DoesNotContain("45.132.98.210", clean);
        Assert.DoesNotContain("203.0.113.77", clean);
        Assert.DoesNotContain("myS3cretPass", clean);
        Assert.DoesNotContain("hunter2password", clean);
        Assert.DoesNotContain("YWVzLTI1Ni1nY206c2VjcmV0cGFzc3dvcmQ", clean);
        Assert.DoesNotContain("SECRETVALUE", clean);

        // Полезное (модель/версия Android) сохраняется — маска не съедает диагностику.
        Assert.Contains("SM-S908E", clean);
        Assert.Contains("Android 14", clean);
    }
}
