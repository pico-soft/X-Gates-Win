using System.Text;
using XGates.Core.Compat;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests.Compat;

/// <summary>Подписка — чужие данные: глубокая вложенность не должна ронять процесс, переполнение стека в .NET не перехватить.</summary>
public sealed class DeepJsonTests
{
    private const int HOSTILE = 200_000;

    private static string Arrays(int depth) => "{\"a\":" + new string('[', depth) + new string(']', depth) + "}";

    private static string Objects(int depth) =>
        new StringBuilder().Insert(0, "{\"a\":", depth).Append("1").Append('}', depth).ToString();

    [Fact]
    public void Hostile_nesting_is_an_ordinary_parse_error()
    {
        Assert.Throws<JSONException>(() => new JSONObject(Arrays(HOSTILE)));
        Assert.Throws<JSONException>(() => new JSONObject(Objects(HOSTILE)));
        Assert.Throws<JSONException>(() => new JSONArray(new string('[', HOSTILE)));
    }

    [Fact]
    public void Link_parsers_survive_hostile_nesting()
    {
        Assert.Null(XhttpExtra.sanitize(Arrays(HOSTILE)));
        Assert.Null(XhttpExtra.sanitize(Objects(HOSTILE)));
        Assert.Null(VmessParser.parse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Objects(HOSTILE)))));
    }

    [Fact]
    public void Ordinary_nesting_still_parses()
    {
        Assert.Equal(1, new JSONObject(Objects(60)).optJSONObject("a")!.length());
        Assert.NotNull(new JSONObject(Arrays(60)).opt("a"));
    }
}
