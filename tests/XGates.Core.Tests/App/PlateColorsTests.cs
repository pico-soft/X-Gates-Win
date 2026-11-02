using XGates.App;

namespace XGates.Core.Tests.App;

public sealed class PlateColorsTests
{
    private static IEnumerable<double> Speeds() => Enumerable.Range(0, 301).Select(i => 0.05 * Math.Pow(400.0, i / 300.0));   // 0,05 … 20 Мбит/с

    private static (int r, int g, int b) Rgb(uint c) => ((int)(c >> 16) & 0xFF, (int)(c >> 8) & 0xFF, (int)c & 0xFF);

    private static double Luminance(uint c)
    {
        static double lin(int x) { var v = x / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        var (r, g, b) = Rgb(c);
        return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
    }

    private static double Contrast(uint a, uint b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Unknown_speed_is_grey(double mbps) =>
        Assert.Equal(PlateColors.UNKNOWN, PlateColors.of(running: true, problem: false, verified: true, mbps));

    [Fact]
    public void Unconfirmed_connection_is_grey_even_with_a_stored_speed() =>
        Assert.Equal(PlateColors.UNKNOWN, PlateColors.of(running: true, problem: false, verified: false, 15.0));

    [Fact]
    public void Every_known_speed_has_a_colour_not_grey()
    {
        foreach (var mbps in Speeds())
        {
            var (r, g, b) = Rgb(PlateColors.of(running: true, problem: false, verified: true, mbps).bg);
            Assert.True(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) >= 20, $"{mbps} Мбит/с: {r},{g},{b}");
        }
    }

    [Fact]
    public void Scale_runs_from_red_brown_to_the_accepted_green()
    {
        Assert.Equal((0xFF5A2318u, 0xFFF2B8A8u), PlateColors.forSpeed(0.01));
        Assert.Equal((0xFF5A2318u, 0xFFF2B8A8u), PlateColors.forSpeed(0.1));
        Assert.Equal((0xFF1B5E20u, 0xFFA5D6A7u), PlateColors.forSpeed(5.0));
        Assert.Equal((0xFF1B5E20u, 0xFFA5D6A7u), PlateColors.forSpeed(50.0));
    }

    [Fact]
    public void Colour_gets_greener_as_speed_grows()
    {
        var previous = int.MinValue;
        foreach (var mbps in Speeds())
        {
            var (r, g, _) = Rgb(PlateColors.forSpeed(mbps).bg);
            Assert.True(g - r >= previous - 1, $"{mbps} Мбит/с");   // каналы округляются по отдельности: шаг назад на 1 глазу не виден
            previous = g - r;
        }
    }

    [Fact]
    public void Text_stays_readable_on_every_shade()
    {
        foreach (var mbps in Speeds())
        {
            var (bg, fg) = PlateColors.forSpeed(mbps);
            Assert.True(Contrast(bg, fg) >= 4.5, $"{mbps} Мбит/с: {Contrast(bg, fg):F2}");
        }
    }

    [Fact]
    public void Off_and_outage_do_not_depend_on_speed()
    {
        Assert.Equal(PlateColors.of(false, false, false, 0.0), PlateColors.of(false, false, true, 15.0));
        Assert.Equal(PlateColors.of(true, true, false, 0.0), PlateColors.of(true, true, true, 15.0));
        Assert.NotEqual(PlateColors.UNKNOWN, PlateColors.of(true, true, true, 0.0));
    }
}
