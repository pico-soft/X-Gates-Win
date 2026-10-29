using System.Net;
using System.Net.Http.Headers;

namespace XGates.Core.Net;

/// <summary>
/// Минимальный аналог <c>HttpURLConnection</c> для замеров (ServerSpeedTester): GET с connectTimeout на соединение и
/// readTimeout на КАЖДОЕ чтение (SO_TIMEOUT), <see cref="responseCode"/> блокирует до заголовков, <see cref="read"/>
/// возвращает -1 на EOF, <see cref="disconnect"/> рвёт соединение из ДРУГОГО потока (кнопка «Прервать проверку»):
/// идущие connect/read падают с ошибкой — как disconnect() в Java. Без пула соединений (каждый замер — свой сокет).
/// </summary>
public sealed class JavaHttpConnection
{
    private readonly string url;
    private readonly HttpRoute route;
    private readonly CancellationTokenSource abort = new();
    private readonly string userAgent;
    private HttpClient? client;
    private HttpResponseMessage? resp;
    private Stream? body;
    private int code = -1;

    public int connectTimeout { get; set; } = 8_000;
    public int readTimeout { get; set; } = 8_000;

    public JavaHttpConnection(string url, HttpRoute route, string userAgent = "v2rayNG/1.8.0")
    {
        this.url = url;
        this.route = route;
        this.userAgent = userAgent;
    }

    /// <summary>Отправить GET и дождаться статуса (блокирующе). Бросает при ошибке соединения/таймауте/отмене.</summary>
    public int responseCode()
    {
        if (code >= 0) return code;
        client = new HttpClient(route.CreateHandler(connectTimeout), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
        headersCts.CancelAfter(Math.Max(1, connectTimeout) + Math.Max(1, readTimeout));
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        try
        {
            resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!abort.IsCancellationRequested)
        {
            throw new TimeoutException("connect/read timed out");
        }
        code = (int)resp.StatusCode;
        return code;
    }

    public long contentLengthLong => resp?.Content.Headers.ContentLength ?? -1;

    /// <summary>Прочитать в буфер; -1 — EOF. readTimeout — на это чтение.</summary>
    public int read(byte[] buf)
    {
        if (resp == null) responseCode();
        body ??= resp!.Content.ReadAsStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
        cts.CancelAfter(Math.Max(1, readTimeout));
        int n;
        try { n = body.ReadAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!abort.IsCancellationRequested) { throw new TimeoutException("Read timed out"); }
        return n <= 0 ? -1 : n;
    }

    /// <summary>Разорвать соединение (можно из другого потока).</summary>
    public void disconnect()
    {
        try { abort.Cancel(); } catch (ObjectDisposedException) { }
        try { body?.Dispose(); } catch (Exception) { }
        try { resp?.Dispose(); } catch (Exception) { }
        try { client?.Dispose(); } catch (Exception) { }
    }
}

/// <summary>
/// POST-выгрузка «как setChunkedStreamingMode + outputStream» для замера ОТДАЧИ: тело пишет колбэк прямо в сокет
/// (без Content-Length), блокировка записи при насыщении канала = реальная скорость отдачи.
/// </summary>
public sealed class StreamingUpload
{
    private readonly CancellationTokenSource abort = new();
    private HttpClient? client;

    /// <summary>
    /// [writeBody] пишет в поток запроса (синхронно, в своём цикле) и возвращает, ждать ли ответ (false — запись
    /// оборвалась/упёрлась в потолок → ответ не ждём, как в Kotlin). Возврат — HTTP-код или -1.
    /// </summary>
    public int send(string url, HttpRoute route, int connectTimeoutMs, int readTimeoutMs, string userAgent, Func<Stream, bool> writeBody)
    {
        client = new HttpClient(route.CreateHandler(connectTimeoutMs), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        bool waitResponse = true;
        var content = new PushContent(s => waitResponse = writeBody(s));
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.TransferEncodingChunked = true;
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var send = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        content.WaitBodyDone(abort.Token);
        if (!waitResponse) { disconnect(); return -1; }
        try
        {
            if (!send.Wait(Math.Max(1, readTimeoutMs))) { disconnect(); return -1; }
            using var r = send.Result;
            return (int)r.StatusCode;
        }
        catch (Exception) { return -1; }
    }

    public void disconnect()
    {
        try { abort.Cancel(); } catch (ObjectDisposedException) { }
        try { client?.Dispose(); } catch (Exception) { }
    }

    private sealed class PushContent(Action<Stream> write) : HttpContent
    {
        private readonly ManualResetEventSlim done = new();
        private Exception? error;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            try { write(new SyncStream(stream)); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
            return error != null ? Task.FromException(error) : Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length) { length = -1; return false; }

        public void WaitBodyDone(CancellationToken ct)
        {
            try { done.Wait(ct); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Синхронная запись в поток запроса HttpClient (его поток не поддерживает sync Write в .NET — оборачиваем).</summary>
    private sealed class SyncStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.FlushAsync().GetAwaiter().GetResult();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }
}
