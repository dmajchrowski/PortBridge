using System.Net;
using System.Net.Sockets;
using PortBridge.Core.Config;
using PortBridge.Core.Protocols;
using PortBridge.Core.Serial;
using PortBridge.Core.Util;

namespace PortBridge.Core.Engine;

/// <summary>Listens for TCP clients. Several clients may be connected at once; routing rules decide who talks.</summary>
internal sealed class TcpServerEndpoint : Endpoint
{
    private readonly List<IpRule> _allow;
    private int _pending;

    public TcpServerEndpoint(EndpointConfig cfg, BridgeContext ctx, IEndpointHost host) : base(cfg, ctx, host)
    {
        _allow = cfg.AllowedClients.Select(r => IpRule.TryParse(r, out var rule) ? rule : (IpRule?)null)
            .Where(r => r is not null).Select(r => r!.Value).ToList();
    }

    private string ListenText => $"Listening on {(string.IsNullOrWhiteSpace(Config.Host) ? "all interfaces" : Config.Host)}, port {Config.Port}";

    protected override async Task RunAsync(CancellationToken ct)
    {
        var failures = new FailureTracker(Ctx, Label);
        while (!ct.IsCancellationRequested)
        {
            Socket? listener = null;
            try
            {
                var bind = string.IsNullOrWhiteSpace(Config.Host) ? IPAddress.IPv6Any : IPAddress.Parse(Config.Host);
                listener = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                if (bind.Equals(IPAddress.IPv6Any)) listener.DualMode = true;
                // Prevent another process from binding the same port and stealing connections.
                listener.ExclusiveAddressUse = true;
                listener.Bind(new IPEndPoint(bind, Config.Port));
                listener.Listen(64);
                failures.Succeeded();
                UpdateStatus();
                Ctx.Info($"{Label}: {ListenText.ToLowerInvariant()}", EventCategory.Connection);

                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptAsync(ct);
                    _ = HandleClientAsync(client, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var msg = $"cannot listen on port {Config.Port}: {SocketTuning.Describe(ex)}";
                failures.Failed(msg);
                Fail(msg);
            }
            finally
            {
                listener?.Dispose();
            }
            await Task.Delay(5000, ct);
        }
    }

    private void UpdateStatus()
    {
        var n = SessionCount;
        if (n == 0) SetStatus(EndpointStatus.Waiting, ListenText);
        else SetStatus(EndpointStatus.Connected, n == 1 ? $"1 client connected · port {Config.Port}" : $"{n} clients connected · port {Config.Port}");
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken ct)
    {
        var remote = (socket.RemoteEndPoint as IPEndPoint)!;
        var addr = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        var who = $"{addr}:{remote.Port}";

        if (!IpRule.IsAllowed(addr, _allow))
        {
            Ctx.Warn($"{Label}: rejected {who} (not in the allowed client list)", EventCategory.Connection);
            socket.Dispose();
            return;
        }

        if (Config.MaxClients > 0 && SessionCount + Volatile.Read(ref _pending) >= Config.MaxClients)
        {
            Ctx.Warn($"{Label}: rejected {who} (limit of {Config.MaxClients} client{(Config.MaxClients == 1 ? "" : "s")} reached)", EventCategory.Connection);
            socket.Dispose();
            return;
        }

        Interlocked.Increment(ref _pending);
        TcpSession? session = null;
        try
        {
            SocketTuning.Apply(socket, Config);
            var (stream, tls) = await TlsHelper.ServerAsync(socket, Config, Ctx.Certificates, ct);
            session = new TcpSession(this, socket, stream, tls, ct);
            if (Config.Rfc2217)
            {
                var s = session;
                s.AttachRfc2217(new Rfc2217Protocol(server: true, raw => s.SendControl(raw), () => Host.GetRfc2217Port(this)));
            }
        }
        catch (Exception ex)
        {
            Ctx.Warn($"{Label}: connection from {who} failed: {SocketTuning.Describe(ex)}", EventCategory.Connection);
            socket.Dispose();
            return;
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }

        AddSession(session);
        UpdateStatus();
        Ctx.Info($"{Label}: client {who} connected{(session.Detail is { } d ? $" ({d})" : "")}", EventCategory.Connection);
        try
        {
            await session.RunAsync(data => Received(session, data));
        }
        finally
        {
            RemoveSession(session, "connection ended");
            if (!ct.IsCancellationRequested)
            {
                UpdateStatus();
                Ctx.Info($"{Label}: client {who} disconnected — {session.CloseReason}", EventCategory.Connection);
            }
        }
    }

    internal override void OnPeerModemLines(Endpoint source, ModemLines lines)
    {
        if (!Config.Rfc2217) return;
        foreach (var s in Sessions.OfType<TcpSession>()) s.NotifyModemLines(lines);
    }
}

/// <summary>Connects out to a TCP server and keeps reconnecting until stopped.</summary>
internal sealed class TcpClientEndpoint : Endpoint
{
    private readonly SemaphoreSlim _peerSignal = new(0, 1);
    private volatile TcpSession? _session;
    private ModemLines? _remoteLines;

    public TcpClientEndpoint(EndpointConfig cfg, BridgeContext ctx, IEndpointHost host) : base(cfg, ctx, host) { }

    private string Target => $"{Config.Host}:{Config.Port}";

    public override IReadOnlyDictionary<string, string>? Details =>
        _remoteLines is { } l ? new Dictionary<string, string> { ["remoteLines"] = l.ToString() } : null;

    protected override async Task RunAsync(CancellationToken ct)
    {
        var failures = new FailureTracker(Ctx, Label);
        while (!ct.IsCancellationRequested)
        {
            if (Config.ConnectOnDemand && !Host.IsPeerActive(this))
            {
                SetStatus(EndpointStatus.Waiting, "Idle — connects when the other side becomes active");
                await _peerSignal.WaitAsync(ct);
                continue;
            }

            // After a failure the endpoint stays in Error while it retries, instead of flipping to Waiting every attempt.
            if (failures.Count == 0) SetStatus(EndpointStatus.Waiting, $"Connecting to {Target}");
            else SetStatus(EndpointStatus.Error, $"{LastError} — retrying (attempt {failures.Count + 1})");
            Socket? socket = null;
            try
            {
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(Math.Max(500, Config.ConnectTimeoutMs));
                    try
                    {
                        await socket.ConnectAsync(Config.Host, Config.Port, timeout.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"no answer within {Config.ConnectTimeoutMs} ms");
                    }
                }
                SocketTuning.Apply(socket, Config);
                var (stream, tls) = await TlsHelper.ClientAsync(socket, Config, ct);
                var session = new TcpSession(this, socket, stream, tls, ct);
                if (Config.Rfc2217)
                {
                    // Initial DTR/RTS: those of the application on a virtual COM port on the other side when it has
                    // passthrough enabled and the port open; otherwise the configured values.
                    var rfc = new Rfc2217Protocol(server: false, raw => session.SendControl(raw), null, SerialLineSettings.From(Config),
                        Config.Dtr, Config.Rts, controlLines: () => Host.GetPeerControlLines(this));
                    rfc.RemoteModemLines += lines =>
                    {
                        _remoteLines = lines;
                        Host.OnModemLinesChanged(this, lines);
                    };
                    session.AttachRfc2217(rfc);
                }

                socket = null; // owned by the session now
                var attempts = failures.Count;
                failures.Succeeded();
                _session = session;
                AddSession(session);
                SetStatus(EndpointStatus.Connected, $"Connected to {Target}{(tls.Length > 0 ? $" ({tls})" : "")}");
                Ctx.Info(attempts > 0 ? $"{Label}: connected after {attempts + 1} attempts" : $"{Label}: connected", EventCategory.Connection);

                await session.RunAsync(data => Received(session, data));

                _session = null;
                RemoveSession(session, "connection ended");
                if (ct.IsCancellationRequested) break;
                Ctx.Warn($"{Label}: disconnected — {session.CloseReason}", EventCategory.Connection);
                SetStatus(EndpointStatus.Waiting, $"Disconnected — {session.CloseReason}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var msg = $"cannot connect to {Target}: {SocketTuning.Describe(ex)}";
                failures.Failed(msg);
                Fail(msg);
            }
            finally
            {
                socket?.Dispose();
            }

            if (Config.ConnectOnDemand && !Host.IsPeerActive(this)) continue;
            await Task.Delay(Math.Max(100, Config.ReconnectDelayMs), ct);
        }
    }

    /// <summary>Application DTR/RTS changes are sent as RFC 2217 SET-CONTROL; a raw TCP peer gets no sideband data.</summary>
    internal override void OnPeerControlLines(ControlLines lines)
    {
        if (Config.Rfc2217) _session?.Rfc2217?.SetControlLines(lines);
    }

    internal override void OnPeerActivityChanged(bool active)
    {
        if (!Config.ConnectOnDemand) return;
        if (active)
        {
            if (_peerSignal.CurrentCount == 0) try { _peerSignal.Release(); } catch (SemaphoreFullException) { }
        }
        else
        {
            _session?.Close("the other side became inactive (connect on demand)");
        }
    }
}

/// <summary>UDP socket. Sends to the configured remote, or replies to whoever sent last when no remote is set.</summary>
internal sealed class UdpEndpoint : Endpoint
{
    public UdpEndpoint(EndpointConfig cfg, BridgeContext ctx, IEndpointHost host) : base(cfg, ctx, host) { }

    protected override async Task RunAsync(CancellationToken ct)
    {
        var failures = new FailureTracker(Ctx, Label);
        while (!ct.IsCancellationRequested)
        {
            UdpSession? session = null;
            try
            {
                var localPort = Config.LocalPort > 0 ? Config.LocalPort : Config.Port;
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.ExclusiveAddressUse = true;
                // Stop Windows from failing the next receive with WSAECONNRESET after an ICMP port-unreachable.
                const int SIO_UDP_CONNRESET = -1744830452;
                socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
                if (Config.Broadcast) socket.EnableBroadcast = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, localPort));

                IPEndPoint? remote = null;
                if (!string.IsNullOrWhiteSpace(Config.Host))
                {
                    var addrs = await Dns.GetHostAddressesAsync(Config.Host, AddressFamily.InterNetwork, ct);
                    if (addrs.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                    remote = new IPEndPoint(addrs[0], Config.Port);
                }

                session = new UdpSession(this, socket, remote);
                failures.Succeeded();
                AddSession(session);
                SetStatus(EndpointStatus.Connected, remote is null
                    ? $"Bound to UDP port {localPort} · replies to last sender"
                    : $"UDP port {localPort} ⇄ {remote}");
                Ctx.Info($"{Label}: bound to UDP port {localPort}", EventCategory.Connection);

                var writer = session.StartWriter(ct);
                // A session that ends on its own (writer failure, overflow, kick) must be retired and replaced,
                // not left registered while the receive loop keeps feeding it.
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var closedSession = session;
                _ = closedSession.Closed.ContinueWith(_ => { try { sessionCts.Cancel(); } catch (ObjectDisposedException) { } },
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                var buffer = new byte[65536];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                while (!sessionCts.IsCancellationRequested)
                {
                    var r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, from, sessionCts.Token);
                    session.LastSender = (IPEndPoint)r.RemoteEndPoint;
                    Received(session, buffer.AsSpan(0, r.ReceivedBytes));
                }
                await writer;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (session is { IsClosed: true })
            {
                Ctx.Warn($"{Label}: session ended ({session.CloseReason}); rebinding", EventCategory.Connection);
                SetStatus(EndpointStatus.Waiting, $"Rebinding — {session.CloseReason}");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP "port unreachable" from a previous send; the socket is still fine but we restart cleanly.
                continue;
            }
            catch (Exception ex)
            {
                var msg = SocketTuning.Describe(ex);
                failures.Failed(msg);
                Fail(msg);
            }
            finally
            {
                if (session is not null) RemoveSession(session, "socket closed");
            }
            await Task.Delay(Math.Max(1000, Config.ReconnectDelayMs), ct);
        }
    }
}

internal sealed class UdpSession(Endpoint owner, Socket socket, IPEndPoint? remote) : Session(owner)
{
    private const int MaxDatagram = 65507;
    private CancellationTokenSource? _cts;
    private Task _writer = Task.CompletedTask;

    public IPEndPoint? LastSender { get; set; }

    public override string Remote => remote?.ToString() ?? LastSender?.ToString() ?? "(waiting for first datagram)";

    public override Task WriterCompletion => _writer;

    public Task StartWriter(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return _writer = Task.Run(async () =>
        {
            try { await WriterLoopAsync(_cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { WriterFailed(ex); }
        });
    }

    protected override async ValueTask WriteCoreAsync(byte[] data, CancellationToken ct)
    {
        var target = remote ?? LastSender;
        if (target is null) return;
        for (var off = 0; off < data.Length; off += MaxDatagram)
        {
            var n = Math.Min(MaxDatagram, data.Length - off);
            try
            {
                await socket.SendToAsync(data.AsMemory(off, n), SocketFlags.None, target, ct);
            }
            catch (SocketException) { /* UDP is best effort */ }
        }
    }

    protected override void OnClose()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }
}
