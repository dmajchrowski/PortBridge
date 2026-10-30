using PortBridge.Core.Config;
using PortBridge.Core.Engine;

namespace PortBridge.Tests;

/// <summary>F2: per-destination admission/replay/routing is serialised, so held bytes keep FIFO order and every byte
/// has a reported outcome. Interleavings are forced with the production hook points, not with sleeps.</summary>
public class HoldOrderingTests
{
    private static (BridgeHarness H, ProbeEndpoint Src, ProbeEndpoint Dst, ProbeSession SrcSession) Create(int holdKb = 512)
    {
        var src = new EndpointConfig { Kind = EndpointKind.TcpClient, Host = "src", Port = 1 };
        var dst = new EndpointConfig { Kind = EndpointKind.TcpClient, Host = "dst", Port = 2, HoldWhileDisconnected = true, HoldBufferKb = holdKb };
        var h = new BridgeHarness(src, [dst], _ => true);
        var a = (ProbeEndpoint)h.Bridge.Primary;
        var b = (ProbeEndpoint)h.Bridge.Targets[0];
        h.Bridge.StartAsync().GetAwaiter().GetResult();
        var sa = new ProbeSession(a);
        a.Open(sa);
        return (h, a, b, sa);
    }

    [Fact]
    public async Task New_bytes_cannot_overtake_held_bytes_while_a_session_is_being_published()
    {
        var (h, a, b, sa) = Create();
        await using var _ = h;
        a.Feed(sa, "old1");
        a.Feed(sa, "old2");
        Assert.Equal(8, h.Bridge.HeldBytes);

        using var registered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        b.InterleavingHook = p => { if (p == "session-registered") { registered.Set(); release.Wait(5000); } };
        var sb = new ProbeSession(b);
        var opening = Task.Run(() => b.Open(sb));
        Assert.True(registered.Wait(5000));

        a.Feed(sa, "new"); // the session is registered with the endpoint but not admitted by the bridge yet
        release.Set();
        await opening;

        await Wait.Until(() => sb.Text.Length == 11, what: "all bytes delivered");
        Assert.Equal("old1old2new", sb.Text);
        Assert.Equal(0, h.Bridge.HeldBytes);
        Assert.Equal(11, h.Bridge.Stats.ReplayedBytes);
    }

    [Fact]
    public async Task A_producer_that_saw_no_session_cannot_strand_bytes_after_the_drain()
    {
        var (h, a, b, sa) = Create();
        await using var _ = h;
        using var selected = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = 0;
        h.Bridge.InterleavingHook = p =>
        {
            if (p == "deliver:selected" && Interlocked.Exchange(ref first, 1) == 0) { selected.Set(); release.Wait(5000); }
        };

        var producer = Task.Run(() => a.Feed(sa, "x")); // decides "nobody connected", then pauses
        Assert.True(selected.Wait(5000));
        var sb = new ProbeSession(b);
        var opening = Task.Run(() => b.Open(sb));
        var admittedWhileProducerUndecided = await Task.WhenAny(opening, Task.Delay(300)) == opening;
        release.Set();
        await producer;
        await opening;

        await Wait.Until(() => sb.Text == "x", what: "held byte delivered");
        Assert.False(admittedWhileProducerUndecided);
        Assert.Equal(0, h.Bridge.HeldBytes);
    }

    [Fact]
    public async Task Data_arriving_during_replay_queues_behind_the_backlog()
    {
        var (h, a, b, sa) = Create();
        await using var _ = h;
        a.Feed(sa, "old1");
        a.Feed(sa, "old2");
        using var paused = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var n = 0;
        h.Bridge.InterleavingHook = p =>
        {
            if (p == "replay:chunk" && Interlocked.Increment(ref n) == 2) { paused.Set(); release.Wait(5000); }
        };

        var sb = new ProbeSession(b);
        var opening = Task.Run(() => b.Open(sb));
        Assert.True(paused.Wait(5000)); // old1 queued, old2 not yet
        var producer = Task.Run(() => a.Feed(sa, "new"));
        var routedDuringReplay = await Task.WhenAny(producer, Task.Delay(300)) == producer;
        release.Set();
        await opening;
        await producer;

        await Wait.Until(() => sb.Text.Length == 11, what: "all bytes delivered");
        Assert.False(routedDuringReplay);
        Assert.Equal("old1old2new", sb.Text);
    }

    [Fact]
    public async Task Hold_eviction_is_reported_as_dropped()
    {
        var (h, a, b, sa) = Create(holdKb: 1);
        await using var _ = h;
        a.Feed(sa, new string('1', 600));
        a.Feed(sa, new string('2', 600));
        a.Feed(sa, new string('3', 600));

        Assert.Equal(1200, h.Bridge.Stats.HoldEvictedBytes);
        Assert.Equal(1200, h.Bridge.Stats.DroppedBytes);
        Assert.Equal(600, h.Bridge.HeldBytes);

        var sb = new ProbeSession(b);
        b.Open(sb);
        await Wait.Until(() => sb.Text.Length == 600, what: "newest data delivered");
        Assert.Equal(new string('3', 600), sb.Text);
        Assert.Equal(600, h.Bridge.Stats.ReplayedBytes);
    }

    [Fact]
    public async Task Replay_rejected_by_a_closing_session_is_kept_in_order_and_accounted()
    {
        var (h, a, b, sa) = Create();
        await using var _ = h;
        a.Feed(sa, "aaaaaaaaaa");
        a.Feed(sa, "bbbbbbbbbb");
        a.Feed(sa, "cccccccccc");

        // Slow receiver: accepts 10 bytes, then its queue overflows during the replay and it closes.
        var sb = new ProbeSession(b, maxQueued: 15, holdWrites: true);
        b.Open(sb);
        Assert.True(sb.IsClosed);
        Assert.Contains("overflow", sb.CloseReason);
        Assert.Equal(20, h.Bridge.HeldBytes);
        Assert.Equal(10, h.Bridge.Stats.ReplayedBytes);
        Assert.Equal(1, h.JournalCount("kept 20 for the next connection"));
        Assert.Equal(0, h.JournalCount("queued 30 buffered bytes"));

        a.Feed(sa, "dd"); // the closed session is not a destination any more
        b.Drop(sb);
        await Wait.Until(() => h.Bridge.PendingSettlements == 0, what: "accounting settled"); // sb's writer may still be stopping
        Assert.Equal(10, h.Bridge.Stats.UnsentBytes); // queued to sb, never written

        var sb2 = new ProbeSession(b);
        b.Open(sb2);
        await Wait.Until(() => sb2.Text.Length == 22, what: "kept bytes delivered");
        Assert.Equal("bbbbbbbbbbccccccccccdd", sb2.Text);
        Assert.Equal(0, h.Bridge.HeldBytes);
    }

    /// <summary>ER1: a TCP server destination sending to the First session, with a second session already admitted as
    /// standby. The selected session overflows (its bytes are held); newer data must not overtake the held bytes.</summary>
    private static (BridgeHarness H, ProbeEndpoint Src, ProbeEndpoint Srv, ProbeSession SrcSession, ProbeSession A, ProbeSession B) CreateFirstWithStandby()
    {
        var src = new EndpointConfig { Kind = EndpointKind.TcpClient, Host = "src", Port = 1 };
        var dst = new EndpointConfig { Kind = EndpointKind.TcpServer, Port = 2, TransmitTo = SessionSelect.First, HoldWhileDisconnected = true };
        var h = new BridgeHarness(src, [dst], _ => true);
        var a = (ProbeEndpoint)h.Bridge.Primary;
        var srv = (ProbeEndpoint)h.Bridge.Targets[0];
        h.Bridge.StartAsync().GetAwaiter().GetResult();
        var sa = new ProbeSession(a);
        a.Open(sa);
        var first = new ProbeSession(srv, maxQueued: 10, holdWrites: true); // selected; stops reading
        var standby = new ProbeSession(srv);
        srv.Open(first);
        srv.Open(standby);
        a.Feed(sa, "accepted"); // 8 bytes queued to the selected session, never written
        a.Feed(sa, "old");      // 8 + 3 > 10: it overflows and closes; the only selected session refused → held
        return (h, a, srv, sa, first, standby);
    }

    [Fact]
    public async Task First_session_failover_replays_the_held_backlog_before_newer_data()
    {
        var (h, a, srv, sa, first, standby) = CreateFirstWithStandby();
        await using var _ = h;
        Assert.True(first.IsClosed);
        Assert.Equal(3, h.Bridge.HeldBytes);
        Assert.Empty(standby.Transmitted); // the standby was not selected before the failover

        a.Feed(sa, "new"); // the standby is now the first open session
        await Wait.Until(() => standby.Text.Length >= 6, what: "standby received old+new");
        Assert.Equal("oldnew", standby.Text);
        Assert.Equal(0, h.Bridge.HeldBytes);

        srv.Drop(first);
        await Wait.Until(() => h.Bridge.PendingSettlements == 0, what: "accounting settled");
        Assert.Equal(8, h.Bridge.Stats.UnsentBytes);  // accepted by the dead session, never written
        Assert.Equal(8, h.Bridge.Stats.DroppedBytes); // nothing else was lost
        Assert.Equal(3, h.Bridge.Stats.ReplayedBytes);
    }

    [Fact]
    public async Task Losing_the_selected_session_hands_the_backlog_to_the_admitted_standby()
    {
        var (h, _, srv, _, first, standby) = CreateFirstWithStandby();
        await using var __ = h;
        srv.Drop(first); // no new data arrives afterwards
        await Wait.Until(() => standby.Text == "old", what: "backlog replayed into the standby");
        Assert.Equal(0, h.Bridge.HeldBytes);
    }

    [Fact]
    public async Task A_session_closed_before_admission_receives_nothing()
    {
        var (h, a, b, sa) = Create();
        await using var _ = h;
        a.Feed(sa, "held");
        var sb = new ProbeSession(b);
        sb.Close("went away");
        b.Open(sb);
        Assert.Equal(4, h.Bridge.HeldBytes);
        Assert.Empty(sb.Transmitted);
        b.Drop(sb);

        var sb2 = new ProbeSession(b);
        b.Open(sb2);
        await Wait.Until(() => sb2.Text == "held", what: "delivered to the next session");
    }
}
