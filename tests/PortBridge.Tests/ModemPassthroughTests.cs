using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;
using PortBridge.Core.Protocols;
using PortBridge.Core.Serial;

namespace PortBridge.Tests;

/// <summary>The remote serial port behind an RFC 2217 server, recording what clients asked for.</summary>
internal sealed class RecordingPort : IRfc2217Port
{
    public SerialLineSettings Settings { get; set; } = new(9600, 8, SerialParity.None, SerialStopBits.One, FlowControl.None);
    public SerialLineSettings Apply(SerialLineSettings requested) => Settings = requested;
    public volatile bool DtrValue, RtsValue = true;
    /// <summary>When true the port ignores DTR changes (e.g. DTR is used for handshaking).</summary>
    public bool RefuseDtr;
    public bool Dtr { get => DtrValue; set { if (!RefuseDtr) DtrValue = value; } }
    public bool Rts { get => RtsValue; set => RtsValue = value; }
    public bool Break { get; set; }
    public void Purge(bool rx, bool tx) { }
    public ModemLines Lines { get; set; } = ModemLines.Cts | ModemLines.Dsr | ModemLines.Dcd;
}

/// <summary>Loopback-only stand-in for a device server: RFC 2217 (production server role) or raw TCP.</summary>
internal sealed class LoopbackDeviceServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _rfc2217;
    private Socket? _client;
    private Rfc2217Protocol? _protocol;
    public readonly RecordingPort Port = new();
    public readonly MemoryStream Data = new();
    public long RawBytes;
    public int Connections;

    public LoopbackDeviceServer(bool rfc2217)
    {
        _rfc2217 = rfc2217;
        _listener.Start();
        _ = AcceptLoop();
    }

    public int PortNumber => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try { s = await _listener.AcceptSocketAsync(_cts.Token); } catch { return; }
            _client = s;
            Interlocked.Increment(ref Connections);
            var protocol = _rfc2217 ? new Rfc2217Protocol(true, raw => { try { s.Send(raw); } catch { } }, () => Port) : null;
            _protocol = protocol;
            protocol?.Start();
            _ = ReadLoop(s, protocol);
        }
    }

    private async Task ReadLoop(Socket s, Rfc2217Protocol? protocol)
    {
        var buf = new byte[4096];
        var sink = new ArrayBufferWriter<byte>();
        while (true)
        {
            int n;
            try { n = await s.ReceiveAsync(buf, _cts.Token); } catch { return; }
            if (n == 0) return;
            Interlocked.Add(ref RawBytes, n);
            lock (Data)
            {
                if (protocol is null) Data.Write(buf, 0, n);
                else
                {
                    sink.ResetWrittenCount();
                    protocol.Decode(buf.AsSpan(0, n), sink);
                    Data.Write(sink.WrittenSpan);
                }
            }
        }
    }

    public string Text { get { lock (Data) return Encoding.Latin1.GetString(Data.ToArray()); } }

    public void NotifyModem(ModemLines lines)
    {
        Port.Lines = lines;
        _protocol?.NotifyModemLines(lines);
    }

    public void DropClient()
    {
        try { _client?.Shutdown(SocketShutdown.Both); } catch { }
        _client?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>F5 (new feature coverage plus the "never acknowledge what wasn't applied" regression).</summary>
public class ModemPassthroughTests
{
    private static readonly SerialLineSettings NoFlow = new(9600, 8, SerialParity.None, SerialStopBits.One, FlowControl.None);

    private static void Pump(Rfc2217Protocol server, Rfc2217Protocol client, Queue<byte[]> toServer, Queue<byte[]> toClient)
    {
        var sink = new ArrayBufferWriter<byte>();
        for (var i = 0; i < 20 && (toServer.Count > 0 || toClient.Count > 0); i++)
        {
            while (toServer.Count > 0) server.Decode(toServer.Dequeue(), sink);
            while (toClient.Count > 0) client.Decode(toClient.Dequeue(), sink);
        }
        Assert.Equal(0, sink.WrittenCount);
    }

    [Fact]
    public void Client_sends_application_lines_initially_then_only_changes()
    {
        var port = new RecordingPort { DtrValue = true, RtsValue = false };
        var app = new ControlLines(Dtr: false, Rts: true);
        Queue<byte[]> toServer = new(), toClient = new();
        var server = new Rfc2217Protocol(true, toClient.Enqueue, () => port);
        var client = new Rfc2217Protocol(false, toServer.Enqueue, null, NoFlow, clientDtr: true, clientRts: false, controlLines: () => app);
        server.Start();
        client.Start();
        Pump(server, client, toServer, toClient);
        Assert.False(port.Dtr); // the application's state, not the configured default
        Assert.True(port.Rts);
        Assert.False(client.ConfirmedDtr);

        client.SetControlLines(new ControlLines(false, true));
        Assert.Empty(toServer); // unchanged: nothing sent
        client.SetControlLines(new ControlLines(true, true));
        Assert.Single(toServer);
        Pump(server, client, toServer, toClient);
        Assert.True(port.Dtr);
        Assert.True(client.ConfirmedDtr);
    }

    [Fact]
    public void Lines_used_for_hardware_handshaking_are_not_passed()
    {
        Queue<byte[]> toServer = new();
        var client = new Rfc2217Protocol(false, toServer.Enqueue, null, NoFlow with { Flow = FlowControl.RtsCts },
            controlLines: () => new ControlLines(true, true));
        client.EnsureClientSettingsSent();
        toServer.Clear();
        client.SetControlLines(new ControlLines(true, false));
        Assert.Empty(toServer);
    }

    [Fact]
    public void Server_reports_current_modem_state_when_the_client_sets_its_mask()
    {
        var port = new RecordingPort { Lines = ModemLines.Cts | ModemLines.Dcd };
        Queue<byte[]> toServer = new(), toClient = new();
        var server = new Rfc2217Protocol(true, toClient.Enqueue, () => port);
        var client = new Rfc2217Protocol(false, toServer.Enqueue, null, NoFlow);
        ModemLines? seen = null;
        client.RemoteModemLines += l => seen = l;
        server.Start();
        client.Start();
        Pump(server, client, toServer, toClient);
        Assert.Equal(ModemLines.Cts | ModemLines.Dcd, seen);
    }

    private static byte ControlReply(IRfc2217Port port, byte request)
    {
        var sent = new List<byte[]>();
        var server = new Rfc2217Protocol(true, sent.Add, () => port);
        server.Decode([255, 250, 44, 5, request, 255, 240], new ArrayBufferWriter<byte>());
        var reply = sent.Last(); // IAC SB 44 105 <value> IAC SE
        Assert.Equal(105, reply[3]);
        return reply[4];
    }

    [Fact]
    public void Server_never_acknowledges_a_control_change_that_was_not_applied()
    {
        Assert.Equal(9, ControlReply(new RecordingPort { RefuseDtr = true }, 8));  // DTR ON refused → "DTR is OFF"
        Assert.Equal(8, ControlReply(new RecordingPort(), 8));
        Assert.Equal(14, ControlReply(new RecordingPort(), 15)); // inbound XON/XOFF can't be set separately: "none" in effect
        Assert.Equal(16, ControlReply(new RecordingPort { Settings = NoFlow with { Flow = FlowControl.RtsCts } }, 13));
        Assert.Equal(1, ControlReply(new RecordingPort(), 99)); // unknown request: current outbound flow, nothing changed
    }

    /// <summary>ER5: a failed native BREAK on a physical port, through the production SerialEndpoint adapter that the
    /// bridge hands to RFC 2217 server sessions, must be reported as not applied.</summary>
    [Fact]
    public async Task Failed_break_on_a_physical_port_is_not_acknowledged()
    {
        var serialCfg = new EndpointConfig { Kind = EndpointKind.Serial, PortName = "COM96", ReopenDelayMs = 100 };
        var serverCfg = new EndpointConfig { Kind = EndpointKind.TcpServer, Port = 1, Rfc2217 = true };
        await using var h = new BridgeHarness(serialCfg, [serverCfg], c => c.Kind == EndpointKind.TcpServer);
        await h.Bridge.StartAsync();
        var serial = (SerialEndpoint)h.Bridge.Primary;
        await Wait.Until(() => serial.Status == EndpointStatus.Connected, what: "port open");
        var adapter = ((IEndpointHost)h.Bridge).GetRfc2217Port(h.Bridge.Targets[0]);
        Assert.Same(serial, adapter);
        var port = h.Ports.Current!;

        port.BreakFails = true;
        Assert.Equal(6, ControlReply(adapter, 5)); // BREAK ON failed → "BREAK is OFF"
        Assert.False(adapter.Break);
        Assert.False(port.BreakOn);

        port.BreakFails = false;
        Assert.Equal(5, ControlReply(adapter, 5));
        Assert.True(port.BreakOn);

        port.BreakFails = true;
        Assert.Equal(5, ControlReply(adapter, 6)); // BREAK OFF failed → still on
        Assert.True(adapter.Break);
    }

    /// <summary>ER3: the application already has the virtual port open with DTR and RTS low (hidden side sees DCD
    /// only). RFC 2217 negotiation completes long before the line monitor's first sample; the remote must still end up
    /// with both lines low without any later application transition.</summary>
    [Fact]
    public async Task Lines_held_low_from_the_start_reach_the_remote_even_if_negotiation_beats_the_first_sample()
    {
        await using var device = new LoopbackDeviceServer(rfc2217: true);
        device.Port.DtrValue = true;
        device.Port.RtsValue = true;
        var client = new EndpointConfig { Kind = EndpointKind.TcpClient, Host = "127.0.0.1", Port = device.PortNumber, Rfc2217 = true, ReconnectDelayMs = 100 };
        await using var h = new BridgeHarness(VirtualCfg(), [client], _ => false);
        ((SerialEndpoint)h.Bridge.Primary).LineMonitorInterval = TimeSpan.FromSeconds(60); // no sample during the test
        h.Ports.InitialLines = ModemLines.Dcd; // app open, DTR off, RTS off
        await h.Bridge.StartAsync();
        await Wait.Until(() => h.Bridge.State == "connected", what: "connected");
        await Wait.Until(() => !device.Port.Dtr && !device.Port.Rts, 5000, "remote DTR and RTS low");
    }

    private static EndpointConfig VirtualCfg() => new() { Kind = EndpointKind.VirtualSerial, PortName = "COM97", ModemPassthrough = true };

    [Fact]
    public async Task Virtual_port_lines_follow_an_rfc2217_link_both_ways_and_resync_after_reconnect()
    {
        await using var device = new LoopbackDeviceServer(rfc2217: true);
        device.Port.DtrValue = false;
        device.Port.RtsValue = true;
        var client = new EndpointConfig { Kind = EndpointKind.TcpClient, Host = "127.0.0.1", Port = device.PortNumber, Rfc2217 = true, ReconnectDelayMs = 100 };
        await using var h = new BridgeHarness(VirtualCfg(), [client], _ => false);
        h.Ports.InitialLines = ModemLines.Dcd | ModemLines.Dsr; // application opened COM97 with DTR on, RTS off
        await h.Bridge.StartAsync();

        // Application → remote: initial state.
        await Wait.Until(() => device.Port.Dtr && !device.Port.Rts, what: "remote DTR on / RTS off");
        // Application raises RTS.
        h.Ports.Current!.LinesSeen = ModemLines.Dcd | ModemLines.Dsr | ModemLines.Cts;
        await Wait.Until(() => device.Port.Rts, what: "remote RTS on");

        // Remote → application (com0com: app CTS = our RTS, app DSR+DCD = our DTR).
        await Wait.Until(() => h.Ports.Current!.Rts && h.Ports.Current!.Dtr, what: "initial remote state CTS+DSR");
        device.NotifyModem(ModemLines.Dsr);
        await Wait.Until(() => !h.Ports.Current!.Rts && h.Ports.Current!.Dtr, what: "remote CTS off");
        device.NotifyModem(ModemLines.Cts);
        await Wait.Until(() => h.Ports.Current!.Rts && !h.Ports.Current!.Dtr, what: "remote DSR off");

        // Link lost: the remote state is unknown, so the application sees the configured idle state (DTR on) again;
        // after reconnecting, the remote port gets the application's current DTR and the server re-reports its lines.
        var mark = h.Ports.Current!.OutputChanges.Count;
        device.Port.DtrValue = false;
        device.DropClient();
        await Wait.Until(() => device.Connections >= 2 && device.Port.Dtr, 10_000, "resync after reconnect");
        Assert.Contains("DTR=True", h.Ports.Current!.OutputChanges.Skip(mark));
        await Wait.Until(() => h.Ports.Current!.Rts && !h.Ports.Current!.Dtr, what: "remote state re-reported");
        Assert.Equal(1, h.Ports.OpenCount);
    }

    [Fact]
    public async Task Raw_tcp_peer_gets_data_but_no_sideband_bytes_for_line_changes()
    {
        await using var device = new LoopbackDeviceServer(rfc2217: false);
        var client = new EndpointConfig { Kind = EndpointKind.TcpClient, Host = "127.0.0.1", Port = device.PortNumber, ReconnectDelayMs = 100 };
        await using var h = new BridgeHarness(VirtualCfg(), [client], _ => false);
        h.Ports.InitialLines = ModemLines.Dcd | ModemLines.Dsr;
        await h.Bridge.StartAsync();
        await Wait.Until(() => h.Bridge.State == "connected", what: "connected");

        for (var i = 0; i < 4; i++)
        {
            h.Ports.Current!.LinesSeen ^= ModemLines.Cts | ModemLines.Dsr;
            await Task.Delay(150); // let the 100 ms line monitor see each state
        }
        h.Ports.Current!.Incoming("hi");
        await Wait.Until(() => device.Text == "hi", what: "data arrives");
        await Task.Delay(300); // negative check
        Assert.Equal(2, Interlocked.Read(ref device.RawBytes));
    }
}
