using System.Collections.Concurrent;
using PortBridge.Core.Config;
using PortBridge.Core.Protocols;
using PortBridge.Core.Serial;

namespace PortBridge.Core.Engine;

/// <summary>
/// Connects one source endpoint to one or more targets in a star:
/// data from the source goes to every target, data from any target goes back to the source.
/// Two endpoints give TCP-Com's classic serial⇄TCP converter; more targets give port sharing / fan-out
/// without chaining several instances through loopback connections.
///
/// Ordering: everything that decides where bytes for one destination go — admitting a new session, replaying the
/// hold buffer into it, routing new data, holding data while nobody is connected — runs under that destination's
/// gate. A session only receives data after it was admitted and the backlog was replayed, so newer bytes can never
/// overtake held ones and nothing is added to the hold buffer after it was drained for a live session.
/// </summary>
public sealed class Bridge : IEndpointHost
{
    private readonly TrafficHub _traffic;
    private readonly Dictionary<Endpoint, Destination> _dest = new();
    private readonly ConcurrentDictionary<long, Packetizer> _packetizers = new();
    private readonly Dictionary<Endpoint, bool> _sideActive = new();
    private readonly Lock _activityGate = new();
    private readonly Func<EndpointConfig, BridgeContext, IEndpointHost, Endpoint?>? _factory;

    /// <summary>Per-destination routing state, guarded by <see cref="Gate"/>.</summary>
    private sealed class Destination(HoldBuffer? hold)
    {
        public readonly Lock Gate = new();
        public readonly HashSet<long> Admitted = [];
        public readonly HoldBuffer? Hold = hold;
    }

    public BridgeConfig Config { get; }
    public BridgeContext Ctx { get; }
    public Endpoint Primary { get; }
    public IReadOnlyList<Endpoint> Targets { get; }
    public BridgeStats Stats { get; } = new();
    public bool Running { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }

    public IEnumerable<Endpoint> Endpoints => Targets.Prepend(Primary);

    /// <summary>Test seam for controlled interleavings ("deliver:selected", "replay:chunk"). Null in production.</summary>
    internal Action<string>? InterleavingHook { get; set; }

    private int _pendingSettlements;

    /// <summary>Closed sessions whose writer has not settled yet, so their unsent bytes are not known yet.</summary>
    internal int PendingSettlements => Volatile.Read(ref _pendingSettlements);

    public Bridge(BridgeConfig config, BridgeContext ctx, TrafficHub traffic) : this(config, ctx, traffic, null) { }

    /// <summary><paramref name="factory"/> lets tests supply endpoints; returning null uses the normal endpoint type.</summary>
    internal Bridge(BridgeConfig config, BridgeContext ctx, TrafficHub traffic, Func<EndpointConfig, BridgeContext, IEndpointHost, Endpoint?>? factory)
    {
        Config = config;
        Ctx = ctx;
        _traffic = traffic;
        _factory = factory;
        Primary = Create(config.Primary);
        Targets = config.Targets.Select(Create).ToList();
        foreach (var ep in Endpoints)
            _dest[ep] = new Destination(ep.Config.HoldWhileDisconnected ? new HoldBuffer(Math.Max(1, ep.Config.HoldBufferKb) * 1024L) : null);
    }

    private Endpoint Create(EndpointConfig cfg) => _factory?.Invoke(cfg, Ctx, this) ?? cfg.Kind switch
    {
        EndpointKind.Serial or EndpointKind.VirtualSerial => new SerialEndpoint(cfg, Ctx, this),
        EndpointKind.TcpServer => new TcpServerEndpoint(cfg, Ctx, this),
        EndpointKind.TcpClient => new TcpClientEndpoint(cfg, Ctx, this),
        EndpointKind.Udp => new UdpEndpoint(cfg, Ctx, this),
        _ => throw new NotSupportedException(cfg.Kind.ToString()),
    };

    public async Task StartAsync()
    {
        if (Running) return;
        Stats.Reset();
        Running = true;
        StartedAt = DateTimeOffset.Now;
        Ctx.Info("Bridge started", EventCategory.Config);
        foreach (var ep in Endpoints) await ep.StartAsync();
    }

    public async Task StopAsync()
    {
        if (!Running) return;
        Running = false;
        await Task.WhenAll(Endpoints.Select(e => e.StopAsync()));
        foreach (var p in _packetizers.Values) p.Dispose();
        _packetizers.Clear();
        foreach (var d in _dest.Values)
        {
            lock (d.Gate)
            {
                if (d.Hold is not { } h) continue;
                var lost = h.Drain().Sum(c => (long)c.Length);
                if (lost > 0) Stats.AddDropped(lost);
            }
        }
        StartedAt = null;
        Ctx.Info("Bridge stopped", EventCategory.Config);
    }

    public long HeldBytes => _dest.Values.Sum(d => d.Hold?.Bytes ?? 0);

    private IReadOnlyList<Endpoint> Opposite(Endpoint ep) => ep == Primary ? Targets : [Primary];

    /// <summary>Admitted, open sessions of <paramref name="ep"/> that take part in a direction. Caller holds the gate.</summary>
    private static List<Session> Admitted(Endpoint ep, Destination d, SessionSelect mode)
    {
        var open = ep.Sessions.Where(s => !s.IsClosed && d.Admitted.Contains(s.Seq)).ToList();
        if (!ep.Config.IsMultiSession) return mode == SessionSelect.None ? [] : open;
        return mode switch
        {
            SessionSelect.All => open,
            SessionSelect.First => open.Count > 0 ? [open[0]] : [],
            _ => [],
        };
    }

    // ---- IEndpointHost ----

    void IEndpointHost.OnData(Endpoint ep, Session session, ReadOnlySpan<byte> data)
    {
        if (!Running) return;
        var pk = _packetizers.GetOrAdd(session.Seq, _ => new Packetizer(ep.Config, chunk => Route(ep, session, chunk)));
        // OnSessionClosed runs after Close; re-checking here means a racing callback can't leave a packetizer behind.
        if (session.IsClosed)
        {
            if (_packetizers.TryRemove(session.Seq, out var stale)) stale.Dispose();
            Stats.AddDropped(data.Length);
            return;
        }
        pk.Push(data);
    }

    private void Route(Endpoint src, Session session, byte[] chunk)
    {
        if (!src.Config.Receive)
        {
            Stats.AddDropped(chunk.Length);
            return;
        }
        if (src.Config.IsMultiSession)
        {
            var allowed = src.Config.ReceiveFrom switch
            {
                SessionSelect.All => true,
                SessionSelect.First => src.FirstSession == session,
                _ => false,
            };
            if (!allowed)
            {
                Stats.AddDropped(chunk.Length);
                return;
            }
        }

        var forward = src == Primary;
        if (forward) Stats.AddForward(chunk.Length);
        else Stats.AddReverse(chunk.Length);

        var label = src.Config.IsMultiSession ? $"{src.Label} · {session.Remote}" : src.Label;
        _traffic.Record(Config, forward ? TrafficDirection.Forward : TrafficDirection.Reverse, label, chunk);

        foreach (var dest in Opposite(src)) Deliver(dest, chunk);
    }

    private void Deliver(Endpoint dest, byte[] chunk)
    {
        if (!dest.Config.Transmit) return;
        var d = _dest[dest];
        lock (d.Gate)
        {
            var sessions = Admitted(dest, d, dest.Config.TransmitTo);
            InterleavingHook?.Invoke("deliver:selected");
            // Held bytes go first. The selected session may have changed since they were held (e.g. TransmitTo=First
            // failing over to an already admitted standby), so replay them now; if that stops, queue behind them.
            if (d.Hold is { Bytes: > 0 } backlog && sessions.Count == 1 && !Replay(dest, sessions[0], backlog))
            {
                var held = backlog.Add(chunk);
                if (held > 0) Stats.AddEvicted(held);
                return;
            }
            var accepted = 0;
            foreach (var s in sessions)
            {
                if (s.TrySend(chunk))
                {
                    accepted++;
                    Stats.AddQueued(chunk.Length);
                }
                else if (sessions.Count > 1 || d.Hold is null)
                {
                    Stats.AddDropped(chunk.Length);
                }
            }
            if (accepted > 0) return;
            // Nobody connected (or the only session just refused it): keep it, in order, behind older held data.
            if (d.Hold is { } hold && (sessions.Count <= 1))
            {
                var evicted = hold.Add(chunk);
                if (evicted > 0) Stats.AddEvicted(evicted);
            }
            else if (sessions.Count == 0)
            {
                Stats.AddDropped(chunk.Length);
            }
        }
    }

    void IEndpointHost.OnSessionOpened(Endpoint ep, Session session)
    {
        var d = _dest[ep];
        lock (d.Gate)
        {
            // A session that already ended (or was replaced) before admission must not be published.
            if (!session.IsClosed && ep.IsRegistered(session))
            {
                d.Admitted.Add(session.Seq);
                if (d.Hold is { Bytes: > 0 } hold && ep.Config.Transmit && Admitted(ep, d, ep.Config.TransmitTo).Contains(session))
                    Replay(ep, session, hold);
            }
        }
        UpdateActivity();
    }

    /// <summary>Moves the backlog into a session, oldest first. Caller holds the destination gate. Chunks the session
    /// refuses (it closed or overflowed meanwhile) go back to the front of the hold buffer. True if everything was queued.</summary>
    private bool Replay(Endpoint ep, Session session, HoldBuffer hold)
    {
        var pending = hold.Drain();
        var total = pending.Sum(p => (long)p.Length);
        long queued = 0;
        var i = 0;
        for (; i < pending.Count; i++)
        {
            InterleavingHook?.Invoke("replay:chunk");
            if (!session.TrySend(pending[i])) break;
            queued += pending[i].Length;
        }
        Stats.AddQueued(queued);
        Stats.AddReplayed(queued);
        if (i == pending.Count)
        {
            Ctx.Info($"{ep.Label}: queued {total:N0} buffered bytes for delivery", EventCategory.Connection);
            return true;
        }
        var rest = pending.GetRange(i, pending.Count - i);
        var kept = total - queued;
        var evicted = hold.Restore(rest);
        if (evicted > 0) Stats.AddEvicted(evicted);
        Ctx.Warn($"{ep.Label}: the connection ended while replaying buffered data ({session.CloseReason ?? "closed"}); " +
                 $"queued {queued:N0} of {total:N0} bytes, kept {kept - evicted:N0} for the next connection" +
                 (evicted > 0 ? $", {evicted:N0} discarded (buffer full)" : ""), EventCategory.Connection);
        return false;
    }

    void IEndpointHost.OnSessionClosed(Endpoint ep, Session session, string reason)
    {
        var d = _dest[ep];
        lock (d.Gate)
        {
            d.Admitted.Remove(session.Seq);
            // If the session that was receiving is gone, a remaining admitted one (TransmitTo=First standby) takes over
            // the backlog now rather than whenever the next data happens to arrive.
            if (Running && d.Hold is { Bytes: > 0 } hold && ep.Config.Transmit &&
                Admitted(ep, d, ep.Config.TransmitTo) is [var next])
                Replay(ep, next, hold);
        }
        SettleUnsent(session);
        if (_packetizers.TryRemove(session.Seq, out var pk))
        {
            pk.Flush();
            pk.Dispose();
        }
        UpdateActivity();
    }

    /// <summary>Bytes queued to the session but never written are lost: report them instead of counting them delivered.
    /// A write still in flight (writer not settled, e.g. it outlived the stop timeout) may yet complete, so the count is
    /// taken only once the writer has stopped — never before, which would report written bytes as unsent.</summary>
    private void SettleUnsent(Session session)
    {
        void Take()
        {
            var unsent = session.TakeUnsentBytes();
            if (unsent > 0) Stats.AddUnsent(unsent);
        }
        var writer = session.WriterCompletion;
        if (writer.IsCompleted)
        {
            Take();
            return;
        }
        Interlocked.Increment(ref _pendingSettlements);
        _ = writer.ContinueWith(_ =>
        {
            try { Take(); }
            finally { Interlocked.Decrement(ref _pendingSettlements); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Tells each side when the other side gains its first or loses its last session.</summary>
    private void UpdateActivity()
    {
        var notify = new List<(Endpoint, bool)>();
        lock (_activityGate)
        {
            foreach (var ep in Endpoints)
            {
                var active = ((IEndpointHost)this).IsPeerActive(ep);
                if (!_sideActive.TryGetValue(ep, out var was) || was != active)
                {
                    _sideActive[ep] = active;
                    notify.Add((ep, active));
                }
            }
        }
        foreach (var (ep, active) in notify) ep.OnPeerActivityChanged(active);
    }

    bool IEndpointHost.IsPeerActive(Endpoint ep) => Opposite(ep).Any(o => o.HasSession);

    void IEndpointHost.OnModemLinesChanged(Endpoint ep, ModemLines lines)
    {
        foreach (var other in Opposite(ep)) other.OnPeerModemLines(ep, lines);
    }

    void IEndpointHost.OnControlLinesChanged(Endpoint ep, ControlLines lines)
    {
        foreach (var other in Opposite(ep)) other.OnPeerControlLines(lines);
    }

    ControlLines? IEndpointHost.GetPeerControlLines(Endpoint ep) =>
        Opposite(ep).OfType<SerialEndpoint>().Select(s => s.AppControlLines).FirstOrDefault(l => l is not null);

    IRfc2217Port IEndpointHost.GetRfc2217Port(Endpoint ep) =>
        Opposite(ep).OfType<SerialEndpoint>().FirstOrDefault(s => s.Config.Kind == EndpointKind.Serial)
        ?? (IRfc2217Port)_detached.GetOrAdd(ep, e => new DetachedRfc2217Port(SerialLineSettings.From(e.Config)));

    private readonly ConcurrentDictionary<Endpoint, DetachedRfc2217Port> _detached = new();

    // ---- Operations from the UI ----

    /// <summary>Sends test data to an endpoint as if it came from the other side. Returns the sessions that accepted it.</summary>
    public int Inject(string endpointId, byte[] data)
    {
        var dest = Endpoints.FirstOrDefault(e => e.Config.Id == endpointId)
                   ?? throw new KeyNotFoundException("Unknown endpoint");
        var d = _dest[dest];
        var accepted = 0;
        lock (d.Gate)
        {
            foreach (var s in Admitted(dest, d, SessionSelect.All))
            {
                if (s.TrySend(data))
                {
                    accepted++;
                    Stats.AddQueued(data.Length);
                }
            }
        }
        _traffic.Record(Config, dest == Primary ? TrafficDirection.Reverse : TrafficDirection.Forward, dest.Label, data, injected: true);
        return accepted;
    }

    public bool Kick(long sessionSeq) => Endpoints.Any(e => e.KickSession(sessionSeq));

    /// <summary>Overall state for the dashboard: error beats waiting beats connected.</summary>
    public string State
    {
        get
        {
            if (!Running) return "stopped";
            var eps = Endpoints.ToList();
            if (eps.Any(e => e.Status == EndpointStatus.Error)) return "error";
            if (Primary.HasSession && Targets.Any(t => t.HasSession)) return "connected";
            return "waiting";
        }
    }
}
