namespace XGates.Core.Libv2ray;

/// <summary>Аналог <c>libv2ray.CoreCallbackHandler</c> (gomobile-интерфейс AndroidLibXrayLite).</summary>
public interface CoreCallbackHandler
{
    long startup();
    long shutdown();
    long onEmitStatus(long l, string? s);
}

/// <summary>Исключение старта/конфига ядра (в Kotlin gomobile бросал java.lang.Exception с текстом ошибки Go).</summary>
public sealed class XrayCoreException(string message, Exception? inner = null) : Exception(message, inner);
