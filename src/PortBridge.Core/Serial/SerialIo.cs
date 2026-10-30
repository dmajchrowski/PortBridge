namespace PortBridge.Core.Serial;

/// <summary>Output control lines driven by one side of a serial link.</summary>
public readonly record struct ControlLines(bool Dtr, bool Rts)
{
    public override string ToString() => $"DTR {(Dtr ? "on" : "off")}, RTS {(Rts ? "on" : "off")}";
}

/// <summary>An open serial device. Implemented by <see cref="Win32SerialPort"/>; tests substitute a fake driver.</summary>
public interface ISerialPort : IDisposable
{
    /// <summary>Waits until at least one byte has been read.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    ModemLines GetModemLines();
    bool Dtr { get; set; }
    bool Rts { get; set; }
    /// <summary>Sets or clears a break condition; throws if the device refuses.</summary>
    void SetBreak(bool on);
    void Purge(bool rx, bool tx);
    void Configure(SerialLineSettings settings);
    string? TakeLineErrors();
}

public interface ISerialPortFactory
{
    ISerialPort Open(string device, SerialLineSettings settings, bool dtr, bool rts);
}

public sealed class Win32SerialPortFactory : ISerialPortFactory
{
    public static readonly Win32SerialPortFactory Instance = new();
    public ISerialPort Open(string device, SerialLineSettings settings, bool dtr, bool rts) => Win32SerialPort.Open(device, settings, dtr, rts);
}

/// <summary>Provides the device a virtual COM endpoint opens (the hidden side of a com0com pair).</summary>
public interface IVirtualPortProvider
{
    Task<string> EnsurePairAsync(string comName, bool emulateBaud, CancellationToken ct);
}
