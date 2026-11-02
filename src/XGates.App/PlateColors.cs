namespace XGates.App;

/// <summary>
/// Цвет плашки активного сервера (правило Elyor 2026-11-02): серая только когда неизвестно, всё измеренное — в цвете.
/// Скорость идёт плавно от тёмно-красно-коричневого (до 0,1 Мбит/с) к зелёному (от 5). Шкала логарифмическая: на обычной
/// шкале всё ниже 1 Мбит/с слилось бы в один тон.
/// </summary>
public static class PlateColors
{
    private static readonly (uint bg, uint fg) OFF = (0xFF3A3A3Au, 0xFFBDBDBDu);
    private static readonly (uint bg, uint fg) OUTAGE = (0xFF7F1D1Du, 0xFFFFCDD2u);
    internal static readonly (uint bg, uint fg) UNKNOWN = (0xFF5C5C5Cu, 0xFFE8E8E8u);

    internal static readonly (double mbps, uint bg, uint fg)[] SCALE =
    [
        (0.1, 0xFF5A2318u, 0xFFF2B8A8u),
        (0.4, 0xFF5D4037u, 0xFFD7CCC8u),
        (1.5, 0xFF465222u, 0xFFD9E2B0u),
        (5.0, 0xFF1B5E20u, 0xFFA5D6A7u),
    ];

    public static (uint bg, uint fg) of(bool running, bool problem, bool verified, double speedMbps) =>
        !running ? OFF :
        problem ? OUTAGE :
        !verified || speedMbps <= 0.0 ? UNKNOWN :
        forSpeed(speedMbps);

    internal static (uint bg, uint fg) forSpeed(double mbps)
    {
        if (mbps <= SCALE[0].mbps) return (SCALE[0].bg, SCALE[0].fg);
        if (mbps >= SCALE[^1].mbps) return (SCALE[^1].bg, SCALE[^1].fg);
        var i = Array.FindIndex(SCALE, s => mbps < s.mbps);
        var (a, b) = (SCALE[i - 1], SCALE[i]);
        var t = Math.Log(mbps / a.mbps) / Math.Log(b.mbps / a.mbps);
        return (mix(a.bg, b.bg, t), mix(a.fg, b.fg, t));
    }

    private static uint mix(uint a, uint b, double t)
    {
        uint channel(int shift)
        {
            double x = (a >> shift) & 0xFF, y = (b >> shift) & 0xFF;
            return (uint)Math.Round(x + (y - x) * t) << shift;
        }
        return 0xFF000000u | channel(16) | channel(8) | channel(0);
    }
}
