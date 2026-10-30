using System.Text;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;

namespace PortBridge.Tests;

public class PacketizerTests
{
    private static (Packetizer, List<string>) Make(PacketMode mode, int max = 2000, int timeout = 50, string delim = "\\r\\n")
    {
        var output = new List<string>();
        var cfg = new EndpointConfig { PacketMode = mode, MaxPacketBytes = max, PacketTimeoutMs = timeout, Delimiter = delim };
        var p = new Packetizer(cfg, b => { lock (output) output.Add(Encoding.ASCII.GetString(b)); });
        return (p, output);
    }

    [Fact]
    public void Immediate_splits_at_max()
    {
        var (p, output) = Make(PacketMode.Immediate, max: 4);
        p.Push("abcdefghij"u8);
        Assert.Equal(["abcd", "efgh", "ij"], output);
    }

    [Fact]
    public void Delimiter_frames_across_reads()
    {
        var (p, output) = Make(PacketMode.Delimiter, timeout: 5000);
        p.Push("ST,GS,  12.5kg\r"u8);
        p.Push("\nST,GS,  12."u8);
        p.Push("6kg\r\nxx"u8);
        Assert.Equal(["ST,GS,  12.5kg\r\n", "ST,GS,  12.6kg\r\n"], output);
        p.Flush();
        Assert.Equal("xx", output[^1]);
    }

    [Fact]
    public async Task Idle_timeout_groups_bursts()
    {
        var (p, output) = Make(PacketMode.IdleTimeout, timeout: 80);
        p.Push("12"u8);
        p.Push("34"u8);
        p.Push("56"u8);
        Assert.Empty(output);
        await Task.Delay(400);
        Assert.Equal(["123456"], output);
    }

    [Fact]
    public void Idle_timeout_still_respects_max()
    {
        var (p, output) = Make(PacketMode.IdleTimeout, max: 3, timeout: 10_000);
        p.Push("abcdefg"u8);
        Assert.Equal(["abc", "def"], output);
    }
}
