using PortBridge.Core.Config;

namespace PortBridge.Core.Engine;

public sealed record SessionView(
    string Id,
    string Remote,
    DateTimeOffset ConnectedAt,
    long BytesIn,
    long BytesOut,
    long Queued,
    string? Detail);

public sealed record EndpointView(
    string Id,
    EndpointKind Kind,
    string Label,
    string Summary,
    EndpointStatus Status,
    string StatusText,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    IReadOnlyList<SessionView> Sessions,
    IReadOnlyDictionary<string, string>? Details);

public sealed record StatsView(
    long ForwardBytes,
    long ReverseBytes,
    long ForwardPackets,
    long ReversePackets,
    long DroppedBytes,
    long HeldBytes,
    double ForwardRate,
    double ReverseRate,
    double[] ForwardHistory,
    double[] ReverseHistory,
    long QueuedBytes = 0,
    long UnsentBytes = 0,
    long HoldEvictedBytes = 0,
    long ReplayedBytes = 0);

public sealed record BridgeView(
    string Id,
    string Name,
    string? Notes,
    bool Active,
    bool Running,
    string State,
    DateTimeOffset? StartedAt,
    StatsView? Stats,
    EndpointView Primary,
    IReadOnlyList<EndpointView> Targets,
    BridgeConfig Config);

public static class ViewBuilder
{
    public static BridgeView Build(BridgeConfig cfg, Bridge? rt)
    {
        if (rt is null)
        {
            return new BridgeView(cfg.Id, cfg.Name, cfg.Notes, cfg.Active, false, "stopped", null, null,
                Idle(cfg.Primary), cfg.Targets.Select(Idle).ToList(), cfg);
        }
        var (fh, rh) = rt.Stats.History();
        var stats = new StatsView(rt.Stats.ForwardBytes, rt.Stats.ReverseBytes, rt.Stats.ForwardPackets, rt.Stats.ReversePackets,
            rt.Stats.DroppedBytes, rt.HeldBytes, rt.Stats.ForwardRate, rt.Stats.ReverseRate, fh, rh,
            rt.Stats.QueuedBytes, rt.Stats.UnsentBytes, rt.Stats.HoldEvictedBytes, rt.Stats.ReplayedBytes);
        return new BridgeView(cfg.Id, cfg.Name, cfg.Notes, cfg.Active, rt.Running, rt.State, rt.StartedAt, stats,
            Live(rt.Primary), rt.Targets.Select(Live).ToList(), cfg);
    }

    private static EndpointView Idle(EndpointConfig c) =>
        new(c.Id, c.Kind, string.IsNullOrWhiteSpace(c.Label) ? c.Describe() : c.Label!, c.Describe(),
            EndpointStatus.Stopped, "Stopped", null, null, [], null);

    private static EndpointView Live(Endpoint e) =>
        new(e.Config.Id, e.Config.Kind, e.Label, e.Config.Describe(), e.Status, e.StatusText, e.LastError, e.LastErrorAt,
            e.Sessions.Select(s => new SessionView(s.Seq.ToString(), s.Remote, s.ConnectedAt, s.BytesIn, s.BytesOut, s.QueuedBytes, s.Detail)).ToList(),
            e.Details);
}
