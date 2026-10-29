using System.Text;

namespace XGates.Core.Compat;

/// <summary>Аналог <c>java.net.URISyntaxException</c>.</summary>
public sealed class UriSyntaxException(string input, string reason, int index = -1)
    : FormatException(index > -1 ? $"{reason} at index {index}: {input}" : $"{reason}: {input}")
{
    public string Input { get; } = input;
    public string Reason { get; } = reason;
    public int Index { get; } = index;
}

/// <summary>
/// Порт разбора <c>java.net.URI</c> из Android libcore (ojluni, RFC 2396 + RFC 2732) — ТОЧНО как на телефоне.
/// Kotlin-парсеры ссылок (vless/trojan/ss) опираются на его поведение: какие строки бросают исключение,
/// когда <c>host == null</c> (registry-based authority), что декодируется в userInfo/fragment.
/// <c>System.Uri</c> устроен иначе (мягче и с другой нормализацией) → другой набор серверов и serverKey.
/// Отличия Android от JDK, перенесённые здесь: «_» допустим в имени хоста; в decode() «%» между «[ ]» не
/// декодируется ни в одном компоненте; октеты IPv4 без подсчёта значащих цифр.
/// Перенесено только то, что нужно для разбора (конструктор URI(String) и геттеры), без resolve/normalize.
/// </summary>
public sealed class JavaUri
{
    public string? Scheme { get; private set; }
    /// <summary>Сырой фрагмент (без декодирования), как <c>getRawFragment()</c>.</summary>
    public string? RawFragment { get; private set; }
    public string? RawAuthority { get; private set; }
    public string? RawUserInfo { get; private set; }
    /// <summary><c>getHost()</c>: null, если authority не server-based (напр. «_» в TLD, мусор).</summary>
    public string? Host { get; private set; }
    /// <summary><c>getPort()</c>: -1, если не задан.</summary>
    public int Port { get; private set; } = -1;
    public string? RawPath { get; private set; }
    /// <summary><c>getRawQuery()</c>.</summary>
    public string? RawQuery { get; private set; }
    public string? RawSchemeSpecificPart { get; private set; }

    /// <summary><c>getUserInfo()</c> — percent-decoded (UTF-8).</summary>
    public string? UserInfo => Decode(RawUserInfo);
    /// <summary><c>getFragment()</c> — percent-decoded (UTF-8).</summary>
    public string? Fragment => Decode(RawFragment);
    /// <summary><c>getPath()</c> — percent-decoded.</summary>
    public string? Path => Decode(RawPath);
    /// <summary><c>getQuery()</c> — percent-decoded.</summary>
    public string? Query => Decode(RawQuery);
    /// <summary><c>getAuthority()</c> — percent-decoded.</summary>
    public string? Authority => Decode(RawAuthority);

    /// <summary><c>new URI(str)</c>: бросает <see cref="UriSyntaxException"/> (и, как в Java, иногда FormatException/
    /// OverflowException на гигантских числах в IPv6/IPv4) — вызывающие Kotlin-парсеры ловят любое исключение.</summary>
    public JavaUri(string str)
    {
        ArgumentNullException.ThrowIfNull(str);
        new Parser(this, str).Parse(false);
    }

    public static JavaUri? TryCreate(string str)
    {
        try { return new JavaUri(str); } catch (Exception) { return null; }
    }

    // -- Character classes (как в URI.java: битовые маски для символов < 128) --

    private static long LowMask(string chars)
    {
        long m = 0;
        foreach (var c in chars) if (c < 64) m |= 1L << c;
        return m;
    }

    private static long HighMask(string chars)
    {
        long m = 0;
        foreach (var c in chars) if (c >= 64 && c < 128) m |= 1L << (c - 64);
        return m;
    }

    private static long LowMask(char first, char last)
    {
        long m = 0;
        int f = Math.Max(Math.Min(first, (char)63), (char)0);
        int l = Math.Max(Math.Min(last, (char)63), (char)0);
        for (int i = f; i <= l; i++) m |= 1L << i;
        return m;
    }

    private static long HighMask(char first, char last)
    {
        long m = 0;
        int f = Math.Max(Math.Min((int)first, 127), 64) - 64;
        int l = Math.Max(Math.Min((int)last, 127), 64) - 64;
        for (int i = f; i <= l; i++) m |= 1L << i;
        return m;
    }

    private static bool Match(char c, long lowMask, long highMask)
    {
        if (c == 0) return false; // 0 doesn't have a slot in the mask. So, it never matches.
        if (c < 64) return ((1L << c) & lowMask) != 0;
        if (c < 128) return ((1L << (c - 64)) & highMask) != 0;
        return false;
    }

    private static readonly long L_DIGIT = LowMask('0', '9');
    private const long H_DIGIT = 0L;
    private const long L_UPALPHA = 0L;
    private static readonly long H_UPALPHA = HighMask('A', 'Z');
    private const long L_LOWALPHA = 0L;
    private static readonly long H_LOWALPHA = HighMask('a', 'z');
    private static readonly long L_ALPHA = L_LOWALPHA | L_UPALPHA;
    private static readonly long H_ALPHA = H_LOWALPHA | H_UPALPHA;
    private static readonly long L_ALPHANUM = L_DIGIT | L_ALPHA;
    private static readonly long H_ALPHANUM = H_DIGIT | H_ALPHA;
    private static readonly long L_HEX = L_DIGIT;
    private static readonly long H_HEX = HighMask('A', 'F') | HighMask('a', 'f');
    private static readonly long L_MARK = LowMask("-_.!~*'()");
    private static readonly long H_MARK = HighMask("-_.!~*'()");
    private static readonly long L_UNRESERVED = L_ALPHANUM | L_MARK;
    private static readonly long H_UNRESERVED = H_ALPHANUM | H_MARK;
    private static readonly long L_RESERVED = LowMask(";/?:@&=+$,[]");
    private static readonly long H_RESERVED = HighMask(";/?:@&=+$,[]");
    private const long L_ESCAPED = 1L;
    private const long H_ESCAPED = 0L;
    private static readonly long L_URIC = L_RESERVED | L_UNRESERVED | L_ESCAPED;
    private static readonly long H_URIC = H_RESERVED | H_UNRESERVED | H_ESCAPED;
    private static readonly long L_PCHAR = L_UNRESERVED | L_ESCAPED | LowMask(":@&=+$,");
    private static readonly long H_PCHAR = H_UNRESERVED | H_ESCAPED | HighMask(":@&=+$,");
    private static readonly long L_PATH = L_PCHAR | LowMask(";/");
    private static readonly long H_PATH = H_PCHAR | HighMask(";/");
    private static readonly long L_DASH = LowMask("-");
    private static readonly long H_DASH = HighMask("-");
    private static readonly long L_UNDERSCORE = LowMask("_");
    private static readonly long H_UNDERSCORE = HighMask("_");
    private static readonly long L_DOT = LowMask(".");
    private static readonly long H_DOT = HighMask(".");
    private static readonly long L_USERINFO = L_UNRESERVED | L_ESCAPED | LowMask(";:&=+$,");
    private static readonly long H_USERINFO = H_UNRESERVED | H_ESCAPED | HighMask(";:&=+$,");
    private static readonly long L_REG_NAME = L_UNRESERVED | L_ESCAPED | LowMask("$,;:@&=+");
    private static readonly long H_REG_NAME = H_UNRESERVED | H_ESCAPED | HighMask("$,;:@&=+");
    private static readonly long L_SERVER = L_USERINFO | L_ALPHANUM | L_DASH | LowMask(".:@[]");
    private static readonly long H_SERVER = H_USERINFO | H_ALPHANUM | H_DASH | HighMask(".:@[]");
    private static readonly long L_SERVER_PERCENT = L_SERVER | LowMask("%");
    private static readonly long H_SERVER_PERCENT = H_SERVER | HighMask("%");
    private static readonly long L_SCHEME = L_ALPHA | L_DIGIT | LowMask("+-.");
    private static readonly long H_SCHEME = H_ALPHA | H_DIGIT | HighMask("+-.");
    private static readonly long L_SCOPE_ID = L_ALPHANUM | LowMask("_.");
    private static readonly long H_SCOPE_ID = H_ALPHANUM | HighMask("_.");

    /// <summary>Java <c>Character.isSpaceChar</c>: Zs/Zl/Zp.</summary>
    private static bool IsSpaceChar(char c)
    {
        var cat = char.GetUnicodeCategory(c);
        return cat is System.Globalization.UnicodeCategory.SpaceSeparator
            or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator;
    }

    /// <summary>Java <c>Character.isISOControl</c>: U+0000..U+001F, U+007F..U+009F.</summary>
    private static bool IsIsoControl(char c) => c <= '\u001F' || (c >= '\u007F' && c <= '\u009F');

    // -- Escaping/decoding (URI.decode из Android) --

    private static int DecodeHex(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    private static byte DecodeHex(char c1, char c2) => (byte)(((DecodeHex(c1) & 0xf) << 4) | (DecodeHex(c2) & 0xf));

    /// <summary>
    /// <c>URI.decode(String)</c> Android: последовательности %XX → UTF-8 (битые → U+FFFD); «%» между «[» и «]» — как есть.
    /// Вход уже прошёл проверку парсера (каждый «%» вне скобок — валидная пара).
    /// </summary>
    internal static string? Decode(string? s)
    {
        if (s == null) return s;
        int n = s.Length;
        if (n == 0) return s;
        if (s.IndexOf('%') < 0) return s;

        var sb = new StringBuilder(n);
        var bb = new List<byte>(n);
        char c = s[0];
        bool betweenBrackets = false;
        for (int i = 0; i < n;)
        {
            if (c == '[') betweenBrackets = true;
            else if (betweenBrackets && c == ']') betweenBrackets = false;
            if (c != '%' || betweenBrackets)
            {
                sb.Append(c);
                if (++i >= n) break;
                c = s[i];
                continue;
            }
            bb.Clear();
            for (;;)
            {
                bb.Add(DecodeHex(s[++i], s[++i]));
                if (++i >= n) break;
                c = s[i];
                if (c != '%') break;
            }
            sb.Append(Encoding.UTF8.GetString(bb.ToArray()));
        }
        return sb.ToString();
    }

    private sealed class Parser(JavaUri uri, string input)
    {
        private bool requireServerAuthority;
        private int ipv6byteCount;

        private void Fail(string reason) => throw new UriSyntaxException(input, reason);
        private void Fail(string reason, int p) => throw new UriSyntaxException(input, reason, p);
        private void FailExpecting(string expected, int p) => Fail("Expected " + expected, p);

        private bool At(int start, int end, char c) => start < end && input[start] == c;

        private bool At(int start, int end, string s)
        {
            int p = start;
            int sn = s.Length;
            if (sn > end - p) return false;
            int i = 0;
            while (i < sn)
            {
                if (input[p++] != s[i]) break;
                i++;
            }
            return i == sn;
        }

        private int Scan(int start, int end, char c) => start < end && input[start] == c ? start + 1 : start;

        private int Scan(int start, int end, string err, string stop)
        {
            int p = start;
            while (p < end)
            {
                char c = input[p];
                if (err.IndexOf(c) >= 0) return -1;
                if (stop.IndexOf(c) >= 0) break;
                p++;
            }
            return p;
        }

        private int ScanEscape(int start, int n, char first)
        {
            int p = start;
            char c = first;
            if (c == '%')
            {
                if (p + 3 <= n && Match(input[p + 1], L_HEX, H_HEX) && Match(input[p + 2], L_HEX, H_HEX))
                    return p + 3;
                Fail("Malformed escape pair", p);
            }
            else if (c > 128 && !IsSpaceChar(c) && !IsIsoControl(c))
            {
                // Allow unescaped but visible non-US-ASCII chars
                return p + 1;
            }
            return p;
        }

        private int Scan(int start, int n, long lowMask, long highMask)
        {
            int p = start;
            while (p < n)
            {
                char c = input[p];
                if (Match(c, lowMask, highMask)) { p++; continue; }
                if ((lowMask & L_ESCAPED) != 0)
                {
                    int q = ScanEscape(p, n, c);
                    if (q > p) { p = q; continue; }
                }
                break;
            }
            return p;
        }

        private void CheckChars(int start, int end, long lowMask, long highMask, string what)
        {
            int p = Scan(start, end, lowMask, highMask);
            if (p < end) Fail("Illegal character in " + what, p);
        }

        private void CheckChar(int p, long lowMask, long highMask, string what) =>
            CheckChars(p, p + 1, lowMask, highMask, what);

        // [<scheme>:]<scheme-specific-part>[#<fragment>]
        public void Parse(bool rsa)
        {
            requireServerAuthority = rsa;
            int ssp;
            int n = input.Length;
            int p = Scan(0, n, "/?#", ":");
            if (p >= 0 && At(p, n, ':'))
            {
                if (p == 0) FailExpecting("scheme name", 0);
                CheckChar(0, L_ALPHA, H_ALPHA, "scheme name");
                CheckChars(1, p, L_SCHEME, H_SCHEME, "scheme name");
                uri.Scheme = input.Substring(0, p);
                p++; // Skip ':'
                ssp = p;
                if (At(p, n, '/'))
                {
                    p = ParseHierarchical(p, n);
                }
                else
                {
                    int q = Scan(p, n, "", "#");
                    if (q <= p) FailExpecting("scheme-specific part", p);
                    CheckChars(p, q, L_URIC, H_URIC, "opaque part");
                    p = q;
                }
            }
            else
            {
                ssp = 0;
                p = ParseHierarchical(0, n);
            }
            uri.RawSchemeSpecificPart = input.Substring(ssp, p - ssp);
            if (At(p, n, '#'))
            {
                CheckChars(p + 1, n, L_URIC, H_URIC, "fragment");
                uri.RawFragment = input.Substring(p + 1, n - (p + 1));
                p = n;
            }
            if (p < n) Fail("end of URI", p);
        }

        // [//authority]<path>[?<query>]
        private int ParseHierarchical(int start, int n)
        {
            int p = start;
            if (At(p, n, '/') && At(p + 1, n, '/'))
            {
                p += 2;
                int q0 = Scan(p, n, "", "/?#");
                if (q0 > p) p = ParseAuthority(p, q0);
                else if (q0 < n) { /* DEVIATION: Allow empty authority prior to non-empty path, query component or fragment identifier */ }
                else FailExpecting("authority", p);
            }
            int q = Scan(p, n, "", "?#"); // DEVIATION: May be empty
            CheckChars(p, q, L_PATH, H_PATH, "path");
            uri.RawPath = input.Substring(p, q - p);
            p = q;
            if (At(p, n, '?'))
            {
                p++;
                q = Scan(p, n, "", "#");
                CheckChars(p, q, L_URIC, H_URIC, "query");
                uri.RawQuery = input.Substring(p, q - p);
                p = q;
            }
            return p;
        }

        // authority = server | reg_name
        private int ParseAuthority(int start, int n)
        {
            int p = start;
            int q = p;
            UriSyntaxException? ex = null;

            bool serverChars;
            if (Scan(p, n, "", "]") > p)
            {
                // contains a literal IPv6 address, therefore % is allowed
                serverChars = Scan(p, n, L_SERVER_PERCENT, H_SERVER_PERCENT) == n;
            }
            else
            {
                serverChars = Scan(p, n, L_SERVER, H_SERVER) == n;
            }
            bool regChars = Scan(p, n, L_REG_NAME, H_REG_NAME) == n;

            if (regChars && !serverChars)
            {
                // Must be a registry-based authority
                uri.RawAuthority = input.Substring(p, n - p);
                return n;
            }

            if (serverChars)
            {
                // Might be (probably is) a server-based authority, so attempt to parse it as such.
                // If the attempt fails, try to treat it as a registry-based authority.
                try
                {
                    q = ParseServer(p, n);
                    if (q < n) FailExpecting("end of authority", q);
                    uri.RawAuthority = input.Substring(p, n - p);
                }
                catch (UriSyntaxException x)
                {
                    // Undo results of failed parse
                    uri.RawUserInfo = null;
                    uri.Host = null;
                    uri.Port = -1;
                    if (requireServerAuthority) throw;
                    ex = x;
                    q = p;
                }
            }

            if (q < n)
            {
                if (regChars) uri.RawAuthority = input.Substring(p, n - p); // Registry-based authority
                else if (ex != null) throw ex; // probably due to a malformed IPv6 address
                else Fail("Illegal character in authority", q);
            }
            return n;
        }

        // [<userinfo>@]<host>[:<port>]
        private int ParseServer(int start, int n)
        {
            int p = start;
            int q;

            // userinfo
            q = Scan(p, n, "/?#", "@");
            if (q >= p && At(q, n, '@'))
            {
                CheckChars(p, q, L_USERINFO, H_USERINFO, "user info");
                uri.RawUserInfo = input.Substring(p, q - p);
                p = q + 1; // Skip '@'
            }

            // hostname, IPv4 address, or IPv6 address
            if (At(p, n, '['))
            {
                // DEVIATION from RFC2396: Support IPv6 addresses, per RFC2732
                p++;
                q = Scan(p, n, "/?#", "]");
                if (q > p && At(q, n, ']'))
                {
                    // look for a "%" scope id
                    int r = Scan(p, q, "", "%");
                    if (r > p)
                    {
                        ParseIPv6Reference(p, r);
                        if (r + 1 == q) Fail("scope id expected");
                        CheckChars(r + 1, q, L_SCOPE_ID, H_SCOPE_ID, "scope id");
                    }
                    else
                    {
                        ParseIPv6Reference(p, q);
                    }
                    uri.Host = input.Substring(p - 1, q + 1 - (p - 1));
                    p = q + 1;
                }
                else
                {
                    FailExpecting("closing bracket for IPv6 address", q);
                }
            }
            else
            {
                q = ParseIPv4Address(p, n);
                if (q <= p) q = ParseHostname(p, n);
                p = q;
            }

            // port
            if (At(p, n, ':'))
            {
                p++;
                q = Scan(p, n, "", "/");
                if (q > p)
                {
                    CheckChars(p, q, L_DIGIT, H_DIGIT, "port number");
                    if (!int.TryParse(input.AsSpan(p, q - p), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var port))
                        Fail("Malformed port number", p);
                    uri.Port = port;
                    p = q;
                }
            }
            if (p < n) FailExpecting("port number", p);
            return p;
        }

        // Scan a string of decimal digits whose value fits in a byte
        private int ScanByte(int start, int n)
        {
            int p = start;
            int q = Scan(p, n, L_DIGIT, H_DIGIT);
            if (q <= p) return q;
            // Java Integer.parseInt: переполнение → NumberFormatException (ловится в ParseIPv4Address).
            if (int.Parse(input.AsSpan(p, q - p), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture) > 255) return p;
            return q;
        }

        private int ScanIPv4Address(int start, int n, bool strict)
        {
            int p = start;
            int q;
            int m = Scan(p, n, L_DIGIT | L_DOT, H_DIGIT | H_DOT);
            if (m <= p || (strict && m != n)) return -1;
            for (;;)
            {
                // Per RFC2732: At most three digits per byte. Further constraint: Each element fits in a byte
                if ((q = ScanByte(p, m)) <= p) break; p = q;
                if ((q = Scan(p, m, '.')) <= p) break; p = q;
                if ((q = ScanByte(p, m)) <= p) break; p = q;
                if ((q = Scan(p, m, '.')) <= p) break; p = q;
                if ((q = ScanByte(p, m)) <= p) break; p = q;
                if ((q = Scan(p, m, '.')) <= p) break; p = q;
                if ((q = ScanByte(p, m)) <= p) break; p = q;
                if (q < m) break;
                return q;
            }
            Fail("Malformed IPv4 address", q);
            return -1;
        }

        private int TakeIPv4Address(int start, int n, string expected)
        {
            int p = ScanIPv4Address(start, n, true);
            if (p <= start) FailExpecting(expected, start);
            return p;
        }

        private int ParseIPv4Address(int start, int n)
        {
            int p;
            try
            {
                p = ScanIPv4Address(start, n, false);
            }
            catch (UriSyntaxException) { return -1; }
            catch (FormatException) { return -1; }
            catch (OverflowException) { return -1; }

            if (p > start && p < n)
            {
                // IPv4 address is followed by something - check that it's a ":" as this is the only valid
                // character to follow an address.
                if (input[p] != ':') p = -1;
            }
            if (p > start) uri.Host = input.Substring(start, p - start);
            return p;
        }

        // hostname = domainlabel [ "." ] | 1*( domainlabel "." ) toplabel [ "." ]
        private int ParseHostname(int start, int n)
        {
            int p = start;
            int q;
            int l = -1; // Start of last parsed label
            do
            {
                // Android-changed: Allow underscore in hostname.
                // domainlabel = alphanum [ *( alphanum | "-" | "_" ) alphanum ]
                q = Scan(p, n, L_ALPHANUM, H_ALPHANUM);
                if (q <= p) break;
                l = p;
                p = q;
                q = Scan(p, n, L_ALPHANUM | L_DASH | L_UNDERSCORE, H_ALPHANUM | H_DASH | H_UNDERSCORE);
                if (q > p)
                {
                    if (input[q - 1] == '-') Fail("Illegal character in hostname", q - 1);
                    p = q;
                }
                q = Scan(p, n, '.');
                if (q <= p) break;
                p = q;
            } while (p < n);

            if (p < n && !At(p, n, ':')) Fail("Illegal character in hostname", p);
            if (l < 0) FailExpecting("hostname", start);

            // for a fully qualified hostname check that the rightmost label starts with an alpha character.
            if (l > start && !Match(input[l], L_ALPHA, H_ALPHA)) Fail("Illegal character in hostname", l);

            uri.Host = input.Substring(start, p - start);
            return p;
        }

        // IPv6 address parsing, from RFC2373 (revised grammar as in URI.java)
        private int ParseIPv6Reference(int start, int n)
        {
            int p = start;
            int q;
            bool compressedZeros = false;

            q = ScanHexSeq(p, n);

            if (q > p)
            {
                p = q;
                if (At(p, n, "::"))
                {
                    compressedZeros = true;
                    p = ScanHexPost(p + 2, n);
                }
                else if (At(p, n, ':'))
                {
                    p = TakeIPv4Address(p + 1, n, "IPv4 address");
                    ipv6byteCount += 4;
                }
            }
            else if (At(p, n, "::"))
            {
                compressedZeros = true;
                p = ScanHexPost(p + 2, n);
            }
            if (p < n) Fail("Malformed IPv6 address", start);
            if (ipv6byteCount > 16) Fail("IPv6 address too long", start);
            if (!compressedZeros && ipv6byteCount < 16) Fail("IPv6 address too short", start);
            if (compressedZeros && ipv6byteCount == 16) Fail("Malformed IPv6 address", start);

            return p;
        }

        private int ScanHexPost(int start, int n)
        {
            int p = start;
            int q;

            if (p == n) return p;

            q = ScanHexSeq(p, n);
            if (q > p)
            {
                p = q;
                if (At(p, n, ':'))
                {
                    p++;
                    p = TakeIPv4Address(p, n, "hex digits or IPv4 address");
                    ipv6byteCount += 4;
                }
            }
            else
            {
                p = TakeIPv4Address(p, n, "hex digits or IPv4 address");
                ipv6byteCount += 4;
            }
            return p;
        }

        // Scan a hex sequence; return -1 if one could not be scanned
        private int ScanHexSeq(int start, int n)
        {
            int p = start;
            int q;

            q = Scan(p, n, L_HEX, H_HEX);
            if (q <= p) return -1;
            if (At(q, n, '.')) return -1; // Beginning of IPv4 address
            if (q > p + 4) Fail("IPv6 hexadecimal digit sequence too long", p);
            ipv6byteCount += 2;
            p = q;
            while (p < n)
            {
                if (!At(p, n, ':')) break;
                if (At(p + 1, n, ':')) break; // "::"
                p++;
                q = Scan(p, n, L_HEX, H_HEX);
                if (q <= p) FailExpecting("digits for an IPv6 address", p);
                if (At(q, n, '.'))
                {
                    // Beginning of IPv4 address
                    p--;
                    break;
                }
                if (q > p + 4) Fail("IPv6 hexadecimal digit sequence too long", p);
                ipv6byteCount += 2;
                p = q;
            }

            return p;
        }
    }
}
