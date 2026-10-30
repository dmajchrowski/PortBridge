using PortBridge.Core.Config;
using PortBridge.Core.Util;

namespace PortBridge.Core.Engine;

/// <summary>Holds data for an endpoint that currently has nobody connected ("Buffer data if TCP/IP port closed").
/// When full, the oldest data is discarded so the most recent readings survive.
/// Callers serialise access per destination (see Bridge); the internal lock only keeps the counters consistent.</summary>
internal sealed class HoldBuffer(long maxBytes)
{
    private readonly LinkedList<byte[]> _q = new();
    private readonly Lock _gate = new();
    private long _bytes;

    public long Bytes => Interlocked.Read(ref _bytes);
    public long MaxBytes => maxBytes;
    public long Discarded { get; private set; }

    /// <summary>Appends a chunk; returns how many (oldest) bytes were evicted to stay within the limit.</summary>
    public long Add(byte[] chunk)
    {
        lock (_gate)
        {
            _q.AddLast(chunk);
            Interlocked.Add(ref _bytes, chunk.Length);
            return EvictLocked();
        }
    }

    /// <summary>Puts chunks that could not be replayed back at the front, in their original order.</summary>
    public long Restore(IReadOnlyList<byte[]> chunks)
    {
        lock (_gate)
        {
            for (var i = chunks.Count - 1; i >= 0; i--)
            {
                _q.AddFirst(chunks[i]);
                Interlocked.Add(ref _bytes, chunks[i].Length);
            }
            return EvictLocked();
        }
    }

    private long EvictLocked()
    {
        long evicted = 0;
        while (_bytes > maxBytes && _q.First is { } first)
        {
            _q.RemoveFirst();
            Interlocked.Add(ref _bytes, -first.Value.Length);
            evicted += first.Value.Length;
        }
        Discarded += evicted;
        return evicted;
    }

    public List<byte[]> Drain()
    {
        lock (_gate)
        {
            var all = _q.ToList();
            _q.Clear();
            Interlocked.Exchange(ref _bytes, 0);
            return all;
        }
    }
}

/// <summary>
/// Groups bytes read from one session into packets before they are forwarded:
///  Immediate   – forward as read (split at MaxPacketBytes).
///  IdleTimeout – wait until the line is quiet for PacketTimeoutMs or MaxPacketBytes are collected (TCP-Com's "wait for timeout").
///  Delimiter   – forward complete frames ending in the delimiter; an optional timeout flushes partial frames.
/// </summary>
internal sealed class Packetizer : IDisposable
{
    private readonly PacketMode _mode;
    private readonly int _timeoutMs;
    private readonly int _max;
    private readonly byte[] _delimiter;
    private readonly Action<byte[]> _emit;
    private readonly Lock _gate = new();
    private readonly Timer? _timer;
    private byte[] _buf = new byte[256];
    private int _count;
    private bool _disposed;

    public Packetizer(EndpointConfig cfg, Action<byte[]> emit)
    {
        _mode = cfg.PacketMode;
        _timeoutMs = cfg.PacketTimeoutMs;
        _max = cfg.MaxPacketBytes > 0 ? cfg.MaxPacketBytes : int.MaxValue;
        _delimiter = cfg.PacketMode == PacketMode.Delimiter ? DataFormat.Unescape(cfg.Delimiter) : [];
        _emit = emit;
        if (_mode != PacketMode.Immediate) _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Push(ReadOnlySpan<byte> data)
    {
        if (_mode == PacketMode.Immediate)
        {
            while (data.Length > 0)
            {
                var n = Math.Min(data.Length, _max);
                _emit(data[..n].ToArray());
                data = data[n..];
            }
            return;
        }

        lock (_gate)
        {
            if (_disposed) return;
            var scanFrom = Math.Max(0, _count - _delimiter.Length + 1);
            Append(data);

            if (_mode == PacketMode.Delimiter && _delimiter.Length > 0)
            {
                while (true)
                {
                    var idx = _buf.AsSpan(scanFrom, _count - scanFrom).IndexOf(_delimiter);
                    if (idx < 0) break;
                    EmitFront(scanFrom + idx + _delimiter.Length);
                    scanFrom = 0;
                }
            }

            while (_count >= _max) EmitFront(_max);

            if (_count > 0 && _timeoutMs > 0) _timer!.Change(_timeoutMs, Timeout.Infinite);
            else _timer!.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_count + data.Length > _buf.Length)
        {
            var size = _buf.Length;
            while (size < _count + data.Length) size *= 2;
            Array.Resize(ref _buf, size);
        }
        data.CopyTo(_buf.AsSpan(_count));
        _count += data.Length;
    }

    private void EmitFront(int n)
    {
        var packet = _buf.AsSpan(0, n).ToArray();
        _buf.AsSpan(n, _count - n).CopyTo(_buf);
        _count -= n;
        _emit(packet);
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_count > 0) EmitFront(_count);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
        }
    }
}

/// <summary>Byte counters and a one-minute throughput history for the dashboard sparklines.</summary>
public sealed class BridgeStats
{
    private const int HistoryLength = 60;
    private long _fwd, _rev, _fwdPackets, _revPackets, _dropped;
    private long _queued, _unsent, _evicted, _replayed;
    private long _lastFwd, _lastRev;
    private readonly double[] _fwdHistory = new double[HistoryLength];
    private readonly double[] _revHistory = new double[HistoryLength];
    private int _head;
    private readonly Lock _gate = new();

    public long ForwardBytes => Interlocked.Read(ref _fwd);
    public long ReverseBytes => Interlocked.Read(ref _rev);
    public long ForwardPackets => Interlocked.Read(ref _fwdPackets);
    public long ReversePackets => Interlocked.Read(ref _revPackets);
    /// <summary>All bytes that will never reach a receiver: not routable, rejected by a full/closed session,
    /// evicted from a hold buffer, or queued to a session that ended before writing them.</summary>
    public long DroppedBytes => Interlocked.Read(ref _dropped);
    /// <summary>Bytes accepted into session send queues (actual writes are per session: BytesOut).</summary>
    public long QueuedBytes => Interlocked.Read(ref _queued);
    /// <summary>Part of <see cref="DroppedBytes"/>: queued to a session that closed before writing them.</summary>
    public long UnsentBytes => Interlocked.Read(ref _unsent);
    /// <summary>Part of <see cref="DroppedBytes"/>: oldest data evicted from a full hold buffer.</summary>
    public long HoldEvictedBytes => Interlocked.Read(ref _evicted);
    /// <summary>Held bytes queued to a session when it connected.</summary>
    public long ReplayedBytes => Interlocked.Read(ref _replayed);
    public double ForwardRate { get; private set; }
    public double ReverseRate { get; private set; }

    public void AddForward(int n) { Interlocked.Add(ref _fwd, n); Interlocked.Increment(ref _fwdPackets); }
    public void AddReverse(int n) { Interlocked.Add(ref _rev, n); Interlocked.Increment(ref _revPackets); }
    public void AddDropped(long n) => Interlocked.Add(ref _dropped, n);
    public void AddQueued(long n) => Interlocked.Add(ref _queued, n);
    public void AddReplayed(long n) => Interlocked.Add(ref _replayed, n);

    public void AddUnsent(long n)
    {
        Interlocked.Add(ref _unsent, n);
        AddDropped(n);
    }

    public void AddEvicted(long n)
    {
        Interlocked.Add(ref _evicted, n);
        AddDropped(n);
    }

    /// <summary>Called once per second.</summary>
    public void Sample()
    {
        lock (_gate)
        {
            var f = ForwardBytes;
            var r = ReverseBytes;
            ForwardRate = f - _lastFwd;
            ReverseRate = r - _lastRev;
            _lastFwd = f;
            _lastRev = r;
            _fwdHistory[_head] = ForwardRate;
            _revHistory[_head] = ReverseRate;
            _head = (_head + 1) % HistoryLength;
        }
    }

    public (double[] Forward, double[] Reverse) History()
    {
        lock (_gate)
        {
            var f = new double[HistoryLength];
            var r = new double[HistoryLength];
            for (var i = 0; i < HistoryLength; i++)
            {
                f[i] = _fwdHistory[(_head + i) % HistoryLength];
                r[i] = _revHistory[(_head + i) % HistoryLength];
            }
            return (f, r);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _fwd, 0);
            Interlocked.Exchange(ref _rev, 0);
            Interlocked.Exchange(ref _fwdPackets, 0);
            Interlocked.Exchange(ref _revPackets, 0);
            Interlocked.Exchange(ref _dropped, 0);
            Interlocked.Exchange(ref _queued, 0);
            Interlocked.Exchange(ref _unsent, 0);
            Interlocked.Exchange(ref _evicted, 0);
            Interlocked.Exchange(ref _replayed, 0);
            _lastFwd = _lastRev = 0;
            Array.Clear(_fwdHistory);
            Array.Clear(_revHistory);
        }
    }
}
