namespace XGates.Core.Platform;

/// <summary>
/// Потоки пула сразу, без ожидания. Код перенесён с блокирующего Kotlin: замеры, пинги и монитор ждут сетевые операции, блокируя поток, а завершение самой операции
/// выполняет пул. Его минимум равен числу ядер: стоит монитору занять эти потоки, и новые появляются по одному в полсекунды, живой сервер выглядит мёртвым.
/// Опыт на 2 ядрах, 8 занятых потоков: замер 7,4 с вместо 0,3 с, с этой настройкой 0,4 с. Потоки создаются только при нужде и завершаются после простоя.
/// </summary>
public static class ThreadPoolSetup
{
    public const int MIN_THREADS = 64;

    public static void apply()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, MIN_THREADS), Math.Max(io, MIN_THREADS));
    }
}
