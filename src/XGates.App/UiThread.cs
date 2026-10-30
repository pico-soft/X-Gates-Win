namespace XGates.App;

/// <summary>
/// «Главный поток» экрана — аналог activity.runOnUiThread. Всё состояние модели меняется ТОЛЬКО через него
/// (как mutableStateOf в Compose — с main-потока). WPF: Dispatcher; тесты: свой однопоточный исполнитель.
/// </summary>
public interface IUiThread
{
    void post(Action action);
}

/// <summary>Однопоточный исполнитель (тесты/без UI): задачи по очереди на одном фоновом потоке.</summary>
public sealed class SerialUiThread : IUiThread, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> queue = new();
    private readonly Thread thread;

    public SerialUiThread()
    {
        thread = new Thread(() =>
        {
            foreach (var a in queue.GetConsumingEnumerable())
            {
                try { a(); } catch (Exception e) { XGates.Core.Platform.Log.e("SerialUiThread", "задача упала", e); }
            }
        }) { IsBackground = true, Name = "ui" };
        thread.Start();
    }

    public void post(Action action)
    {
        try { queue.Add(action); } catch (InvalidOperationException) { /* закрыт */ }
    }

    /// <summary>Дождаться, пока выполнится всё, что уже поставлено в очередь (тесты).</summary>
    public void drain(int timeoutMs = 10_000)
    {
        using var done = new ManualResetEventSlim();
        post(done.Set);
        done.Wait(timeoutMs);
    }

    public void Dispose() { queue.CompleteAdding(); thread.Join(5000); }
}
