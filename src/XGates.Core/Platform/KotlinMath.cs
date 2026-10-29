namespace XGates.Core.Platform;

/// <summary>Числовые функции Kotlin stdlib с их семантикой.</summary>
public static class KotlinMath
{
    /// <summary>Kotlin <c>Double.roundToInt()</c> = Java Math.round: floor(x + 0.5); NaN → исключение; насыщение.</summary>
    public static int roundToInt(this double x)
    {
        if (double.IsNaN(x)) throw new ArgumentException("Cannot round NaN value.");
        double r = Math.Floor(x + 0.5);
        if (r >= int.MaxValue) return int.MaxValue;
        if (r <= int.MinValue) return int.MinValue;
        return (int)r;
    }

    /// <summary>Kotlin <c>Double.roundToLong()</c>.</summary>
    public static long roundToLong(this double x)
    {
        if (double.IsNaN(x)) throw new ArgumentException("Cannot round NaN value.");
        double r = Math.Floor(x + 0.5);
        if (r >= long.MaxValue) return long.MaxValue;
        if (r <= long.MinValue) return long.MinValue;
        return (long)r;
    }

    // Kotlin coerceIn БРОСАЕТ на пустом диапазоне (min > max) — Android-код местами на это натыкается (см. decisions.md).
    private static ArgumentException emptyRange(object max, object min) =>
        new($"Cannot coerce value to an empty range: maximum {max} is less than minimum {min}.");

    public static int coerceIn(this int v, int min, int max) => min > max ? throw emptyRange(max, min) : v < min ? min : v > max ? max : v;
    public static long coerceIn(this long v, long min, long max) => min > max ? throw emptyRange(max, min) : v < min ? min : v > max ? max : v;
    public static double coerceIn(this double v, double min, double max) => min > max ? throw emptyRange(max, min) : v < min ? min : v > max ? max : v;
    public static int coerceAtLeast(this int v, int min) => v < min ? min : v;
    public static long coerceAtLeast(this long v, long min) => v < min ? min : v;
    public static double coerceAtLeast(this double v, double min) => v < min ? min : v;
    public static int coerceAtMost(this int v, int max) => v > max ? max : v;
    public static long coerceAtMost(this long v, long max) => v > max ? max : v;
    public static double coerceAtMost(this double v, double max) => v > max ? max : v;
}
