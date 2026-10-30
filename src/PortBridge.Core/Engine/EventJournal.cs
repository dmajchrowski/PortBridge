using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using PortBridge.Core.Security;

namespace PortBridge.Core.Engine;

[JsonConverter(typeof(JsonStringEnumConverter<EventLevel>))]
public enum EventLevel { Info, Warning, Error }

public sealed record EngineEvent(
    long Id,
    DateTimeOffset Time,
    EventLevel Level,
    string? BridgeId,
    string? BridgeName,
    string Message,
    string Category);

public static class EventCategory
{
    public const string General = "general";
    public const string Connection = "connection";
    public const string Config = "config";
}

/// <summary>In-memory ring of recent events plus a daily log file (TCP-Com's ErrorLog.txt, but for everything).</summary>
public sealed class EventJournal : IAsyncDisposable
{
    private const int Capacity = 5000;
    private readonly LinkedList<EngineEvent> _ring = new();
    private readonly Lock _gate = new();
    private readonly Channel<EngineEvent> _fileQueue = Channel.CreateUnbounded<EngineEvent>(new() { SingleReader = true });
    private readonly Task _writer;
    private long _nextId;

    public string? LogDirectory { get; set; }
    /// <summary>Used when <see cref="LogDirectory"/> cannot be written safely, e.g. because it was replaced by a link.</summary>
    public string? FallbackDirectory { get; set; }
    public bool WriteToFile { get; set; } = true;
    public int RetentionDays { get; set; } = 30;

    public event Action<EngineEvent>? Added;

    public EventJournal(string? logDirectory = null, string? fallbackDirectory = null)
    {
        LogDirectory = logDirectory;
        FallbackDirectory = fallbackDirectory;
        _writer = Task.Run(WriteLoopAsync);
    }

    public EngineEvent Add(EventLevel level, string message, string? bridgeId = null, string? bridgeName = null,
        string category = EventCategory.General)
    {
        var e = new EngineEvent(Interlocked.Increment(ref _nextId), DateTimeOffset.Now, level, bridgeId, bridgeName, message, category);
        lock (_gate)
        {
            _ring.AddLast(e);
            if (_ring.Count > Capacity) _ring.RemoveFirst();
        }
        if (WriteToFile && LogDirectory is not null) _fileQueue.Writer.TryWrite(e);
        try { Added?.Invoke(e); } catch { /* listeners must not break the engine */ }
        return e;
    }

    public List<EngineEvent> Query(int limit = 200, string? bridgeId = null, EventLevel? minLevel = null, long? beforeId = null)
    {
        var result = new List<EngineEvent>(Math.Min(limit, 500));
        lock (_gate)
        {
            for (var node = _ring.Last; node is not null && result.Count < limit; node = node.Previous)
            {
                var e = node.Value;
                if (beforeId is { } b && e.Id >= b) continue;
                if (bridgeId is not null && e.BridgeId != bridgeId) continue;
                if (minLevel is { } m && e.Level < m) continue;
                result.Add(e);
            }
        }
        return result;
    }

    public void Clear()
    {
        lock (_gate) _ring.Clear();
    }

    private async Task WriteLoopAsync()
    {
        DateOnly? lastCleanup = null;
        string? refused = null; // why the log folder was last refused; reported once until it changes
        await foreach (var e in _fileQueue.Reader.ReadAllAsync())
        {
            if (LogDirectory is null) continue;
            var today = DateOnly.FromDateTime(e.Time.LocalDateTime);
            var name = $"events-{today:yyyyMMdd}.log";
            var bridge = e.BridgeName is null ? "" : $"[{e.BridgeName}] ";
            var line = Encoding.UTF8.GetBytes($"{e.Time:yyyy-MM-dd HH:mm:ss.fff} {e.Level.ToString().ToUpperInvariant(),-7} {bridge}{e.Message}{Environment.NewLine}");
            try
            {
                await AppendAsync(LogDirectory, name, line);
                refused = null;
            }
            catch (Exception ex) when (FallbackDirectory is not null)
            {
                try { await AppendAsync(FallbackDirectory, name, line); } catch { /* the in-memory journal still works */ }
                if (refused != ex.Message)
                {
                    refused = ex.Message;
                    Add(EventLevel.Warning, $"The log folder {LogDirectory} cannot be used ({ex.Message}); logs are written to {FallbackDirectory} instead.",
                        category: EventCategory.Config);
                }
            }
            catch
            {
                // Disk full / permissions: the in-memory journal still works.
            }

            if (lastCleanup != today)
            {
                lastCleanup = today;
                Cleanup(LogDirectory, "events-*.log", RetentionDays);
                if (FallbackDirectory is not null) Cleanup(FallbackDirectory, "events-*.log", RetentionDays);
            }
        }
    }

    /// <summary>Other users may edit or delete the file meanwhile; it is opened for each line.</summary>
    private static async Task AppendAsync(string dir, string name, byte[] line)
    {
        await using var file = SafeLogFiles.OpenAppend(dir, null, name);
        await file.WriteAsync(line);
    }

    internal static void Cleanup(string dir, string pattern, int days)
    {
        if (days <= 0) return;
        try { SafeLogFiles.DeleteOlderThan(dir, pattern, DateTime.Now.AddDays(-days)); } catch { /* next day */ }
    }

    public async ValueTask DisposeAsync()
    {
        _fileQueue.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(2000));
    }
}
