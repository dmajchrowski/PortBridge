using System.Text;
using PortBridge.Core.Engine;
using PortBridge.Core.Protocols;

namespace PortBridge.Tests;

public class TapDecoderTests
{
    // 7980NS (entry device) on the source side, the RPP transmitter (paging terminal) on the target side, as on TR01.
    private const TrafficDirection Entry = TrafficDirection.Forward, Terminal = TrafficDirection.Reverse;

    /// <summary>"&lt;CR&gt;" style names to bytes, so test data reads like the Monitor.</summary>
    private static byte[] B(string s)
    {
        var names = new Dictionary<string, byte>
        {
            ["STX"] = 0x02, ["ETX"] = 0x03, ["EOT"] = 0x04, ["ACK"] = 0x06, ["LF"] = 0x0A, ["CR"] = 0x0D,
            ["NAK"] = 0x15, ["ETB"] = 0x17, ["ESC"] = 0x1B, ["RS"] = 0x1E, ["US"] = 0x1F,
        };
        var bytes = new List<byte>();
        for (var i = 0; i < s.Length; i++)
        {
            var close = s[i] == '<' ? s.IndexOf('>', i) : -1;
            if (close > i && names.TryGetValue(s[(i + 1)..close], out var b)) { bytes.Add(b); i = close; }
            else bytes.Add((byte)s[i]);
        }
        return [.. bytes];
    }

    /// <summary>A page block with a correct checksum.</summary>
    private static string Page(string fields, string end = "<ETX>")
    {
        var body = B("<STX>" + fields + end);
        var sum = body.Sum(b => b & 0x7F) & 0xFFF;
        return $"<STX>{fields}{end}{(char)(0x30 + ((sum >> 8) & 0xF))}{(char)(0x30 + ((sum >> 4) & 0xF))}{(char)(0x30 + (sum & 0xF))}<CR>";
    }

    [Fact]
    public void Explains_the_7980NS_cycle_and_a_page_as_seen_on_TR01()
    {
        var d = new TapDecoder();
        Assert.Equal("Log off: no more pages", d.Decode(Entry, B("<EOT><CR>")));
        Assert.Equal("115 goodbye · Disconnect", d.Decode(Terminal, B("115<CR><ESC><EOT><CR>")));
        Assert.Equal("Wake-up", d.Decode(Entry, B("<CR>")));
        Assert.Equal("Logon prompt (ID=)", d.Decode(Terminal, B("ID=<CR>")));
        Assert.Equal("Log on: paging, category 1", d.Decode(Entry, B("<ESC>PG1<CR>")));
        Assert.Equal("Logon accepted · Go ahead: ready for pages", d.Decode(Terminal, B("<CR><ACK><CR><ESC>[p<CR>")));
        Assert.Equal("Page to 100: \"++ ICU_102:A WATER\" (checksum OK)", d.Decode(Entry, B("<STX>100<CR>++ ICU_102:A WATER<CR><ETX>517<CR>")));
        Assert.Equal("Page accepted", d.Decode(Terminal, B("<CR><ACK><CR>")));
    }

    [Fact]
    public void Shows_a_wrong_checksum()
    {
        var d = new TapDecoder();
        Assert.Equal("Page to 100: \"Hi\" (checksum WRONG: 999, should be 161)", d.Decode(Entry, B("<STX>100<CR>Hi<CR><ETX>999<CR>")));
        Assert.Equal("Not accepted: checksum or transmission error, send the block again (NAK)", d.Decode(Terminal, B("<CR><NAK><CR>")));
    }

    [Fact]
    public void Waits_for_the_rest_of_a_message_that_arrives_in_pieces()
    {
        var d = new TapDecoder();
        Assert.Equal("Log off: no more pages", d.Decode(Entry, B("<EOT><CR>")));
        var page = B(Page("100<CR>Room 12 call<CR>"));
        Assert.Null(d.Decode(Entry, page[..5]));
        Assert.Null(d.Decode(Entry, page[5..^1]));
        Assert.Equal("Page to 100: \"Room 12 call\" (checksum OK)", d.Decode(Entry, page[^1..]));

        // A USB adapter that delivers the reply byte by byte
        Assert.Null(d.Decode(Terminal, B("<CR>")));
        Assert.Null(d.Decode(Terminal, B("<ACK>")));
        Assert.Equal("Page accepted", d.Decode(Terminal, B("<CR>")));
    }

    [Fact]
    public void Follows_a_page_over_several_blocks()
    {
        var d = new TapDecoder();
        Assert.Equal("Page to 100: \"First part\" – continues in the next block (checksum OK)", d.Decode(Entry, B(Page("100<CR>First part<CR>", "<ETB>"))));
        Assert.Equal("Block accepted, send the next one", d.Decode(Terminal, B("<CR><ACK><CR>")));
        Assert.Equal("Page continued: \"second part\" (checksum OK)", d.Decode(Entry, B(Page("second part<CR>"))));
        Assert.Equal("Page accepted", d.Decode(Terminal, B("<CR><ACK><CR>")));
        Assert.Equal("Page to 200 (tone only) (checksum OK)", d.Decode(Entry, B(Page("200<CR><CR>"))));
    }

    [Theory]
    [InlineData("<CR><RS><CR>", "Page abandoned (RS): refused, e.g. an invalid pager ID")]
    [InlineData("211 Page sent<CR><CR><ACK><CR>", "211 page(s) sent: \"Page sent\" · Page accepted")]
    [InlineData("501<CR>", "501 error: time-out waiting for input")]
    [InlineData("599 Something odd<CR>", "599 error: \"Something odd\"")]
    [InlineData("110 1.8<CR>", "110 TAP version: \"1.8\"")]
    [InlineData("Processing - Please Wait<CR>", "Message: \"Processing - Please Wait\"")]
    public void Explains_terminal_replies_and_message_codes(string reply, string expected)
    {
        var d = new TapDecoder();
        d.Decode(Entry, B("<CR>"));
        d.Decode(Terminal, B("ID=<CR>")); // the terminal side is known from here on
        d.Decode(Entry, B(Page("100<CR>Hi<CR>")));
        Assert.Equal(expected, d.Decode(Terminal, B(reply)));
    }

    [Fact]
    public void Never_shows_the_logon_password_and_folds_repeated_wake_ups()
    {
        var d = new TapDecoder();
        Assert.Equal("Wake-up ×3", d.Decode(Entry, B("<CR><CR><CR>")));
        var text = d.Decode(Entry, B("<ESC>PG1secret<CR>"));
        Assert.Equal("Log on: paging, category 1, with password", text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public void Stays_quiet_on_traffic_that_is_not_TAP()
    {
        var d = new TapDecoder();
        Assert.Null(d.Decode(Entry, Encoding.ASCII.GetBytes("ST,GS,+0012.50kg\r\n")));
        Assert.Null(d.Decode(Terminal, [0x80, 0xFF, 0x00, 0x7F]));
        Assert.Null(d.Decode(Entry, new byte[70_000])); // a large binary chunk is passed over quickly
    }
}
