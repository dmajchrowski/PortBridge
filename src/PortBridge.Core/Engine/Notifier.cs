using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using PortBridge.Core.Config;

namespace PortBridge.Core.Engine;

/// <summary>
/// Reacts to journal events: runs the configured command on errors (TCP-Com's "run program on error")
/// and posts to a webhook. Throttled per bridge so a flapping link can't spawn hundreds of processes.
/// </summary>
public sealed class Notifier : IDisposable
{
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(60);
    private readonly EventJournal _journal;
    private readonly Func<AppSettings> _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCommand = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastHook = new();
    private readonly Func<ProcessStartInfo, IDisposable?> _start;
    private readonly Func<string?> _commandBlocked;

    /// <param name="startProcess">Process boundary (tests record instead of starting).</param>
    /// <param name="commandBlocked">Returns why commands must not run in this process right now (null = allowed).
    /// The service uses it to refuse commands from configuration it has not verified as administrator-controlled.</param>
    public Notifier(EventJournal journal, Func<AppSettings> settings,
        Func<ProcessStartInfo, IDisposable?>? startProcess = null, Func<string?>? commandBlocked = null)
    {
        _journal = journal;
        _settings = settings;
        _start = startProcess ?? (psi => Process.Start(psi));
        _commandBlocked = commandBlocked ?? (() => null);
        _journal.Added += OnEvent;
    }

    /// <summary>Always the system's cmd.exe — never one found through the application folder, working directory or PATH.</summary>
    internal static string CommandInterpreter => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static bool Allow(ConcurrentDictionary<string, DateTimeOffset> map, string key)
    {
        var now = DateTimeOffset.Now;
        if (map.TryGetValue(key, out var last) && now - last < Throttle) return false;
        map[key] = now;
        return true;
    }

    private void OnEvent(EngineEvent e)
    {
        if (e.Category == "notifier") return; // never react to our own messages
        var s = _settings();
        var key = e.BridgeId ?? "_";

        if (e.Level == EventLevel.Error && !string.IsNullOrWhiteSpace(s.OnErrorCommand) && Allow(_lastCommand, key))
            RunCommand(s.OnErrorCommand!, e);

        var wantHook = e.Level >= EventLevel.Warning || (s.WebhookIncludeConnections && e.Category == EventCategory.Connection);
        var throttled = !(e.Level == EventLevel.Info && e.Category == EventCategory.Connection);
        if (wantHook && !string.IsNullOrWhiteSpace(s.WebhookUrl) && (!throttled || Allow(_lastHook, key + e.Level)))
            _ = PostAsync(s.WebhookUrl!, e);
    }

    private void RunCommand(string command, EngineEvent e)
    {
        if (_commandBlocked() is { } why)
        {
            _journal.Add(EventLevel.Warning, $"Error command not run: {why}", category: "notifier");
            return;
        }
        try
        {
            var expanded = command
                .Replace("%BRIDGE%", e.BridgeName ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("%MESSAGE%", e.Message.Replace("\"", "'"), StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(CommandInterpreter)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppPaths.DataDir,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(expanded);
            psi.Environment["PORTBRIDGE_BRIDGE"] = e.BridgeName ?? "";
            psi.Environment["PORTBRIDGE_MESSAGE"] = e.Message;
            psi.Environment["PORTBRIDGE_LEVEL"] = e.Level.ToString();
            psi.Environment["PORTBRIDGE_TIME"] = e.Time.ToString("O");
            using var p = _start(psi);
        }
        catch (Exception ex)
        {
            _journal.Add(EventLevel.Warning, $"Error command could not be started: {ex.Message}", category: "notifier");
        }
    }

    private async Task<string?> PostAsync(string url, EngineEvent e)
    {
        try
        {
            var text = $"[{Environment.MachineName}] {(e.BridgeName is null ? "" : e.BridgeName + ": ")}{e.Message}";
            using var resp = await _http.PostAsJsonAsync(url, new
            {
                text,
                level = e.Level.ToString().ToLowerInvariant(),
                bridge = e.BridgeName,
                bridgeId = e.BridgeId,
                message = e.Message,
                category = e.Category,
                time = e.Time,
                host = Environment.MachineName,
            });
            if (resp.IsSuccessStatusCode) return null;
            var err = $"Webhook returned HTTP {(int)resp.StatusCode}";
            _journal.Add(EventLevel.Warning, err, category: "notifier");
            return err;
        }
        catch (Exception ex)
        {
            var err = $"Webhook failed: {ex.Message}";
            _journal.Add(EventLevel.Warning, err, category: "notifier");
            return err;
        }
    }

    /// <summary>Returns null on success, otherwise the error.</summary>
    public Task<string?> TestWebhookAsync(string url) =>
        PostAsync(url, new EngineEvent(0, DateTimeOffset.Now, EventLevel.Info, null, null, "Test notification from PortBridge", "test"));

    public void Dispose()
    {
        _journal.Added -= OnEvent;
        _http.Dispose();
    }
}
