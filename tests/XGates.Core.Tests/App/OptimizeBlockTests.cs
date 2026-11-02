using System.Globalization;
using XGates.App;
using XGates.Core.Settings;

namespace XGates.Core.Tests.App;

/// <summary>«Периодический перемер и проверки» (OptimizeChecksBlock Kotlin): деления, правка АКТИВНОГО набора, состав строк, расход.</summary>
public class OptimizeBlockTests
{
    private static readonly AppSettings D = SettingsStore.DEFAULTS;

    [Fact]
    public void Sliders_edit_active_set_and_cost_lines()
    {
        Assert.Equal(4, OptimizeBlock.optimizeIndex(D));                  // 6 часов (обычный набор)
        Assert.Equal(1, OptimizeBlock.checkIndex(D));                     // 1 мин
        Assert.Equal(7, OptimizeBlock.nearestIndex(OptimizeBlock.OPT_SECS, 0));      // 0 = «Выкл» справа
        Assert.Equal(2, OptimizeBlock.nearestIndex(OptimizeBlock.CHK_SECS, 100));    // ближайшее деление

        var save = D with { trafficSaveMode = true, economyEnabled = true };
        Assert.Equal(7, OptimizeBlock.optimizeIndex(save));               // у экономии перемер выкл
        var edited = OptimizeBlock.pickOptimize(save, 2);
        Assert.Equal(3600, edited.optimizeSecSave);
        Assert.Equal(D.optimizeSecNormal, edited.optimizeSecNormal);      // обычный набор не тронут
        Assert.Equal(300, OptimizeBlock.pickCheck(D, 3).connectionCheckIntervalSec);

        Assert.StartsWith("⚙ Правите набор режима: Обычный (экономия выключена).", OptimizeBlock.modeLine(D));
        Assert.Contains("Экономия (мобильная сеть)", OptimizeBlock.modeLine(save with { trafficSaveAutoByNetwork = true }));

        // Перемер выкл → в блоке только «сколько быстрых» и «свежесть»; вкл → полный набор строк, правки — в активный набор.
        Assert.Equal(2, OptimizeBlock.rows(save).Count);
        var rows = OptimizeBlock.rows(D);
        Assert.Equal(9, rows.Count);
        var top = rows.Single(r => r.label == "Размер топа (быстрых серверов)");
        Assert.Equal(7, top.commit(D, "7")!.topBatchNormal);
        Assert.Equal(D.topBatchSave, top.commit(D, "7")!.topBatchSave);
        Assert.Equal(7200, rows.Single(r => r.label == "Предел давности замера").commit(D, "2")!.topFreshSec);

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        Assert.Equal("≈ 1,5 ГБ/мес", OptimizeBlock.fmtData(1536, ru));
        Assert.Equal("≈ 43 МБ/мес", OptimizeBlock.fmtData(43.2, ru));
        Assert.DoesNotContain("на очень медленном", OptimizeBlock.totalLine(save));   // перемер выключен: скорость сервера на расход не влияет
        Assert.StartsWith("Расход проверок: ≈ 124 МБ/мес", OptimizeBlock.checkCostLine(D).Replace(",", "."));   // 43200 проверок × 3000 Б
    }

    // Замер на достаточность при пороге 8 Мбит/с: 512 КиБ прогрева + 1,2 МБ, в мебибайтах.
    private const double ENOUGH = (524_288 + 1_200_000) / 1_048_576.0;

    [Fact]
    public void Top_upkeep_follows_the_monitor_schedule()
    {
        // От 10 Мбит/с раз в час: 720 проходов × (активный + подмешанный), весь топ раз в 6 ч: 120 × 5.
        Assert.Equal(720 * 2 * ENOUGH + 120 * 5 * ENOUGH, OptimizeBlock.optimizeMbPerMonth(21600, 5, 1, 8.0, 21600, 12.0), 6);
        // Ниже 1 Мбит/с каждую минуту. Медленный активный ограничен окном замера 1,6 с, кандидат может быть быстрым.
        var active = 0.5 / 8.0 * 1_000_000.0 * 1.6 / 1_048_576.0;
        Assert.Equal(43_200 * (active + ENOUGH) + 120 * 5 * ENOUGH, OptimizeBlock.optimizeMbPerMonth(21600, 5, 1, 8.0, 21600, 0.5), 6);
        // Скорость не измерена: монитор проверяет как медленный.
        Assert.True(OptimizeBlock.optimizeMbPerMonth(21600, 5, 1, 8.0, 21600, null) > 40_000.0);
        // Настройка задаёт самую редкую проверку: 15 минут чаще часа, и топ перемеряется не чаще предела давности.
        Assert.Equal(2880 * 2 * ENOUGH + 120 * 5 * ENOUGH, OptimizeBlock.optimizeMbPerMonth(900, 5, 1, 8.0, 21600, 12.0), 6);
        Assert.Equal(0.0, OptimizeBlock.optimizeMbPerMonth(0, 5, 1, 8.0, 21600, 12.0));
    }

    [Fact]
    public void Total_counts_every_background_measure()
    {
        var full = (D.speedWarmupBudgetBytes + D.activeMeasureBudgetBytes) / 1_048_576.0;
        var rating = 720 * 10 * (full + 116.0 / 1024.0);                                             // «Живые» устаревают за час, топ 10
        var liveness = 1440 * (524_288 + 300_000) / 1_048_576.0;                                     // раз в 30 мин, порог 2 Мбит/с
        var plate = 8640 * (2 * full + D.uploadMeasureBudgetBytes / 1_048_576.0);                    // проба плашки раз в 5 мин
        var keep = OptimizeBlock.optimizeMbPerMonth(D.activeOptimizeSec, D.activeTopBatch, D.topMixUnmeasured, D.sufficientMbps, D.topFreshSec, 12.0);
        Assert.Equal(rating + liveness + plate + keep, OptimizeBlock.backgroundMbPerMonth(D, 12.0), 3);

        // В экономии нет перемера и пробы плашки, а рейтинг и живость остаются (монитор их не отключает): ноль здесь был бы неправдой.
        var save = D with { trafficSaveMode = true, economyEnabled = true };
        var saveRating = 720 * 3 * (full + 116.0 / 1024.0);
        Assert.Equal(saveRating + liveness + 180 * ENOUGH, OptimizeBlock.backgroundMbPerMonth(save, 12.0), 3);

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = ru;
        try
        {
            var total = OptimizeBlock.totalLine(D);
            Assert.Contains(OptimizeBlock.fmtData(OptimizeBlock.backgroundMbPerMonth(D, 10.0), ru) + " на быстром сервере", total);
            Assert.Contains(OptimizeBlock.fmtData(OptimizeBlock.backgroundMbPerMonth(D, 0.5), ru) + " на очень медленном", total);
            Assert.Contains("строка «Тест»", total);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
