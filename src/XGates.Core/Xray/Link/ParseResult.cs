namespace XGates.Core.Xray.Link;

/// <summary>
/// Типизированный результат разбора ссылки. «Не падать на неизвестном протоколе» —
/// это про то, что ошибка/неподдержка возвращаются значением, а не исключением.
/// </summary>
public abstract record ParseResult
{
    private ParseResult() { }

    /// <summary>Разобрали в один из наших четырёх протоколов.</summary>
    public sealed record Supported(ServerProfile profile) : ParseResult;

    /// <summary>Схема распознана, но исполняется не нашим ядром (hysteria2/tuic/…), ждёт sing-box.</summary>
    public sealed record Unsupported(string scheme, string raw) : ParseResult;

    /// <summary>Схема наша, но строка битая; либо схема вообще неизвестна.</summary>
    public sealed record Invalid(string reason, string raw) : ParseResult;
}
