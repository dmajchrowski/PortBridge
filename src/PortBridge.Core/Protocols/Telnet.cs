using System.Buffers;

namespace PortBridge.Core.Protocols;

public static class Telnet
{
    public const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
    public const byte OptBinary = 0, OptEcho = 1, OptSga = 3, OptComPort = 44;

    /// <summary>Doubles every 0xFF so payload bytes are not taken for commands.</summary>
    public static byte[] EscapeData(ReadOnlySpan<byte> data)
    {
        var count = 0;
        foreach (var b in data) if (b == IAC) count++;
        if (count == 0) return data.ToArray();
        var result = new byte[data.Length + count];
        var j = 0;
        foreach (var b in data)
        {
            result[j++] = b;
            if (b == IAC) result[j++] = IAC;
        }
        return result;
    }

    public static byte[] Option(byte command, byte option) => [IAC, command, option];

    public static byte[] Subnegotiation(byte option, ReadOnlySpan<byte> payload)
    {
        var buf = new List<byte>(payload.Length + 6) { IAC, SB, option };
        foreach (var b in payload)
        {
            buf.Add(b);
            if (b == IAC) buf.Add(IAC);
        }
        buf.Add(IAC);
        buf.Add(SE);
        return buf.ToArray();
    }
}

/// <summary>Streaming Telnet decoder: separates payload bytes from option negotiation and subnegotiation.
/// State is kept between calls so commands split across TCP reads are handled.</summary>
public sealed class TelnetDecoder
{
    private enum State { Data, Iac, Option, Sb, SbOption, SbData, SbIac }

    private State _state = State.Data;
    private byte _command;
    private byte _sbOption;
    private readonly List<byte> _sb = new(16);

    public Action<byte, byte>? OnOption;              // (command, option)
    public Action<byte, byte[]>? OnSubnegotiation;    // (option, payload)

    public void Decode(ReadOnlySpan<byte> input, IBufferWriter<byte> data)
    {
        var runStart = -1;
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            switch (_state)
            {
                case State.Data:
                    if (b == Telnet.IAC)
                    {
                        if (runStart >= 0) { data.Write(input[runStart..i]); runStart = -1; }
                        _state = State.Iac;
                    }
                    else if (runStart < 0) runStart = i;
                    break;

                case State.Iac:
                    switch (b)
                    {
                        case Telnet.IAC:
                            data.Write([Telnet.IAC]);
                            _state = State.Data;
                            break;
                        case Telnet.WILL or Telnet.WONT or Telnet.DO or Telnet.DONT:
                            _command = b;
                            _state = State.Option;
                            break;
                        case Telnet.SB:
                            _state = State.SbOption;
                            break;
                        default:
                            // NOP, GA, AYT, ... carry no payload.
                            _state = State.Data;
                            break;
                    }
                    break;

                case State.Option:
                    OnOption?.Invoke(_command, b);
                    _state = State.Data;
                    break;

                case State.SbOption:
                    _sbOption = b;
                    _sb.Clear();
                    _state = State.SbData;
                    break;

                case State.SbData:
                    if (b == Telnet.IAC) _state = State.SbIac;
                    else if (_sb.Count < 1024) _sb.Add(b);
                    break;

                case State.SbIac:
                    if (b == Telnet.SE)
                    {
                        OnSubnegotiation?.Invoke(_sbOption, _sb.ToArray());
                        _state = State.Data;
                    }
                    else
                    {
                        if (b == Telnet.IAC && _sb.Count < 1024) _sb.Add(Telnet.IAC);
                        _state = State.SbData;
                    }
                    break;
            }
        }
        if (runStart >= 0) data.Write(input[runStart..]);
    }
}

/// <summary>Option negotiation per RFC 1143 (simplified): answers requests without looping and tracks what is enabled.</summary>
public sealed class TelnetNegotiator
{
    private readonly bool[] _us = new bool[256], _him = new bool[256], _pendingWill = new bool[256], _pendingDo = new bool[256];
    private readonly HashSet<byte> _localOk, _remoteOk;
    private readonly Action<byte[]> _send;

    public event Action<byte>? Changed;

    public TelnetNegotiator(Action<byte[]> send, IEnumerable<byte> localSupported, IEnumerable<byte> remoteSupported)
    {
        _send = send;
        _localOk = [.. localSupported];
        _remoteOk = [.. remoteSupported];
    }

    public bool Local(byte opt) => _us[opt];
    public bool Remote(byte opt) => _him[opt];

    public void RequestWill(byte opt)
    {
        if (_us[opt] || _pendingWill[opt]) return;
        _pendingWill[opt] = true;
        _send(Telnet.Option(Telnet.WILL, opt));
    }

    public void RequestDo(byte opt)
    {
        if (_him[opt] || _pendingDo[opt]) return;
        _pendingDo[opt] = true;
        _send(Telnet.Option(Telnet.DO, opt));
    }

    public void Handle(byte command, byte opt)
    {
        switch (command)
        {
            case Telnet.WILL:
                if (_pendingDo[opt]) { _pendingDo[opt] = false; _him[opt] = true; Changed?.Invoke(opt); }
                else if (!_him[opt])
                {
                    if (_remoteOk.Contains(opt)) { _him[opt] = true; _send(Telnet.Option(Telnet.DO, opt)); Changed?.Invoke(opt); }
                    else _send(Telnet.Option(Telnet.DONT, opt));
                }
                break;
            case Telnet.WONT:
                if (_pendingDo[opt]) _pendingDo[opt] = false;
                else if (_him[opt]) { _him[opt] = false; _send(Telnet.Option(Telnet.DONT, opt)); Changed?.Invoke(opt); }
                break;
            case Telnet.DO:
                if (_pendingWill[opt]) { _pendingWill[opt] = false; _us[opt] = true; Changed?.Invoke(opt); }
                else if (!_us[opt])
                {
                    if (_localOk.Contains(opt)) { _us[opt] = true; _send(Telnet.Option(Telnet.WILL, opt)); Changed?.Invoke(opt); }
                    else _send(Telnet.Option(Telnet.WONT, opt));
                }
                break;
            case Telnet.DONT:
                if (_pendingWill[opt]) _pendingWill[opt] = false;
                else if (_us[opt]) { _us[opt] = false; _send(Telnet.Option(Telnet.WONT, opt)); Changed?.Invoke(opt); }
                break;
        }
    }
}
