using PortBridge.Core.Config;
using PortBridge.Core.Protocols;
using PortBridge.Core.Serial;

namespace PortBridge.Core.Engine;

/// <summary>
/// A physical COM port, or a virtual one backed by a com0com pair. Physical ports have a session while open.
/// Virtual ports have a session only while some application has the COM port open (detected via DCD = remote open).
/// Reopens automatically, so unplugging and replugging a USB adapter recovers by itself.
///
/// Session lifecycle: every session ends through <see cref="Session.Close"/> (overflow, write failure, kick, port
/// error, app closed the port, stop). The endpoint observes that through <see cref="Session.Closed"/> and, under one
/// lifecycle gate, retires it: waits for its writer to stop, unregisters it, then decides whether to open a
/// replacement on the same port handle or to reopen the port. A virtual port therefore recovers while the
/// application keeps COMx open.
/// </summary>
internal sealed class SerialEndpoint : Endpoint, IRfc2217Port
{
    /// <summary>Consecutive write failures (without any byte written in between) before the port handle is reopened.</summary>
    internal const int MaxWriteFaultsBeforeReopen = 2;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private volatile ISerialPort? _port;
    private volatile SerialSession? _session;
    private CancellationTokenSource? _portCts;
    private string? _reopenReason;
    private int _writeFaults;
    private SerialLineSettings _settings;
    private ModemLines _lines;
    private bool _break;
    private ControlLines? _appLines;
    private ControlLines? _publishedAppLines;
    private ModemLines? _remoteLines;
    private bool _peerActive;

    public SerialEndpoint(EndpointConfig cfg, BridgeContext ctx, IEndpointHost host) : base(cfg, ctx, host)
    {
        _settings = SerialLineSettings.From(cfg);
    }

    /// <summary>Per-session send queue limit (tests lower it to provoke overflow).</summary>
    internal long SendQueueLimit { get; set; } = 16 * 1024 * 1024;

    /// <summary>How long a retiring session's writer may take to stop before the port handle is reopened.</summary>
    internal TimeSpan WriterStopTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Delay before reopening the port; defaults to the configured reopen delay.</summary>
    internal int? ReopenDelayOverrideMs { get; set; }

    /// <summary>Delay before trying again after Windows refused the com0com driver: every try runs the driver installer,
    /// and nothing changes until an administrator trusts the publisher.</summary>
    internal TimeSpan DriverRefusedRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Line monitor period; defaults to 100 ms (virtual) / 250 ms (physical). Tests stretch it to control ordering.</summary>
    internal TimeSpan? LineMonitorInterval { get; set; }

    private bool IsVirtual => Config.Kind == EndpointKind.VirtualSerial;

    private bool Passthrough => IsVirtual && Config.ModemPassthrough;

    public ModemLines Lines => _lines;

    /// <summary>The application's DTR/RTS on a virtual port (com0com wires them to our DSR/CTS), while it has the port open.</summary>
    internal ControlLines? AppControlLines => Passthrough && _session is not null ? _appLines : null;

    public override IReadOnlyDictionary<string, string>? Details
    {
        get
        {
            var port = _port;
            if (port is null) return null;
            var d = new Dictionary<string, string>
            {
                ["lines"] = _lines.ToString(),
                ["settings"] = IsVirtual ? "" : _settings.ToString(),
                ["dtr"] = port.Dtr ? "on" : "off",
                ["rts"] = port.Rts ? "on" : "off",
            };
            if (Passthrough)
            {
                d["passthrough"] = "DTR/RTS → remote; remote CTS → CTS, remote DSR → DSR+DCD (RI, break and baud/format are not passed)";
                if (_remoteLines is { } r) d["remoteLines"] = r.ToString();
            }
            return d;
        }
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        var failures = new FailureTracker(Ctx, Label);
        while (!ct.IsCancellationRequested)
        {
            var reopenRequested = false;
            var driverRefused = false;
            try
            {
                string device;
                var settings = _settings;
                var dtr = Config.Dtr;
                if (IsVirtual)
                {
                    if (failures.Count == 0) SetStatus(EndpointStatus.Starting, $"Preparing virtual {Config.PortName}");
                    device = await Ctx.VirtualPorts.EnsurePairAsync(Config.PortName, Config.EmulateBaudRate, ct);
                    // The bridge side's speed doesn't matter for com0com unless baud emulation is on.
                    settings = new SerialLineSettings(115200, 8, SerialParity.None, SerialStopBits.One, FlowControl.None);
                    dtr = DesiredVirtualDtr();
                }
                else
                {
                    device = Config.PortName;
                }

                using var port = Ctx.SerialPorts.Open(device, settings, dtr, IsVirtual ? DesiredVirtualRts() : Config.Rts);
                using var portCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _reopenReason = null;
                _break = false; // a freshly opened handle has no break condition
                _portCts = portCts;
                _port = port;
                _writeFaults = 0;
                failures.Succeeded();

                if (IsVirtual)
                {
                    SetStatus(EndpointStatus.Waiting, $"{Config.PortName} ready — waiting for an application to open it");
                    Ctx.Info($"{Label}: virtual port {Config.PortName} is available", EventCategory.Connection);
                }
                else
                {
                    Ctx.Info($"{Label}: opened {Config.PortName} at {settings}", EventCategory.Connection);
                }
                await SyncSessionAsync(port, null, portCts.Token);

                var modem = MonitorLinesAsync(port, portCts.Token);
                try
                {
                    var buffer = new byte[16 * 1024];
                    while (!portCts.IsCancellationRequested)
                    {
                        var n = await port.ReadAsync(buffer, portCts.Token);
                        // An application may write right after opening, before the line monitor notices it;
                        // and a session that just died is replaced before its data is routed.
                        var s = _session;
                        if (s is null || s.IsClosed) s = await SyncSessionAsync(port, null, portCts.Token);
                        if (s is not null) Received(s, buffer.AsSpan(0, n));
                    }
                    portCts.Token.ThrowIfCancellationRequested(); // stop or reopen request
                }
                finally
                {
                    portCts.Cancel();
                    try { await modem; } catch { /* ignore */ }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (_reopenReason is not null)
            {
                reopenRequested = true;
                Ctx.Warn($"{Label}: reopening {Config.PortName} — {_reopenReason}", EventCategory.Connection);
                SetStatus(EndpointStatus.Waiting, $"Reopening {Config.PortName} — {_reopenReason}");
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                failures.Failed(msg);
                Fail(msg);
                driverRefused = ex is Com0ComDriverNotTrustedException;
            }
            finally
            {
                await RetireCurrentAsync(_port is null ? "port closed" : "port error");
                _port = null;
                _portCts = null;
            }

            if (!ct.IsCancellationRequested && !reopenRequested && Status != EndpointStatus.Error)
                SetStatus(EndpointStatus.Waiting, $"Reopening {Config.PortName}…");
            var delay = TimeSpan.FromMilliseconds(Math.Max(reopenRequested ? 0 : 500, ReopenDelayOverrideMs ?? Config.ReopenDelayMs));
            await Task.Delay(driverRefused ? DriverRefusedRetryDelay : delay, ct);
        }
        await RetireCurrentAsync("bridge stopped");
    }

    /// <summary>
    /// The single place where sessions are created and retired. Serialised by <see cref="_lifecycle"/>, so a closed
    /// session is replaced exactly once, never while its writer still runs, and callbacks for an old port are ignored.
    /// </summary>
    private async Task<SerialSession?> SyncSessionAsync(ISerialPort port, ModemLines? known, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(port, _port) || ct.IsCancellationRequested) return null; // stale callback

            if (_session is { IsClosed: true } dead)
            {
                var stopped = await RetireAsync(dead);
                if (!stopped)
                {
                    RequestReopen($"the writer of the previous session did not stop within {WriterStopTimeout.TotalSeconds:0.#} s");
                    return null;
                }
                if (dead.WriteFaulted)
                {
                    _writeFaults = dead.BytesOut > 0 ? 1 : _writeFaults + 1;
                    if (_writeFaults >= MaxWriteFaultsBeforeReopen)
                    {
                        RequestReopen($"writing failed {_writeFaults} times in a row ({dead.CloseReason})");
                        return null;
                    }
                }
                else
                {
                    _writeFaults = 0;
                }
                // Data already handed to the driver for the dead session is not wanted by its replacement.
                try { port.Purge(rx: false, tx: true); } catch { /* best effort */ }
                Ctx.Warn($"{Label}: session ended ({dead.CloseReason}); recovering", EventCategory.Connection);
            }

            bool wanted;
            ModemLines lines = default;
            if (!IsVirtual)
            {
                wanted = true;
            }
            else
            {
                try { lines = known ?? port.GetModemLines(); }
                catch { return _session; }
                wanted = lines.HasFlag(ModemLines.Dcd);
            }

            if (wanted && _session is null)
            {
                // The application's DTR/RTS snapshot exists before the session is published, so an RFC 2217 link that
                // negotiates before the line monitor's first sample still sends the application's real lines.
                if (IsVirtual) _appLines = AppLinesFrom(lines);
                OpenSession(port);
                if (IsVirtual)
                {
                    SetStatus(EndpointStatus.Connected, $"{Config.PortName} in use by an application");
                    Ctx.Info($"{Label}: an application opened {Config.PortName}", EventCategory.Connection);
                }
                else
                {
                    SetStatus(EndpointStatus.Connected, $"{Config.PortName} open · {_settings}");
                }
            }
            else if (!wanted && _session is { } s)
            {
                s.Close("application closed the port");
                if (!await RetireAsync(s))
                {
                    // Same rule as for any other dead session: a handle with old-session output still outstanding is
                    // never given to a new session; only disposing it ends that I/O.
                    RequestReopen($"the writer of the closed session did not stop within {WriterStopTimeout.TotalSeconds:0.#} s");
                    return null;
                }
                SetStatus(EndpointStatus.Waiting, $"{Config.PortName} ready — waiting for an application to open it");
                Ctx.Info($"{Label}: the application closed {Config.PortName}", EventCategory.Connection);
            }
            else if (_session is null && IsVirtual && Status == EndpointStatus.Connected)
            {
                SetStatus(EndpointStatus.Waiting, $"{Config.PortName} ready — waiting for an application to open it");
            }
            return _session;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void OpenSession(ISerialPort port)
    {
        var session = new SerialSession(this, port, IsVirtual ? $"{Config.PortName} (opened by an application)" : $"{Config.PortName} @ {_settings}", SendQueueLimit);
        _session = session;
        session.Start();
        AddSession(session);
        var portCt = _portCts?.Token ?? CancellationToken.None;
        // Owner observes terminal close: overflow, write failure or kick must not leave a dead session registered.
        _ = session.Closed.ContinueWith(_ => OnSessionEndedAsync(session, port, portCt), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _publishedAppLines = null;
        PublishAppLines();
    }

    private static ControlLines AppLinesFrom(ModemLines lines) => new(lines.HasFlag(ModemLines.Dsr), lines.HasFlag(ModemLines.Cts));

    /// <summary>Passes the application's DTR/RTS on whenever they differ from what was last passed on for this session
    /// (including the first time), so no state — e.g. both low from the start — is ever skipped.</summary>
    private void PublishAppLines()
    {
        if (!Passthrough || _session is null || _appLines is not { } l || l == _publishedAppLines) return;
        _publishedAppLines = l;
        Host.OnControlLinesChanged(this, l);
    }

    private async Task OnSessionEndedAsync(SerialSession session, ISerialPort port, CancellationToken portCt)
    {
        if (!ReferenceEquals(_session, session) || portCt.IsCancellationRequested) return;
        try { await SyncSessionAsync(port, null, portCt); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Ctx.Warn($"{Label}: session recovery failed: {ex.Message}"); }
    }

    /// <summary>Waits for the session's writer, then unregisters it. Caller holds the lifecycle gate.
    /// Returns false if the writer did not stop in time (outstanding I/O; only disposing the port ends it).</summary>
    private async Task<bool> RetireAsync(SerialSession s)
    {
        if (ReferenceEquals(_session, s)) _session = null;
        var stopped = await Task.WhenAny(s.WriterCompletion, Task.Delay(WriterStopTimeout)) == s.WriterCompletion;
        RemoveSession(s, s.CloseReason ?? "session ended");
        return stopped;
    }

    private async Task RetireCurrentAsync(string reason)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_session is not { } s) return;
            s.Close(reason);
            await RetireAsync(s);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void RequestReopen(string reason)
    {
        _reopenReason = reason;
        try { _portCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task MonitorLinesAsync(ISerialPort port, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(LineMonitorInterval ?? TimeSpan.FromMilliseconds(IsVirtual ? 100 : 250));
        var first = true;
        while (await timer.WaitForNextTickAsync(ct))
        {
            ModemLines lines;
            try { lines = port.GetModemLines(); }
            catch { return; }

            if (!IsVirtual && port.TakeLineErrors() is { } errors)
                Ctx.Warn($"{Label}: line errors on {Config.PortName}: {errors} (check baud rate / parity)");

            if (!first && lines == _lines) continue;
            var previous = _lines;
            _lines = lines;
            first = false;

            if (IsVirtual)
            {
                // com0com (default pin wiring): our DSR = application's DTR, our CTS = application's RTS.
                _appLines = AppLinesFrom(lines);
                await SyncSessionAsync(port, lines, ct);
                PublishAppLines();
            }
            else if (previous != lines)
            {
                Host.OnModemLinesChanged(this, lines);
            }
        }
    }

    // ---- What the application on a virtual port sees (com0com: its CTS = our RTS, its DSR and DCD = our DTR) ----

    private bool DesiredVirtualDtr()
    {
        var dtr = Config.SignalPeerState ? Host.IsPeerActive(this) : Config.Dtr;
        if (Passthrough && _remoteLines is { } r) dtr = dtr && r.HasFlag(ModemLines.Dsr);
        return dtr;
    }

    private bool DesiredVirtualRts() => Passthrough && _remoteLines is { } r ? r.HasFlag(ModemLines.Cts) : Config.Rts;

    private void ApplyVirtualOutputs()
    {
        if (_port is not { } p) return;
        p.Dtr = DesiredVirtualDtr();
        p.Rts = DesiredVirtualRts();
    }

    internal override void OnPeerActivityChanged(bool active)
    {
        _peerActive = active;
        if (!IsVirtual) return;
        // A lost link means the remote modem state is unknown: fall back to the configured idle state.
        if (!active) _remoteLines = null;
        if (Config.SignalPeerState || Passthrough) ApplyVirtualOutputs();
    }

    internal override void OnPeerModemLines(Endpoint source, ModemLines lines)
    {
        // Only modem state reported by an RFC 2217 server is mirrored; a physical COM port on the other side is not
        // passed through (documented limitation).
        if (!Passthrough || !_peerActive || source is not TcpClientEndpoint { Config.Rfc2217: true }) return;
        _remoteLines = lines;
        ApplyVirtualOutputs();
    }

    // ---- IRfc2217Port: lets RFC 2217 clients on the other side change this port's settings ----

    public SerialLineSettings Settings => _settings;

    public SerialLineSettings Apply(SerialLineSettings requested)
    {
        // A virtual port's line settings belong to the application on the other end of the pair: nothing to apply.
        if (IsVirtual) return _settings;
        var port = _port;
        if (port is null) return _settings = requested; // applied on next open
        port.Configure(requested);
        if (requested != _settings) Ctx.Info($"{Label}: remote client changed settings to {requested}");
        _settings = requested;
        SetStatus(EndpointStatus.Connected, $"{Config.PortName} open · {requested}");
        return requested;
    }

    public bool Dtr
    {
        get => _port?.Dtr ?? Config.Dtr;
        set { if (_port is { } p) p.Dtr = value; }
    }

    public bool Rts
    {
        get => _port?.Rts ?? Config.Rts;
        set { if (_port is { } p) p.Rts = value; }
    }

    /// <summary>Break state in effect. Only changes when the device accepted the change (a failing
    /// <see cref="ISerialPort.SetBreak"/> throws), so RFC 2217 replies never acknowledge a break that was not applied.</summary>
    public bool Break
    {
        get => _break;
        set
        {
            if (_port is not { } p) return; // no open port: nothing applied
            p.SetBreak(value);
            _break = value;
        }
    }

    public void Purge(bool rx, bool tx) => _port?.Purge(rx, tx);
}

internal sealed class SerialSession : Session
{
    private readonly ISerialPort _port;
    private readonly string _description;
    private readonly long _maxQueued;
    private readonly CancellationTokenSource _cts = new();
    private Task _writer = Task.CompletedTask;

    public SerialSession(Endpoint owner, ISerialPort port, string description, long maxQueued = 16 * 1024 * 1024) : base(owner)
    {
        _port = port;
        _description = description;
        _maxQueued = maxQueued;
    }

    public override string Remote => _description;

    protected override long MaxQueuedBytes => _maxQueued;

    public override Task WriterCompletion => _writer;

    public void Start() => _writer = Task.Run(async () =>
    {
        try { await WriterLoopAsync(_cts.Token); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        // Anything else — including a cancellation nobody asked for (the driver aborting the write) — is a write
        // failure: the session is closed and replaced instead of staying registered with a dead writer.
        catch (Exception ex) { WriterFailed(ex); }
    });

    protected override ValueTask WriteCoreAsync(byte[] data, CancellationToken ct) => _port.WriteAsync(data, ct);

    protected override void OnClose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}
