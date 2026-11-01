using System.Collections.Concurrent;
using XGates.Core.Compat;
using XGates.Core.Monitor;
using XGates.Core.Platform;
using XGates.Core.Service;
using XGates.Core.Settings;
using XGates.Core.Subscription;
using XGates.Core.Xray.Link;

namespace XGates.Core.Xray;

/**
 * Полный адаптивный тест (новый ЕДИНЫЙ порядок, ТЗ Elyor 2026-10-14). Порт 1:1 с xray/FullTestRunner.kt.
 *   Этап 0: ФЕТЧ подписок из сети (свежий список) — делает вызывающий (onFullTest) ДО run.
 *   Этап 1: ping ВСЕХ → отсев мёртвых (pingMs>=0), сорт по возр. пинга. Пока связи нет, откликнувшиеся сразу идут в
 *           [FirstAliveRace]: первый передавший данные становится активным, не дожидаясь конца пинга (ТЗ Elyor 2026-11-01).
 *   Этап 2: МИНИМАЛЬНЫЙ замер (~100 КБ) ВСЕХ живых. У кого throughput ≥ minGate (0.5 Мбит/с) — кандидат на точный
 *           замер; к ПЕРВОМУ живому сразу подключаемся (непрерывность, Пр.95). В «Живых» эти числа — «≈» (быстрые).
 *   Этап 3: ПОЛНЫЙ точный замер ТОПа прошедших minGate — лимит fullCap: 10 (обычный)/3 (экономия).
 *           Переключаемся на реально быстрейший (инкумбент — кратный порог).
 *
 * Общий БЮДЖЕТ времени [fullTestBudgetSec] + сторож застоя защищают от зависания. Тот же принцип (пинг→минимальный
 * всех→полный топ) использует фоновый монитор (NetworkMonitor). proxy-check НЕ нужен — real-ping проходит протокол.
 */
public static class FullTestRunner
{
    // Промпт 103. ПРЕДОХРАНИТЕЛЬ «Этап 2» не должен виснуть.
    //  STALL — фаза не двигается (нет ни одного шага прогресса) дольше этого → честно завершаем.
    //  PRIORITY_CAP — приоритетный замер активного туннеля (прямой+↓+↑ с фолбэками) может тянуться ~2 мин
    //  на провалах отдачи; он идёт ДО перебора и НЕ покрыт кооперативным бюджетом (тот проверяется лишь на
    //  верху цикла). Ограничиваем его вклад в видимый «застой», не давая застопорить бар на 0/N.
    private const long STALL_LIMIT_MS = 120_000L;
    private const long PRIORITY_MEASURE_CAP_MS = 60_000L;
    // Пр.151: сколько ГОДНЫХ набрать на быстром Этапе 2 (после подключения) прежде чем перейти к точному Этапу 3.
    // Хватает пула для точного замера топ-K; не мерим все десятки серверов подряд.
    private const int FAST_SCAN_ENOUGH = 10;
    // TCP-коннект ничего не стоит, а ждёт таймаута на каждом мёртвом адресе: при тысяче серверов и 16 потоках пинг шёл минуты.
    private const int PING_CONCURRENCY = 48;
    // Сколько после конца пинга ещё даём гонке за первый живой, прежде чем идти к последовательным замерам.
    private static readonly TimeSpan RACE_WAIT = TimeSpan.FromSeconds(60);
    private const long RACE_CONNECT_WAIT_MS = 8_000L;

    public sealed record Result(
        ServerProfile? connected,   // к чему подключены в итоге
        ServerProfile? fastest,     // самый быстрый по замеру
        double fastestMbps,
        int aliveCount,
        bool cancelled);

    public interface Handle
    {
        void cancel();
    }

    private sealed class HandleImpl(Action onCancel) : Handle
    {
        public void cancel() => onCancel();
    }

    public static Handle run(
        Context context,
        IReadOnlyList<ServerProfile> allServers,
        Action<string> onPhase,
        Action<ServerProfile> connect,
        Action<Result> onDone,
        double? marginRatio = null,   // живой порог из настроек
        Action<ServerProfile, int>? onPingResult = null,
        Action<ServerProfile, double, bool>? onSpeedResult = null,   // full=true полный замер, false минимальный («≈»)
        Action<int, int>? emitProgress = null)                       // числовой прогресс наружу (для бара)
    {
        var margin = marginRatio ?? SettingsStore.current().marginRatio;
        onPingResult ??= (_, _) => { };
        onSpeedResult ??= (_, _, _) => { };
        emitProgress ??= (_, _) => { };
        var appCtx = context.applicationContext;
        ServerSpeedTester.resetMeasureAbort();   // Промпт 123.D: снять флаг отмены прошлого теста — иначе замеры «немы»
        var cancelled = false;
        // Промпт 103: терминальное состояние прогресса — РОВНО ОДИН раз. Сторож фазы и штатное завершение
        // могут прийти оба; второй вызов onDone игнорируем, чтобы не дёргать флаги/персист/UI дважды.
        var doneFired = 0;
        void finishOnce(Result r) { if (Interlocked.CompareExchange(ref doneFired, 1, 0) == 0) onDone(r); }
        ServerTester.TestHandle? pingHandle = null;

        var pingByKey = new ConcurrentDictionary<string, int>();

        // Заблокированных НЕ мерим (в отличие от отключённых по протоколу): блокировка — «не нужен вовсе».
        var blocklist = BlocklistStore.current();
        var testable = allServers.Where(it => !ServerFilter.isBlocked(it, blocklist)).ToList();

        string key(ServerProfile p) => SubscriptionManager.serverKey(p);
        string label(ServerProfile p) => p.remarks.IfBlank(p.address);
        string fmt(double v) => $"{(v * 10).roundToInt() / 10.0}";

        // Связь нужна сейчас: гонка принимает откликнувшихся прямо во время пинга.
        var raceSettings = SettingsStore.current();
        var race = !FirstAliveRace.tunnelNeeded() ? null : new FirstAliveRace(appCtx, p =>
        {
            if (Volatile.Read(ref cancelled)) return;
            onPhase($"Подключено: {label(p)}. Ищу быстрее…");
            MonitorLog.@switch(appCtx, ServerLabels.displayForKey(appCtx, ProxyState.state.value.serverKey), ServerLabels.display(p), "полный тест: первый живой");
            connect(p);
        });

        // --- Этап 2: speed + early-connect, ПОСЛЕДОВАТЕЛЬНО (обычный режим — мерим всех живых) ---
        void startSpeedPhase(List<ServerProfile> aliveByPing)
        {
            if (Volatile.Read(ref cancelled)) { finishOnce(new Result(null, null, 0.0, aliveByPing.Count, true)); return; }
            if (aliveByPing.Count == 0)
            {
                onPhase("Рабочих серверов нет. Проверьте интернет");
                finishOnce(new Result(null, null, 0.0, 0, Volatile.Read(ref cancelled)));
                return;
            }
            var s = SettingsStore.current();
            // ТЗ Elyor (новый ЕДИНЫЙ порядок, обычный и экономия): МИНИМАЛЬНЫЙ замер (~100 КБ) ВСЕХ живых → у кого
            // throughput ≥ minGate (0.5 Мбит/с) — те в ПОЛНЫЙ точный замер, но не больше fullCap (10 обычный / 3
            // экономия). minGate НИЖЕ финального «держим» (1 Мбит/с): минимальный замер по короткому окну занижает,
            // 0.5 отсекает мёртвых/призраков, но не рубит жизнеспособных. Экономия теперь = только меньший fullCap.
            var candidates = aliveByPing;   // отсортированы по ВОЗР. пинга (лучший пинг первым)
            var minGate = 0.5;
            // ТЗ Elyor (2026-10-15): строим ТОП по скорости, не меряя все живые (на tcping-пуле их сотни, многие
            // мёртвые по трафику). Минимальный замер идёт от ЛУЧШЕГО ПИНГА к худшему; СТОП, когда набрали targetTop
            // годных (≥minGate) ИЛИ прошли maxScan (пул мёртвый — не пылесосим все 269), ИЛИ бюджет. Полный замер —
            // только топ fullCap. Это чинит «Этап 2 ползёт 269» и расход 5 ГБ/сут.
            var fullCap = s.trafficSaveMode ? 3 : 10;              // сколько ТОЧНО мерить (топ для подключения)
            var scanGoodTarget = s.trafficSaveMode ? 3 : 10;       // стоп на 10 живых (ТЗ Elyor «как и ранее»)
            // Потолок сканирования БОЛЬШОЙ (было 60 → при пуле 400+ не набиралось даже 10 живых, т.к. живые с худшим
            // пингом за его пределами). 180 гарантирует: если 10 живых есть в первых 180 по пингу — наберём. Стоп всё
            // равно на scanGoodTarget(10) ИЛИ бюджете — обычно набирается рано (живые фронт-загружены).
            var maxScan = (s.trafficSaveMode ? 30 : 180).coerceAtMost(candidates.Count);
            var budgetMs = s.fullTestBudgetSec.coerceAtLeast(30) * 1000L;
            onPhase("Ищу самые быстрые серверы…");
            emitProgress(0, maxScan);

            // Промпт 103. ПРЕДОХРАНИТЕЛЬ ЗАВЕРШЕНИЯ. Наблюдение из поля: «Этап 2» долго стоял, плашка зеленела
            // (монитор), а бар не двигался. Причина: приоритетный замер активного туннеля (ниже) идёт ДО перебора
            // и НЕ покрыт кооперативным бюджетом. Ниже — независимый сторож: если фаза не двигается дольше
            // STALL_LIMIT_MS ИЛИ вышла за бюджет, прогресс ЧЕСТНО завершается (finishOnce ⇒ бар гаснет, монитор снова
            // активен). Держим «пульс» фазы (beat) и лучшее-на-сейчас (hb*), чтобы сторож выдал осмысленный итог.
            ServerProfile? hbConnected = null;
            ServerProfile? hbBest = null;
            long lastMoveMs = JavaSystem.currentTimeMillis();
            var phaseStartWall = JavaSystem.currentTimeMillis();
            void beat() => Interlocked.Exchange(ref lastMoveMs, JavaSystem.currentTimeMillis());

            KThread.start(() =>
            {
                while (Volatile.Read(ref doneFired) == 0)
                {
                    Thread.Sleep(3_000);
                    if (Volatile.Read(ref doneFired) == 1) break;
                    var nowMs = JavaSystem.currentTimeMillis();
                    var overBudget = nowMs - phaseStartWall > budgetMs + 60_000;
                    var stalled = nowMs - Interlocked.Read(ref lastMoveMs) > STALL_LIMIT_MS;
                    if (overBudget || stalled)
                    {
                        Volatile.Write(ref cancelled, true);   // попросить рабочий поток выйти на ближайшей проверке
                        try
                        {
                            onPhase(overBudget
                                ? "Время проверки вышло, завершаю…"
                                : "Проверка не двигается, завершаю…");
                        }
                        catch (Exception) { }
                        finishOnce(new Result(Volatile.Read(ref hbConnected) ?? Volatile.Read(ref hbBest), Volatile.Read(ref hbBest), 0.0, candidates.Count, false));
                        break;
                    }
                }
            }, "fulltest-watchdog");

            KThread.start(() =>
            {
                var phaseStart = JavaSystem.nanoTime();
                ServerProfile? connected = null;
                var connectedSpeed = 0.0;
                var keepActive = false;   // Промпт 108: инкумбент = активный сервер «в норме» → консервативно (Пр.90 «держать лучший ×N»)
                ServerProfile? best = null;
                var bestSpeed = 0.0;
                var measured = 0;
                var goodOnes = new List<ServerProfile>();        // прошедшие minGate на минимальном → точный замер (топ fullCap)
                var minimalByKey = new Dictionary<string, double>();   // скорость МИНИМАЛЬНОГО замера этого прогона (для сорта топа)
                // Промпт 93.C: фон не должен ронять процесс — любое исключение фазы завершает тест штатно (finishOnce).
                try
                {
                    // ПРИОРИТЕТ АКТИВНОГО ТУННЕЛЯ (обратная связь): если есть активное соединение — мерим ЕГО ПЕРВЫМ,
                    // ДО общего перебора. Плашка сразу показывает реальную ↓/↑ активного сервера. Промпт 103: замер идёт
                    // на ОТДЕЛЬНОМ потоке с потолком времени — он может длиться ~2 мин на провалах отдачи, и раньше это
                    // «подвешивало» бар на 0/N без обратной связи.
                    // serverKey назначается раньше, чем поднимется ядро: только running значит, что этот сервер есть что мерить.
                    var activeServerKey = ProxyState.state.value.running ? ProxyState.state.value.serverKey : null;
                    if (activeServerKey != null)
                    {
                        onPhase("Проверяю текущий сервер…");
                        beat();
                        // Промпт 104.B: отдача (↑) — display-only и сток → не делаем. Промпт 108: замер на отдельном
                        // потоке с потолком. Промпт 109: ЭТОТ живой замер — ТОЛЬКО для «живой» ↓/↑ на плашке; в РЕШЕНИЕ
                        // его число НЕ берём (см. ниже — оно систематически занижено).
                        var probe = KThread.start(() => { try { NetworkMonitor.probeActiveSpeedNow(appCtx, includeUpload: false); } catch (Exception) { } }, "fulltest-active-probe");
                        probe.Join(TimeSpan.FromMilliseconds(PRIORITY_MEASURE_CAP_MS));
                        if (probe.IsAlive)
                        {
                            // Промпт 104.A: потолок обязан ПРЕРЫВАТЬ работу, а не просто перестать её ждать — иначе
                            // фоновый замер продолжит качать через активный SOCKS ПАРАЛЛЕЛЬНО с перебором временных
                            // инстансов и испортит их числа (то самое наложение, ради устранения которого делался Пр.101).
                            try { ServerSpeedTester.abortCurrentMeasure(); } catch (Exception) { }
                            probe.Join(TimeSpan.FromMilliseconds(10_000));          // дождаться РЕАЛЬНОГО завершения после прерывания
                            try { ServerSpeedTester.resetMeasureAbort(); } catch (Exception) { }   // снять флаг — иначе монитор/следующие замеры «немы»
                        }
                        beat();
                        // Промпт 109.D: КРИТЕРИЙ СОПОСТАВИМОСТИ. Замер через ЖИВОЙ SOCKS систематически ЗАНИЖАЕТ (один и
                        // тот же сервер: живой 2.67 vs temp-инстанс 68). Поэтому для РЕШЕНИЯ активный сервер меряем ТЕМ ЖЕ
                        // способом, что кандидата — свежим temp-инстансом (measureSpeed) → числа сравнимы. Активный «в норме»
                        // (≥ порога) → ИНКУМБЕНТ (Пр.90 «держать лучший ×N»); иначе — честный текст + прежний ранний коннект.
                        var activeProfile = allServers.FirstOrDefault(it => key(it) == activeServerKey);
                        var activeMbps = activeProfile != null && !Volatile.Read(ref cancelled)
                            ? ServerSpeedTester.measureSpeed(appCtx, activeProfile) : -1.0;
                        if (activeProfile != null && activeMbps > 0.0) onSpeedResult(activeProfile, activeMbps, true);   // measureSpeed — полный; в «Живые»
                        beat();
                        // Находка порта №8: активный, поставленный на паузу («Не использовать»), НЕ инкумбент — иначе
                        // кнопка ничего не переключала (замена должна была быть в keepBestMultiplier раз быстрее).
                        var activePaused = activeProfile != null && ServerFilter.isPaused(activeProfile, blocklist);
                        if (activePaused)
                        {
                            onPhase("Ищу замену текущему серверу…");
                        }
                        else if (activeProfile != null && activeMbps >= s.monitorTunnelThreshold)
                        {
                            connected = activeProfile; connectedSpeed = activeMbps; Volatile.Write(ref hbConnected, activeProfile); keepActive = true;
                            onPhase("Текущий сервер работает, ищу быстрее…");
                        }
                        else
                        {
                            onPhase(activeMbps <= 0.0
                                ? "Текущий сервер не отвечает, ищу рабочий…"
                                : "Текущий сервер медленный, ищу быстрее…");
                        }
                    }
                    foreach (var p in candidates)
                    {
                        if (Volatile.Read(ref cancelled)) break;
                        // Общий бюджет времени (Промпт 82): не мерим бесконечно — стоп по лимиту.
                        if ((JavaSystem.nanoTime() - phaseStart) / 1_000_000 > budgetMs)
                        {
                            onPhase("Время проверки вышло, завершаю…");
                            break;
                        }
                        // Набрали топ годных ИЛИ просканировали потолок (пул мёртвый) → хватит минимального.
                        if (goodOnes.Count >= scanGoodTarget) { onPhase("Рабочие серверы найдены, сравниваю скорость…"); break; }
                        if (measured >= maxScan) { onPhase("Сравниваю скорость найденных серверов…"); break; }
                        // Активный уже измерен ПОЛНЫМ выше — минимально не перемеряем (число не затирается на «≈»); не в счёт.
                        if (activeServerKey != null && key(p) == activeServerKey) continue;
                        // МИНИМАЛЬНЫЙ замер (~100 КБ, 1 пробник/3с — быстро падает на мёртвых). От лучшего пинга к худшему.
                        var mbps = ServerSpeedTester.measureSpeedMinimal(appCtx, p);
                        minimalByKey[key(p)] = mbps;
                        if (Volatile.Read(ref cancelled)) break;   // Промпт 103: сторож/отмена сработали во время замера — не действуем по нему
                        measured++;
                        onSpeedResult(p, mbps, false);   // минимальный → «≈» (быстрый/приблизительный)
                        // Порог minGate: есть throughput → в топ-кандидаты + ранний коннект к ПЕРВОМУ живому (непрерывность, Пр.95).
                        if (mbps >= minGate && !ServerFilter.isPaused(p, blocklist) && ServerFilter.protocolAllowed(p, s))
                        {
                            goodOnes.Add(p);
                            if (mbps > bestSpeed) { bestSpeed = mbps; best = p; Volatile.Write(ref hbBest, p); }
                            if (connected == null)
                            {
                                connected = p; connectedSpeed = mbps; Volatile.Write(ref hbConnected, p);
                                onPhase($"Подключено: {label(p)}. Ищу быстрее…");
                                var from = ServerLabels.displayForKey(appCtx, ProxyState.state.value.serverKey);
                                MonitorLog.@switch(appCtx, from, ServerLabels.display(p), "полный тест: первый рабочий", $"{fmt(mbps)} Мбит/с");
                                connect(p);
                            }
                        }
                        onPhase($"Проверено серверов: {measured} · рабочих: {goodOnes.Count}");
                        emitProgress(measured, maxScan);
                        beat();   // Промпт 103: шаг прогресса — «пульс» для сторожа
                    }
                    // ── Этап 3: ТОЧНЫЙ замер топа. Минимальный замер не различает самого быстрого (все ≈). Полным замером
                    // мерим ТОП fullCap прошедших minGate (по минимальному числу ЭТОГО прогона) → переключаемся на реально
                    // быстрейший, если заметно быстрее подключённого (инкумбент — кратный порог).
                    var bestFull = 0.0; ServerProfile? bestFullP = null;
                    if (!Volatile.Read(ref cancelled) && goodOnes.Count > 0)
                    {
                        var topK = goodOnes.DistinctBy(key)
                            .OrderByDescending(it => minimalByKey.GetValueOrDefault(key(it), 0.0))   // по МИНИМАЛЬНОМУ замеру этого прогона
                            .Take(fullCap).ToList();
                        onPhase($"Сравниваю скорость лучших серверов: {topK.Count}…");
                        foreach (var p in topK)
                        {
                            if (Volatile.Read(ref cancelled)) break;
                            if ((JavaSystem.nanoTime() - phaseStart) / 1_000_000 > budgetMs) break;
                            var full = ServerSpeedTester.measureSpeed(appCtx, p);   // ПОЛНЫЙ/точный замер
                            if (Volatile.Read(ref cancelled)) break;
                            onSpeedResult(p, full, true);
                            if (full > bestFull) { bestFull = full; bestFullP = p; }
                            if (full > bestSpeed) { bestSpeed = full; best = p; Volatile.Write(ref hbBest, p); }
                            beat();
                        }
                        var pick = bestFullP;
                        var conn = connected;
                        // Инкумбент «в норме» → порог КРАТНЫЙ (keepBestMultiplier, Пр.90); иначе апгрейд по марже (>10%).
                        var need = keepActive ? connectedSpeed * s.keepBestMultiplier : connectedSpeed * (1 + margin);
                        if (!Volatile.Read(ref cancelled) && pick != null && (conn == null || (key(pick) != key(conn) && bestFull > need)))
                        {
                            onPhase($"Нашёлся быстрее: {label(pick)}, переключаюсь…");
                            var from = ServerLabels.displayForKey(appCtx, ProxyState.state.value.serverKey);
                            MonitorLog.@switch(appCtx, from, ServerLabels.display(pick), "полный тест: точный топ", $"было {fmt(connectedSpeed)} → стало {fmt(bestFull)} Мбит/с");
                            connected = pick; connectedSpeed = bestFull; Volatile.Write(ref hbConnected, pick);
                            connect(pick);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.w("FullTestRunner", "тест прервался", e);
                    try { onPhase("Проверка прервалась из-за ошибки"); } catch (Exception) { }
                }
                finishOnce(new Result(connected ?? best ?? race?.winner, best, bestSpeed, candidates.Count, Volatile.Read(ref cancelled)));
            }, "fulltest-speed");
        }

        // --- Этап 1: ping всех (кроме заблокированных) — нужен для ранжирования, дёшев ---
        onPhase("Проверяю серверы…");
        emitProgress(0, testable.Count);
        pingHandle = ServerTester.testAll(
            context: appCtx,
            servers: testable,
            concurrency: PING_CONCURRENCY,   // в (ТЗ Elyor): «пинг всех» быстрее — больше параллелизма (пинг дёшев)
            onResult: (p, ms) =>
            {
                var v = (int)ms;
                pingByKey[key(p)] = v;
                onPingResult(p, v);
                if (v >= 0 && !ServerFilter.isPaused(p, blocklist) && ServerFilter.protocolAllowed(p, raceSettings)) race?.offer(p);
            },
            onProgress: (done, total) =>
            {
                onPhase($"Проверяю серверы: {done} из {total}");
                emitProgress(done, total);
            },
            onFinish: () =>
            {
                if (Volatile.Read(ref cancelled))
                {
                    race?.cancel();
                    finishOnce(new Result(null, null, 0.0, 0, true));
                }
                else
                {
                    // Промпт 108: defensive-дедуп по serverKey. По логам один и тот же сервер попадал в перебор
                    // 6–15 раз (реестр отдаёт дубли) → тест мерил его многократно и тянулся минутами. Один serverKey
                    // за прогон меряем ОДИН раз (корневую дупликацию реестра разобрать отдельно).
                    var alive = testable
                        .Where(it => (pingByKey.TryGetValue(key(it), out var pv) ? pv : it.pingMs ?? -1) >= 0)
                        .DistinctBy(key)
                        .OrderBy(it => pingByKey.TryGetValue(key(it), out var pv) ? pv : it.pingMs ?? int.MaxValue)
                        .ToList();
                    if (race == null) { startSpeedPhase(alive); return; }
                    KThread.start(() =>
                    {
                        race.complete();
                        race.join(RACE_WAIT);
                        race.cancel();
                        // Подключение асинхронно: без ожидания Этап 2 не увидел бы активного и переключился бы зря.
                        if (race.winner is { } won)
                        {
                            var end = JavaSystem.currentTimeMillis() + RACE_CONNECT_WAIT_MS;
                            while (!(ProxyState.state.value.running && ProxyState.state.value.serverKey == key(won)) &&
                                   JavaSystem.currentTimeMillis() < end && !Volatile.Read(ref cancelled))
                                Thread.Sleep(200);
                        }
                        startSpeedPhase(alive);
                    }, "fulltest-race-wait");
                }
            });

        return new HandleImpl(() =>
        {
            Volatile.Write(ref cancelled, true);
            race?.cancel();
            pingHandle?.cancel();
            // Промпт 123.D: флаг cancelled проверяется лишь МЕЖДУ замерами — идущий замер скорости
            // (measureSpeed активного/кандидата, приоритетный замер) висел бы до своего таймаута.
            // Рвём ИДУЩЕЕ соединение замера немедленно → «Прервать» действует за секунды, а не за минуты.
            try { ServerSpeedTester.abortCurrentMeasure(); } catch (Exception) { }
        });
    }
}
