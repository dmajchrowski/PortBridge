using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using PortBridge.Core.Config;
using PortBridge.Core.Security;
using PortBridge.Host;

namespace PortBridge.Desktop;

/// <summary>
/// Desktop shell: tray icon + dashboard window. If the Windows service is running the window just connects
/// to it; otherwise the engine runs inside this process (like TCP-Com) until the user installs the service.
/// </summary>
internal sealed class DesktopApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly Icon _icon;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly EventWaitHandle _showSignal;
    private readonly RegisteredWaitHandle _showWait;
    private readonly bool _startMinimized;
    private HostRuntime? _embedded;
    private CancellationTokenSource? _connecting;
    private string _baseUrl = "";
    private int _activeBridges;
    private bool _notifications = true;
    private bool _toldAboutTray;
    private bool _busy;

    public static int Run(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = new Mutex(true, @"Local\PortBridge.Desktop", out var first);
        if (!first)
        {
            // Already running: bring the existing window forward instead.
            try { EventWaitHandle.OpenExisting(@"Local\PortBridge.Show").Set(); } catch { /* ignore */ }
            return 0;
        }
        Application.Run(new DesktopApp(args.Contains("--minimized")));
        GC.KeepAlive(mutex);
        return 0;
    }

    private DesktopApp(bool startMinimized)
    {
        _startMinimized = startMinimized;
        _icon = LoadIcon();
        _form = new MainForm(_icon);
        _form.MessageReceived += OnPageMessage;
        _form.ExitRequested += () => _ = ExitAsync();
        _form.HiddenToTray += () =>
        {
            if (_toldAboutTray) return;
            _toldAboutTray = true;
            _tray!.ShowBalloonTip(4000, "PortBridge is still running", "Bridges keep running in the background. Double-click the tray icon to reopen.", ToolTipIcon.Info);
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PortBridge", null, (_, _) => _form.BringToFrontNow()).Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add("Open in browser", null, (_, _) => OpenBrowser());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start all bridges", null, (_, _) => _form.Post(new { type = "command", command = "startAll" }));
        menu.Items.Add("Stop all bridges", null, (_, _) => _form.Post(new { type = "command", command = "stopAll" }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _ = ExitAsync());

        _tray = new NotifyIcon { Icon = _icon, Text = "PortBridge", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => _form.BringToFrontNow();
        _tray.BalloonTipClicked += (_, _) => _form.BringToFrontNow();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PortBridge.Show");
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => _form.BeginInvoke(new Action(_form.BringToFrontNow)), null, -1, false);

        _ = _form.Handle; // creates the UI SynchronizationContext before any await
        if (!_startMinimized) _form.Show();
        _form.BeginInvoke(new Action(() => _ = StartAsync()));
    }

    private static Icon LoadIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("portbridge.ico");
        return s is null ? SystemIcons.Application : new Icon(s);
    }

    private bool ServiceMode => _embedded is null;

    private async Task StartAsync()
    {
        MigrateRunKey();
        await _form.InitAsync();
        if (ServiceControl.IsRunning) await ConnectToServiceAsync();
        else await StartEmbeddedAsync();
    }

    private async Task StartEmbeddedAsync()
    {
        _connecting?.Cancel();
        _form.ShowStatus("Starting the PortBridge engine…");
        try
        {
            _embedded = await HostRuntime.StartAsync(HostMode.Desktop);
        }
        catch (Exception ex)
        {
            _form.ShowStatus("PortBridge could not start:\n" + ex.Message);
            return;
        }
        _embedded.WebRestarted += () => _form.BeginInvoke(new Action(NavigateEmbedded));
        _notifications = _embedded.Engine.Settings.DesktopNotifications;
        _form.CloseToTray = _embedded.Engine.Settings.CloseToTray;
        if (_embedded.WebError is { } err)
        {
            _form.ShowStatus(err + "\n\nChange the dashboard port in %ProgramData%\\PortBridge\\config.json (settings.webPort) or stop the program using it.");
            return;
        }
        NavigateEmbedded();
    }

    private void NavigateEmbedded()
    {
        if (_embedded is null || _embedded.WebError is not null) return;
        _baseUrl = _embedded.LocalUrl;
        _form.Navigate($"{_baseUrl}/auth/local?token={_embedded.Auth.LocalToken}");
    }

    private async Task ConnectToServiceAsync(int timeoutSeconds = 20)
    {
        _form.ShowStatus("Connecting to the PortBridge service…");
        AppSettings cfg;
        try
        {
            cfg = new ConfigStore().Load().Settings;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // The service keeps its configuration administrator-only; without it, assume the default dashboard address
            // (a custom port or HTTPS is not discovered: open the dashboard in a browser, or run PortBridge as administrator).
            cfg = new AppSettings();
        }
        _baseUrl = $"{(cfg.UseHttps ? "https" : "http")}://localhost:{cfg.WebPort}";
        _form.CloseToTray = cfg.CloseToTray;
        _notifications = cfg.DesktopNotifications;

        _connecting?.Cancel();
        var connecting = _connecting = new CancellationTokenSource();
        if (await WaitForDashboardAsync(TimeSpan.FromSeconds(timeoutSeconds), 500, connecting.Token))
        {
            NavigateToService();
            return;
        }
        if (connecting.IsCancellationRequested) return;
        _form.ShowStatus($"The PortBridge service's dashboard does not answer at {_baseUrl} yet. PortBridge keeps trying.\n" +
            "If this lasts, check that the PortBridge service is running, and look in the Windows event log: the service refuses " +
            "to start from an unprotected folder or with an unsecurable data folder.");
        _ = KeepTryingAsync(connecting.Token);
    }

    private async Task KeepTryingAsync(CancellationToken ct)
    {
        if (await WaitForDashboardAsync(Timeout.InfiniteTimeSpan, 2000, ct)) NavigateToService();
    }

    private async Task<bool> WaitForDashboardAsync(TimeSpan timeout, int intervalMs, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        var deadline = timeout == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : DateTime.UtcNow + timeout;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var r = await http.GetAsync($"{_baseUrl}/api/auth/status", ct);
                if (r.IsSuccessStatusCode) return true;
            }
            catch { /* not up yet */ }
            if (DateTime.UtcNow > deadline) return false;
            try { await Task.Delay(intervalMs, ct); }
            catch (OperationCanceledException) { }
        }
        return false;
    }

    private void NavigateToService()
    {
        // The token file is readable by Administrators only, and PortBridge always runs as administrator.
        var token = AuthService.ReadLocalTokenFile();
        _form.Navigate(token is null ? _baseUrl + "/#/login" : $"{_baseUrl}/auth/local?token={token}");
    }

    // ---------------- messages from the dashboard ----------------

    private void OnPageMessage(JsonElement msg)
    {
        var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "hello":
                if (msg.TryGetProperty("closeToTray", out var ct)) _form.CloseToTray = ct.GetBoolean();
                if (msg.TryGetProperty("notifications", out var n)) _notifications = n.GetBoolean();
                if (msg.TryGetProperty("active", out var a)) _activeBridges = a.GetInt32();
                if (msg.TryGetProperty("total", out var total))
                    _tray.Text = Truncate($"PortBridge — {_activeBridges} of {total.GetInt32()} bridges running", 63);
                SendShellInfo();
                break;

            case "notify":
                if (!_notifications || (_form.Visible && _form.ContainsFocus)) break;
                var level = msg.TryGetProperty("level", out var l) ? l.GetString() : "info";
                _tray.ShowBalloonTip(5000, Get(msg, "title") ?? "PortBridge", Get(msg, "message") ?? "",
                    level == "error" ? ToolTipIcon.Error : level == "warning" ? ToolTipIcon.Warning : ToolTipIcon.Info);
                break;

            case "installService":
                _ = InstallServiceAsync();
                break;

            case "uninstallService":
                _ = UninstallServiceAsync();
                break;

            case "openFolder":
                var which = Get(msg, "which");
                var path = which == "logs" ? AppPaths.LogDir : which == "traffic" ? AppPaths.TrafficLogDir : AppPaths.DataDir;
                if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                break;

            case "openExternal":
                if (Uri.TryCreate(Get(msg, "url"), UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
                    Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true });
                break;

            case "openBrowser":
                OpenBrowser();
                break;

            case "reconnect":
                _ = ReconnectAsync();
                break;

            case "startWithWindows":
                SetStartWithWindows(msg.TryGetProperty("enabled", out var e) && e.GetBoolean());
                SendShellInfo();
                break;

            case "exit":
                _ = ExitAsync();
                break;
        }
    }

    private static string? Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private void SendShellInfo() => _form.Post(new
    {
        type = "shell",
        mode = ServiceMode ? "service" : "embedded",
        serviceInstalled = ServiceControl.IsInstalled,
        startWithWindows = StartWithWindows,
        elevated = Environment.IsPrivilegedProcess,
    });

    private void OpenBrowser()
    {
        if (_baseUrl.Length > 0) Process.Start(new ProcessStartInfo(_baseUrl) { UseShellExecute = true });
    }

    private async Task ReconnectAsync()
    {
        await Task.Delay(1500);
        if (ServiceMode) await ConnectToServiceAsync();
        else NavigateEmbedded();
    }

    // ---------------- service handover ----------------

    private async Task InstallServiceAsync()
    {
        if (_busy || _embedded is null) return;
        var answer = MessageBox.Show(_form,
            "Install PortBridge as a Windows service?\n\n" +
            "Bridges will start with Windows and keep running when nobody is signed in. " +
            "This window will then connect to the service.\n\n" +
            "The service runs as SYSTEM, so it is only installed from a folder that only administrators can change " +
            "(e.g. C:\\Program Files\\PortBridge). The configuration folder becomes administrator-only. Because other users " +
            "could edit it until now, the dashboard password and remote access are reset, an on-error command or custom " +
            "com0com path is set aside until re-entered, and existing virtual COM pairs must be adopted again on the Ports page.",
            "Install service", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;

        _busy = true;
        try
        {
            _form.ShowStatus("Handing over to the Windows service…");
            await _embedded.DisposeAsync(); // saves state and frees the COM ports
            _embedded = null;

            var rc = await ServiceControl.RunElevatedAsync("service install --start");
            if (rc != 0)
            {
                MessageBox.Show(_form, InstallResult.Describe(rc), "Install service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                await StartEmbeddedAsync();
                return;
            }
            await ConnectToServiceAsync(30);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task UninstallServiceAsync()
    {
        if (_busy || !ServiceControl.IsInstalled) return;
        var answer = MessageBox.Show(_form,
            "Remove the PortBridge Windows service?\n\nBridges will then run inside this app and stop when you exit it.",
            "Remove service", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;

        _busy = true;
        try
        {
            _form.ShowStatus("Removing the Windows service…");
            var rc = await ServiceControl.RunElevatedAsync("service uninstall");
            if (rc != 0)
            {
                MessageBox.Show(_form, rc is null ? "Cancelled." : $"Removing the service failed (code {rc}).", "Remove service",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                await ConnectToServiceAsync();
                return;
            }
            await StartEmbeddedAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    // ---------------- start with Windows ----------------
    // PortBridge always runs as administrator, and Windows never starts such programs from the Run key, so "start the
    // app when I sign in" is a scheduled task that runs at this user's sign-in with highest privileges (no UAC prompt).

    private bool? _startWithWindows;

    private bool StartWithWindows => _startWithWindows ??= SignInTask.Exists();

    private void SetStartWithWindows(bool enabled)
    {
        _startWithWindows = null;
        try
        {
            if (!enabled)
            {
                SignInTask.Delete();
                return;
            }
            var exe = Environment.ProcessPath!;
            if (ProtectedPathPolicy.Default.CheckExecutable(exe, ServiceInstaller.Companions(exe)).Count > 0)
            {
                // A task that starts this exe as administrator would let anyone who can replace it run code as administrator.
                MessageBox.Show(_form, "PortBridge can only start itself at sign-in when it is installed in a folder only administrators " +
                    "can change, like C:\\Program Files\\PortBridge. Install it with its setup program.",
                    "Start the app when I sign in", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SignInTask.Create(exe);
        }
        catch (Exception ex)
        {
            MessageBox.Show(_form, "Windows Task Scheduler refused the change: " + ex.Message, "Start the app when I sign in",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Earlier versions used the Run key, which cannot start PortBridge now that it runs as administrator;
    /// move that setting to the task.</summary>
    private void MigrateRunKey()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue("PortBridge") is null) return;
            k.DeleteValue("PortBridge", false);
            var exe = Environment.ProcessPath!;
            if (!StartWithWindows && ProtectedPathPolicy.Default.CheckExecutable(exe, ServiceInstaller.Companions(exe)).Count == 0)
                SignInTask.Create(exe);
        }
        catch { /* the switch in Settings shows what is set */ }
        _startWithWindows = null;
    }

    // ---------------- exit ----------------

    private async Task ExitAsync()
    {
        if (_busy) return;
        if (_embedded is not null && _activeBridges > 0)
        {
            var answer = MessageBox.Show(_form,
                $"Exiting stops {_activeBridges} running bridge{(_activeBridges == 1 ? "" : "s")}.\n\n" +
                "Tip: install PortBridge as a Windows service (Settings) to keep bridges running without this app.\n\nExit anyway?",
                "Exit PortBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }
        _busy = true;
        _connecting?.Cancel();
        _tray.Visible = false;
        _form.AllowClose = true;
        _form.Hide();
        if (_embedded is not null) await _embedded.DisposeAsync();
        _showWait.Unregister(null);
        _tray.Dispose();
        _form.Close();
        ExitThread();
    }
}
