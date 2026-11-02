// Тесты делят состояние процесса, как и Kotlin-синглтоны (SettingsStore, WinSettingsStore/порты, ProxyState, ядро xray,
// переопределения для тестов). Параллельные коллекции xUnit портили друг другу это состояние (напр. золотые конфиги
// видели чужие порты, замер отдачи — чужой адрес пробника) → весь прогон последовательно.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestProcessSetup
{
    /// <summary>Тесты идут с тем же пулом потоков, что и приложение (AppBootstrap.start).</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => XGates.Core.Platform.ThreadPoolSetup.apply();
}
