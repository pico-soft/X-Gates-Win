using System.Diagnostics;
using System.Text;

namespace XGates.Core.Platform;

/// <summary>
/// Аналог <c>android.util.Log</c> (те же <c>v/d/i/w/e(tag, msg[, tr])</c>) — чтобы строки логов переносились как есть.
/// Куда писать — решает приложение (<see cref="Sink"/>): на Windows файл <c>logs\</c> в каталоге данных; в тестах — память.
/// </summary>
public static class Log
{
    public const int VERBOSE = 2, DEBUG = 3, INFO = 4, WARN = 5, ERROR = 6;

    public interface ILogSink
    {
        void Write(int priority, string tag, string msg, Exception? tr);
    }

    /// <summary>Приёмник; по умолчанию — Trace (отладчик/консоль).</summary>
    public static ILogSink Sink { get; set; } = new TraceSink();

    public static int v(string tag, string msg) { Sink.Write(VERBOSE, tag, msg, null); return 0; }
    public static int d(string tag, string msg) { Sink.Write(DEBUG, tag, msg, null); return 0; }
    public static int d(string tag, string msg, Exception? tr) { Sink.Write(DEBUG, tag, msg, tr); return 0; }
    public static int i(string tag, string msg) { Sink.Write(INFO, tag, msg, null); return 0; }
    public static int i(string tag, string msg, Exception? tr) { Sink.Write(INFO, tag, msg, tr); return 0; }
    public static int w(string tag, string msg) { Sink.Write(WARN, tag, msg, null); return 0; }
    public static int w(string tag, string msg, Exception? tr) { Sink.Write(WARN, tag, msg, tr); return 0; }
    public static int w(string tag, Exception tr) { Sink.Write(WARN, tag, "", tr); return 0; }
    public static int e(string tag, string msg) { Sink.Write(ERROR, tag, msg, null); return 0; }
    public static int e(string tag, string msg, Exception? tr) { Sink.Write(ERROR, tag, msg, tr); return 0; }

    internal static string Letter(int p) => p switch { VERBOSE => "V", DEBUG => "D", INFO => "I", WARN => "W", ERROR => "E", _ => "?" };

    public static string Format(int priority, string tag, string msg, Exception? tr)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(Letter(priority)).Append('/').Append(tag).Append(": ").Append(msg);
        if (tr != null) sb.Append('\n').Append(tr);
        return sb.ToString();
    }

    private sealed class TraceSink : ILogSink
    {
        public void Write(int priority, string tag, string msg, Exception? tr) => Trace.WriteLine(Format(priority, tag, msg, tr));
    }

    /// <summary>Файл с ротацией по размеру (одна предыдущая копия .1). Потокобезопасен.</summary>
    public sealed class FileSink(string path, long maxBytes = 4 * 1024 * 1024) : ILogSink
    {
        private readonly object gate = new();

        public void Write(int priority, string tag, string msg, Exception? tr)
        {
            var line = Format(priority, tag, msg, tr) + Environment.NewLine;
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > maxBytes) File.Move(path, path + ".1", overwrite: true);
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
                catch (IOException) { /* лог — не повод падать */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
