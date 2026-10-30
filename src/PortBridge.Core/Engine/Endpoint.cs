using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using PortBridge.Core.Config;
using PortBridge.Core.Protocols;
using PortBridge.Core.Serial;

namespace PortBridge.Core.Engine;

[JsonConverter(typeof(JsonStringEnumConverter<EndpointStatus>))]
public enum EndpointStatus { Stopped, Starting, Waiting, Connected, Error }

/// <summary>Callbacks from endpoints to the bridge that owns them.</summary>
internal interface IEndpointHost
{
    void OnSessionOpened(Endpoint endpoint, Session session);
    void OnSessionClosed(Endpoint endpoint, Session session, string reason);
    void OnData(Endpoint endpoint, Session session, ReadOnlySpan<byte> data);
    bool IsPeerActive(Endpoint endpoint);
    void OnModemLinesChanged(Endpoint endpoint, ModemLines lines);
    /// <summary>RFC 2217 server sessions control the serial port on the other side of the bridge.</summary>
    IRfc2217Port GetRfc2217Port(Endpoint endpoint);
    /// <summary>A virtual COM endpoint saw the application change its DTR/RTS outputs.</summary>
    void OnControlLinesChanged(Endpoint endpoint, ControlLines lines);
    /// <summary>Current application DTR/RTS of a virtual COM endpoint on the other side, if one is open.</summary>
    ControlLines? GetPeerControlLines(Endpoint endpoint);
}

/// <summary>Everything an endpoint needs from the outside world.</summary>
public sealed class BridgeContext(string bridgeId, string bridgeName, EventJournal journal, Com0ComManager com0com, CertificateProvider certs,
    ISerialPortFactory? serialPorts = null, IVirtualPortProvider? virtualPorts = null)
{
    public string BridgeId { get; } = bridgeId;
    public string BridgeName { get; } = bridgeName;
    public EventJournal Journal { get; } = journal;
    public Com0ComManager Com0Com { get; } = com0com;
    public CertificateProvider Certificates { get; } = certs;
    /// <summary>Opens serial devices (Win32 in production, a fake driver in tests).</summary>
    public ISerialPortFactory SerialPorts { get; } = serialPorts ?? Win32SerialPortFactory.Instance;
    /// <summary>Creates/validates the com0com pair behind a virtual COM endpoint.</summary>
    public IVirtualPortProvider VirtualPorts { get; } = virtualPorts ?? com0com;

    public void Info(string msg, string category = EventCategory.General) => Journal.Add(EventLevel.Info, msg, BridgeId, BridgeName, category);
    public void Warn(string msg, string category = EventCategory.General) => Journal.Add(EventLevel.Warning, msg, BridgeId, BridgeName, category);
    public void Error(string msg, string category = EventCategory.General) => Journal.Add(EventLevel.Error, msg, BridgeId, BridgeName, category);
}

/// <summary>One live connection of an endpoint: the open serial port, a TCP client, the UDP socket.
/// Outgoing data is queued and written by a dedicated loop so a slow peer never blocks the bridge.</summary>
public abstract class Session
{
    private static long _nextSeq;
    private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _queued, _bytesIn, _bytesOut;
    private int _closing;

    protected Session(Endpoint owner)
    {
        Owner = owner;
        LastReceive = ConnectedAt;
    }

    public long Seq { get; } = Interlocked.Increment(ref _nextSeq);
    public Endpoint Owner { get; }
    public abstract string Remote { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset LastReceive { get; private set; }
    public long BytesIn => Interlocked.Read(ref _bytesIn);
    /// <summary>Bytes actually written to the device/socket (not just queued).</summary>
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    /// <summary>Data accepted by <see cref="TrySend"/> that has not been written yet, plus any other queued output.</summary>
    public long QueuedBytes => Interlocked.Read(ref _queued) + OtherQueuedBytes;
    public bool IsClosed => Volatile.Read(ref _closing) != 0;
    public string? CloseReason { get; private set; }
    /// <summary>The session ended because writing to the device/socket failed (as opposed to overflow, kick or remote close).</summary>
    public bool WriteFaulted { get; private set; }

    /// <summary>Completes (never inline with the caller) once <see cref="Close"/> has run. Owners use it to retire the session.</summary>
    public Task Closed => _closed.Task;

    /// <summary>Completes when the writer loop has stopped, i.e. no more output I/O is outstanding.</summary>
    public virtual Task WriterCompletion => Task.CompletedTask;

    /// <summary>Extra information for the UI, e.g. TLS version or RFC 2217 state.</summary>
    public virtual string? Detail => null;

    protected virtual long MaxQueuedBytes => 16 * 1024 * 1024;

    /// <summary>Output queued outside the data queue (e.g. RFC 2217 control replies); counts against the same limit.</summary>
    protected virtual long OtherQueuedBytes => 0;

    /// <summary>Data bytes currently accepted but unwritten.</summary>
    protected long DataQueuedBytes => Interlocked.Read(ref _queued);

    internal void CountIn(int n)
    {
        Interlocked.Add(ref _bytesIn, n);
        LastReceive = DateTimeOffset.Now;
    }

    /// <summary>Serialises admission (data and any other queued output, e.g. RFC 2217 replies) so the check against
    /// the shared limit and the reservation happen atomically, and nothing is admitted once Close has begun.</summary>
    protected Lock AdmissionGate { get; } = new();

    /// <summary>Queues data for the writer. False means the bytes were NOT accepted (closed session or overflow).
    /// True only means queued: <see cref="BytesOut"/> grows when they are actually written.</summary>
    public bool TrySend(byte[] data)
    {
        if (data.Length == 0) return false;
        lock (AdmissionGate)
        {
            if (IsClosed) return false;
            if (DataQueuedBytes + OtherQueuedBytes + data.Length <= MaxQueuedBytes)
            {
                Interlocked.Add(ref _queued, data.Length);
                if (_queue.Writer.TryWrite(data)) return true;
                Interlocked.Add(ref _queued, -data.Length); // completed by Close
                return false;
            }
        }
        Close("send queue overflow — the receiver is not reading fast enough");
        return false;
    }

    protected async Task WriterLoopAsync(CancellationToken ct)
    {
        await foreach (var data in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await WriteCoreAsync(data, ct).ConfigureAwait(false);
            Interlocked.Add(ref _bytesOut, data.Length);
            // Only count bytes as gone from the queue once they were really written.
            Interlocked.Add(ref _queued, -data.Length);
        }
    }

    protected abstract ValueTask WriteCoreAsync(byte[] data, CancellationToken ct);

    /// <summary>Called by subclasses when the writer loop died on an I/O error.</summary>
    protected void WriterFailed(Exception ex)
    {
        if (!IsClosed) WriteFaulted = true;
        Close($"write failed: {ex.Message}");
    }

    /// <summary>Data that was accepted but will never be written. Only exact once the session is closed and its writer
    /// has settled (<see cref="WriterCompletion"/>): a write still in flight may yet complete. Returns it once.</summary>
    internal long TakeUnsentBytes()
    {
        lock (AdmissionGate) return Math.Max(0, Interlocked.Exchange(ref _queued, 0));
    }

    /// <summary>Ends the session. Safe to call more than once and from any thread.</summary>
    public void Close(string reason)
    {
        lock (AdmissionGate)
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0) return;
            CloseReason = reason;
            _queue.Writer.TryComplete();
        }
        try { OnClose(); }
        finally { _closed.TrySetResult(); }
    }

    protected abstract void OnClose();
}

public abstract class Endpoint
{
    private readonly ConcurrentDictionary<long, Session> _sessions = new();
    private CancellationTokenSource? _cts;
    private Task? _run;

    internal Endpoint(EndpointConfig config, BridgeContext ctx, IEndpointHost host)
    {
        Config = config;
        Ctx = ctx;
        Host = host;
    }

    public EndpointConfig Config { get; }
    public BridgeContext Ctx { get; }
    internal IEndpointHost Host { get; }

    public EndpointStatus Status { get; private set; } = EndpointStatus.Stopped;
    public string StatusText { get; private set; } = "Stopped";
    public string? LastError { get; private set; }
    public DateTimeOffset? LastErrorAt { get; private set; }

    public string Label => string.IsNullOrWhiteSpace(Config.Label) ? Config.Describe() : Config.Label!;

    public IReadOnlyList<Session> Sessions => _sessions.Values.OrderBy(s => s.Seq).ToList();
    public bool HasSession => !_sessions.IsEmpty;
    public int SessionCount => _sessions.Count;

    public Session? FirstSession
    {
        get
        {
            Session? first = null;
            foreach (var s in _sessions.Values)
                if (first is null || s.Seq < first.Seq) first = s;
            return first;
        }
    }

    /// <summary>Extra live state for the UI (modem lines, negotiated settings...).</summary>
    public virtual IReadOnlyDictionary<string, string>? Details => null;

    /// <summary>Test seam for controlled interleavings; called at named points ("session-registered"). Null in production.</summary>
    internal Action<string>? InterleavingHook { get; set; }

    public async Task StartAsync()
    {
        // A restart must not overlap the previous run (two loops would fight over one port/socket).
        if (_run is { IsCompleted: false } previous) await Task.WhenAny(previous, Task.Delay(5000));
        _cts = new CancellationTokenSource();
        SetStatus(EndpointStatus.Starting, "Starting");
        var token = _cts.Token;
        _run = Task.Run(async () =>
        {
            try { await RunAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Fail($"Stopped unexpectedly: {ex.Message}");
            }
        });
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        foreach (var s in _sessions.Values) s.Close("bridge stopped");
        if (_run is not null) await Task.WhenAny(_run, Task.Delay(5000));
        foreach (var s in _sessions.Values) RemoveSession(s, "bridge stopped");
        SetStatus(EndpointStatus.Stopped, "Stopped");
        _cts?.Dispose();
        _cts = null;
    }

    protected abstract Task RunAsync(CancellationToken ct);

    /// <summary>The other side of the bridge gained or lost its last session.</summary>
    internal virtual void OnPeerActivityChanged(bool active) { }

    /// <summary>Modem lines of a serial endpoint on the other side changed (used by RFC 2217 servers),
    /// or an RFC 2217 server reported its modem lines (used by virtual COM endpoints).</summary>
    internal virtual void OnPeerModemLines(Endpoint source, ModemLines lines) { }

    /// <summary>The application on a virtual COM port on the other side changed DTR/RTS (used by RFC 2217 clients).</summary>
    internal virtual void OnPeerControlLines(ControlLines lines) { }

    protected void SetStatus(EndpointStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }

    protected void Fail(string message)
    {
        LastError = message;
        LastErrorAt = DateTimeOffset.Now;
        SetStatus(EndpointStatus.Error, message);
    }

    /// <summary>Registers a session and lets the bridge admit it. The bridge only routes data to a session after
    /// <see cref="IEndpointHost.OnSessionOpened"/> admitted it, so registration and admission need not be atomic.</summary>
    protected void AddSession(Session s)
    {
        _sessions[s.Seq] = s;
        InterleavingHook?.Invoke("session-registered");
        Host.OnSessionOpened(this, s);
    }

    /// <summary>Unregisters and closes a session; the bridge hears about it exactly once.</summary>
    protected void RemoveSession(Session s, string reason)
    {
        if (_sessions.TryRemove(s.Seq, out _))
        {
            s.Close(reason);
            Host.OnSessionClosed(this, s, s.CloseReason ?? reason);
        }
    }

    internal bool IsRegistered(Session s) => _sessions.TryGetValue(s.Seq, out var r) && ReferenceEquals(r, s);

    protected void Received(Session s, ReadOnlySpan<byte> data)
    {
        // A closed session is being retired; its callbacks must not create new routing state.
        if (data.IsEmpty || s.IsClosed) return;
        s.CountIn(data.Length);
        Host.OnData(this, s, data);
    }

    public bool KickSession(long seq)
    {
        if (!_sessions.TryGetValue(seq, out var s)) return false;
        s.Close("disconnected by user");
        return true;
    }

    /// <summary>Retries forever; logs the first failure and then only when the message changes, to keep the journal readable.</summary>
    protected sealed class FailureTracker(BridgeContext ctx, string label)
    {
        private string? _last;
        private int _count;
        private DateTimeOffset _lastLogged;

        public int Count => _count;

        public void Failed(string message)
        {
            _count++;
            var now = DateTimeOffset.Now;
            if (message != _last || now - _lastLogged > TimeSpan.FromMinutes(10))
            {
                ctx.Error(_count > 1 ? $"{label}: {message} (attempt {_count})" : $"{label}: {message}");
                _last = message;
                _lastLogged = now;
            }
        }

        public void Succeeded()
        {
            _count = 0;
            _last = null;
        }
    }
}
