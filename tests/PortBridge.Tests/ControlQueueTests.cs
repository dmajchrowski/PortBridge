using System.Threading.Channels;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;
using PortBridge.Core.Protocols;
using PortBridge.Core.Serial;

namespace PortBridge.Tests;

/// <summary>A peer connection: the test scripts what the peer sends; writes either complete or never complete
/// (a peer that stopped reading). Disposal ends reads (EOF) and fails pending writes, like a closed socket.</summary>
internal sealed class FakePeerStream : Stream
{
    private readonly Channel<byte[]> _fromPeer = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _disposed = new();
    public volatile bool PeerReads;
    private long _bytesWritten;
    private int _pendingWrites;
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    public int PendingWrites => Volatile.Read(ref _pendingWrites);

    public FakePeerStream(bool peerReads) => PeerReads = peerReads;

    public void PeerSends(byte[] data) => _fromPeer.Writer.TryWrite(data);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
            var data = await _fromPeer.Reader.ReadAsync(linked.Token);
            data.CopyTo(buffer);
            return data.Length;
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested) { return 0; }
        catch (ChannelClosedException) { return 0; }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pendingWrites);
        try
        {
            if (!PeerReads)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
                await Task.Delay(Timeout.Infinite, linked.Token); // never completes: the peer's window is full
            }
            Interlocked.Add(ref _bytesWritten, data.Length);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingWrites);
        }
    }

    protected override void Dispose(bool disposing)
    {
        _disposed.Cancel();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>F6: RFC 2217 control replies are bounded; a command-producing peer that stops reading is disconnected
/// deterministically, writers are reaped, and valid negotiation keeps working.</summary>
public class ControlQueueTests
{
    // IAC SB COM-PORT-OPTION SIGNATURE IAC SE: a request the server must answer.
    private static readonly byte[] SignatureRequest = [255, 250, 44, 0, 255, 240];

    private static (TcpSession Session, FakePeerStream Peer) Create(bool peerReads, int maxMessages = 16, long maxBytes = 4096, long maxQueued = 1 << 20)
    {
        var cfg = new EndpointConfig { Kind = EndpointKind.TcpServer, Rfc2217 = true };
        var ctx = new BridgeContext("b", "b", new EventJournal(), new Com0ComManager(), new CertificateProvider());
        var owner = new ProbeEndpoint(cfg, ctx, new NullHost());
        var peer = new FakePeerStream(peerReads);
        var s = new TcpSession(owner, null, peer, "", CancellationToken.None, "peer", maxMessages, maxBytes, maxQueued);
        s.AttachRfc2217(new Rfc2217Protocol(true, raw => s.SendControl(raw), () => new DetachedRfc2217Port(
            new SerialLineSettings(9600, 8, SerialParity.None, SerialStopBits.One, FlowControl.None))));
        return (s, peer);
    }

    [Fact]
    public async Task Command_flood_from_a_peer_that_does_not_read_closes_the_session_within_bounds()
    {
        var (s, peer) = Create(peerReads: false);
        var run = s.RunAsync(_ => { });
        for (var i = 0; i < 500; i++) peer.PeerSends(SignatureRequest);

        await Wait.Until(() => s.IsClosed, what: "slow-peer policy closed the session");
        Assert.Contains("control reply queue full", s.CloseReason);
        Assert.True(s.ControlQueueCount <= 16);
        Assert.True(await Task.WhenAny(run, Task.Delay(5000)) == run, "RunAsync must reap both writers");
        Assert.True(s.WriterCompletion.IsCompleted);
        Assert.Equal(0, peer.PendingWrites);
        Assert.False(s.SendControl([1, 2, 3])); // a closed session admits nothing
        Assert.False(s.TrySend([1]));
    }

    [Fact]
    public void Admission_boundaries_for_messages_and_bytes_are_exact()
    {
        var (byCount, _) = Create(peerReads: false, maxMessages: 4, maxBytes: 1000);
        for (var i = 0; i < 4; i++) Assert.True(byCount.SendControl(new byte[10]));
        Assert.False(byCount.IsClosed);
        Assert.False(byCount.SendControl(new byte[10]));
        Assert.True(byCount.IsClosed);

        var (byBytes, _) = Create(peerReads: false, maxMessages: 100, maxBytes: 100);
        Assert.True(byBytes.SendControl(new byte[60]));
        Assert.True(byBytes.SendControl(new byte[40]));  // exactly at the limit
        Assert.Equal(100, byBytes.QueuedBytes);
        Assert.False(byBytes.SendControl(new byte[1]));
        Assert.True(byBytes.IsClosed);
    }

    [Fact]
    public void Data_and_control_share_one_queue_limit()
    {
        var (s, _) = Create(peerReads: false, maxMessages: 100, maxBytes: 1000, maxQueued: 150);
        Assert.True(s.TrySend(new byte[100]));
        Assert.True(s.SendControl(new byte[50]));  // 150 total
        Assert.Equal(150, s.QueuedBytes);
        Assert.False(s.SendControl(new byte[1])); // data + control would exceed the session limit
        Assert.True(s.IsClosed);

        var (t, _) = Create(peerReads: false, maxMessages: 100, maxBytes: 1000, maxQueued: 150);
        Assert.True(t.SendControl(new byte[100]));
        Assert.False(t.TrySend(new byte[51])); // the data path counts queued control bytes too
        Assert.True(t.IsClosed);
    }

    /// <summary>ER4: with 100 data bytes queued against a 150-byte budget, a 40-byte control reply has passed its
    /// check but not committed when a 50-byte data send arrives. Both must not be admitted (190 &gt; 150).</summary>
    [Fact]
    public async Task Control_and_data_reserve_the_shared_budget_atomically()
    {
        var (s, _) = Create(peerReads: false, maxMessages: 100, maxBytes: 1000, maxQueued: 150);
        Assert.True(s.TrySend(new byte[100]));
        Task<bool>? data = null;
        var dataFinishedInsideWindow = false;
        s.InterleavingHook = p =>
        {
            if (p != "control:checked" || data is not null) return;
            data = Task.Run(() => s.TrySend(new byte[50]));
            dataFinishedInsideWindow = data.Wait(300); // bounded: give the data path every chance to slip in
        };
        var control = s.SendControl(new byte[40]);
        var dataAdmitted = await data!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(dataFinishedInsideWindow, "data admission completed between control's check and its commit");
        Assert.False(control && dataAdmitted, "both admitted: the shared 150-byte budget was exceeded");
        Assert.True(s.QueuedBytes <= 150, "queued bytes exceed the shared budget");
    }

    [Fact]
    public async Task Concurrent_data_and_control_against_a_stalled_peer_terminate_without_deadlock()
    {
        var (s, peer) = Create(peerReads: false, maxMessages: 64, maxBytes: 8192, maxQueued: 64 * 1024);
        var run = s.RunAsync(_ => { });
        var data = Task.Run(() => { for (var i = 0; i < 10_000 && !s.IsClosed; i++) s.TrySend(new byte[64]); });
        var control = Task.Run(() => { for (var i = 0; i < 10_000 && !s.IsClosed; i++) peer.PeerSends(SignatureRequest); });
        await Task.WhenAll(data, control).WaitAsync(TimeSpan.FromSeconds(10));
        await Wait.Until(() => s.IsClosed, what: "closed by a bound");
        Assert.True(await Task.WhenAny(run, Task.Delay(5000)) == run, "stop is bounded");
        Assert.True(s.QueuedBytes <= 64 * 1024);
    }

    [Fact]
    public async Task Stop_reaps_writers_blocked_on_a_stalled_peer()
    {
        var (s, peer) = Create(peerReads: false);
        var run = s.RunAsync(_ => { });
        Assert.True(s.TrySend(new byte[10]));
        await Wait.Until(() => peer.PendingWrites > 0, what: "a write is stuck");
        s.Close("bridge stopped");
        Assert.True(await Task.WhenAny(run, Task.Delay(5000)) == run, "stop must not wait forever");
        Assert.Equal(0, peer.PendingWrites);
    }

    [Fact]
    public async Task Valid_negotiation_still_gets_its_replies()
    {
        // Production limits: a burst of requests from a peer that reads its replies stays well inside them.
        var (s, peer) = Create(peerReads: true, TcpSession.DefaultMaxControlMessages, TcpSession.DefaultMaxControlBytes);
        var run = s.RunAsync(_ => { });
        for (var i = 0; i < 100; i++) peer.PeerSends(SignatureRequest);
        await Wait.Until(() => peer.BytesWritten >= 100 * 16, what: "replies written");
        Assert.False(s.IsClosed);
        await Wait.Until(() => s.ControlQueueCount == 0, what: "control queue drained");
        s.Close("done");
        await run;
    }
}
