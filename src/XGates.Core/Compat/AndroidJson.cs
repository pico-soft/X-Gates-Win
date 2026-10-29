using System.Globalization;
using System.Text;

namespace XGates.Core.Compat;

// Имена методов org.json — в Java-регистре (optString, put, length…): Kotlin-код переносится построчно.

/// <summary>Аналог <c>org.json.JSONException</c>.</summary>
public sealed class JSONException(string message) : Exception(message);

/// <summary>
/// Порт <c>org.json</c> из Android libcore — с ЕГО мягкими правилами разбора (комментарии, одинарные кавычки,
/// строки без кавычек, «=»/«=&gt;», «;» как разделитель, хвост после первого значения игнорируется, hex/octal числа)
/// и приведениями <c>opt*</c> (<c>optString</c> числа → «443», <c>optInt</c> строки «443» → 443, <c>NULL</c> → "null").
/// Kotlin-код (VmessParser, JsonConfigParser) рассчитан именно на эти правила. Значения: string, int, long,
/// double, bool, <see cref="JSONObject"/>, <see cref="JSONArray"/>, <see cref="JSONObject.NULL"/>.
/// </summary>
internal static class JSON
{
    public static double CheckDouble(double d)
    {
        if (double.IsInfinity(d) || double.IsNaN(d)) throw new JSONException("Forbidden numeric value: " + JavaNumbers.JavaDoubleToString(d));
        return d;
    }

    public static bool? ToBoolean(object? value)
    {
        if (value is bool b) return b;
        if (value is string s)
        {
            if (string.Equals("true", s, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals("false", s, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return null;
    }

    public static double? ToDouble(object? value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        string s => JavaNumbers.ParseJavaDouble(s),
        _ => null,
    };

    public static int? ToInteger(object? value) => value switch
    {
        int i => i,
        long l => unchecked((int)l),
        double d => JavaNumbers.D2I(d),
        string s => JavaNumbers.ParseJavaDouble(s) is double d ? JavaNumbers.D2I(d) : null,
        _ => null,
    };

    public static long? ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        double d => JavaNumbers.D2L(d),
        string s => JavaNumbers.ParseJavaDouble(s) is double d ? JavaNumbers.D2L(d) : null,
        _ => null,
    };

    /// <summary>Java <c>String.valueOf(value)</c> для значений org.json.</summary>
    public static string? ToString(object? value) => value switch
    {
        null => null,
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => JavaNumbers.JavaDoubleToString(d),
        bool b => b ? "true" : "false",
        _ => value.ToString(),
    };

    internal static bool IsNumber(object? v) => v is int or long or double;

    internal static double DoubleValue(object v) => v switch { int i => i, long l => l, double d => d, _ => 0 };
}

public sealed class JSONObject
{
    private sealed class NullValue
    {
        public override bool Equals(object? o) => o == this || o == null;
        public override int GetHashCode() => 0;
        public override string ToString() => "null";
    }

    /// <summary><c>JSONObject.NULL</c>.</summary>
    public static readonly object NULL = new NullValue();

    // LinkedHashMap: порядок вставки; повторный put сохраняет исходную позицию ключа.
    private readonly List<string> nameOrder = [];
    private readonly Dictionary<string, object> map = new(StringComparer.Ordinal);

    public JSONObject() { }

    /// <summary><c>new JSONObject(String)</c>: первое значение должно быть объектом; хвост после него — игнорируется.</summary>
    public JSONObject(string json) : this(new JSONTokener(json)) { }

    internal JSONObject(JSONTokener readFrom)
    {
        object obj = readFrom.NextValue();
        if (obj is JSONObject o)
        {
            nameOrder = o.nameOrder;
            map = o.map;
        }
        else throw TypeMismatch(obj, "JSONObject");
    }

    internal static JSONException TypeMismatch(object? actual, string requiredType) =>
        actual == null
            ? new JSONException("Value is null.")
            : new JSONException("Value " + actual + " of type " + actual.GetType().Name + " cannot be converted to " + requiredType);

    public int length() => nameOrder.Count;

    public IEnumerable<string> keys() => nameOrder.ToArray();

    public JSONObject put(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (value == null)
        {
            if (map.Remove(name)) nameOrder.Remove(name);
            return this;
        }
        if (JSON.IsNumber(value)) JSON.CheckDouble(JSON.DoubleValue(value));
        if (!map.ContainsKey(name)) nameOrder.Add(name);
        map[name] = value;
        return this;
    }

    public bool has(string name) => map.ContainsKey(name);

    public bool isNull(string name)
    {
        var value = opt(name);
        return value == null || value == NULL;
    }

    public object? opt(string? name) => name != null && map.TryGetValue(name, out var v) ? v : null;

    public string optString(string name) => optString(name, "");

    public string optString(string name, string fallback) => JSON.ToString(opt(name)) ?? fallback;

    public int optInt(string name) => optInt(name, 0);

    public int optInt(string name, int fallback) => JSON.ToInteger(opt(name)) ?? fallback;

    public long optLong(string name, long fallback = 0L) => JSON.ToLong(opt(name)) ?? fallback;

    public double optDouble(string name, double fallback = double.NaN) => JSON.ToDouble(opt(name)) ?? fallback;

    public bool optBoolean(string name) => optBoolean(name, false);

    public bool optBoolean(string name, bool fallback) => JSON.ToBoolean(opt(name)) ?? fallback;

    public JSONObject? optJSONObject(string name) => opt(name) as JSONObject;

    public JSONArray? optJSONArray(string name) => opt(name) as JSONArray;

    /// <summary>Компактный JSON (как <c>JSONObject.toString()</c> Android: «/» экранируется как «\/»).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        WriteTo(sb);
        return sb.ToString();
    }

    internal void WriteTo(StringBuilder sb)
    {
        sb.Append('{');
        bool first = true;
        foreach (var k in nameOrder)
        {
            if (!first) sb.Append(',');
            first = false;
            JSONStringer.WriteString(sb, k);
            sb.Append(':');
            JSONStringer.WriteValue(sb, map[k]);
        }
        sb.Append('}');
    }

    /// <summary><c>JSONObject.numberToString</c>.</summary>
    public static string NumberToString(object number)
    {
        double doubleValue = JSON.DoubleValue(number);
        JSON.CheckDouble(doubleValue);
        if (number is double d && d == 0 && double.IsNegative(d)) return "-0";
        long longValue = number switch { int i => i, long l => l, double dd => JavaNumbers.D2L(dd), _ => 0 };
        if (doubleValue == longValue) return longValue.ToString(CultureInfo.InvariantCulture);
        return JSON.ToString(number)!;
    }
}

public sealed class JSONArray
{
    private readonly List<object?> values = [];

    public JSONArray() { }

    /// <summary><c>new JSONArray(String)</c>.</summary>
    public JSONArray(string json) : this(new JSONTokener(json)) { }

    internal JSONArray(JSONTokener readFrom)
    {
        object obj = readFrom.NextValue();
        if (obj is JSONArray a) values = a.values;
        else throw JSONObject.TypeMismatch(obj, "JSONArray");
    }

    public int length() => values.Count;

    /// <summary><c>put(Object)</c>: null допустим (так JSONTokener кладёт «пустые» элементы «[1,,2]»).</summary>
    public JSONArray put(object? value)
    {
        if (value != null && JSON.IsNumber(value)) JSON.CheckDouble(JSON.DoubleValue(value));
        values.Add(value);
        return this;
    }

    public object? opt(int index) => index < 0 || index >= values.Count ? null : values[index];

    public string optString(int index) => optString(index, "");

    public string optString(int index, string fallback) => JSON.ToString(opt(index)) ?? fallback;

    public int optInt(int index, int fallback = 0) => JSON.ToInteger(opt(index)) ?? fallback;

    public JSONObject? optJSONObject(int index) => opt(index) as JSONObject;

    public JSONArray? optJSONArray(int index) => opt(index) as JSONArray;

    public override string ToString()
    {
        var sb = new StringBuilder();
        WriteTo(sb);
        return sb.ToString();
    }

    internal void WriteTo(StringBuilder sb)
    {
        sb.Append('[');
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(',');
            JSONStringer.WriteValue(sb, values[i]);
        }
        sb.Append(']');
    }
}

/// <summary>Сериализация как у <c>org.json.JSONStringer</c> Android (без отступов).</summary>
internal static class JSONStringer
{
    public static void WriteValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case JSONArray a: a.WriteTo(sb); break;
            case JSONObject o: o.WriteTo(sb); break;
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case int or long or double: sb.Append(JSONObject.NumberToString(value)); break;
            default:
                if (value == JSONObject.NULL) sb.Append("null");
                else WriteString(sb, value.ToString()!);
                break;
        }
    }

    public static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': case '\\': case '/': sb.Append('\\').Append(c); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c <= 0x1F) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}

/// <summary>Порт <c>org.json.JSONTokener</c> Android.</summary>
internal sealed class JSONTokener
{
    private readonly string input;
    private int pos;

    public JSONTokener(string? input)
    {
        if (input != null && input.StartsWith('\uFEFF')) input = input.Substring(1);
        this.input = input ?? throw new NullReferenceException();
    }

    public object NextValue()
    {
        int c = NextCleanInternal();
        switch (c)
        {
            case -1: throw SyntaxError("End of input");
            case '{': return ReadObject();
            case '[': return ReadArray();
            case '\'':
            case '"': return NextString((char)c);
            default:
                pos--;
                return ReadLiteral();
        }
    }

    private int NextCleanInternal()
    {
        int inLength = input.Length;
        while (pos < inLength)
        {
            int c = input[pos++];
            switch (c)
            {
                case '\t': case ' ': case '\n': case '\r':
                    continue;
                case '/':
                    if (pos == inLength) return c;
                    char peek = input[pos];
                    switch (peek)
                    {
                        case '*':
                            // skip a /* c-style comment */
                            pos++;
                            int commentEnd = input.IndexOf("*/", pos, StringComparison.Ordinal);
                            if (commentEnd == -1) throw SyntaxError("Unterminated comment");
                            pos = commentEnd + 2;
                            continue;
                        case '/':
                            // skip a // end-of-line comment
                            pos++;
                            SkipToEndOfLine();
                            continue;
                        default:
                            return c;
                    }
                case '#':
                    // Skip a # hash end-of-line comment. The JSON RFC doesn't specify this behavior, but it's
                    // required to parse existing documents.
                    SkipToEndOfLine();
                    continue;
                default:
                    return c;
            }
        }
        return -1;
    }

    private void SkipToEndOfLine()
    {
        for (; pos < input.Length; pos++)
        {
            char c = input[pos];
            if (c == '\r' || c == '\n')
            {
                pos++;
                break;
            }
        }
    }

    public string NextString(char quote)
    {
        StringBuilder? builder = null;
        int start = pos;
        int inLength = input.Length;
        while (pos < inLength)
        {
            int c = input[pos++];
            if (c == quote)
            {
                if (builder == null) return input.Substring(start, pos - 1 - start);
                builder.Append(input, start, pos - 1 - start);
                return builder.ToString();
            }
            if (c == '\\')
            {
                if (pos == inLength) throw SyntaxError("Unterminated escape sequence");
                builder ??= new StringBuilder();
                builder.Append(input, start, pos - 1 - start);
                builder.Append(ReadEscapeCharacter());
                start = pos;
            }
        }
        throw SyntaxError("Unterminated string");
    }

    private char ReadEscapeCharacter()
    {
        char escaped = input[pos++];
        switch (escaped)
        {
            case 'u':
                if (pos + 4 > input.Length) throw SyntaxError("Unterminated escape sequence");
                string hex = input.Substring(pos, 4);
                pos += 4;
                // Java Integer.parseInt(hex, 16): допускает знак «+»/«-» ("-001" → -1 → (char)0xFFFF).
                long? v = JavaNumbers.ParseJavaLong(hex, 16);
                if (v == null) throw SyntaxError("Invalid escape sequence: " + hex);
                return unchecked((char)v.Value);
            case 't': return '\t';
            case 'b': return '\b';
            case 'n': return '\n';
            case 'r': return '\r';
            case 'f': return '\f';
            default: return escaped;
        }
    }

    private object ReadLiteral()
    {
        string literal = NextToInternal("{}[]/\\:,=;# \t\f");

        if (literal.Length == 0) throw SyntaxError("Expected literal value");
        if (string.Equals("null", literal, StringComparison.OrdinalIgnoreCase)) return JSONObject.NULL;
        if (string.Equals("true", literal, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals("false", literal, StringComparison.OrdinalIgnoreCase)) return false;

        // try to parse as an integral type...
        if (literal.IndexOf('.') == -1)
        {
            int @base = 10;
            string number = literal;
            if (number.StartsWith("0x", StringComparison.Ordinal) || number.StartsWith("0X", StringComparison.Ordinal))
            {
                number = number.Substring(2);
                @base = 16;
            }
            else if (number.StartsWith('0') && number.Length > 1)
            {
                number = number.Substring(1);
                @base = 8;
            }
            long? longValue = JavaNumbers.ParseJavaLong(number, @base);
            if (longValue != null)
            {
                if (longValue <= int.MaxValue && longValue >= int.MinValue) return (int)longValue.Value;
                return longValue.Value;
            }
        }

        // ...next try to parse as a floating point...
        double? dbl = JavaNumbers.ParseJavaDouble(literal);
        if (dbl != null) return dbl.Value;

        // ... finally give up. We have an unquoted string
        return literal;
    }

    private string NextToInternal(string excluded)
    {
        int start = pos;
        for (; pos < input.Length; pos++)
        {
            char c = input[pos];
            if (c == '\r' || c == '\n' || excluded.IndexOf(c) != -1) return input.Substring(start, pos - start);
        }
        return input.Substring(start);
    }

    private JSONObject ReadObject()
    {
        var result = new JSONObject();

        // Peek to see if this is the empty object.
        int first = NextCleanInternal();
        if (first == '}') return result;
        if (first != -1) pos--;

        while (true)
        {
            object name = NextValue();
            if (name is not string sname)
                throw SyntaxError("Names must be strings, but " + name + " is of type " + name.GetType().Name);

            // Expect the name/value separator to be either a colon ':', an equals sign '=', or an arrow "=>".
            int separator = NextCleanInternal();
            if (separator != ':' && separator != '=') throw SyntaxError("Expected ':' after " + sname);
            if (pos < input.Length && input[pos] == '>') pos++;

            result.put(sname, NextValue());

            switch (NextCleanInternal())
            {
                case '}': return result;
                case ';':
                case ',': continue;
                default: throw SyntaxError("Unterminated object");
            }
        }
    }

    private JSONArray ReadArray()
    {
        var result = new JSONArray();

        // to cover input that ends with ",]".
        bool hasTrailingSeparator = false;

        while (true)
        {
            switch (NextCleanInternal())
            {
                case -1: throw SyntaxError("Unterminated array");
                case ']':
                    if (hasTrailingSeparator) result.put(null);
                    return result;
                case ',':
                case ';':
                    // A separator without a value first means "null".
                    result.put(null);
                    hasTrailingSeparator = true;
                    continue;
                default:
                    pos--;
                    break;
            }

            result.put(NextValue());

            switch (NextCleanInternal())
            {
                case ']': return result;
                case ',':
                case ';':
                    hasTrailingSeparator = true;
                    continue;
                default: throw SyntaxError("Unterminated array");
            }
        }
    }

    private JSONException SyntaxError(string message) => new(message + " at character " + pos + " of " + input);
}
