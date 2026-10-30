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
        Assert.Equal(10, rows.Count);
        var top = rows.Single(r => r.label == "Размер топа (быстрых серверов)");
        Assert.Equal(7, top.commit(D, "7")!.topBatchNormal);
        Assert.Equal(D.topBatchSave, top.commit(D, "7")!.topBatchSave);
        Assert.Equal(7200, rows.Single(r => r.label == "Предел давности замера").commit(D, "2")!.topFreshSec);

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        Assert.Equal("≈ 1,5 ГБ/мес", OptimizeBlock.fmtData(1536, ru));
        Assert.Equal("≈ 43 МБ/мес", OptimizeBlock.fmtData(43.2, ru));
        // 6 ч → 120 циклов/мес × (5 + 1) × (0.5 + 8/8×1.2) = 1224 МБ
        Assert.Equal(1224.0, OptimizeBlock.optimizeMbPerMonth(21600, 5, 1, 8.0), 6);
        Assert.Contains("≈ 0 (перемер выкл — только реактивно при потере связи)", OptimizeBlock.totalLine(save));
        Assert.StartsWith("Расход проверок: ≈ 124 МБ/мес", OptimizeBlock.checkCostLine(D).Replace(",", "."));   // 43200 проверок × 3000 Б
    }
}
