using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using PortBridge.Desktop;
using PortBridge.Host;

namespace PortBridge;

public static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("service", StringComparison.OrdinalIgnoreCase))
            return ServiceCommand(args.Skip(1).ToArray());

        if (WindowsServiceHelpers.IsWindowsService() || args.Contains("--service"))
            return RunHosted(HostMode.Service);

        if (args.Contains("--headless"))
        {
            AttachConsole(-1);
            Console.WriteLine("PortBridge running headless. Press Ctrl+C to stop.");
            return RunHosted(HostMode.Headless);
        }

        return DesktopApp.Run(args);
    }

    private static int RunHosted(HostMode mode)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceControl.Name);
        builder.Services.AddHostedService(_ => new EngineService(mode));
        builder.Build().Run();
        return 0;
    }

    /// <summary>PortBridge.exe service install [--start] | uninstall | start | stop | status</summary>
    private static int ServiceCommand(string[] args)
    {
        AttachConsole(-1);
        var cmd = args.FirstOrDefault()?.ToLowerInvariant();
        if (cmd != "status" && !Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("Run this from an elevated (Administrator) prompt.");
            return 5;
        }
        try
        {
            switch (cmd)
            {
                case "install":
                    return ServiceControl.Install(start: args.Contains("--start"));
                case "uninstall":
                    return ServiceControl.Uninstall();
                case "start":
                    // Same registered-image check as install: never ask the SCM to run an unprotected executable as SYSTEM.
                    return ServiceInstaller.Default(Console.Error).StartInstalled();
                case "stop":
                    return ServiceControl.Stop() ? 0 : 1;
                case "status":
                    Console.WriteLine(ServiceControl.Status()?.ToString() ?? "NotInstalled");
                    return 0;
                default:
                    Console.WriteLine("Usage: PortBridge.exe service install [--start] | uninstall | start | stop | status");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
