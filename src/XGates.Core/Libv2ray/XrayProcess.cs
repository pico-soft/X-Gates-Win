using System.Diagnostics;
using System.Text;

namespace XGates.Core.Libv2ray;

/// <summary>
/// Один запущенный xray (дочерний процесс <c>xray run -c stdin:</c>). Конфиг идёт через stdin — на диск пароли/UUID
/// серверов не пишутся. Без окна консоли (CreateNoWindow), привязка к Job Object (Windows) — сироты не остаются.
/// Готовность — строка «core: Xray … started» (ядро печатает её ПОСЛЕ запуска inbound-слушателей) или выход процесса
/// с ошибкой (порт занят, битый конфиг) → <see cref="XrayCoreException"/> с хвостом вывода ядра.
/// </summary>
internal sealed class XrayProcess : IDisposable
{
    private const int TailLines = 60;
    private readonly Process process;
    private readonly LinkedList<string> tail = new();
    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Pid { get; }

    private XrayProcess(Process p)
    {
        process = p;
        Pid = p.Id;
    }

    public bool IsRunning
    {
        get { try { return !process.HasExited; } catch (InvalidOperationException) { return false; } }
    }

    public string RecentOutput
    {
        get { lock (tail) return string.Join("\n", tail); }
    }

    public static XrayProcess Start(string xrayPath, string configJson, TimeSpan startTimeout)
    {
        if (!File.Exists(xrayPath)) throw new XrayCoreException("xray не найден: " + xrayPath);
        var psi = new ProcessStartInfo(xrayPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Конфиг идёт через stdin: явно UTF-8 БЕЗ BOM (по умолчанию на Windows — кодовая страница консоли). Сейчас это
            // страховка: CoreController пересобирает конфиг System.Text.Json, и всё не-ASCII уходит как \uXXXX.
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(xrayPath)!,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("stdin:");
        // geoip.dat/geosite.dat — рядом с бинарём (как initCoreEnv(filesDir) на Android).
        psi.Environment["XRAY_LOCATION_ASSET"] = Path.GetDirectoryName(xrayPath)!;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        XrayProcess? self = null;
        p.OutputDataReceived += (_, e) => self?.OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => self?.OnLine(e.Data);
        p.Exited += (_, _) => self?.started.TrySetResult(false);
        if (!p.Start()) throw new XrayCoreException("не удалось запустить xray");
        self = new XrayProcess(p);
        ChildProcessJob.Attach(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            p.StandardInput.Write(configJson);
            p.StandardInput.Close();
        }
        catch (IOException) { /* процесс уже умер — причина будет в выводе */ }

        bool ok;
        var wait = self.started.Task.Wait(startTimeout);
        ok = wait && self.started.Task.Result && self.IsRunning;
        if (!ok)
        {
            if (!p.HasExited) p.WaitForExit(200);
            var reason = self.IsRunning ? "xray не сообщил о старте за " + startTimeout.TotalSeconds + " с" : "xray завершился при старте";
            var output = self.RecentOutput;
            self.Dispose();
            throw new XrayCoreException(reason + (output.Length > 0 ? ":\n" + output : ""));
        }
        return self;
    }

    private void OnLine(string? line)
    {
        if (line == null) return;
        lock (tail)
        {
            tail.AddLast(line);
            while (tail.Count > TailLines) tail.RemoveFirst();
        }
        if (line.Contains("core: Xray ", StringComparison.Ordinal) && line.Contains(" started", StringComparison.Ordinal))
            started.TrySetResult(true);
    }

    /// <summary>Остановить и дождаться выхода (порты освобождены, когда метод вернулся).</summary>
    public void Stop()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Stop();
        process.Dispose();
    }
}
