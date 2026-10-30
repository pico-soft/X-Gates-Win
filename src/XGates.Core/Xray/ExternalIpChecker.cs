using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using XGates.Core.Net;
using XGates.Core.Platform;

namespace XGates.Core.Xray;

/**
 * Внешний IP ЧЕРЕЗ активный SOCKS-туннель (127.0.0.1:SOCKS_PORT). Порт 1:1 с xray/ExternalIpChecker.kt.
 * Индикатор РЕАЛЬНОЙ живости туннеля: галочки портов говорят лишь что локальный сокет слушает,
 * а полученный IP — что трафик реально ходит наружу через сервер.
 *
 * Цепочка фолбэков (каждый возвращает голый IP текстом): ipify → ifconfig.me → ipinfo.io.
 * Таймаут 5с на каждый. Возвращает IP или null (нет ответа).
 *
 * Пр.144 (КРИТИЧНО, ⭐ живость): КАЖДАЯ проба — СВЕЖИЙ сокет через SOCKS, НЕ переиспользуем keep-alive.
 * КОРЕНЬ бага «зелёный, но связи нет»: раньше проба шла через HttpURLConnection (OkHttp пул) и МОГЛА
 * переиспользовать давно открытый живой keep-alive коннект к ipify. Когда сервер переставал принимать НОВЫЕ
 * соединения (реальный трафик и спидтест мертвы), СТАРЫЙ коннект ещё отвечал → проба ложно «ок», монитор
 * не видел обрыв, восстановление НЕ запускалось. Свежий сокет каждый раз = проба ходит тем же путём, что и
 * реальный трафик (новое соединение), и честно падает, когда туннель не пропускает. Плюс remote-DNS через
 * SOCKS (как реальный трафик), Connection: close, антикэш-нонс.
 */
public static partial class ExternalIpChecker
{
    private const string TAG = "ExternalIpChecker";
    private const int TIMEOUT_MS = 5_000;
    private const int MAX_RESP_BYTES = 8 * 1024;   // ответ крошечный (IP); больше не читаем

    // (host, path) — путь без нонса; нонс добавим при запросе, чтобы исключить любой промежуточный кэш.
    private static readonly (string host, string path)[] endpoints =
    [
        ("api.ipify.org", "/"),
        ("ifconfig.me", "/ip"),
        ("ipinfo.io", "/ip"),
    ];
    private static long nonce;

    [GeneratedRegex("[0-9]{1,3}(\\.[0-9]{1,3}){3}|[0-9a-fA-F:]{2,}:[0-9a-fA-F:]{2,}")]
    private static partial Regex IpLike();

    // Промпт 101.A — ОДИН запрос внешнего адреса за раз, из ЕДИНОЙ точки. РАНЬШЕ петля интерфейса и монитор
    // запрашивали независимо, не зная друг о друге; пока запрос был коротким — наложений не было, но стоило
    // ему затянуться (мёртвый канал) — запросы шли внахлёст и ДУШИЛИ туннель (вечное «проверяется связь»).
    // Теперь: пока запрос выполняется, новые НЕ создаются — пришедшие ЖДУТ его результат (coalesce) и свой
    // запрос НЕ делают. Никаких новых потоков здесь не создаём (потоки — на стороне вызывающих).
    // НЕ ТРОГАЕМ (Промпт 101.D): таймаут, цепочку фолбэков, логику вердикта — только сериализация.
    private static readonly object @lock = new();
    private static volatile bool inFlight;
    private static volatile string? lastResult;
    private static long generation;

    // Тестовый шов + счётчики (Промпт 101.C): подменяемая «одна работа» и наблюдаемая параллельность/число
    // фактических запросов — чтобы ЧИСЛАМИ показать «один запрос за раз». В проде onceOverrideForTest=null.
    internal static Func<int, string?>? onceOverrideForTest;
    private static int concurrentNow;
    internal static volatile int maxConcurrentSeen;
    internal static int onceCount;
    internal static void resetTestCounters() { maxConcurrentSeen = 0; onceCount = 0; }

    /**
     * Быстрая проверка прохода для подбора (Пр.146.Ф2): 1 эндпоинт × короткий таймаут. НЕ полные 3×5с — при
     * переборе кандидатов важна скорость, а один свежий проход через туннель уже доказывает живость.
     */
    public static bool probePass(int? socksPort = null, int timeoutMs = 4000) =>   // null = порт из настроек (как дефолт Kotlin — на момент вызова)
        fetch(socksPort, timeoutMs, maxEndpoints: 1) != null;

    /**
     * ЗАДЕРЖКА активного туннеля (мс) — свежий TCP-коннект ЧЕРЕЗ активный SOCKS к удалённому хосту: время
     * установления = RTT туннеля до бэкенда. Для пинга АКТИВНОГО сервера надёжнее, чем cold-start temp-инстанс:
     * для xhttp/reality холодное рукопожатие не успевает в таймаут → ложный «✗» на живом сервере, а тёплый
     * активный туннель отвечает честно. Свежий сокет (Пр.144) → честно падает, если туннель мёртв (не кэш).
     * Возврат мс (≥0) или -1 (нет прохода). Блокирующий — звать на фоне.
     */
    public static int latencyMs(int? socksPort = null, string host = "www.gstatic.com", string path = "/generate_204", int timeoutMs = 4000, int port = 80)
    {
        Socket? raw = null;
        try
        {
            // ВАЖНО: голый SOCKS-коннект НЕ годится для пинга — xray отвечает на SOCKS-CONNECT локально мгновенно
            // (не ждёт коннекта до бэкенда) → ~1 мс (фейк). Меряем РЕАЛЬНЫЙ round-trip: HTTP-запрос (без TLS) и время
            // до ПЕРВОГО БАЙТА ответа = полный проход туда-обратно через туннель до бэкенда. gstatic /generate_204 —
            // крошечный ответ (204). Работает и для xhttp/reality (обычный трафик проходит), в отличие от cold-start пинга.
            raw = Socks5.Connect(socksPort ?? XrayConfig.SOCKS_PORT, host, port, timeoutMs);   // SOCKS сам резолвит (как трафик)
            raw.ReceiveTimeout = timeoutMs;
            var req = $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: curl/8.0\r\nConnection: close\r\n\r\n";
            var t0 = JavaSystem.nanoTime();
            raw.Send(Encoding.ASCII.GetBytes(req));
            var one = new byte[1];
            var first = raw.Receive(one);   // первый байт ответа = round-trip ЧЕРЕЗ туннель
            return first <= 0 ? -1 : ((int)((JavaSystem.nanoTime() - t0) / 1_000_000)).coerceAtLeast(1);
        }
        catch (Exception e)
        {
            Log.i(TAG, $"latencyMs FAIL {JavaExceptionNames.simpleName(e)}: {e.Message}");
            return -1;
        }
        finally
        {
            try { raw?.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>Единая точка запроса внешнего адреса. Блокирующий — запускать на фоновом потоке (как и раньше).</summary>
    public static string? fetch(int? socksPort = null, int timeoutMs = TIMEOUT_MS, int maxEndpoints = int.MaxValue)
    {
        lock (@lock)
        {
            if (inFlight)
            {
                // Запрос уже идёт — дождаться ЕГО результата, свой НЕ запускать (не плодим параллельные).
                var gen = generation;
                while (inFlight && generation == gen)
                {
                    try { System.Threading.Monitor.Wait(@lock); } catch (ThreadInterruptedException) { return lastResult; }
                }
                Log.i(TAG, "запрос внешнего адреса уже шёл — отдан его результат (без нового)");
                return lastResult;
            }
            inFlight = true;
        }
        string? result = null;
        try
        {
            var n = Interlocked.Increment(ref concurrentNow);
            if (n > maxConcurrentSeen) maxConcurrentSeen = n;
            Interlocked.Increment(ref onceCount);
            var sp = socksPort ?? XrayConfig.SOCKS_PORT;
            result = onceOverrideForTest?.Invoke(sp) ?? fetchOnce(sp, timeoutMs, maxEndpoints);
        }
        finally
        {
            Interlocked.Decrement(ref concurrentNow);
            lock (@lock)
            {
                lastResult = result;
                inFlight = false;
                generation++;
                System.Threading.Monitor.PulseAll(@lock);
            }
        }
        return result;
    }

    /// <summary>Подмена эндпоинтов/порта TLS (только тесты: герметичная сеть).</summary>
    internal static (string host, string path)[]? endpointsOverrideForTest;
    internal static int tlsPortForTest = 443;
    internal static RemoteCertificateValidationCallback? certValidationForTest;

    /**
     * Один фактический сетевой запрос (цепочка фолбэков). Вызывается ТОЛЬКО из [fetch] под флагом inFlight.
     * Пр.144: СВЕЖИЙ TCP+TLS сокет ЧЕРЕЗ SOCKS на каждый вызов (никакого пула/keep-alive). DNS — remote через
     * SOCKS (createUnresolved), как у реального трафика. Успех = реально прошёл HTTP 200 с телом-IP через туннель.
     */
    private static string? fetchOnce(int socksPort, int timeoutMs = TIMEOUT_MS, int maxEndpoints = int.MaxValue)
    {
        foreach (var (host, path) in (endpointsOverrideForTest ?? endpoints).Take(maxEndpoints.coerceAtLeast(1)))
        {
            var n = Interlocked.Increment(ref nonce);
            Socket? raw = null;
            try
            {
                // Имя резолвит SOCKS-сервер (remote DNS, socks5h) тем же путём, что реальный трафик.
                raw = Socks5.Connect(socksPort, host, tlsPortForTest, timeoutMs);
                raw.ReceiveTimeout = timeoutMs;
                raw.SendTimeout = timeoutMs;
                using var ns = new NetworkStream(raw, ownsSocket: false);
                using var ssl = new SslStream(ns, leaveInnerStreamOpen: false, certValidationForTest);
                ssl.ReadTimeout = timeoutMs;
                ssl.WriteTimeout = timeoutMs;
                ssl.AuthenticateAsClient(host);   // реальный TLS через туннель — уже доказывает, что трафик ходит
                var req = $"GET {path}?_={n} HTTP/1.1\r\n" +
                          $"Host: {host}\r\n" +
                          "User-Agent: curl/8.0\r\n" +
                          "Accept: */*\r\n" +
                          "Cache-Control: no-cache, no-store\r\n" +
                          "Connection: close\r\n\r\n";
                ssl.Write(Encoding.ASCII.GetBytes(req));
                ssl.Flush();
                // читаем до EOF (Connection: close), но не более MAX_RESP_BYTES
                var buf = new MemoryStream();
                var chunk = new byte[2048];
                while (buf.Length < MAX_RESP_BYTES)
                {
                    var r = ssl.Read(chunk, 0, chunk.Length);
                    if (r <= 0) break;
                    buf.Write(chunk, 0, r);
                }
                var resp = Encoding.ASCII.GetString(buf.ToArray());
                var nl = resp.IndexOf("\r\n", StringComparison.Ordinal);
                var statusLine = nl >= 0 ? resp.Substring(0, nl) : "";
                if (!statusLine.Contains(" 200")) { Log.i(TAG, $"{host} → {statusLine}"); continue; }
                var hb = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var body = hb >= 0 ? resp.Substring(hb + 4) : "";
                var m = IpLike().Match(body);
                var ip = m.Success ? m.Value : null;
                if (ip != null && ip.Length is >= 7 and <= 45)
                {
                    Log.i(TAG, $"IP через туннель = {ip} (via {host}, свежий сокет)");
                    return ip;
                }
                Log.i(TAG, $"{host} → 200, но IP в теле не распознан");
            }
            catch (Exception e)
            {
                Log.i(TAG, $"{host} FAIL {JavaExceptionNames.simpleName(e)}: {e.Message}");
            }
            finally
            {
                try { raw?.Dispose(); } catch (Exception) { }
            }
        }
        return null;
    }
}
