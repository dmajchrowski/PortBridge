using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using PortBridge.Core.Config;
using static PortBridge.Core.Serial.Native;

namespace PortBridge.Core.Serial;

public readonly record struct SerialLineSettings(int BaudRate, int DataBits, SerialParity Parity, SerialStopBits StopBits, FlowControl Flow)
{
    public static SerialLineSettings From(EndpointConfig c) => new(c.BaudRate, c.DataBits, c.Parity, c.StopBits, c.FlowControl);
    public override string ToString() => $"{BaudRate}-{DataBits}-{Parity.ToString()[0]}-{StopBits switch { SerialStopBits.Two => "2", SerialStopBits.OnePointFive => "1.5", _ => "1" }}";
}

[Flags]
public enum ModemLines { None = 0, Cts = 1, Dsr = 2, Ring = 4, Dcd = 8 }

/// <summary>
/// Thin overlapped-I/O wrapper over a Win32 COM device. Unlike System.IO.Ports it can open any device path
/// (the hidden com0com side is \\.\CNCBn), exposes all modem lines, and reads asynchronously without polling.
/// </summary>
public sealed class Win32SerialPort : ISerialPort
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private bool _dtr, _rts;

    public string DevicePath { get; }

    private Win32SerialPort(string path, SafeFileHandle handle)
    {
        DevicePath = path;
        _handle = handle;
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
    }

    public static string ToDevicePath(string name) => name.StartsWith(@"\\.\", StringComparison.Ordinal) ? name : @"\\.\" + name;

    public static Win32SerialPort Open(string name, SerialLineSettings settings, bool dtr, bool rts)
    {
        var path = ToDevicePath(name);
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(DescribeOpenError(name, err), err);
        }

        try
        {
            if (GetFileType(handle) != FILE_TYPE_CHAR) throw new IOException($"{name} is not a serial device.");
            SetupComm(handle, 65536, 65536);
            Apply(handle, settings, dtr, rts);

            // Return as soon as any byte is available; wake up at least once a second when idle.
            var timeouts = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = uint.MaxValue,
                ReadTotalTimeoutMultiplier = uint.MaxValue,
                ReadTotalTimeoutConstant = 1000,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = 0,
            };
            if (!SetCommTimeouts(handle, ref timeouts)) ThrowLast("SetCommTimeouts");

            // Drop anything left in the driver from a previous session (com0com keeps buffered data).
            PurgeComm(handle, PURGE_RXABORT | PURGE_RXCLEAR | PURGE_TXABORT | PURGE_TXCLEAR);
            ClearCommError(handle, out _, out _);

            var port = new Win32SerialPort(path, handle) { _dtr = dtr, _rts = rts };
            return port;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string DescribeOpenError(string name, int err) => err switch
    {
        2 or 3 => $"{name} does not exist (is the device plugged in?)",
        5 => $"{name} is in use by another program (access denied)",
        31 => $"{name} is not functioning (device error)",
        _ => $"Cannot open {name}: {new Win32Exception(err).Message}",
    };

    private static void ThrowLast(string what)
    {
        var err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        throw new IOException($"{what} failed: {new Win32Exception(err).Message}", err);
    }

    private static void SetBit(ref uint flags, int bit, bool on)
    {
        if (on) flags |= 1u << bit;
        else flags &= ~(1u << bit);
    }

    private static void Apply(SafeFileHandle handle, SerialLineSettings s, bool dtr, bool rts)
    {
        var dcb = new DCB { DCBlength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DCB>() };
        if (!GetCommState(handle, ref dcb)) ThrowLast("GetCommState");

        dcb.BaudRate = (uint)s.BaudRate;
        dcb.ByteSize = (byte)s.DataBits;
        dcb.Parity = s.Parity switch
        {
            SerialParity.Odd => 1,
            SerialParity.Even => 2,
            SerialParity.Mark => 3,
            SerialParity.Space => 4,
            _ => 0,
        };
        dcb.StopBits = s.StopBits switch
        {
            SerialStopBits.OnePointFive => 1,
            SerialStopBits.Two => 2,
            _ => 0,
        };

        var f = dcb.Flags;
        SetBit(ref f, fBinary, true);
        SetBit(ref f, fParity, s.Parity != SerialParity.None);
        SetBit(ref f, fOutxCtsFlow, s.Flow == FlowControl.RtsCts);
        SetBit(ref f, fOutxDsrFlow, s.Flow == FlowControl.DtrDsr);
        SetBit(ref f, fDsrSensitivity, false);
        SetBit(ref f, fTXContinueOnXoff, true);
        SetBit(ref f, fOutX, s.Flow == FlowControl.XonXoff);
        SetBit(ref f, fInX, s.Flow == FlowControl.XonXoff);
        SetBit(ref f, fErrorChar, false);
        SetBit(ref f, fNull, false);
        SetBit(ref f, fAbortOnError, false);

        // DTR: 0 disable, 1 enable, 2 handshake.  RTS: 0 disable, 1 enable, 2 handshake.
        var dtrMode = s.Flow == FlowControl.DtrDsr ? 2u : dtr ? 1u : 0u;
        var rtsMode = s.Flow == FlowControl.RtsCts ? 2u : rts ? 1u : 0u;
        f &= ~(3u << fDtrControl);
        f |= dtrMode << fDtrControl;
        f &= ~(3u << fRtsControl);
        f |= rtsMode << fRtsControl;
        dcb.Flags = f;

        dcb.XonChar = 0x11;
        dcb.XoffChar = 0x13;
        dcb.XonLim = 2048;
        dcb.XoffLim = 512;

        if (!SetCommState(handle, ref dcb))
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            throw new IOException($"The port rejected {s} ({new Win32Exception(err).Message})", err);
        }
    }

    public void Configure(SerialLineSettings settings) => Apply(_handle, settings, _dtr, _rts);

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        // A zero-byte result is the 1 s idle timeout, not end-of-stream.
        while (true)
        {
            var n = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n > 0) return n;
            ct.ThrowIfCancellationRequested();
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _stream.WriteAsync(data, ct);

    public ModemLines GetModemLines()
    {
        if (!GetCommModemStatus(_handle, out var s)) ThrowLast("GetCommModemStatus");
        var lines = ModemLines.None;
        if ((s & MS_CTS_ON) != 0) lines |= ModemLines.Cts;
        if ((s & MS_DSR_ON) != 0) lines |= ModemLines.Dsr;
        if ((s & MS_RING_ON) != 0) lines |= ModemLines.Ring;
        if ((s & MS_RLSD_ON) != 0) lines |= ModemLines.Dcd;
        return lines;
    }

    public bool Dtr
    {
        get => _dtr;
        set { if (EscapeCommFunction(_handle, value ? SETDTR : CLRDTR)) _dtr = value; }
    }

    public bool Rts
    {
        get => _rts;
        set { if (EscapeCommFunction(_handle, value ? SETRTS : CLRRTS)) _rts = value; }
    }

    public void SetBreak(bool on)
    {
        if (!EscapeCommFunction(_handle, on ? SETBREAK : CLRBREAK)) ThrowLast(on ? "SetCommBreak" : "ClearCommBreak");
    }

    /// <summary>Discards what the driver has buffered. The ABORT flags are not used: they would also cancel this port's own
    /// pending read, which the read loop takes for a port failure (RFC 2217 clients such as pyserial purge on every open).</summary>
    public void Purge(bool rx, bool tx)
    {
        uint flags = 0;
        if (rx) flags |= PURGE_RXCLEAR;
        if (tx) flags |= PURGE_TXCLEAR;
        PurgeComm(_handle, flags);
    }

    /// <summary>Line errors since the last call (framing, parity, overrun), or null if none.</summary>
    public string? TakeLineErrors()
    {
        if (!ClearCommError(_handle, out var e, out _) || e == 0) return null;
        var parts = new List<string>();
        if ((e & CE_FRAME) != 0) parts.Add("framing");
        if ((e & CE_RXPARITY) != 0) parts.Add("parity");
        if ((e & (CE_OVERRUN | CE_RXOVER)) != 0) parts.Add("overrun");
        if ((e & CE_BREAK) != 0) parts.Add("break");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { /* handle already gone (device unplugged) */ }
        _handle.Dispose();
    }
}
