using System.Text;
using XGates.Core.Xray.Link;

namespace XGates.Core.Tests;

/// <summary>
/// Находка порта №2: `vmess://&lt;base64&gt;=?хвост` (хвост без &amp;, ~1.5% реальных ссылок) раньше отбрасывался —
/// хвост уходил в Base64. Паритет с Kotlin держит golden (корпус vmess.txt), здесь — что сервер именно ПРИНЯТ.
/// </summary>
public class VmessTailTests
{
    private static string Link(string ps, bool pad, string tail)
    {
        var json = "{\"v\":\"2\",\"ps\":\"" + ps + "\",\"add\":\"example.com\",\"port\":\"443\",\"id\":\"11111111-2222-3333-4444-555555555555\",\"net\":\"ws\",\"path\":\"/w\",\"tls\":\"tls\"}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return "vmess://" + (pad ? b64 : b64.TrimEnd('=')) + tail;
    }

    [Theory]
    [InlineData(true, "?remarks=tail")]
    [InlineData(false, "?ed=2048")]
    public void Base64_with_query_tail_is_accepted(bool pad, string tail)
    {
        var p = VmessParser.parse(Link("tailed", pad, tail));
        Assert.NotNull(p);
        Assert.Equal("tailed", p!.remarks);
        Assert.Equal("example.com", p.address);
        Assert.Equal(443, p.port);
        Assert.Equal("/w", p.path);
    }

    [Fact]
    public void Query_format_with_ampersand_is_still_deferred()
    {
        // vmess-std (uuid@host:port?…&…) по-прежнему откладывается — правка его не задевает.
        Assert.Null(VmessParser.parse("vmess://11111111-2222-3333-4444-555555555555@example.com:443?type=ws&security=tls#std"));
    }
}
