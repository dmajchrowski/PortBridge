using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;
using PortBridge.Core.Protocols;
using PortBridge.Core.Serial;

namespace PortBridge.Tests;

/// <summary>Worktree-owned scratch space (TEMP is redirected there by the repair runsettings).</summary>
internal static class Scratch
{
    public static string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-" + prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

internal static class Wait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met: " + (what ?? "?"));
            await Task.Delay(10);
        }
    }
}

/// <summary>Fake serial driver: the test plays the application (or device) on the other end.</summary>
internal sealed class FakeSerialPort : ISerialPort
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private int _concurrent;

    public FakeSerialPort(string device) => Device = device;

    public string Device { get; }
    /// <summary>Lines this side sees (for a com0com bridge side: DCD = app open, DSR = app DTR, CTS = app RTS).</summary>
    public volatile ModemLines LinesSeen;
    public readonly ConcurrentQueue<byte[]> Written = new();
    /// <summary>Called for every write before it "completes"; may block (honouring the token) or throw.</summary>
    public Func<byte[], CancellationToken, Task>? WriteBehavior;
    public int MaxConcurrentWrites;
    public int WriteCalls;
    public int TxPurges;
    public volatile bool Disposed;
    public readonly ConcurrentQueue<string> OutputChanges = new();
    private bool _dtr, _rts;

    public string WrittenText => Encoding.Latin1.GetString(Written.SelectMany(b => b).ToArray());

    public void Incoming(string text) => _incoming.Writer.TryWrite(Encoding.Latin1.GetBytes(text));

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var data = await _incoming.Reader.ReadAsync(ct);
        data.CopyTo(buffer);
        return data.Length;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Interlocked.Increment(ref WriteCalls);
        var now = Interlocked.Increment(ref _concurrent);
        lock (this) MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, now);
        try
        {
            if (WriteBehavior is { } b) await b(data.ToArray(), ct);
            // A real driver may complete a write successfully even though cancellation was requested meanwhile.
            if (!CompleteDespiteCancellation) ct.ThrowIfCancellationRequested();
            Written.Enqueue(data.ToArray());
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public ModemLines GetModemLines()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return LinesSeen;
    }

    public bool Dtr
    {
        get => _dtr;
        set { _dtr = value; OutputChanges.Enqueue($"DTR={value}"); }
    }

    public bool Rts
    {
        get => _rts;
        set { _rts = value; OutputChanges.Enqueue($"RTS={value}"); }
    }

    /// <summary>Writes that return from <see cref="WriteBehavior"/> count as completed even if cancellation was requested.</summary>
    public volatile bool CompleteDespiteCancellation;
    /// <summary>The driver refuses SET/CLEAR BREAK (EscapeCommFunction returning FALSE).</summary>
    public volatile bool BreakFails;
    public volatile bool BreakOn;

    public void SetBreak(bool on)
    {
        if (BreakFails) throw new IOException("SetCommBreak failed");
        BreakOn = on;
    }
    public void Purge(bool rx, bool tx) { if (tx) Interlocked.Increment(ref TxPurges); }
    public void Configure(SerialLineSettings settings) { }
    public string? TakeLineErrors() => null;

    public void Dispose()
    {
        Disposed = true;
        _incoming.Writer.TryComplete(new IOException("port closed"));
    }
}

internal sealed class FakeSerialPortFactory : ISerialPortFactory
{
    public readonly List<FakeSerialPort> Opened = [];
    /// <summary>Initial lines of each newly opened port.</summary>
    public ModemLines InitialLines = ModemLines.None;
    public Action<FakeSerialPort>? OnOpen;

    public FakeSerialPort? Current { get { lock (Opened) return Opened.LastOrDefault(); } }
    public int OpenCount { get { lock (Opened) return Opened.Count; } }

    public ISerialPort Open(string device, SerialLineSettings settings, bool dtr, bool rts)
    {
        var p = new FakeSerialPort(device) { LinesSeen = InitialLines };
        p.Dtr = dtr;
        p.Rts = rts;
        OnOpen?.Invoke(p);
        lock (Opened) Opened.Add(p);
        return p;
    }
}

internal sealed class FakeVirtualPorts : IVirtualPortProvider
{
    public int Calls;
    /// <summary>When set, creating the pair fails with this exception.</summary>
    public Func<Exception>? Fails;
    public Task<string> EnsurePairAsync(string comName, bool emulateBaud, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Fails is { } f ? Task.FromException<string>(f()) : Task.FromResult(@"\\.\CNCB99");
    }
}

/// <summary>A session whose writer records what it "transmits"; the writer can be held to simulate a slow receiver.</summary>
internal sealed class ProbeSession : Session
{
    private readonly long _max;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    public readonly ConcurrentQueue<byte[]> Transmitted = new();
    public TaskCompletionSource WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProbeSession(Endpoint owner, long maxQueued = 16 * 1024 * 1024, bool holdWrites = false) : base(owner)
    {
        _max = maxQueued;
        if (!holdWrites) WriteGate.TrySetResult();
        _writer = Task.Run(async () =>
        {
            try { await WriterLoopAsync(_cts.Token); }
            catch (OperationCanceledException) { }
        });
    }

    public override string Remote => "probe";
    protected override long MaxQueuedBytes => _max;
    public override Task WriterCompletion => _writer;
    public string Text => Encoding.Latin1.GetString(Transmitted.SelectMany(b => b).ToArray());
    public List<string> Chunks => Transmitted.Select(b => Encoding.Latin1.GetString(b)).ToList();

    protected override async ValueTask WriteCoreAsync(byte[] data, CancellationToken ct)
    {
        await WriteGate.Task.WaitAsync(ct);
        Transmitted.Enqueue(data);
    }

    protected override void OnClose() => _cts.Cancel();
}

/// <summary>An endpoint driven by the test: it opens/closes sessions and feeds received data on demand.</summary>
internal sealed class ProbeEndpoint(EndpointConfig cfg, BridgeContext ctx, IEndpointHost host) : Endpoint(cfg, ctx, host)
{
    protected override Task RunAsync(CancellationToken ct)
    {
        SetStatus(EndpointStatus.Waiting, "probe");
        return Task.Delay(Timeout.Infinite, ct);
    }

    public void Open(Session s) => AddSession(s);
    public void Drop(Session s, string reason = "probe closed") => RemoveSession(s, reason);
    public void Feed(Session s, string text) => Received(s, Encoding.Latin1.GetBytes(text));
}

internal sealed class NullHost : IEndpointHost
{
    public void OnSessionOpened(Endpoint endpoint, Session session) { }
    public void OnSessionClosed(Endpoint endpoint, Session session, string reason) { }
    public void OnData(Endpoint endpoint, Session session, ReadOnlySpan<byte> data) { }
    public bool IsPeerActive(Endpoint endpoint) => false;
    public void OnModemLinesChanged(Endpoint endpoint, ModemLines lines) { }
    public IRfc2217Port GetRfc2217Port(Endpoint endpoint) => new DetachedRfc2217Port(new SerialLineSettings(9600, 8, SerialParity.None, SerialStopBits.One, FlowControl.None));
    public void OnControlLinesChanged(Endpoint endpoint, ControlLines lines) { }
    public ControlLines? GetPeerControlLines(Endpoint endpoint) => null;
}

/// <summary>A real <see cref="Bridge"/> (production routing) whose serial I/O and selected endpoints are fakes.</summary>
internal sealed class BridgeHarness : IAsyncDisposable
{
    public readonly FakeSerialPortFactory Ports = new();
    public readonly FakeVirtualPorts Virtual = new();
    public readonly EventJournal Journal = new();
    public readonly TrafficHub Traffic = new();
    public Bridge Bridge { get; }

    /// <param name="probe">Which endpoint configs become <see cref="ProbeEndpoint"/>s.</param>
    public BridgeHarness(EndpointConfig primary, IEnumerable<EndpointConfig> targets, Func<EndpointConfig, bool> probe)
    {
        var cfg = new BridgeConfig { Name = "test", Primary = primary, Targets = targets.ToList() };
        var ctx = new BridgeContext(cfg.Id, cfg.Name, Journal, new Com0ComManager(), new CertificateProvider(), Ports, Virtual);
        Bridge = new Bridge(cfg, ctx, Traffic, (c, x, h) => probe(c) ? new ProbeEndpoint(c, x, h) : null);
    }

    public int JournalCount(string fragment) => Journal.Query(5000).Count(e => e.Message.Contains(fragment));

    public async ValueTask DisposeAsync()
    {
        await Bridge.StopAsync();
        await Traffic.DisposeAsync();
        await Journal.DisposeAsync();
    }
}
