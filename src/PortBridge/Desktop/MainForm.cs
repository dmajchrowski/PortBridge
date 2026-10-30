using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace PortBridge.Desktop;

/// <summary>Native window hosting the dashboard in WebView2.</summary>
internal sealed class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(17, 19, 24) };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(160, 168, 184),
        BackColor = Color.FromArgb(17, 19, 24),
        Font = new Font("Segoe UI", 11f),
        Text = "Starting PortBridge…",
    };
    private bool _webReady;

    public event Action<JsonElement>? MessageReceived;
    public bool AllowClose;
    public bool CloseToTray = true;
    public event Action? HiddenToTray;

    public MainForm(Icon icon)
    {
        Text = "PortBridge";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1320, 860);
        MinimumSize = new Size(880, 600);
        BackColor = Color.FromArgb(17, 19, 24);
        Controls.Add(_web);
        Controls.Add(_status);
        _status.BringToFront();
    }

    public void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visible = true;
        _status.BringToFront();
    }

    public async Task InitAsync()
    {
        if (_webReady) return;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, WebViewDataFolder());
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowStatus("The Microsoft Edge WebView2 Runtime is missing.\nInstall it from https://go.microsoft.com/fwlink/p/?LinkId=2124703\nor use “Open in browser” from the tray icon.");
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached || Environment.GetEnvironmentVariable("PORTBRIDGE_DEVTOOLS") == "1";
        core.Settings.IsZoomControlEnabled = true;
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                MessageReceived?.Invoke(doc.RootElement.Clone());
            }
            catch (JsonException) { }
        };
        core.NewWindowRequested += (_, e) =>
        {
            // Links that open new windows (exports, docs) go to the system browser or download normally.
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !u.IsLoopback)
            {
                e.Handled = true;
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            }
        };
        // Our own dashboard may use the self-signed certificate when HTTPS is enabled.
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            if (Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost"))
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) _status.Visible = false;
        };
        _webReady = true;
    }

    /// <summary>WebView2 never runs its browser as administrator: for an elevated app it starts the browser as the user who
    /// owns the desktop. For an administrator that is the same account, but a standard user who opened PortBridge with an
    /// administrator's name and password owns the desktop themselves, and the browser, running as them, cannot use a folder
    /// in the administrator's profile ("Microsoft Edge can't read and write to its data directory").</summary>
    private static string WebViewDataFolder()
    {
        var own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortBridge", "WebView2");
        try
        {
            var name = SessionString(WTSUserName);
            if (string.IsNullOrEmpty(name)) return own;
            var sid = (SecurityIdentifier)new NTAccount(SessionString(WTSDomainName) ?? "", name).Translate(typeof(SecurityIdentifier));
            if (sid == WindowsIdentity.GetCurrent().User) return own;
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid.Value);
            if (key?.GetValue("ProfileImagePath") is not string profile || profile.Length == 0) return own;
            profile = Environment.ExpandEnvironmentVariables(profile);
            var dir = Path.Combine(profile, "AppData", "Local", "PortBridge", "WebView2");
            // That user can change their own profile, and this process is elevated: don't follow a link they put there.
            for (var p = dir; p.Length > profile.Length; p = Path.GetDirectoryName(p)!)
                if (new DirectoryInfo(p) is { Exists: true } d && d.Attributes.HasFlag(FileAttributes.ReparsePoint)) return own;
            return dir;
        }
        catch (Exception)
        {
            return own;
        }
    }

    private const int WTSUserName = 5, WTSDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>A string about this process's own session (WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION).</summary>
    private static string? SessionString(int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, -1, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    public void Navigate(string url)
    {
        if (!_webReady) return;
        _web.CoreWebView2.Navigate(url);
    }

    public void Post(object message)
    {
        if (!_webReady) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, PortBridge.Core.Config.Json.Options)); }
        catch (InvalidOperationException) { }
    }

    public event Action? ExitRequested;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (CloseToTray)
            {
                Hide();
                HiddenToTray?.Invoke();
            }
            else
            {
                ExitRequested?.Invoke();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    public void BringToFrontNow()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }
}
