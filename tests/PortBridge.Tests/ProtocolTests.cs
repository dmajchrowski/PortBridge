using System.Buffers;
using PortBridge.Core.Config;
using PortBridge.Core.Protocols;
using PortBridge.Core.Serial;
using PortBridge.Core.Util;

namespace PortBridge.Tests;

public class TelnetTests
{
    [Fact]
    public void Escapes_and_unescapes_0xFF()
    {
        var data = new byte[] { 1, 0xFF, 2 };
        var escaped = Telnet.EscapeData(data);
        Assert.Equal(new byte[] { 1, 0xFF, 0xFF, 2 }, escaped);

        var dec = new TelnetDecoder();
        var output = new ArrayBufferWriter<byte>();
        dec.Decode(escaped, output);
        Assert.Equal(data, output.WrittenSpan.ToArray());
    }

    [Fact]
    public void Separates_commands_split_across_reads()
    {
        var dec = new TelnetDecoder();
        var options = new List<(byte, byte)>();
        var subs = new List<(byte, byte[])>();
        dec.OnOption = (c, o) => options.Add((c, o));
        dec.OnSubnegotiation = (o, p) => subs.Add((o, p));
        var output = new ArrayBufferWriter<byte>();

        byte[] stream = [(byte)'A', 255, 251, 44, (byte)'B', 255, 250, 44, 1, 0, 0, 0x25, 0x80, 255, 240, (byte)'C'];
        foreach (var b in stream) dec.Decode([b], output); // worst case: one byte per read

        Assert.Equal("ABC", System.Text.Encoding.ASCII.GetString(output.WrittenSpan));
        Assert.Equal([(Telnet.WILL, (byte)44)], options);
        Assert.Single(subs);
        Assert.Equal(new byte[] { 1, 0, 0, 0x25, 0x80 }, subs[0].Item2);
    }

    [Fact]
    public void Negotiator_does_not_loop()
    {
        var sentA = new List<byte[]>();
        var sentB = new List<byte[]>();
        TelnetNegotiator? a = null, b = null;
        a = new TelnetNegotiator(x => sentA.Add(x), [Telnet.OptBinary], [Telnet.OptBinary]);
        b = new TelnetNegotiator(x => sentB.Add(x), [Telnet.OptBinary], [Telnet.OptBinary]);

        a.RequestWill(Telnet.OptBinary);
        // deliver a -> b, b -> a until quiet
        for (var round = 0; round < 10 && (sentA.Count > 0 || sentB.Count > 0); round++)
        {
            var fromA = sentA.ToList(); sentA.Clear();
            foreach (var m in fromA) b.Handle(m[1], m[2]);
            var fromB = sentB.ToList(); sentB.Clear();
            foreach (var m in fromB) a.Handle(m[1], m[2]);
        }
        Assert.Empty(sentA);
        Assert.Empty(sentB);
        Assert.True(a.Local(Telnet.OptBinary));
        Assert.True(b.Remote(Telnet.OptBinary));
    }
}

public class Rfc2217Tests
{
    private sealed class FakePort : IRfc2217Port
    {
        public SerialLineSettings Settings { get; set; } = new(9600, 8, SerialParity.None, SerialStopBits.One, FlowControl.None);
        public SerialLineSettings Apply(SerialLineSettings requested) => Settings = requested;
        public bool Dtr { get; set; }
        public bool Rts { get; set; }
        public bool Break { get; set; }
        public int Purges;
        public bool PurgeThrows;
        public void Purge(bool rx, bool tx)
        {
            Purges++;
            if (PurgeThrows) throw new ObjectDisposedException("port"); // the port is being closed
        }
        public ModemLines Lines => ModemLines.Cts;
    }

    private static readonly byte[] ClientWillComPort = [Telnet.IAC, Telnet.WILL, Telnet.OptComPort];

    private static byte[] ModemNotification(byte value) => Telnet.Subnegotiation(Telnet.OptComPort, [107, value]);

    [Fact]
    public void Client_settings_reach_the_server_port()
    {
        var port = new FakePort();
        Rfc2217Protocol? server = null, client = null;
        var toServer = new Queue<byte[]>();
        var toClient = new Queue<byte[]>();
        server = new Rfc2217Protocol(true, toClient.Enqueue, () => port);
        client = new Rfc2217Protocol(false, toServer.Enqueue, null,
            new SerialLineSettings(115200, 7, SerialParity.Even, SerialStopBits.Two, FlowControl.RtsCts), clientDtr: false, clientRts: true);

        server.Start();
        client.Start();
        var sink = new ArrayBufferWriter<byte>();
        for (var i = 0; i < 20 && (toServer.Count > 0 || toClient.Count > 0); i++)
        {
            while (toServer.Count > 0) server.Decode(toServer.Dequeue(), sink);
            while (toClient.Count > 0) client.Decode(toClient.Dequeue(), sink);
        }

        Assert.True(server.ComPortActive);
        Assert.Equal(115200, port.Settings.BaudRate);
        Assert.Equal(7, port.Settings.DataBits);
        Assert.Equal(SerialParity.Even, port.Settings.Parity);
        Assert.Equal(SerialStopBits.Two, port.Settings.StopBits);
        Assert.Equal(FlowControl.RtsCts, port.Settings.Flow);
        Assert.False(port.Dtr);
        Assert.Equal("115200-7-E-2", client.ConfirmedSettings);
        Assert.Equal("PortBridge", server.PeerSignature);
        Assert.Equal(0, sink.WrittenCount); // no control bytes leak into the data stream
    }

    [Fact]
    public void Modem_state_byte_has_deltas()
    {
        var v = Rfc2217Protocol.ModemByte(ModemLines.Cts | ModemLines.Dcd, ModemLines.Cts);
        Assert.Equal(0x10 | 0x80 | 0x08, v);
    }

    [Fact]
    public void Server_reports_modem_state_once_the_option_is_agreed()
    {
        // pyserial never sends SET-MODEMSTATE-MASK and cannot read any line before a first NOTIFY-MODEMSTATE.
        var sent = new List<byte[]>();
        var server = new Rfc2217Protocol(true, sent.Add, () => new FakePort());
        server.Start();
        server.Decode(ClientWillComPort, new ArrayBufferWriter<byte>());

        Assert.True(server.ComPortActive);
        Assert.Single(sent, m => m.SequenceEqual(ModemNotification(0x10)));
    }

    [Fact]
    public void Server_answers_a_modem_state_poll()
    {
        var sent = new List<byte[]>();
        var server = new Rfc2217Protocol(true, sent.Add, () => new FakePort());
        server.Start();
        server.Decode(ClientWillComPort, new ArrayBufferWriter<byte>());
        sent.Clear();

        // pyserial's poll_modem: a client NOTIFY-MODEMSTATE with no value.
        server.Decode([Telnet.IAC, Telnet.SB, Telnet.OptComPort, 7, Telnet.IAC, Telnet.SE], new ArrayBufferWriter<byte>());

        Assert.Equal(ModemNotification(0x10), Assert.Single(sent));
    }

    [Fact]
    public void Purge_is_acknowledged_even_while_the_port_closes()
    {
        var sent = new List<byte[]>();
        var server = new Rfc2217Protocol(true, sent.Add, () => new FakePort { PurgeThrows = true });

        server.Decode([Telnet.IAC, Telnet.SB, Telnet.OptComPort, 12, 2, Telnet.IAC, Telnet.SE], new ArrayBufferWriter<byte>());

        Assert.Equal(Telnet.Subnegotiation(Telnet.OptComPort, [112, 2]), sent.Last());
    }
}

public class UtilTests
{
    [Theory]
    [InlineData("\\r\\n", new byte[] { 13, 10 })]
    [InlineData("A\\x02B", new byte[] { 65, 2, 66 })]
    [InlineData("\\\\", new byte[] { 92 })]
    [InlineData("\\x", new byte[] { 92, 120 })]
    public void Unescape(string text, byte[] expected) => Assert.Equal(expected, DataFormat.Unescape(text));

    [Fact]
    public void Hex_parsing()
    {
        Assert.True(DataFormat.TryParseHex("01 0a FF", out var a));
        Assert.Equal(new byte[] { 1, 10, 255 }, a);
        Assert.True(DataFormat.TryParseHex("0x01,0x02", out var b));
        Assert.Equal(new byte[] { 1, 2 }, b);
        Assert.False(DataFormat.TryParseHex("0g", out _));
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.77", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("10.0.0.5", "10.0.0.5", true)]
    [InlineData("10.0.0.5", "::ffff:10.0.0.5", true)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    public void Ip_rules(string rule, string ip, bool expected)
    {
        Assert.True(IpRule.TryParse(rule, out var r));
        Assert.Equal(expected, r.Matches(System.Net.IPAddress.Parse(ip)));
    }

    [Fact]
    public void Validator_detects_port_conflicts()
    {
        var a = new BridgeConfig
        {
            Name = "A",
            Primary = new EndpointConfig { Kind = EndpointKind.Serial, PortName = "COM3" },
            Targets = [new EndpointConfig { Kind = EndpointKind.TcpServer, Port = 5000 }],
        };
        var b = new BridgeConfig
        {
            Name = "B",
            Primary = new EndpointConfig { Kind = EndpointKind.Serial, PortName = "com3" },
            Targets = [new EndpointConfig { Kind = EndpointKind.TcpServer, Port = 5000 }],
        };
        var errors = Validator.Validate(b, [a]);
        Assert.Contains(errors, e => e.Contains("COM3"));
        Assert.Contains(errors, e => e.Contains("5000"));
        Assert.Empty(Validator.Validate(a, []));
    }
}
