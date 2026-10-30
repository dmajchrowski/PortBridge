using PortBridge.Core.Config;
using PortBridge.Core.Engine;
using PortBridge.Core.Serial;

namespace PortBridge.Tests;

/// <summary>F1: a serial session whose writer dies (overflow, write failure, kick) is retired and replaced through the
/// production SerialEndpoint/Bridge, with a fake driver standing in for com0com / the COM port.</summary>
public class SerialLifecycleTests
{
    private const ModemLines AppOpen = ModemLines.Dcd | ModemLines.Dsr | ModemLines.Cts;

    private static EndpointConfig Virtual() => new() { Kind = EndpointKind.VirtualSerial, PortName = "COM99", ReopenDelayMs = 100 };
    private static EndpointConfig Physical() => new() { Kind = EndpointKind.Serial, PortName = "COM98", ReopenDelayMs = 100 };
    private static EndpointConfig Probe() => new() { Kind = EndpointKind.TcpClient, Host = "probe", Port = 1 };

    private static (BridgeHarness H, SerialEndpoint Serial, ProbeEndpoint Probe) Create(EndpointConfig serialCfg)
    {
        var h = new BridgeHarness(serialCfg, [Probe()], c => c.Kind == EndpointKind.TcpClient);
        var serial = (SerialEndpoint)h.Bridge.Primary;
        serial.ReopenDelayOverrideMs = 0;
        serial.WriterStopTimeout = TimeSpan.FromSeconds(2);
        return (h, serial, (ProbeEndpoint)h.Bridge.Targets[0]);
    }

    private static async Task<Session> Connected(SerialEndpoint serial, Session? notThis = null)
    {
        // One snapshot per check: Sessions is rebuilt on every access and may change between two reads.
        Session? found = null;
        await Wait.Until(() => serial.Sessions is [var s] && s != notThis && serial.Status == EndpointStatus.Connected && (found = s) is not null,
            what: "a live serial session");
        return found!;
    }

    [Fact]
    public async Task Overflow_retires_session_and_recovers_while_the_app_keeps_the_virtual_port_open()
    {
        var (h, serial, probe) = Create(Virtual());
        await using var _ = h;
        h.Ports.InitialLines = AppOpen;
        serial.SendQueueLimit = 100;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Ports.OnOpen = p => p.WriteBehavior = (_, ct) => gate.Task.WaitAsync(ct);
        await h.Bridge.StartAsync();
        var s1 = await Connected(serial);
        var ps = new ProbeSession(probe);
        probe.Open(ps);

        probe.Feed(ps, new string('a', 60));
        await Wait.Until(() => h.Ports.Current!.WriteCalls == 1, what: "writer busy");
        probe.Feed(ps, new string('b', 60)); // 60 in flight + 60 > 100: overflow

        var s2 = await Connected(serial, s1);
        Assert.True(s1.IsClosed);
        Assert.Contains("overflow", s1.CloseReason);
        Assert.True(s1.WriterCompletion.IsCompleted, "old writer must be finished before the replacement exists");
        Assert.DoesNotContain(s1, serial.Sessions);
        Assert.Equal(1, h.Ports.OpenCount); // the application's COM port was never pulled away
        Assert.True(h.Ports.Current!.TxPurges >= 1);
        Assert.Equal("connected", (await WaitState(h, "connected")));

        gate.SetResult();
        probe.Feed(ps, "hello");
        await Wait.Until(() => h.Ports.Current!.WrittenText == "hello", what: "data through the new session");
        Assert.Equal(1, h.Ports.Current!.MaxConcurrentWrites);
        Assert.NotEqual(s1.Seq, s2.Seq);
        // 60 queued to the dead session but never written + 60 refused by it.
        Assert.Equal(60, h.Bridge.Stats.UnsentBytes);
        Assert.Equal(120, h.Bridge.Stats.DroppedBytes);
    }

    private static async Task<string> WaitState(BridgeHarness h, string state)
    {
        await Wait.Until(() => h.Bridge.State == state, what: "bridge state " + state);
        return h.Bridge.State;
    }

    [Fact]
    public async Task Write_failure_replaces_session_then_reopens_port_when_it_repeats()
    {
        var (h, serial, probe) = Create(Virtual());
        await using var _ = h;
        h.Ports.InitialLines = AppOpen;
        var failures = 0;
        h.Ports.OnOpen = p => p.WriteBehavior = (_, _) =>
            Interlocked.Increment(ref failures) <= 2 ? throw new IOException("device error") : Task.CompletedTask;
        await h.Bridge.StartAsync();
        var s1 = await Connected(serial);
        var ps = new ProbeSession(probe);
        probe.Open(ps);

        probe.Feed(ps, "x1");
        var s2 = await Connected(serial, s1);
        Assert.True(s1.WriteFaulted);
        Assert.Equal(1, h.Ports.OpenCount); // first failure: new session on the same handle

        var firstPort = h.Ports.Current!;
        probe.Feed(ps, "x2");
        await Wait.Until(() => h.Ports.OpenCount == 2, what: "port reopened after repeated failure");
        var s3 = await Connected(serial, s2);
        Assert.True(firstPort.Disposed);
        Assert.Equal(1, h.JournalCount("writing failed 2 times in a row"));

        probe.Feed(ps, "ok");
        await Wait.Until(() => h.Ports.Current!.WrittenText == "ok", what: "write after reopen");
        Assert.Equal(1, serial.SessionCount);
        Assert.NotEqual(s2.Seq, s3.Seq);
    }

    [Fact]
    public async Task Kick_replaces_session_without_reopening_and_app_close_then_leaves_it_waiting()
    {
        var (h, serial, probe) = Create(Virtual());
        await using var _ = h;
        h.Ports.InitialLines = AppOpen;
        await h.Bridge.StartAsync();
        var s1 = await Connected(serial);
        probe.Open(new ProbeSession(probe));

        Assert.True(h.Bridge.Kick(s1.Seq));
        var s2 = await Connected(serial, s1);
        Assert.Equal(1, h.Ports.OpenCount);
        Assert.Equal(1, h.JournalCount("session ended (disconnected by user)"));

        h.Ports.Current!.LinesSeen = ModemLines.None; // the application closes COM99
        await Wait.Until(() => serial.SessionCount == 0 && serial.Status == EndpointStatus.Waiting, what: "waiting after app close");
        Assert.True(s2.IsClosed);
        Assert.Equal("waiting", h.Bridge.State);
        Assert.False(serial.HasSession);
    }

    [Fact]
    public async Task Repeated_and_concurrent_close_replaces_exactly_once()
    {
        var (h, serial, probe) = Create(Virtual());
        await using var _ = h;
        h.Ports.InitialLines = AppOpen;
        await h.Bridge.StartAsync();
        var s1 = await Connected(serial);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0) s1.Close("closed by test");
            else h.Bridge.Kick(s1.Seq);
        })));
        var s2 = await Connected(serial, s1);
        s1.Close("again");
        await Task.Delay(300); // negative check: no further churn
        Assert.Single(serial.Sessions);
        Assert.Same(s2, serial.Sessions[0]);
        Assert.Equal(1, h.JournalCount("recovering"));
    }

    [Fact]
    public async Task Restart_uses_a_fresh_port_and_single_writer()
    {
        var (h, serial, probe) = Create(Virtual());
        await using var _ = h;
        h.Ports.InitialLines = AppOpen;
        await h.Bridge.StartAsync();
        var s1 = await Connected(serial);
        var first = h.Ports.Current!;

        await h.Bridge.StopAsync();
        Assert.True(first.Disposed);
        Assert.Equal(0, serial.SessionCount);
        Assert.True(s1.WriterCompletion.IsCompleted);
        Assert.Equal(EndpointStatus.Stopped, serial.Status);

        await h.Bridge.StartAsync();
        await Wait.Until(() => h.Ports.OpenCount == 2, what: "reopened");
        await Connected(serial, s1);
        var ps = new ProbeSession(probe);
        probe.Open(ps);
        probe.Feed(ps, "again");
        await Wait.Until(() => h.Ports.Current!.WrittenText == "again", what: "write after restart");
        Assert.Empty(first.Written);
        Assert.Equal(1, serial.SessionCount);
    }

    /// <summary>ER2: the application closes COMx while a write is still outstanding past the stop timeout, then opens it
    /// again. The handle with old-session I/O must not get a replacement session; the late successful completion
    /// must be counted as written, not as unsent, and no counter may go negative.</summary>
    [Fact]
    public async Task App_close_while_a_write_outlives_the_stop_timeout_never_reuses_the_handle_and_settles_accounting()
    {
        var (h, serial, probe) = Create(Virtual());
        await using var _ = h;
        serial.WriterStopTimeout = TimeSpan.FromMilliseconds(200);
        h.Ports.InitialLines = AppOpen;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = 0;
        h.Ports.OnOpen = p =>
        {
            if (Interlocked.Increment(ref opened) != 1) return; // only the first handle has the stuck write
            p.CompleteDespiteCancellation = true;
            p.WriteBehavior = (_, _) => release.Task;           // ignores cancellation; the driver completes it later
        };
        await h.Bridge.StartAsync();
        var s1 = await Connected(serial);
        var first = h.Ports.Current!;
        var ps = new ProbeSession(probe);
        probe.Open(ps);
        probe.Feed(ps, "hello");
        await Wait.Until(() => first.WriteCalls == 1, what: "write outstanding");

        first.LinesSeen = ModemLines.None; // the application closes COM99 …
        await Wait.Until(() => s1.IsClosed, what: "session closed on app close");
        first.LinesSeen = AppOpen;         // … and opens it again while the old write is still pending

        var s2 = await Connected(serial, s1);
        Assert.False(s1.WriterCompletion.IsCompleted, "the old write is still outstanding");
        Assert.True(first.Disposed, "a handle with outstanding old-session I/O must not be reused");
        Assert.Equal(2, h.Ports.OpenCount);

        release.SetResult(); // the old write completes successfully after all
        await s1.WriterCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await Wait.Until(() => h.Bridge.PendingSettlements == 0, what: "accounting settled");
        Assert.Equal(5, s1.BytesOut);
        Assert.Equal(0, s1.QueuedBytes);
        Assert.Equal(0, h.Bridge.Stats.UnsentBytes);
        Assert.Equal(0, h.Bridge.Stats.DroppedBytes);

        probe.Feed(ps, "next");
        await Wait.Until(() => h.Ports.Current!.WrittenText == "next", what: "replacement writes on the new handle");
        Assert.NotSame(first, h.Ports.Current);
        Assert.NotEqual(s1.Seq, s2.Seq);
    }

    /// <summary>A cancellation the endpoint did not request (e.g. the driver aborting the write) is a write failure:
    /// the session must be retired and replaced instead of keeping a registered session with a dead writer.</summary>
    [Fact]
    public async Task Unrequested_cancellation_from_the_driver_is_a_write_failure()
    {
        var (h, serial, probe) = Create(Virtual());
        await using var _ = h;
        h.Ports.InitialLines = AppOpen;
        var calls = 0;
        h.Ports.OnOpen = p => p.WriteBehavior = (_, _) =>
            Interlocked.Increment(ref calls) == 1 ? throw new OperationCanceledException("aborted by the driver") : Task.CompletedTask;
        await h.Bridge.StartAsync();
        var s1 = await Connected(serial);
        var ps = new ProbeSession(probe);
        probe.Open(ps);

        probe.Feed(ps, "x1");
        await Connected(serial, s1);
        Assert.True(s1.WriteFaulted);
        probe.Feed(ps, "ok");
        await Wait.Until(() => h.Ports.Current!.WrittenText == "ok", what: "replacement session writes");
    }

    [Fact]
    public async Task Physical_port_overflow_recovers_immediately()
    {
        var (h, serial, probe) = Create(Physical());
        await using var _ = h;
        serial.SendQueueLimit = 100;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Ports.OnOpen = p => p.WriteBehavior = (_, ct) => gate.Task.WaitAsync(ct);
        await h.Bridge.StartAsync();
        var s1 = await Connected(serial);
        var ps = new ProbeSession(probe);
        probe.Open(ps);

        probe.Feed(ps, new string('a', 60));
        await Wait.Until(() => h.Ports.Current!.WriteCalls == 1, what: "writer busy");
        probe.Feed(ps, new string('b', 60));
        await Connected(serial, s1);
        Assert.Equal(1, h.Ports.OpenCount);
        gate.SetResult();
    }

    [Fact]
    public async Task A_refused_com0com_driver_is_retried_slowly_other_errors_at_the_reopen_pace()
    {
        var (h, serial, _) = Create(Virtual());
        await using var _ = h;
        serial.DriverRefusedRetryDelay = TimeSpan.FromSeconds(30);
        h.Virtual.Fails = () => new Com0ComDriverNotTrustedException("refused");
        await h.Bridge.StartAsync();
        await Wait.Until(() => serial.Status == EndpointStatus.Error, what: "error shown");
        await Task.Delay(1500); // the normal pace (500 ms minimum) would have retried twice by now
        Assert.Equal(1, h.Virtual.Calls);
        Assert.Contains("refused", serial.StatusText);

        var (h2, _, _) = Create(Virtual());
        await using var __ = h2;
        h2.Virtual.Fails = () => new InvalidOperationException("COM99 is already taken by another device.");
        await h2.Bridge.StartAsync();
        await Wait.Until(() => h2.Virtual.Calls >= 3, what: "normal retries");
    }
}
