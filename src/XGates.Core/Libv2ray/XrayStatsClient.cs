using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace XGates.Core.Libv2ray;

/// <summary>
/// Минимальный клиент gRPC <c>xray.app.stats.command.StatsService/GetStats</c> (h2c на 127.0.0.1, без зависимостей):
/// на Android счётчики читаются в процессе (<c>CoreController.queryStats</c> = <c>counter.Set(0)</c>, чтение со сбросом),
/// на Windows ядро — отдельный процесс, поэтому то же самое через его API. Протобуф кодируется вручную: в запросе
/// два поля (name, reset), в ответе Stat{name, value}.
/// </summary>
internal sealed class XrayStatsClient : IDisposable
{
    private const string Path = "/xray.app.stats.command.StatsService/GetStats";
    private readonly HttpClient http;
    private readonly Uri uri;

    public XrayStatsClient(int apiPort)
    {
        // Только loopback и НИКАКОГО системного прокси Windows (он мог бы указывать на наш же порт).
        var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        uri = new Uri($"http://127.0.0.1:{apiPort}{Path}");
    }

    /// <summary>Значение счётчика со сбросом. null — счётчика нет (как <c>counter == nil</c> → 0 в libv2ray) или ошибка.</summary>
    public long? GetAndReset(string name)
    {
        var msg = EncodeGetStatsRequest(name, reset: true);
        var body = new byte[5 + msg.Length];
        body[0] = 0; // без сжатия
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1), (uint)msg.Length);
        msg.CopyTo(body, 5);

        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        req.Headers.TryAddWithoutValidation("TE", "trailers");

        // Синхронный Send для HTTP/2 в .NET не поддержан → асинхронный с ожиданием (вызывается из фоновых потоков,
        // как queryStats на Android; контекста синхронизации UI тут нет).
        using var resp = http.SendAsync(req, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult();
        var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var status = GrpcStatus(resp);
        if (resp.StatusCode != HttpStatusCode.OK || status != "0") return null; // напр. «not found» — счётчика нет
        if (bytes.Length < 5) return 0;
        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1));
        return DecodeGetStatsResponseValue(bytes.AsSpan(5, Math.Min(len, bytes.Length - 5)));
    }

    private static string? GrpcStatus(HttpResponseMessage resp)
    {
        if (resp.TrailingHeaders.TryGetValues("grpc-status", out var t)) return t.FirstOrDefault();
        if (resp.Headers.TryGetValues("grpc-status", out var h)) return h.FirstOrDefault(); // Trailers-Only
        return null;
    }

    internal static byte[] EncodeGetStatsRequest(string name, bool reset)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var ms = new MemoryStream();
        ms.WriteByte(0x0A); // field 1, length-delimited
        WriteVarint(ms, (ulong)nameBytes.Length);
        ms.Write(nameBytes);
        if (reset) { ms.WriteByte(0x10); ms.WriteByte(0x01); } // field 2, varint true
        return ms.ToArray();
    }

    /// <summary>GetStatsResponse{ stat(1): Stat{ name(1), value(2) } } → value (proto3: отсутствует = 0).</summary>
    internal static long DecodeGetStatsResponseValue(ReadOnlySpan<byte> msg)
    {
        long value = 0;
        int pos = 0;
        while (pos < msg.Length)
        {
            ulong key = ReadVarint(msg, ref pos);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            if (field == 1 && wire == 2)
            {
                int len = (int)ReadVarint(msg, ref pos);
                var stat = msg.Slice(pos, len);
                pos += len;
                int p = 0;
                while (p < stat.Length)
                {
                    ulong k = ReadVarint(stat, ref p);
                    int f = (int)(k >> 3), w = (int)(k & 7);
                    if (f == 2 && w == 0) value = (long)ReadVarint(stat, ref p);
                    else Skip(stat, ref p, w);
                }
            }
            else Skip(msg, ref pos, wire);
        }
        return value;
    }

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> b, ref int pos)
    {
        ulong result = 0; int shift = 0;
        while (true)
        {
            byte x = b[pos++];
            result |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return result;
            shift += 7;
        }
    }

    private static void Skip(ReadOnlySpan<byte> b, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0: ReadVarint(b, ref pos); break;
            case 1: pos += 8; break;
            case 2:
            {
                // НЕ «pos += ReadVarint(b, ref pos)»: C# берёт старый pos до сдвига внутри вызова.
                int len = (int)ReadVarint(b, ref pos);
                pos += len;
                break;
            }
            case 5: pos += 4; break;
            default: throw new FormatException("protobuf wire type " + wire);
        }
    }

    public void Dispose() => http.Dispose();
}
