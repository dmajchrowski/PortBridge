using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using PortBridge.Core.Config;
using PortBridge.Core.Serial;

namespace PortBridge.Core.Protocols;

/// <summary>What an RFC 2217 server session may control: normally the physical serial port on the other side of the bridge.</summary>
public interface IRfc2217Port
{
    SerialLineSettings Settings { get; }
    /// <summary>Applies new line settings and returns what the port actually uses.</summary>
    SerialLineSettings Apply(SerialLineSettings requested);
    bool Dtr { get; set; }
    bool Rts { get; set; }
    bool Break { get; set; }
    void Purge(bool rx, bool tx);
    ModemLines Lines { get; }
}

/// <summary>Stand-in when there is no physical port behind the server (e.g. TCP ⇄ TCP): remembers settings so clients
/// see consistent replies. Nothing is applied to any physical line.</summary>
public sealed class DetachedRfc2217Port(SerialLineSettings initial) : IRfc2217Port
{
    public SerialLineSettings Settings { get; private set; } = initial;
    public SerialLineSettings Apply(SerialLineSettings requested) => Settings = requested;
    public bool Dtr { get; set; } = true;
    public bool Rts { get; set; } = true;
    public bool Break { get; set; }
    public void Purge(bool rx, bool tx) { }
    public ModemLines Lines => ModemLines.Cts | ModemLines.Dsr | ModemLines.Dcd;
}

/// <summary>Telnet Com Port Control Option (RFC 2217) for one TCP connection, in either the server or the client role.</summary>
public sealed class Rfc2217Protocol
{
    private const byte SIGNATURE = 0, SET_BAUDRATE = 1, SET_DATASIZE = 2, SET_PARITY = 3, SET_STOPSIZE = 4, SET_CONTROL = 5,
        NOTIFY_LINESTATE = 6, NOTIFY_MODEMSTATE = 7, FLOWCONTROL_SUSPEND = 8, FLOWCONTROL_RESUME = 9,
        SET_LINESTATE_MASK = 10, SET_MODEMSTATE_MASK = 11, PURGE_DATA = 12;
    private const byte ServerOffset = 100;
    private const string OurSignature = "PortBridge";

    private readonly bool _server;
    private readonly Action<byte[]> _sendRaw;
    private readonly Func<IRfc2217Port> _port;
    private readonly TelnetDecoder _decoder = new();
    private readonly TelnetNegotiator _negotiator;
    private readonly SerialLineSettings _clientSettings;
    private readonly bool _clientDtr, _clientRts;
    private readonly Func<ControlLines?>? _controlLines;
    private readonly Lock _clientGate = new();
    private ControlLines? _sentLines;
    private byte _modemMask = 0xFF;
    private ModemLines _lastNotified = (ModemLines)(-1);
    private int _settingsSent;

    public bool Suspended { get; private set; }
    public string? PeerSignature { get; private set; }
    public string? ConfirmedSettings { get; private set; }

    private int _rBaud, _rData;
    private SerialParity _rParity;
    private SerialStopBits _rStop;

    /// <summary>Client role: modem lines reported by the remote server.</summary>
    public event Action<ModemLines>? RemoteModemLines;

    /// <summary>Client role: DTR/RTS as last confirmed by the server's SET-CONTROL replies.</summary>
    public bool? ConfirmedDtr { get; private set; }
    public bool? ConfirmedRts { get; private set; }

    /// <param name="controlLines">Client role: current local DTR/RTS (e.g. of the application on a virtual COM port);
    /// used instead of <paramref name="clientDtr"/>/<paramref name="clientRts"/> when it returns a value.</param>
    public Rfc2217Protocol(bool server, Action<byte[]> sendRaw, Func<IRfc2217Port>? port = null,
        SerialLineSettings clientSettings = default, bool clientDtr = true, bool clientRts = true,
        Func<ControlLines?>? controlLines = null)
    {
        _server = server;
        _sendRaw = sendRaw;
        _port = port ?? (() => new DetachedRfc2217Port(new SerialLineSettings(9600, 8, SerialParity.None, SerialStopBits.One, FlowControl.None)));
        _clientSettings = clientSettings;
        _clientDtr = clientDtr;
        _clientRts = clientRts;
        _controlLines = controlLines;

        byte[] supported = [Telnet.OptBinary, Telnet.OptSga, Telnet.OptComPort];
        _negotiator = new TelnetNegotiator(sendRaw, supported, supported);
        _negotiator.Changed += opt =>
        {
            if (opt != Telnet.OptComPort || !ComPortActive) return;
            // Server: report the current modem lines once, not only later changes. Clients that never send
            // SET-MODEMSTATE-MASK (pyserial) otherwise cannot read CTS/DSR/DCD/RI until a line happens to change.
            if (_server) NotifyModemLines(_port().Lines);
            else SendClientSettings();
        };
        _decoder.OnOption = _negotiator.Handle;
        _decoder.OnSubnegotiation = OnSub;
    }

    public bool ComPortActive => _negotiator.Local(Telnet.OptComPort) || _negotiator.Remote(Telnet.OptComPort);

    public void Start()
    {
        _negotiator.RequestWill(Telnet.OptBinary);
        _negotiator.RequestDo(Telnet.OptBinary);
        _negotiator.RequestWill(Telnet.OptSga);
        _negotiator.RequestDo(Telnet.OptSga);
        if (_server) _negotiator.RequestDo(Telnet.OptComPort);
        else _negotiator.RequestWill(Telnet.OptComPort);
    }

    /// <summary>Client role: push settings even if the server never confirmed the option (many device servers don't).</summary>
    public void EnsureClientSettingsSent()
    {
        if (!_server) SendClientSettings();
    }

    public void Decode(ReadOnlySpan<byte> raw, IBufferWriter<byte> data) => _decoder.Decode(raw, data);

    public byte[] Encode(ReadOnlySpan<byte> data) => Telnet.EscapeData(data);

    private void SendCommand(byte command, ReadOnlySpan<byte> value)
    {
        Span<byte> payload = stackalloc byte[value.Length + 1];
        payload[0] = command;
        value.CopyTo(payload[1..]);
        _sendRaw(Telnet.Subnegotiation(Telnet.OptComPort, payload));
    }

    private void Reply(byte command, ReadOnlySpan<byte> value) => SendCommand((byte)(command + ServerOffset), value);

    private static byte[] U32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v);
        return b;
    }

    // ----- value mapping -----
    private static byte ParityCode(SerialParity p) => p switch
    {
        SerialParity.Odd => 2, SerialParity.Even => 3, SerialParity.Mark => 4, SerialParity.Space => 5, _ => 1,
    };

    private static SerialParity? ParityFrom(byte b) => b switch
    {
        1 => SerialParity.None, 2 => SerialParity.Odd, 3 => SerialParity.Even, 4 => SerialParity.Mark, 5 => SerialParity.Space, _ => null,
    };

    private static byte StopCode(SerialStopBits s) => s switch { SerialStopBits.Two => 2, SerialStopBits.OnePointFive => 3, _ => 1 };

    private static SerialStopBits? StopFrom(byte b) => b switch
    {
        1 => SerialStopBits.One, 2 => SerialStopBits.Two, 3 => SerialStopBits.OnePointFive, _ => null,
    };

    private static byte FlowCode(FlowControl f) => f switch
    {
        FlowControl.XonXoff => 2, FlowControl.RtsCts => 3, FlowControl.DtrDsr => 19, _ => 1,
    };

    public static byte ModemByte(ModemLines lines, ModemLines previous)
    {
        byte v = 0;
        if (lines.HasFlag(ModemLines.Cts)) v |= 0x10;
        if (lines.HasFlag(ModemLines.Dsr)) v |= 0x20;
        if (lines.HasFlag(ModemLines.Ring)) v |= 0x40;
        if (lines.HasFlag(ModemLines.Dcd)) v |= 0x80;
        if ((int)previous >= 0)
        {
            var changed = lines ^ previous;
            if (changed.HasFlag(ModemLines.Cts)) v |= 0x01;
            if (changed.HasFlag(ModemLines.Dsr)) v |= 0x02;
            if (previous.HasFlag(ModemLines.Ring) && !lines.HasFlag(ModemLines.Ring)) v |= 0x04;
            if (changed.HasFlag(ModemLines.Dcd)) v |= 0x08;
        }
        return v;
    }

    private static ModemLines LinesFrom(byte v)
    {
        var l = ModemLines.None;
        if ((v & 0x10) != 0) l |= ModemLines.Cts;
        if ((v & 0x20) != 0) l |= ModemLines.Dsr;
        if ((v & 0x40) != 0) l |= ModemLines.Ring;
        if ((v & 0x80) != 0) l |= ModemLines.Dcd;
        return l;
    }

    // ----- server role -----

    /// <summary>Server role: tell the client the serial port's modem lines changed (respecting its mask).</summary>
    public void NotifyModemLines(ModemLines lines)
    {
        if (!_server || !ComPortActive || lines == _lastNotified) return;
        var value = (byte)(ModemByte(lines, _lastNotified) & _modemMask);
        _lastNotified = lines;
        Reply(NOTIFY_MODEMSTATE, [value]);
    }

    private void OnSub(byte option, byte[] payload)
    {
        if (option != Telnet.OptComPort || payload.Length == 0) return;
        var cmd = payload[0];
        var value = payload.AsSpan(1);
        if (_server && cmd < ServerOffset) HandleClientCommand(cmd, value);
        else if (!_server && cmd >= ServerOffset) HandleServerResponse((byte)(cmd - ServerOffset), value);
    }

    private void HandleClientCommand(byte cmd, ReadOnlySpan<byte> v)
    {
        var port = _port();
        var s = port.Settings;
        switch (cmd)
        {
            case SIGNATURE:
                if (v.Length == 0) Reply(SIGNATURE, Encoding.ASCII.GetBytes(OurSignature));
                else PeerSignature = Encoding.ASCII.GetString(v);
                break;

            case SET_BAUDRATE when v.Length >= 4:
            {
                var baud = (int)BinaryPrimitives.ReadUInt32BigEndian(v);
                if (baud > 0) s = TryApply(port, s with { BaudRate = baud });
                Reply(SET_BAUDRATE, U32(s.BaudRate));
                break;
            }
            case SET_DATASIZE when v.Length >= 1:
                if (v[0] is >= 5 and <= 8) s = TryApply(port, s with { DataBits = v[0] });
                Reply(SET_DATASIZE, [(byte)s.DataBits]);
                break;

            case SET_PARITY when v.Length >= 1:
                if (ParityFrom(v[0]) is { } p) s = TryApply(port, s with { Parity = p });
                Reply(SET_PARITY, [ParityCode(s.Parity)]);
                break;

            case SET_STOPSIZE when v.Length >= 1:
                if (StopFrom(v[0]) is { } st) s = TryApply(port, s with { StopBits = st });
                Reply(SET_STOPSIZE, [StopCode(s.StopBits)]);
                break;

            case SET_CONTROL when v.Length >= 1:
                Reply(SET_CONTROL, [HandleControl(port, v[0])]);
                break;

            case FLOWCONTROL_SUSPEND:
                Suspended = true;
                break;

            case FLOWCONTROL_RESUME:
                Suspended = false;
                break;

            case SET_LINESTATE_MASK when v.Length >= 1:
                Reply(SET_LINESTATE_MASK, [v[0]]);
                break;

            case SET_MODEMSTATE_MASK when v.Length >= 1:
                _modemMask = v[0];
                Reply(SET_MODEMSTATE_MASK, [v[0]]);
                // (Re)synchronise: a client that just set its mask gets the current state, not only later changes.
                _lastNotified = (ModemLines)(-1);
                NotifyModemLines(port.Lines);
                break;

            case NOTIFY_MODEMSTATE:
                // Not a client command in RFC 2217, but pyserial's poll_modem asks for the current state this way.
                _lastNotified = (ModemLines)(-1);
                NotifyModemLines(port.Lines);
                break;

            case PURGE_DATA when v.Length >= 1:
            {
                var what = v[0];
                Try(() => port.Purge(rx: what is 1 or 3, tx: what is 2 or 3));
                Reply(PURGE_DATA, [what]);
                break;
            }
        }
    }

    private static SerialLineSettings TryApply(IRfc2217Port port, SerialLineSettings requested)
    {
        try { return port.Apply(requested); }
        catch { return port.Settings; }
    }

    /// <summary>Applies a SET-CONTROL request and replies with the state the port actually has afterwards,
    /// so a request the port refused or that is not supported is never acknowledged as applied.</summary>
    private static byte HandleControl(IRfc2217Port port, byte c)
    {
        switch (c)
        {
            case 0: return FlowCode(port.Settings.Flow);
            case 1: return FlowCode(TryApply(port, port.Settings with { Flow = FlowControl.None }).Flow);
            case 2: return FlowCode(TryApply(port, port.Settings with { Flow = FlowControl.XonXoff }).Flow);
            case 3: return FlowCode(TryApply(port, port.Settings with { Flow = FlowControl.RtsCts }).Flow);
            case 19: return FlowCode(TryApply(port, port.Settings with { Flow = FlowControl.DtrDsr }).Flow);
            case 4: return port.Break ? (byte)5 : (byte)6;
            case 5: Try(() => port.Break = true); return port.Break ? (byte)5 : (byte)6;
            case 6: Try(() => port.Break = false); return port.Break ? (byte)5 : (byte)6;
            case 7: return port.Dtr ? (byte)8 : (byte)9;
            case 8: Try(() => port.Dtr = true); return port.Dtr ? (byte)8 : (byte)9;
            case 9: Try(() => port.Dtr = false); return port.Dtr ? (byte)8 : (byte)9;
            case 10: return port.Rts ? (byte)11 : (byte)12;
            case 11: Try(() => port.Rts = true); return port.Rts ? (byte)11 : (byte)12;
            case 12: Try(() => port.Rts = false); return port.Rts ? (byte)11 : (byte)12;
            // Inbound flow control follows the (outbound) flow setting and cannot be set separately:
            // report the inbound mode in effect instead of echoing the request.
            case >= 13 and <= 18: return InboundFlowCode(port.Settings.Flow);
            default: return FlowCode(port.Settings.Flow); // unknown request: report current state, change nothing
        }
    }

    private static void Try(Action a)
    {
        try { a(); } catch { /* the reply reports the resulting state */ }
    }

    private static byte InboundFlowCode(FlowControl f) => f switch
    {
        FlowControl.XonXoff => 15, FlowControl.RtsCts => 16, FlowControl.DtrDsr => 18, _ => 14,
    };

    // ----- client role -----

    private void SendClientSettings()
    {
        lock (_clientGate)
        {
            if (Interlocked.Exchange(ref _settingsSent, 1) == 1) return;
            var s = _clientSettings;
            var lines = _controlLines?.Invoke() ?? new ControlLines(_clientDtr, _clientRts);
            SendCommand(SIGNATURE, Encoding.ASCII.GetBytes(OurSignature));
            SendCommand(SET_BAUDRATE, U32(s.BaudRate));
            SendCommand(SET_DATASIZE, [(byte)s.DataBits]);
            SendCommand(SET_PARITY, [ParityCode(s.Parity)]);
            SendCommand(SET_STOPSIZE, [StopCode(s.StopBits)]);
            SendCommand(SET_CONTROL, [FlowCode(s.Flow)]);
            if (s.Flow != FlowControl.DtrDsr) SendCommand(SET_CONTROL, [lines.Dtr ? (byte)8 : (byte)9]);
            if (s.Flow != FlowControl.RtsCts) SendCommand(SET_CONTROL, [lines.Rts ? (byte)11 : (byte)12]);
            _sentLines = lines;
            SendCommand(SET_MODEMSTATE_MASK, [0xFF]);
        }
    }

    /// <summary>Client role: the local DTR/RTS changed (e.g. the application on a virtual COM port toggled them).
    /// Sends SET-CONTROL for what changed, once the initial settings went out; before that the change is picked up
    /// by the initial settings. Lines used for hardware handshaking by the configured flow control are not sent.</summary>
    public void SetControlLines(ControlLines lines)
    {
        if (_server) return;
        lock (_clientGate)
        {
            if (Volatile.Read(ref _settingsSent) == 0 || _sentLines is not { } sent) return;
            var flow = _clientSettings.Flow;
            if (lines.Dtr != sent.Dtr && flow != FlowControl.DtrDsr) SendCommand(SET_CONTROL, [lines.Dtr ? (byte)8 : (byte)9]);
            if (lines.Rts != sent.Rts && flow != FlowControl.RtsCts) SendCommand(SET_CONTROL, [lines.Rts ? (byte)11 : (byte)12]);
            _sentLines = lines;
        }
    }

    private void HandleServerResponse(byte cmd, ReadOnlySpan<byte> v)
    {
        switch (cmd)
        {
            case SIGNATURE:
                PeerSignature = Encoding.ASCII.GetString(v);
                break;
            case SET_BAUDRATE when v.Length >= 4:
                _rBaud = (int)BinaryPrimitives.ReadUInt32BigEndian(v);
                UpdateConfirmed();
                break;
            case SET_DATASIZE when v.Length >= 1:
                _rData = v[0];
                UpdateConfirmed();
                break;
            case SET_PARITY when v.Length >= 1:
                _rParity = ParityFrom(v[0]) ?? _rParity;
                UpdateConfirmed();
                break;
            case SET_STOPSIZE when v.Length >= 1:
                _rStop = StopFrom(v[0]) ?? _rStop;
                UpdateConfirmed();
                break;
            case NOTIFY_MODEMSTATE when v.Length >= 1:
                RemoteModemLines?.Invoke(LinesFrom(v[0]));
                break;
            case SET_CONTROL when v.Length >= 1:
                switch (v[0])
                {
                    case 8: ConfirmedDtr = true; break;
                    case 9: ConfirmedDtr = false; break;
                    case 11: ConfirmedRts = true; break;
                    case 12: ConfirmedRts = false; break;
                }
                break;
        }
    }

    private void UpdateConfirmed()
    {
        if (_rBaud > 0)
            ConfirmedSettings = new SerialLineSettings(_rBaud, _rData == 0 ? 8 : _rData, _rParity, _rStop, FlowControl.None).ToString();
    }
}
