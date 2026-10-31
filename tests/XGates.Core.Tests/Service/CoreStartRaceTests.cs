using System.Text;
using XGates.Core.Monitor;
using XGates.Core.Net;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Tests.Libv2ray;
using XGates.Core.Xray;

namespace XGates.Core.Tests.Service;

/// <summary>
/// Находка порта №5: два почти одновременных старта (полный тест: «первый рабочий» → «точный топ») шли параллельно —
/// второй видел «ядро уже запущено» и НЕ поднимал свой сервер, а статус показывал второй. Правило после правки:
/// старты/стоп ядра идут по очереди, и побеждает ПОСЛЕДНИЙ запрос — и в статусе, и в настоящем туннеле.
/// </summary>
[Collection("xray")]
public sealed class CoreStartRaceTests : IDisposable
{
    private readonly LocalTestNet net = new();
    private readonly Context ctx = new(Path.Combine(Path.GetTempPath(), "xgates-race-" + Guid.NewGuid().ToString("N")));

    public CoreStartRaceTests()
    {
        Directory.CreateDirectory(ctx.filesDir);
        NetworkMonitor.directAliveOverrideForTest = () => true;
        ExternalIpChecker.onceOverrideForTest = _ => "198.51.100.1";
        WinSettingsStore.update(ctx, new WinSettings { socksPort = LocalTestNet.FreePort(), httpPort = LocalTestNet.FreePort() });
    }

    public void Dispose()
    {
        XrayProxyService.beforeCoreUpForTest = null;
        XrayProxyService.shutdown();
        WinSettingsStore.resetForTest();
        NetworkMonitor.directAliveOverrideForTest = null;
        ExternalIpChecker.onceOverrideForTest = null;
        net.Dispose();
        try { Directory.Delete(ctx.filesDir, true); } catch (IOException) { }
    }

    private static bool WaitUntil(Func<bool> cond, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(100); }
        return cond();
    }

    /// <summary>Запрос через НАШ SOCKS до стенда: идёт ли трафик через поднятый туннель.</summary>
    private bool TunnelWorks()
    {
        try
        {
            using var s = Socks5.Connect(XrayConfig.SOCKS_PORT, "127.0.0.1", net.HttpPort, 3000);
            s.ReceiveTimeout = 3000;
            s.Send(Encoding.ASCII.GetBytes("GET /bytes?n=8 HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"));
            var buf = new byte[64];
            return s.Receive(buf) > 0 && Encoding.ASCII.GetString(buf).StartsWith("HTTP/1.1 200");
        }
        catch (Exception) { return false; }
    }

    [Fact]
    public void Back_to_back_starts_leave_the_last_requested_server_running()
    {
        SubscriptionManager.init(ctx);
        for (int round = 0; round < 3; round++)
        {
            int a = net.StartVlessServer(), b = net.StartVlessServer();
            var pa = XGates.Core.Xray.Link.VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{a}?type=tcp&security=none#A{round}")!;
            var pb = XGates.Core.Xray.Link.VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{b}?type=tcp&security=none#B{round}")!;
            string ka = SubscriptionManager.serverKey(pa), kb = SubscriptionManager.serverKey(pb);

            // Из остановленного состояния — два старта подряд, без ожидания (как «первый рабочий» → «точный топ»).
            // Первый старт «застревает» между проверкой «ядро запущено?» и подъёмом — ровно окно гонки (на живом
            // устройстве его даёт медленный старт/проверка маршрута; здесь — детерминированно).
            XrayProxyService.beforeCoreUpForTest = label => { if (label == "A") Thread.Sleep(400); };
            XrayProxyService.stop(ctx);
            Assert.True(WaitUntil(() => !XrayController.isRunning && ProxyState.state.value.message == "остановлено", 10_000), ProxyState.state.value.message);
            XrayProxyService.start(ctx, XrayConfigBuilder.build(pa), "A", ka);
            XrayProxyService.start(ctx, XrayConfigBuilder.build(pb), "B", kb);

            Assert.True(WaitUntil(() => ProxyState.state.value is { running: true } st && st.serverKey == kb, 15_000),
                $"раунд {round}: статус не на последнем сервере: {ProxyState.state.value.serverKey == ka} / {ProxyState.state.value.message}");
            Thread.Sleep(1500);   // дать «опоздавшему» старту A (если он есть) перезаписать статус/туннель
            Assert.Equal(kb, ProxyState.state.value.serverKey);
            // Туннель — НАСТОЯЩИЙ B: сервер A гасим, связь через наш SOCKS обязана остаться.
            net.StopVlessServer(a);
            Assert.True(TunnelWorks(), $"раунд {round}: туннель шёл через A, хотя статус показывает B");
        }
    }

    [Fact]
    public void Start_then_immediate_stop_leaves_core_stopped()
    {
        SubscriptionManager.init(ctx);
        int a = net.StartVlessServer();
        var pa = XGates.Core.Xray.Link.VlessParser.parse($"vless://{LocalTestNet.Uuid}@127.0.0.1:{a}?type=tcp&security=none#A")!;
        XrayProxyService.beforeCoreUpForTest = label => { if (label == "A") Thread.Sleep(400); };
        XrayProxyService.start(ctx, XrayConfigBuilder.build(pa), "A", SubscriptionManager.serverKey(pa));
        XrayProxyService.stop(ctx);   // последний запрос — «стоп»
        Assert.True(WaitUntil(() => ProxyState.state.value.message == "остановлено", 10_000), ProxyState.state.value.message);
        Thread.Sleep(2000);           // опоздавший старт не должен поднять ядро после стопа
        Assert.False(XrayController.isRunning, "ядро поднято после «Стоп»");
        Assert.False(ProxyState.state.value.running);
        Assert.False(TunnelWorks());
    }
}
