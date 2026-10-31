using System.Globalization;
using XGates.Core.Compat;
using XGates.Core.Platform;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Xray.Link;
using static XGates.Core.Xray.Link.ServerProfileKt;

namespace XGates.App;

/// <summary>Подписи и форматирование для экранов — порт private-функций MainActivity.kt (те же правила текста).</summary>
public static class UiFormat
{
    public static string serverLabel(ServerProfile p, Blocklist bl) => $"{p.protocol}  ·  {displayName(p, bl)}  ·  {p.address}:{p.port}";

    /// <summary>Имя ОТ ПРОВАЙДЕРА, как пришло (без оверрайда).</summary>
    public static string providerName(ServerProfile p) => p.remarks.IfBlank(p.address);

    /// <summary>ЕДИНАЯ точка показа имени: пользовательское, если задано, иначе имя провайдера.</summary>
    public static string displayName(ServerProfile p, Blocklist bl) => bl.customName(SubscriptionManager.serverKey(p)) ?? providerName(p);

    /// <summary>Мелкая подпись: протокол · network · security.</summary>
    public static string protoNetSec(ServerProfile p) => $"{p.protocol} · {p.network} · {normalizeStreamSecurity(p.security)}";

    /// <summary>
    /// Подпись-дискриминатор для ОДИНАКОВЫХ имён: serverKey → «протокол · network · security»; совпала и подпись →
    /// + адрес:порт; совпал и он → + «#» и 4 hex хэша ключа. Уникальные имена в словарь не попадают.
    /// Находка порта №9: раньше «гарантией» были 4 последних символа ключа, а ключ кончается пустыми полями
    /// («…|tcp||||||») → у одноимённых серверов подпись «… · ||||», строки неотличимы.
    /// </summary>
    public static Dictionary<string, string> buildDiscriminators(IReadOnlyList<ServerProfile> servers, Blocklist bl)
    {
        var result = new Dictionary<string, string>();
        foreach (var group in servers.GroupBy(it => displayName(it, bl)))
        {
            var g = group.ToList();
            if (g.Count < 2) continue;
            var ambiguous = g.Select(protoNetSec).ToHashSet().Count < g.Count;
            foreach (var p in g)
            {
                var key = SubscriptionManager.serverKey(p);
                if (!ambiguous) { result[key] = protoNetSec(p); continue; }
                var sameAddr = g.Count(it => protoNetSec(it) == protoNetSec(p) && hostPort(it) == hostPort(p)) > 1;
                result[key] = $"{protoNetSec(p)} · {hostPort(p)}" + (sameAddr ? $" · #{keyTag(key)}" : "");
            }
        }
        return result;
    }

    /// <summary>адрес:порт для подписи (IPv6 — в скобках, как в ссылках).</summary>
    public static string hostPort(ServerProfile p) => (p.address.Contains(':') ? $"[{p.address}]" : p.address) + ":" + p.port;

    /// <summary>4 hex от Java String.hashCode ключа — стабильно между запусками и одинаково с Android.</summary>
    internal static string keyTag(string key)
    {
        var h = 0;
        foreach (var ch in key) h = unchecked(31 * h + ch);
        return ((uint)h).ToString("x8")[4..];
    }

    public static string speedAgeTextMs(long t)
    {
        var min = (JavaSystem.currentTimeMillis() - t) / 60_000;
        return min switch
        {
            < 0 => "",
            < 1 => "сейчас",
            < 60 => $"{min} мин назад",
            < 1440 => $"{min / 60} ч назад",
            _ => $"{min / 1440} дн назад",
        };
    }

    public static string speedAgeText(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return "";
        // Kotlin: SimpleDateFormat("yyyy-MM-dd HH:mm") в ЛОКАЛЬНОЙ зоне; parse(String) нестрогий — хвост после минут
        // (напр. «:56») игнорирует. Повторяем: разбираем первые 16 символов.
        var head = ts.Trim();
        if (head.Length > 16) head = head[..16];
        if (!DateTime.TryParseExact(head, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)) return "";
        return speedAgeTextMs(new DateTimeOffset(dt).ToUnixTimeMilliseconds());
    }

    /// <summary>Компактная ячейка скорости: не измерялась ≠ «0» ≠ «✗» (провал) — разные вещи.</summary>
    public static string speedCell(double? mbps) => mbps switch
    {
        null => "не изм.",
        <= 0 => "✗",
        _ => numStr(mbps.Value) + " Мбит/с",
    };

    /// <summary>Число скорости: ≥10 → целое, &lt;10 → 1 знак.</summary>
    public static string numStr(double mbps) =>
        mbps >= 10 ? KotlinMath.roundToInt(mbps).ToString(CultureInfo.InvariantCulture)
                   : JavaNumbers.JavaDoubleToString(KotlinMath.roundToInt(mbps * 10) / 10.0);   // Double.toString: «5.0», не «5»

    /// <summary>Одна десятая, как Kotlin `(v * 10).roundToInt() / 10.0` (Double.toString: «5.0», не «5»).</summary>
    public static string r1(double v) => JavaNumbers.JavaDoubleToString(KotlinMath.roundToInt(v * 10) / 10.0);

    public static string humanAgo(long ms)
    {
        var sec = Math.Max(ms / 1000, 0);
        return sec switch
        {
            < 60 => $"{sec} сек",
            < 3600 => $"{sec / 60} мин",
            _ => $"{sec / 3600} ч {sec % 3600 / 60} мин",
        };
    }

    public static string fmtBytes(long b) => b switch
    {
        < 1024L * 1024 => $"{(b / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} КБ",
        < 1024L * 1024 * 1024 => $"{(b / 1_048_576.0).ToString("0.0", CultureInfo.InvariantCulture)} МБ",
        _ => $"{(b / 1_073_741_824.0).ToString("0.0", CultureInfo.InvariantCulture)} ГБ",
    };

    /// <summary>ч:мм:сс.</summary>
    public static string fmtUptime(long ms)
    {
        var s = Math.Max(ms / 1000, 0);
        return $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}";
    }

    /// <summary>Как monitorTimeFmt Kotlin: «dd.MM HH:mm:ss» в локальной зоне.</summary>
    public static string monitorTime(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture);
}
