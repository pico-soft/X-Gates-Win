using System.Text;
using XGates.Core.Compat;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests.Compat;

/// <summary>
/// Поведение Android, которое golden-эталон (он гоняется на JDK 21, а не на телефоне) проверить НЕ может:
/// в Android libcore java.net.URI свои правки (ojluni, «Android-changed»). Ожидания взяты из исходников AOSP
/// (libcore/ojluni/src/main/java/java/net/URI.java), а не из JDK.
/// </summary>
public class AndroidCompatTests
{
    private const string U = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Underscore_in_hostname_is_allowed_like_android()
    {
        // JDK: host == null (registry authority) → сервер Invalid; Android: «_» разрешён (b/17579865) → сервер жив.
        var u = new JavaUri($"vless://{U}@my_host.example.com:443?type=ws#n");
        Assert.Equal("my_host.example.com", u.Host);
        Assert.NotNull(VlessParser.parse($"vless://{U}@my_host.example.com:443?type=ws#n"));
    }

    [Fact]
    public void Percent_between_brackets_in_fragment_is_not_decoded_like_android()
    {
        // JDK 21 getFragment() декодирует и внутри [ ]; Android decode() — нет.
        var u = new JavaUri($"vless://{U}@h.example.com:443#[%41]%42");
        Assert.Equal("[%41]B", u.Fragment);
    }

    [Fact]
    public void Ipv4_octet_with_leading_zeros_like_android()
    {
        // Android scanByte: Integer.parseInt("0000001") ≤ 255 → октет принят (JDK 21 считает значащие цифры).
        var u = new JavaUri("http://0000001.2.3.4:80/");
        Assert.Equal("0000001.2.3.4", u.Host);
    }

    [Fact]
    public void Uri_rejects_space_and_bad_escape()
    {
        Assert.Throws<UriSyntaxException>(() => new JavaUri("vless://a@h.com:1#a b"));
        Assert.Throws<UriSyntaxException>(() => new JavaUri("vless://a@h.com:1?x=%zz"));
    }

    [Fact]
    public void Uri_registry_authority_gives_null_host()
    {
        var u = new JavaUri("vless://a@-bad-.example.com:443");
        Assert.Null(u.Host);
        Assert.Equal(-1, u.Port);
        Assert.Equal("a@-bad-.example.com:443", u.RawAuthority);
    }

    [Fact]
    public void Base64_skips_non_alphabet_chars_like_android()
    {
        // android.util.Base64 DEFAULT пропускает любой символ вне алфавита (переводы строк, «-», мусор).
        Assert.Equal("ABC", Encoding.UTF8.GetString(AndroidBase64.Decode("QU\nJ-D", AndroidBase64.DEFAULT)));
        Assert.Throws<ArgumentException>(() => AndroidBase64.Decode("QUJDR", AndroidBase64.DEFAULT));   // хвост из 1 символа
        Assert.Throws<ArgumentException>(() => AndroidBase64.Decode("QQ=x", AndroidBase64.DEFAULT));    // данные после одного «=»
        Assert.Equal("A", Encoding.UTF8.GetString(AndroidBase64.Decode("QQ", AndroidBase64.DEFAULT))); // паддинг необязателен
    }

    [Fact]
    public void Json_is_lenient_like_android()
    {
        var o = new JSONObject("{a: 1, 'b': \"x\"; c => 0x10, d: 010, e: 09, /* c */ f: 1.5} хвост игнорируется");
        Assert.Equal(1, o.optInt("a"));
        Assert.Equal("x", o.optString("b"));
        Assert.Equal(16, o.optInt("c"));
        Assert.Equal(8, o.optInt("d"));        // восьмеричное
        Assert.Equal("9.0", o.optString("e")); // "09" не octal → Double 9.0
        Assert.Equal("1.5", o.optString("f"));
        Assert.Equal("", o.optString("missing"));
        Assert.Equal(443, new JSONObject("{\"p\":\"443\"}").optInt("p"));
        Assert.Equal("null", new JSONObject("{\"p\":null}").optString("p"));
        Assert.Equal("{\"x\":\"\\/y\"}", new JSONObject("{\"o\":{\"x\":\"/y\"}}").optString("o"));
    }

    [Fact]
    public void Java_double_to_string()
    {
        Assert.Equal("443.0", JavaNumbers.JavaDoubleToString(443.0));
        Assert.Equal("1.0E7", JavaNumbers.JavaDoubleToString(1e7));
        Assert.Equal("1.5E-4", JavaNumbers.JavaDoubleToString(0.00015));
        Assert.Equal("0.001", JavaNumbers.JavaDoubleToString(0.001));
        Assert.Equal("-0.0", JavaNumbers.JavaDoubleToString(-0.0));
        Assert.Equal("1234567.125", JavaNumbers.JavaDoubleToString(1234567.125));
    }

    [Fact]
    public void Kotlin_text_semantics()
    {
        Assert.True("\u001C\u2028 ".IsBlank());    // Kotlin: \u001C — пробел
        Assert.False("\u0085".IsBlank());          // Kotlin: NEL — НЕ пробел (в .NET — пробел)
        Assert.Equal(443, "+443".ToIntOrNull());
        Assert.Null("443 ".ToIntOrNull());
        Assert.Null("²".ToIntOrNull());
        Assert.Null("2147483648".ToIntOrNull());
        Assert.Equal(int.MinValue, "-2147483648".ToIntOrNull());
        Assert.Equal("a\nb", "\n        a\n        b\n    ".TrimIndent());
        Assert.Equal("a\n  b", "  a\n    b".TrimIndent());
        Assert.True("VLESS://x".StartsWithIgnoreCase("vless://"));
    }
}
