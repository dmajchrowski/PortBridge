using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using PortBridge.Core.Config;

namespace PortBridge.Host;

/// <summary>Install/uninstall/start/stop of the PortBridge Windows service (a real service, unlike TCP-Com's GUI-as-service).</summary>
public static class ServiceControl
{
    public const string Name = "PortBridge";
    public const string DisplayName = "PortBridge Serial ⇄ Network Bridge";

    public static ServiceControllerStatus? Status()
    {
        try
        {
            using var sc = new ServiceController(Name);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static bool IsInstalled => Status() is not null;
    public static bool IsRunning => Status() == ServiceControllerStatus.Running;

    public static string ExePath => Environment.ProcessPath!;

    private static int Sc(params string[] args)
    {
        // Full path: never pick up an sc.exe from the application folder or the working directory.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) Console.Error.WriteLine(output.Trim());
        return p.ExitCode;
    }

    /// <summary>Must run elevated. Refuses (exit codes in <see cref="InstallResult"/>) unless this executable and the data
    /// folder are protected from non-administrators — see <see cref="ServiceInstaller"/>.</summary>
    public static int Install(bool start) =>
        ServiceInstaller.Default(Console.Error).Install(ExePath, ServiceInstaller.Companions(ExePath), AppPaths.DataDir, start);

    public static int Uninstall()
    {
        if (!IsInstalled) return 0;
        Stop();
        return Sc("delete", Name);
    }

    public static bool Start(int timeoutSeconds = 30)
    {
        using var sc = new ServiceController(Name);
        if (sc.Status == ServiceControllerStatus.Running) return true;
        if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(timeoutSeconds));
        return sc.Status == ServiceControllerStatus.Running;
    }

    public static bool Stop(int timeoutSeconds = 30)
    {
        try
        {
            using var sc = new ServiceController(Name);
            if (sc.Status == ServiceControllerStatus.Stopped) return true;
            if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(timeoutSeconds));
            return sc.Status == ServiceControllerStatus.Stopped;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Runs this exe elevated with the given arguments (UAC prompt) and waits. Returns null if the user declined.</summary>
    public static async Task<int?> RunElevatedAsync(string arguments)
    {
        var psi = new ProcessStartInfo(ExePath, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;
        }
    }
}

/// <summary>Runs the engine inside the Windows service (or a console with --headless).</summary>
public sealed class EngineService(HostMode mode) : IHostedService
{
    private HostRuntime? _runtime;

    public async Task StartAsync(CancellationToken cancellationToken) => _runtime = await HostRuntime.StartAsync(mode);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runtime is not null) await _runtime.DisposeAsync();
    }
}
