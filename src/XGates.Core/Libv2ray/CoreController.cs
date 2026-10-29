using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace XGates.Core.Libv2ray;

/// <summary>
/// Аналог <c>libv2ray.CoreController</c> (AndroidLibXrayLite Lib v39) поверх процесса xray.exe — чтобы Kotlin-код,
/// работающий с ядром (XrayController, ServerSpeedTester), переносился без смены логики.
/// Отличия процесса от ядра-в-процессе: <see cref="isRunning"/> честно отражает, жив ли xray.exe (может упасть сам);
/// статистика — через gRPC API ядра, который добавляется в конфиг здесь (вывод XrayConfigBuilder не меняем — паритет).
/// </summary>
public sealed class CoreController
{
    private readonly object coreMutex = new();
    private readonly CoreCallbackHandler callbackHandler;
    private XrayProcess? coreInstance;
    private XrayStatsClient? statsManager;

    internal CoreController(CoreCallbackHandler callbackHandler) => this.callbackHandler = callbackHandler;

    public bool isRunning
    {
        get { lock (coreMutex) return coreInstance?.IsRunning == true; }
    }

    /// <summary>PID процесса ядра (для диагностики/тестов); null — не запущено.</summary>
    public int? pid
    {
        get { lock (coreMutex) return coreInstance?.Pid; }
    }

    /// <summary>
    /// <c>StartLoop(configContent, tunFd)</c>: синхронно, как в Go. Бросает <see cref="XrayCoreException"/>
    /// («config error» / «startup failed» + вывод ядра). tunFd игнорируется: TUN на Windows не используем вовсе.
    /// </summary>
    public void startLoop(string configContent, int tunFd)
    {
        lock (coreMutex)
        {
            if (coreInstance?.IsRunning == true) return; // "Core is already running"
            coreInstance?.Dispose();
            coreInstance = null;
            statsManager?.Dispose();
            statsManager = null;

            JsonObject config;
            try { config = JsonNode.Parse(configContent)?.AsObject() ?? throw new FormatException("empty config"); }
            catch (Exception e) { throw new XrayCoreException("config error: " + e.Message, e); }

            int? apiPort = null;
            if (config.ContainsKey("stats"))
            {
                // statsManager есть только при "stats" (как GetFeature(stats) в Go). API — только на петле.
                apiPort = Libv2ray.freePort();
                config["api"] = new JsonObject
                {
                    ["tag"] = "api",
                    ["listen"] = $"127.0.0.1:{apiPort}",
                    ["services"] = new JsonArray("StatsService"),
                };
            }

            try { coreInstance = XrayProcess.Start(Libv2ray.xrayPath, config.ToJsonString(), Libv2ray.StartTimeout); }
            catch (XrayCoreException e) { throw new XrayCoreException("startup failed: " + e.Message, e); }
            if (apiPort != null) statsManager = new XrayStatsClient(apiPort.Value);
        }
        callbackHandler.startup();
        callbackHandler.onEmitStatus(0, "Started successfully, running");
    }

    /// <summary><c>StopLoop()</c>: идемпотентно; после возврата процесс завершён, порты свободны.</summary>
    public void stopLoop()
    {
        bool wasRunning;
        lock (coreMutex)
        {
            wasRunning = coreInstance != null;
            statsManager?.Dispose();
            statsManager = null;
            coreInstance?.Dispose();
            coreInstance = null;
        }
        if (wasRunning) callbackHandler.onEmitStatus(0, "Core stopped");
    }

    /// <summary>
    /// <c>QueryStats(tag, direct)</c>: счётчик <c>outbound&gt;&gt;&gt;tag&gt;&gt;&gt;traffic&gt;&gt;&gt;direct</c> С ЧТЕНИЕМ-СБРОСОМ
    /// (байты с прошлого опроса). 0 — нет статистики/счётчика (как в Go). Ошибка связи с API — исключение
    /// (Kotlin-обёртка ловит и возвращает null).
    /// </summary>
    public long queryStats(string tag, string direct)
    {
        XrayStatsClient? s;
        lock (coreMutex) s = statsManager;
        if (s == null) return 0;
        return s.GetAndReset($"outbound>>>{tag}>>>traffic>>>{direct}") ?? 0;
    }

    /// <summary>Последние строки вывода ядра (диагностика ошибок старта/падения).</summary>
    public string recentOutput()
    {
        lock (coreMutex) return coreInstance?.RecentOutput ?? "";
    }
}
