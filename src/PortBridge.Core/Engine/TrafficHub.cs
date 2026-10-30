using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using PortBridge.Core.Config;
using PortBridge.Core.Security;
using PortBridge.Core.Util;

namespace PortBridge.Core.Engine;

/// <summary>Forward = from the bridge's source endpoint to its targets; Reverse = from targets back to the source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrafficDirection>))]
public enum TrafficDirection { Forward, Reverse }

public sealed record TrafficRecord(
    long Seq,
    DateTimeOffset Time,
    string BridgeId,
    TrafficDirection Direction,
    string Source,
    byte[] Data,
    bool Injected);

/// <summary>Distributes captured traffic to live monitors, keeps a short history per bridge, and writes traffic logs.</summary>
public sealed class TrafficHub : IAsyncDisposable
{
    private const int HistoryRecords = 400;
    private const int HistoryBytes = 256 * 1024;

    private readonly ConcurrentDictionary<string, History> _history = new();
    private readonly ConcurrentDictionary<string, TrafficFileLogger> _loggers = new();
    private long _seq;

    /// <summary>Traffic logs go to the "traffic" folder inside these log folders (see <see cref="AppPaths.LogDir"/>).</summary>
    public string LogRoot { get; set; } = AppPaths.LogDir;
    public string? FallbackRoot { get; set; } = AppPaths.FallbackLogDir;
    /// <summary>Reports that the shared log folder could not be used.</summary>
    public Action<string>? Warn { get; set; }
    /// <summary>How long traffic is collected before it is written in one go (the file is closed in between).</summary>
    internal TimeSpan LogBatchDelay { get; set; } = TimeSpan.FromSeconds(1);

    public event Action<TrafficRecord>? Recorded;

    public void Record(BridgeConfig bridge, TrafficDirection dir, string source, byte[] data, bool injected = false)
    {
        var rec = new TrafficRecord(Interlocked.Increment(ref _seq), DateTimeOffset.Now, bridge.Id, dir, source, data, injected);
        _history.GetOrAdd(bridge.Id, _ => new History()).Add(rec);

        if (bridge.TrafficLog.Enabled)
        {
            var logger = _loggers.GetOrAdd(bridge.Id, _ => new TrafficFileLogger(LogRoot, FallbackRoot, Warn, LogBatchDelay));
            logger.Write(bridge, rec);
        }
        try { Recorded?.Invoke(rec); } catch { /* ignore */ }
    }

    public List<TrafficRecord> Recent(string bridgeId) =>
        _history.TryGetValue(bridgeId, out var h) ? h.Snapshot() : [];

    public void Clear(string bridgeId) => _history.TryRemove(bridgeId, out _);

    public async Task CloseLoggerAsync(string bridgeId)
    {
        if (_loggers.TryRemove(bridgeId, out var l)) await l.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var l in _loggers.Values) await l.DisposeAsync();
        _loggers.Clear();
    }

    private sealed class History
    {
        private readonly Queue<TrafficRecord> _q = new();
        private readonly Lock _gate = new();
        private int _bytes;

        public void Add(TrafficRecord r)
        {
            lock (_gate)
            {
                _q.Enqueue(r);
                _bytes += r.Data.Length;
                while (_q.Count > HistoryRecords || (_bytes > HistoryBytes && _q.Count > 1))
                    _bytes -= _q.Dequeue().Data.Length;
            }
        }

        public List<TrafficRecord> Snapshot()
        {
            lock (_gate) return [.. _q];
        }
    }
}

/// <summary>Daily traffic log per bridge, written on a background task so slow disks never stall data.</summary>
internal sealed class TrafficFileLogger : IAsyncDisposable
{
    private readonly Channel<(BridgeConfig Bridge, TrafficRecord Rec)> _queue =
        Channel.CreateBounded<(BridgeConfig, TrafficRecord)>(new BoundedChannelOptions(20_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly Task _loop;
    private readonly string _root;
    private readonly string? _fallbackRoot;
    private readonly Action<string>? _warn;
    private readonly TimeSpan _batchDelay;
    private readonly CancellationTokenSource _closing = new();
    private string? _refused;
    private string? _base;
    private int _index; // part of today's log being filled: name.log, name_1.log, …

    public TrafficFileLogger(string root, string? fallbackRoot, Action<string>? warn, TimeSpan batchDelay)
    {
        _root = root;
        _fallbackRoot = fallbackRoot;
        _warn = warn;
        _batchDelay = batchDelay;
        _loop = Task.Run(LoopAsync);
    }

    public void Write(BridgeConfig bridge, TrafficRecord rec) => _queue.Writer.TryWrite((bridge, rec));

    private static string Safe(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "bridge" : sb.ToString();
    }

    /// <summary>
    /// Writes in bursts: traffic is collected for a moment, then the file is opened, appended to and closed again.
    /// Between bursts nothing holds the file, so any viewer can open it (most only share reading, which Windows refuses
    /// while another program has the file open for writing), and a log someone deleted is simply created again.
    /// </summary>
    private async Task LoopAsync()
    {
        var lines = new List<(string Base, long MaxBytes, string Line)>();
        while (await _queue.Reader.WaitToReadAsync())
        {
            try { await Task.Delay(_batchDelay, _closing.Token); } catch (OperationCanceledException) { /* closing: write now */ }
            while (_queue.Reader.TryRead(out var item)) lines.Add(Format(item.Bridge, item.Rec));
            try { await WriteBatchAsync(lines); } catch { /* disk full / permissions: the live monitor still works */ }
            lines.Clear();
        }
    }

    private static (string Base, long MaxBytes, string Line) Format(BridgeConfig bridge, TrafficRecord rec)
    {
        var arrow = rec.Direction == TrafficDirection.Forward ? ">>" : "<<";
        var tag = rec.Injected ? $"{rec.Source} (sent)" : rec.Source;
        var body = bridge.TrafficLog.Format == TrafficLogFormat.Hex
            ? $"{DataFormat.ToHex(rec.Data)}  |{DataFormat.ToEscapedText(rec.Data)}|"
            : DataFormat.ToEscapedText(rec.Data);
        return ($"{Safe(bridge.Name)}-{bridge.Id}-{rec.Time:yyyyMMdd}", Math.Max(1, bridge.TrafficLog.MaxFileMb) * 1024L * 1024L,
            $"{rec.Time:HH:mm:ss.fff} {arrow} [{tag}] ({rec.Data.Length}) {body}");
    }

    private async Task WriteBatchAsync(List<(string Base, long MaxBytes, string Line)> lines)
    {
        for (var i = 0; i < lines.Count;)
        {
            var (name, max, _) = lines[i];
            if (name != _base) (_base, _index) = (name, 0);
            await using var file = OpenPart(name, max);
            await using var writer = new StreamWriter(file, Encoding.UTF8, 4096, leaveOpen: true);
            // Fill this part; when it is full the next round opens the next one.
            for (var size = file.Length; i < lines.Count && lines[i].Base == name && size <= max; i++)
            {
                await writer.WriteLineAsync(lines[i].Line);
                size += Encoding.UTF8.GetByteCount(lines[i].Line) + writer.NewLine.Length;
            }
        }
    }

    /// <summary>The first part of today's log that still has room.</summary>
    private FileStream OpenPart(string name, long max)
    {
        for (; ; _index++)
        {
            var file = Open(_index == 0 ? $"{name}.log" : $"{name}_{_index}.log");
            if (file.Length <= max) return file;
            file.Dispose();
        }
    }

    /// <summary>Opens a traffic log in the shared log folder, or in the fallback folder when the shared one is unsafe.</summary>
    private FileStream Open(string name)
    {
        try
        {
            var stream = SafeLogFiles.OpenAppend(_root, AppPaths.TrafficSubfolder, name);
            _refused = null;
            return stream;
        }
        catch (Exception ex) when (_fallbackRoot is not null)
        {
            if (_refused != ex.Message)
            {
                _refused = ex.Message;
                _warn?.Invoke($"The log folder {_root} cannot be used for traffic logs ({ex.Message}); writing them to {_fallbackRoot} instead.");
            }
            return SafeLogFiles.OpenAppend(_fallbackRoot, AppPaths.TrafficSubfolder, name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _closing.Cancel();
        await Task.WhenAny(_loop, Task.Delay(2000));
    }
}
