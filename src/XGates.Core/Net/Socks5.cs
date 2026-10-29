using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XGates.Core.Net;

/// <summary>
/// Клиент SOCKS5 CONNECT «как Java Socket(Proxy.SOCKS) + InetSocketAddress.createUnresolved»: имя хоста отдаётся
/// прокси (remote DNS — тем же путём, что реальный трафик), рукопожатие укладывается в connect-таймаут, дальше
/// чтения ограничены soTimeout. Нужен ExternalIpChecker: «свежий сокет на каждую пробу» (Пр.144), без пулов HttpClient.
/// </summary>
public static class Socks5
{
    /// <summary>Открыть туннель до host:port через SOCKS5 на 127.0.0.1:[socksPort]. Бросает при отказе/таймауте.</summary>
    public static Socket Connect(int socksPort, string host, int port, int timeoutMs)
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            int left() => (int)Math.Max(1, deadline - Environment.TickCount64);
            var c = sock.ConnectAsync(IPAddress.Loopback, socksPort);
            if (!c.Wait(left())) throw new TimeoutException("connect timed out");
            sock.ReceiveTimeout = left();
            sock.SendTimeout = left();
            sock.Send([0x05, 0x01, 0x00]);                          // ver5, 1 метод: без аутентификации
            var r = ReadExactly(sock, 2);
            if (r[0] != 0x05 || r[1] != 0x00) throw new IOException("SOCKS: метод отклонён");
            var h = Encoding.ASCII.GetBytes(host);
            if (h.Length > 255) throw new ArgumentException("host too long");
            var req = new byte[7 + h.Length];
            req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x03; req[4] = (byte)h.Length;
            h.CopyTo(req, 5);
            req[5 + h.Length] = (byte)(port >> 8); req[6 + h.Length] = (byte)port;
            sock.ReceiveTimeout = left();
            sock.Send(req);
            var head = ReadExactly(sock, 4);
            if (head[1] != 0x00) throw new IOException($"SOCKS: CONNECT отклонён (код {head[1]})");
            int addrLen = head[3] switch { 0x01 => 4, 0x04 => 16, 0x03 => ReadExactly(sock, 1)[0], _ => throw new IOException("SOCKS: неизвестный ATYP") };
            ReadExactly(sock, addrLen + 2);
            return sock;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
        {
            sock.Dispose();
            throw new TimeoutException("SOCKS handshake timed out", e);
        }
        catch
        {
            sock.Dispose();
            throw;
        }
    }

    private static byte[] ReadExactly(Socket s, int n)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = s.Receive(buf, off, n - off, SocketFlags.None);
            if (r <= 0) throw new IOException("SOCKS: соединение закрыто");
            off += r;
        }
        return buf;
    }
}
