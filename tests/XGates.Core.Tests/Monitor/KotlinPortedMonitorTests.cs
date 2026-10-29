using XGates.Core.Monitor;
using XGates.Core.Settings;
using XGates.Core.Xray;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests.Monitor;

/**
 * Порт app/src/test/…/FastRecoveryTest.kt (Пр.143/151): быстрое восстановление — «самый быстрый из ЖИВЫХ первым».
 *  (1) «вероятно живой» = свежий пинг ≥0 ИЛИ известная скорость >0;
 *  (2) порядок = по убыванию скорости, при равной — меньший пинг раньше; не-живые ОТСЕЯНЫ;
 *  (3) нестабильные исключаются, пока есть стабильные.
 */
public class FastRecoveryTest
{
    private static ServerProfile srv(string name, int? ping = null, double? speed = null) => new()
    {
        protocol = Protocol.VLESS, remarks = name, address = $"{name}.example.com", port = 443, credential = "u",
        pingMs = ping, speedMbps = speed,
    };

    [Fact]
    public void likely_alive_by_ping_or_speed()
    {
        Assert.True(NetworkMonitor.isLikelyAlive(srv("a", ping: 120)), "свежий пинг → живой");
        Assert.True(NetworkMonitor.isLikelyAlive(srv("b", speed: 12.0)), "известная скорость → живой");
        Assert.True(NetworkMonitor.isLikelyAlive(srv("c", ping: 80, speed: 30.0)), "пинг+скорость → живой");
    }

    [Fact]
    public void not_alive_without_ping_or_speed()
    {
        Assert.False(NetworkMonitor.isLikelyAlive(srv("x")), "нет данных → не живой");
        Assert.False(NetworkMonitor.isLikelyAlive(srv("y", ping: -1)), "мёртвый пинг −1, скорости нет → не живой");
        Assert.False(NetworkMonitor.isLikelyAlive(srv("z", speed: 0.0)), "скорость 0, пинга нет → не живой");
    }

    [Fact]
    public void order_fastest_first_then_lower_ping()
    {
        var list = new[] { srv("slow", ping: 50, speed: 5.0), srv("fast", ping: 200, speed: 90.0), srv("mid", ping: 30, speed: 40.0) };
        Assert.Equal(["fast", "mid", "slow"], NetworkMonitor.orderFastCandidates(list).Select(it => it.remarks));   // по скорости убыв., пинг вторичен
    }

    [Fact]
    public void order_equal_speed_prefers_lower_ping()
    {
        var list = new[] { srv("hi-ping", ping: 300, speed: 20.0), srv("lo-ping", ping: 40, speed: 20.0) };
        Assert.Equal(["lo-ping", "hi-ping"], NetworkMonitor.orderFastCandidates(list).Select(it => it.remarks));   // равная скорость → меньший пинг раньше
    }

    [Fact]
    public void order_unstable_excluded_when_stable_exists()
    {
        var list = new[] { srv("flappy-fast", ping: 30, speed: 90.0), srv("stable-slow", ping: 50, speed: 8.0) };
        var unstable = new HashSet<string> { "flappy-fast.example.com" };
        Assert.Equal(["stable-slow"], NetworkMonitor.orderFastCandidates(list, it => unstable.Contains(it.address)).Select(it => it.remarks));
    }

    // Пр.151: если СТАБИЛЬНЫХ нет — берём и нестабильных (связь любой ценой).
    [Fact]
    public void order_unstable_kept_when_no_stable()
    {
        var list = new[] { srv("flappy-a", ping: 30, speed: 5.0), srv("flappy-b", ping: 50, speed: 9.0) };
        var unstable = new HashSet<string> { "flappy-a.example.com", "flappy-b.example.com" };
        Assert.Equal(["flappy-b", "flappy-a"], NetworkMonitor.orderFastCandidates(list, it => unstable.Contains(it.address)).Select(it => it.remarks));
    }

    [Fact]
    public void order_drops_non_alive_and_keeps_ping_only()
    {
        var list = new[] { srv("dead", ping: -1), srv("unmeasured"), srv("ping-only", ping: 90), srv("fast", ping: 120, speed: 55.0) };
        Assert.Equal(["fast", "ping-only"], NetworkMonitor.orderFastCandidates(list).Select(it => it.remarks));
    }
}

/**
 * Порт EconomyModeTest.kt (Пр.139): (1) эффективная экономия = мастер + подпункт «только моб.» + тип сети;
 * (2) ЖИВОСТЬ («Живые») зависит ТОЛЬКО от пинга, одинаково во всех режимах и при любой скорости.
 */
[Collection("stores")]
public class EconomyModeTest
{
    [Fact]
    public void master_off_never_economy()
    {
        var s = new AppSettings { economyEnabled = false, trafficSaveAutoByNetwork = true };
        Assert.False(s.economyEffective(isMobile: true));
        Assert.False(s.economyEffective(isMobile: false));
    }

    [Fact]
    public void master_on_mobileOnly_economy_only_on_mobile()
    {
        var s = new AppSettings { economyEnabled = true, trafficSaveAutoByNetwork = true };
        Assert.True(s.economyEffective(isMobile: true), "на мобильной — экономия");
        Assert.False(s.economyEffective(isMobile: false), "на Wi-Fi — обычный");
    }

    [Fact]
    public void master_on_alwaysEconomy()
    {
        var s = new AppSettings { economyEnabled = true, trafficSaveAutoByNetwork = false };
        Assert.True(s.economyEffective(isMobile: true));
        Assert.True(s.economyEffective(isMobile: false));
    }

    [Fact]
    public void defaults_first_install_economy_off_but_suboption_on()
    {
        var d = new AppSettings();
        Assert.False(d.economyEnabled, "первая установка — экономия ВЫКЛ");
        Assert.True(d.trafficSaveAutoByNetwork, "подпункт «только моб.» по дефолту ВКЛ");
    }

    private readonly Blocklist bl = new();
    private static ServerProfile srv() => new() { protocol = Protocol.VLESS, remarks = "n", address = "h.example.com", port = 443, credential = "u" };

    [Fact]
    public void alive_pinged_server_visible_regardless_of_speed()
    {
        var s = new AppSettings();
        Assert.True(ServerFilter.isVisible(srv(), 120, null, s, bl), "не мерена");
        Assert.True(ServerFilter.isVisible(srv(), 120, -1.0, s, bl), "провал -1 не прячет");
        Assert.True(ServerFilter.isVisible(srv(), 120, 0.05, s, bl), "низкая скорость не прячет");
        Assert.True(ServerFilter.isVisible(srv(), 120, 50.0, s, bl), "нормальная");
    }

    [Fact]
    public void dead_ping_not_visible()
    {
        var s = new AppSettings();
        Assert.False(ServerFilter.isVisible(srv(), -1, 50.0, s, bl), "пинг мёртв (-1)");
        Assert.False(ServerFilter.isVisible(srv(), null, 50.0, s, bl), "пинг не мерян (null)");
    }

    [Fact]
    public void visibility_identical_no_matter_the_economy_settings()
    {
        var normal = new AppSettings { economyEnabled = false };
        var economy = new AppSettings { economyEnabled = true, trafficSaveMode = true };
        Assert.Equal(ServerFilter.isVisible(srv(), 100, -1.0, normal, bl), ServerFilter.isVisible(srv(), 100, -1.0, economy, bl));
        Assert.True(ServerFilter.isVisible(srv(), 100, -1.0, economy, bl));
    }
}

/**
 * Порт WhiteListDecisionTest.kt (Пр.140): чистая логика решения о режиме белых списков (без сети/настроек).
 */
public class WhiteListDecisionTest
{
    private static WhiteListDetector.Decision d(bool auto, bool manual, bool active, bool g, bool y) =>
        WhiteListDetector.decide(auto, manual, active, googleOk: g, yandexOk: y);

    [Fact] public void feature_off_forces_normal() { var r = d(false, false, true, false, true); Assert.False(r.active); Assert.False(r.manualNormal); }

    [Fact] public void enters_white_when_google_blocked_yandex_ok() => Assert.True(d(true, false, false, false, true).active, "должен войти в белый режим");

    [Fact]
    public void google_back_returns_to_normal_and_clears_manual()
    {
        var r = d(true, true, true, true, true);
        Assert.False(r.active, "google открыт → обычный");
        Assert.False(r.manualNormal, "ручной override снят");
    }

    [Fact]
    public void manual_normal_keeps_normal_even_if_google_blocked()
    {
        var r = d(true, true, false, false, true);
        Assert.False(r.active, "юзер держит обычный");
        Assert.True(r.manualNormal, "override держится, пока google не вернётся");
    }

    [Fact]
    public void both_dead_is_general_outage_no_change()
    {
        Assert.True(d(true, false, true, false, false).active, "общий обрыв — режим не меняем (был белый)");
        Assert.False(d(true, false, false, false, false).active, "общий обрыв — режим не меняем (был обычный)");
    }

    [Fact] public void normal_stays_normal_when_google_ok() => Assert.False(d(true, false, false, true, true).active);
}
