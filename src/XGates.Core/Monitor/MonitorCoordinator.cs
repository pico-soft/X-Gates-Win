using XGates.Core.Platform;

namespace XGates.Core.Monitor;

/**
 * Координация монитора с остальным приложением (один процесс).
 *
 * 1) Взаимное исключение с РУЧНЫМ полным тестом ([fullTestRunning]): он сам переключает активный
 *    сервер, монитор в это время молчит.
 * 2) ПРОБУЖДЕНИЕ монитора из паузы ([wake]/[awaitWake]): при появлении сети или
 *    ручном действии пользователя пауза «нет интернета» сбрасывается немедленно — иначе возврат в зону
 *    Wi-Fi не помог бы до истечения таймера.
 * Порт 1:1 с monitor/MonitorCoordinator.kt (Channel.CONFLATED → SemaphoreSlim с максимумом 1).
 */
public static class MonitorCoordinator
{
    private static volatile bool _fullTest;

    public static bool fullTestRunning
    {
        get => _fullTest;
        set => _fullTest = value;
    }

    // Как прервать идущий полный тест (ставит onFullTest на время прогона, снимает по завершении).
    public static volatile Action? fullTestCancel;

    /**
     * Промпт 93.E: операции с ИСТОЧНИКАМИ (добавить/обновить/удалить/выключить) и полный тест ВЗАИМНО
     * ИСКЛЮЧЕНЫ — реестр не должен перестраиваться под идущим обходом. ВЫБОР: не очередь, а ЧЕСТНОЕ
     * ПРЕРЫВАНИЕ теста перед изменением данных. ПОЧЕМУ: добавление источника — осознанное действие
     * пользователя, оно должно вступить в силу сразу; тест легко перезапустить (кнопкой/автоподбором).
     * Очередь запутала бы («добавил — ничего не происходит, пока тест идёт»). Прерываем и ЖДЁМ реальной
     * остановки (флаг снимается в onDone теста), только потом меняем данные. Вызывать из ФОНОВОГО потока.
     */
    public static void abortFullTestAndWait(long timeoutMs = 8000L)
    {
        if (!fullTestRunning) return;
        try { fullTestCancel?.Invoke(); } catch (Exception) { }
        var end = JavaSystem.currentTimeMillis() + timeoutMs;
        while (fullTestRunning && JavaSystem.currentTimeMillis() < end) Thread.Sleep(50);
    }

    // Идёт перебор кандидатов монитором (он уже крутит temp-инстансы). Ступень 5 каскада не должна
    // поднимать ЕЩЁ один temp-инстанс параллельно — пропускается на это время.
    private static volatile bool _monitorSearch;
    public static bool monitorSearchRunning
    {
        get => _monitorSearch;
        set => _monitorSearch = value;
    }

    // CONFLATED: держим максимум один сигнал; несколько wake() подряд = одно пробуждение.
    private static readonly SemaphoreSlim wakeups = new(0, 1);

    /** Разбудить монитор (сеть появилась / пользователь подключился или запустил тест). */
    public static void wake()
    {
        try { wakeups.Release(); } catch (SemaphoreFullException) { /* уже есть сигнал — conflated */ }
    }

    /** Съесть накопленные сигналы перед началом ожидания, чтобы «спать» реально до нового события. */
    public static void drainWakeups()
    {
        while (wakeups.Wait(0)) { /* drain */ }
    }

    /** Ждать до [ms] или до wake(). true — разбудили досрочно, false — истекло время. */
    public static Task<bool> awaitWake(long ms, CancellationToken ct = default) =>
        wakeups.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(0, ms)), ct);
}
