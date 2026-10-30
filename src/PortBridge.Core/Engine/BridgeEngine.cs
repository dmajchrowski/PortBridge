using PortBridge.Core.Config;
using PortBridge.Core.Serial;

namespace PortBridge.Core.Engine;

public sealed class ValidationException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Host policy for things the engine may execute.</summary>
public sealed class EngineOptions
{
    /// <summary>Why configured commands must not run right now (null = allowed).</summary>
    public Func<string?>? CommandBlocked { get; init; }
    /// <summary>Why a program (e.g. com0com's setupc.exe) at a path must not be run by this process (null = allowed).</summary>
    public Func<string, string?>? RejectExecutable { get; init; }
    /// <summary>Why com0com ownership records must not be trusted right now (null = trusted).</summary>
    public Func<string?>? OwnershipUntrusted { get; init; }
}

/// <summary>
/// Owns the configuration and every running bridge. All mutations go through here so the
/// on-disk config, the running bridges and the UI stay consistent.
/// </summary>
public sealed class BridgeEngine : IAsyncDisposable
{
    private readonly ConfigStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Bridge> _running = new();
    private readonly CertificateProvider _certs = new();
    private readonly Lock _cfgLock = new();
    private AppConfig _config = new();
    private Timer? _sampler;

    /// <summary>Copy of the bridge list, safe to enumerate while edits happen.</summary>
    private BridgeConfig[] Bridges()
    {
        lock (_cfgLock) return [.. _config.Bridges];
    }

    public EventJournal Journal { get; }
    public TrafficHub Traffic { get; } = new();
    public Com0ComManager Com0Com { get; }
    public Notifier Notifier { get; }
    public CertificateProvider Certificates => _certs;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public BridgeEngine(ConfigStore store, EngineOptions? options = null)
    {
        _store = store;
        Journal = new EventJournal(AppPaths.LogDir, AppPaths.FallbackLogDir);
        Traffic.Warn = msg => Journal.Add(EventLevel.Warning, msg, category: EventCategory.Config);
        Com0Com = new Com0ComManager(() => _config.Settings.Com0ComPath, new Com0ComOptions
        {
            RejectExecutable = options?.RejectExecutable,
            OwnershipUntrusted = options?.OwnershipUntrusted,
            Log = msg => Journal.Add(EventLevel.Warning, msg, category: EventCategory.Config),
        });
        Notifier = new Notifier(Journal, () => _config.Settings, commandBlocked: options?.CommandBlocked);
    }

    public AppSettings Settings => _config.Settings;

    public async Task StartAsync()
    {
        _config = _store.Load();
        ApplyJournalSettings();
        Journal.Add(EventLevel.Info, $"PortBridge engine started ({_config.Bridges.Count} bridge{(_config.Bridges.Count == 1 ? "" : "s")} configured)", category: EventCategory.Config);
        foreach (var b in _config.Bridges.Where(b => b.Active))
        {
            try { await StartRuntimeAsync(b); }
            catch (Exception ex) { Journal.Add(EventLevel.Error, $"Could not start: {ex.Message}", b.Id, b.Name); }
        }
        _sampler = new Timer(_ => Sample(), null, 1000, 1000);
    }

    private void ApplyJournalSettings()
    {
        Journal.WriteToFile = _config.Settings.WriteEventLogFile;
        Journal.RetentionDays = _config.Settings.LogRetentionDays;
    }

    private void Sample()
    {
        Bridge[] bridges;
        lock (_running) bridges = [.. _running.Values];
        foreach (var b in bridges) b.Stats.Sample();
    }

    // ---- Queries ----

    public List<BridgeView> Snapshot()
    {
        var list = new List<BridgeView>();
        foreach (var cfg in Bridges())
        {
            Bridge? rt;
            lock (_running) _running.TryGetValue(cfg.Id, out rt);
            list.Add(ViewBuilder.Build(cfg, rt));
        }
        return list;
    }

    public BridgeView? Get(string id)
    {
        var cfg = Bridges().FirstOrDefault(b => b.Id == id);
        if (cfg is null) return null;
        Bridge? rt;
        lock (_running) _running.TryGetValue(id, out rt);
        return ViewBuilder.Build(cfg, rt);
    }

    public Bridge? Runtime(string id)
    {
        lock (_running) return _running.GetValueOrDefault(id);
    }

    public AppConfig ExportConfig()
    {
        lock (_cfgLock) return Json.Clone(_config);
    }

    public HashSet<string> UsedPortNames() =>
        Bridges().SelectMany(b => b.AllEndpoints()).Where(e => e.IsSerial)
            .Select(e => e.PortName.ToUpperInvariant()).ToHashSet();

    // ---- Mutations ----

    private async Task<T> Locked<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync();
        try { return await action(); }
        finally { _gate.Release(); }
    }

    private Task Locked(Func<Task> action) => Locked(async () => { await action(); return 0; });

    private void Save()
    {
        lock (_cfgLock) _store.Save(_config);
    }

    /// <summary>Creates or replaces a bridge. A running bridge is restarted with the new settings.</summary>
    public Task<BridgeView> SaveBridgeAsync(BridgeConfig incoming) => Locked(async () =>
    {
        if (string.IsNullOrWhiteSpace(incoming.Id)) incoming.Id = EndpointConfig.NewId();
        incoming.Name = incoming.Name?.Trim() ?? "";
        incoming.Targets ??= [];
        foreach (var e in incoming.AllEndpoints())
        {
            if (string.IsNullOrWhiteSpace(e.Id)) e.Id = EndpointConfig.NewId();
            e.PortName = (e.PortName ?? "").Trim().ToUpperInvariant();
            e.Host = (e.Host ?? "").Trim();
            e.AllowedClients = (e.AllowedClients ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            e.Tls ??= new TlsOptions();
        }
        incoming.TrafficLog ??= new TrafficLogOptions();

        var errors = Validator.Validate(incoming, _config.Bridges);
        if (errors.Count > 0) throw new ValidationException(errors);

        var idx = _config.Bridges.FindIndex(b => b.Id == incoming.Id);
        var wasRunning = Runtime(incoming.Id) is not null;
        if (idx >= 0)
        {
            incoming.Active = _config.Bridges[idx].Active;
            if (wasRunning) await StopRuntimeAsync(incoming.Id);
            lock (_cfgLock) _config.Bridges[idx] = incoming;
            Journal.Add(EventLevel.Info, "Settings changed", incoming.Id, incoming.Name, EventCategory.Config);
        }
        else
        {
            lock (_cfgLock) _config.Bridges.Add(incoming);
            Journal.Add(EventLevel.Info, "Bridge created", incoming.Id, incoming.Name, EventCategory.Config);
        }
        Save();
        if (wasRunning || (idx < 0 && incoming.Active)) await StartRuntimeAsync(incoming);
        return Get(incoming.Id)!;
    });

    public Task DeleteBridgeAsync(string id) => Locked(async () =>
    {
        var cfg = _config.Bridges.FirstOrDefault(b => b.Id == id) ?? throw new KeyNotFoundException();
        await StopRuntimeAsync(id);
        lock (_cfgLock) _config.Bridges.Remove(cfg);
        Traffic.Clear(id);
        await Traffic.CloseLoggerAsync(id);
        Save();
        Journal.Add(EventLevel.Info, $"Bridge \"{cfg.Name}\" deleted", category: EventCategory.Config);
    });

    public Task<BridgeView> DuplicateAsync(string id) => Locked(() =>
    {
        var src = _config.Bridges.FirstOrDefault(b => b.Id == id) ?? throw new KeyNotFoundException();
        var copy = Json.Clone(src);
        copy.Id = EndpointConfig.NewId();
        copy.Name = src.Name + " (copy)";
        copy.Active = false;
        foreach (var e in copy.AllEndpoints()) e.Id = EndpointConfig.NewId();
        // Exclusive resources (ports) will clash; the copy is saved inactive so the user can edit it first.
        lock (_cfgLock) _config.Bridges.Add(copy);
        Save();
        return Task.FromResult(ViewBuilder.Build(copy, null));
    });

    public Task SetActiveAsync(string id, bool active) => Locked(async () =>
    {
        var cfg = _config.Bridges.FirstOrDefault(b => b.Id == id) ?? throw new KeyNotFoundException();
        cfg.Active = active;
        Save();
        if (active) await StartRuntimeAsync(cfg);
        else await StopRuntimeAsync(id);
    });

    public Task RestartAsync(string id) => Locked(async () =>
    {
        var cfg = _config.Bridges.FirstOrDefault(b => b.Id == id) ?? throw new KeyNotFoundException();
        await StopRuntimeAsync(id);
        cfg.Active = true;
        Save();
        await StartRuntimeAsync(cfg);
    });

    public Task SetAllActiveAsync(bool active) => Locked(async () =>
    {
        foreach (var cfg in _config.Bridges)
        {
            cfg.Active = active;
            if (active) await StartRuntimeAsync(cfg);
            else await StopRuntimeAsync(cfg.Id);
        }
        Save();
    });

    public Task UpdateSettingsAsync(Action<AppSettings> update) => Locked(() =>
    {
        update(_config.Settings);
        ApplyJournalSettings();
        Save();
        return Task.CompletedTask;
    });

    /// <summary>Replaces (or merges into) the whole configuration, e.g. restoring a backup.</summary>
    public Task ImportAsync(AppConfig imported, bool replace) => Locked(async () =>
    {
        foreach (var id in _config.Bridges.Select(b => b.Id).ToList()) await StopRuntimeAsync(id);
        if (replace)
        {
            var keepPassword = _config.Settings.PasswordHash;
            imported.Settings ??= new AppSettings();
            imported.Bridges ??= [];
            lock (_cfgLock) _config = imported;
            _config.Settings.PasswordHash ??= keepPassword;
        }
        else
        {
            foreach (var b in imported.Bridges)
            {
                b.Id = EndpointConfig.NewId();
                b.Active = false;
                lock (_cfgLock) _config.Bridges.Add(b);
            }
        }
        _config.Bridges ??= [];
        ApplyJournalSettings();
        Save();
        Journal.Add(EventLevel.Info, $"Configuration imported ({imported.Bridges.Count} bridges)", category: EventCategory.Config);
        foreach (var b in _config.Bridges.Where(b => b.Active))
        {
            try { await StartRuntimeAsync(b); }
            catch (Exception ex) { Journal.Add(EventLevel.Error, $"Could not start: {ex.Message}", b.Id, b.Name); }
        }
    });

    private async Task StartRuntimeAsync(BridgeConfig cfg)
    {
        if (Runtime(cfg.Id) is not null) return;
        var ctx = new BridgeContext(cfg.Id, cfg.Name, Journal, Com0Com, _certs);
        var bridge = new Bridge(cfg, ctx, Traffic);
        lock (_running) _running[cfg.Id] = bridge;
        await bridge.StartAsync();
    }

    private async Task StopRuntimeAsync(string id)
    {
        Bridge? b;
        lock (_running)
        {
            if (!_running.Remove(id, out b)) return;
        }
        await b.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _sampler?.Dispose();
        Bridge[] all;
        lock (_running)
        {
            all = [.. _running.Values];
            _running.Clear();
        }
        await Task.WhenAll(all.Select(b => b.StopAsync()));
        Journal.Add(EventLevel.Info, "PortBridge engine stopped", category: EventCategory.Config);
        Notifier.Dispose();
        await Traffic.DisposeAsync();
        await Journal.DisposeAsync();
    }
}
