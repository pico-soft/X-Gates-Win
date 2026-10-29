using System.Text;

namespace XGates.Core.Compat;

/// <summary>
/// Порт декодера <c>android.util.Base64</c> (AOSP). Важно для паритета: он ПРОПУСКАЕТ любой символ вне алфавита
/// (переводы строк, пробелы, «-»/«_» в режиме DEFAULT, мусор), паддинг «=» необязателен, но если есть — строго
/// правильный; одиночный «хвостовой» символ — ошибка. <c>Convert.FromBase64String</c> устроен иначе
/// (бросает на любом постороннем символе) → подписки декодировались бы по-другому.
/// </summary>
internal static class AndroidBase64
{
    public const int DEFAULT = 0;
    public const int NO_PADDING = 1;
    public const int NO_WRAP = 2;
    public const int CRLF = 4;
    public const int URL_SAFE = 8;

    private const int SKIP = -1;
    private const int EQUALS = -2;

    private static readonly int[] DECODE = BuildTable("+/");
    private static readonly int[] DECODE_WEBSAFE = BuildTable("-_");

    private static int[] BuildTable(string c62c63)
    {
        var t = new int[256];
        Array.Fill(t, SKIP);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        for (int i = 0; i < alphabet.Length; i++) t[alphabet[i]] = i;
        t[c62c63[0]] = 62;
        t[c62c63[1]] = 63;
        t['='] = EQUALS;
        return t;
    }

    /// <summary><c>Base64.decode(String, flags)</c>: строка → байты UTF-8 (как <c>str.getBytes()</c> на Android).
    /// Бросает <see cref="ArgumentException"/> («bad base-64»), как IllegalArgumentException в Java.</summary>
    public static byte[] Decode(string str, int flags) => Decode(Encoding.UTF8.GetBytes(str), flags);

    public static byte[] Decode(byte[] input, int flags)
    {
        var output = new byte[input.Length * 3 / 4];
        int[] alphabet = (flags & URL_SAFE) == 0 ? DECODE : DECODE_WEBSAFE;
        int state = 0, value = 0, op = 0, p = 0, len = input.Length;

        while (p < len)
        {
            // Быстрый путь Android-декодера опущен: по комментарию AOSP результат без него тот же.
            int d = alphabet[input[p++] & 0xff];
            switch (state)
            {
                case 0:
                    if (d >= 0) { value = d; ++state; }
                    else if (d != SKIP) throw Bad();
                    break;
                case 1:
                    if (d >= 0) { value = (value << 6) | d; ++state; }
                    else if (d != SKIP) throw Bad();
                    break;
                case 2:
                    if (d >= 0) { value = (value << 6) | d; ++state; }
                    else if (d == EQUALS)
                    {
                        // Emit the last (partial) output tuple; expect exactly one more padding character.
                        output[op++] = (byte)(value >> 4);
                        state = 4;
                    }
                    else if (d != SKIP) throw Bad();
                    break;
                case 3:
                    if (d >= 0)
                    {
                        // Emit the output triple and return to state 0.
                        value = (value << 6) | d;
                        output[op + 2] = (byte)value;
                        output[op + 1] = (byte)(value >> 8);
                        output[op] = (byte)(value >> 16);
                        op += 3;
                        state = 0;
                    }
                    else if (d == EQUALS)
                    {
                        // Emit the last (partial) output tuple; expect no further data or padding characters.
                        output[op + 1] = (byte)(value >> 2);
                        output[op] = (byte)(value >> 10);
                        op += 2;
                        state = 5;
                    }
                    else if (d != SKIP) throw Bad();
                    break;
                case 4:
                    if (d == EQUALS) ++state;
                    else if (d != SKIP) throw Bad();
                    break;
                case 5:
                    if (d != SKIP) throw Bad();
                    break;
            }
        }

        // Done reading input. Now figure out where we are left in the state machine and finish up.
        switch (state)
        {
            case 0: break; // Output length is a multiple of three. Fine.
            case 1: throw Bad(); // Read one extra input byte, which isn't enough to make another output byte.
            case 2: output[op++] = (byte)(value >> 4); break; // Read two extra input bytes → 1 more output byte.
            case 3:
                // Read three extra input bytes, enough to emit 2 more output bytes.
                output[op++] = (byte)(value >> 10);
                output[op++] = (byte)(value >> 2);
                break;
            case 4: throw Bad(); // Read one padding '=' when we expected 2.
            case 5: break; // Read all the padding '='s we expected and no more.
        }

        if (op == output.Length) return output;
        var temp = new byte[op];
        Array.Copy(output, temp, op);
        return temp;
    }

    private static ArgumentException Bad() => new("bad base-64");

    /// <summary><c>Base64.encodeToString(bytes, NO_WRAP)</c> — стандартный алфавит с паддингом, без переносов.</summary>
    public static string EncodeNoWrap(byte[] data) => Convert.ToBase64String(data);
}

/// <summary>Порт <c>java.net.URLDecoder.decode(s, UTF_8)</c> (Android libcore): «+» → пробел; битый «%» — исключение.</summary>
internal static class JavaUrlDecoder
{
    public static string Decode(string s)
    {
        bool needToChange = false;
        int numChars = s.Length;
        var sb = new StringBuilder(numChars > 500 ? numChars / 2 : numChars);
        int i = 0;
        byte[]? bytes = null;
        while (i < numChars)
        {
            char c = s[i];
            switch (c)
            {
                case '+':
                    sb.Append(' ');
                    i++;
                    needToChange = true;
                    break;
                case '%':
                    // Starting with this instance of %, process all consecutive substrings of the form %xy.
                    bytes ??= new byte[(numChars - i) / 3];
                    int pos = 0;
                    while (i + 2 < numChars && c == '%')
                    {
                        if (!IsValidHexChar(s[i + 1]) || !IsValidHexChar(s[i + 2]))
                            throw new ArgumentException("URLDecoder: Illegal hex characters in escape (%) pattern : " + s.Substring(i, 3));
                        int v = Convert.ToInt32(s.Substring(i + 1, 2), 16);
                        bytes[pos++] = (byte)v;
                        i += 3;
                        if (i < numChars) c = s[i];
                    }
                    // A trailing, incomplete byte encoding such as "%x" will cause an exception to be thrown
                    if (i < numChars && c == '%')
                        throw new ArgumentException("URLDecoder: Incomplete trailing escape (%) pattern");
                    sb.Append(Encoding.UTF8.GetString(bytes, 0, pos));
                    needToChange = true;
                    break;
                default:
                    sb.Append(c);
                    i++;
                    break;
            }
        }
        return needToChange ? sb.ToString() : s;
    }

    private static bool IsValidHexChar(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
