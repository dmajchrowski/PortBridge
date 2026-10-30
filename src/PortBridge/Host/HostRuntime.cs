using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;
using PortBridge.Core.Security;

namespace PortBridge.Host;

public enum HostMode { Desktop, Service, Headless }

/// <summary>The engine plus the web server in front of it. The web server can be restarted (new port,
/// HTTPS, remote access) without interrupting any bridge.</summary>
public sealed class HostRuntime : IAsyncDisposable
{
    private WebApplication? _web;

    public HostMode Mode { get; }
    public BridgeEngine Engine { get; }
    public AuthService Auth { get; }
    public string? WebError { get; private set; }
    public int BoundPort { get; private set; }
    public bool BoundHttps { get; private set; }

    public event Action? WebRestarted;

    private HostRuntime(HostMode mode, BridgeEngine engine)
    {
        Mode = mode;
        Engine = engine;
        Auth = new AuthService(() => engine.Settings);
    }

    public static async Task<HostRuntime> StartAsync(HostMode mode)
    {
        var guard = DataDirGuard.Default;
        var policy = ProtectedPathPolicy.Default;
        ServiceTrustResult? trust = null;
        // The service (and any elevated console run) reads configuration that can make it run programs: it only does so
        // from a data folder it has secured, and only from an executable location non-administrators cannot change.
        if (mode == HostMode.Service || (mode == HostMode.Headless && Environment.IsPrivilegedProcess))
        {
            if (mode == HostMode.Service)
            {
                var exe = Environment.ProcessPath!;
                var findings = policy.CheckExecutable(exe, ServiceInstaller.Companions(exe));
                if (findings.Count > 0)
                    throw new InvalidOperationException($"Refusing to run as a service from an unprotected location: " +
                        string.Join("; ", findings.Take(5).Select(f => $"{f.Path}: {f.Problem}")));
            }
            trust = ServiceTrust.SecureDataFolder(guard, AppPaths.DataDir); // throws: fail closed
        }
        else
        {
            AppPaths.EnsureDataDir();
        }

        var engine = new BridgeEngine(new ConfigStore(),
            ServiceTrust.EngineOptions(trust is not null, guard, policy, () => Environment.IsPrivilegedProcess));
        await engine.StartAsync();
        if (trust is { Quarantined: true })
            engine.Journal.Add(EventLevel.Warning,
                $"{DataDirMigration.Reason} Reset: {string.Join("; ", trust.Reset)}. {ServiceTrust.BootstrapHint}", category: EventCategory.Config);
        var rt = new HostRuntime(mode, engine);
        if (!rt.Auth.WriteLocalTokenFile())
            engine.Journal.Add(EventLevel.Warning,
                "The local sign-in token could not be written to a new file (another program holds the old one open); the desktop window will ask for the password.",
                category: EventCategory.Config);
        await rt.StartWebAsync();
        return rt;
    }

    public string LocalUrl => $"{(BoundHttps ? "https" : "http")}://localhost:{BoundPort}";

    private async Task StartWebAsync()
    {
        var s = Engine.Settings;
        try
        {
            _web = WebServer.Build(this);
            await _web.StartAsync();
            BoundPort = s.WebPort;
            BoundHttps = s.UseHttps;
            WebError = null;
            Engine.Journal.Add(EventLevel.Info,
                $"Dashboard available at {LocalUrl}{(s.AllowRemote ? " (remote access enabled)" : "")}", category: EventCategory.Config);
        }
        catch (Exception ex)
        {
            WebError = $"The dashboard could not start on port {s.WebPort}: {ex.GetBaseException().Message}";
            Engine.Journal.Add(EventLevel.Error, WebError, category: EventCategory.Config);
            if (_web is not null) await _web.DisposeAsync();
            _web = null;
        }
    }

    public async Task RestartWebAsync()
    {
        if (_web is not null)
        {
            await StopWebAsync(_web);
            _web = null;
        }
        await StartWebAsync();
        WebRestarted?.Invoke();
    }

    /// <summary>Live dashboard sockets close themselves when the server stops; anything still open after a few seconds
    /// is cut off rather than waited for (the default would hold a restart or service stop for 30 s).</summary>
    private static async Task StopWebAsync(WebApplication web)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await web.StopAsync(timeout.Token); } catch { /* ignore */ }
        await web.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_web is not null) await StopWebAsync(_web);
        await Engine.DisposeAsync();
    }
}
