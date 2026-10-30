using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;
using PortBridge.Core.Security;

namespace PortBridge.Host;

/// <summary>The Service Control Manager, as far as installing PortBridge needs it. Tests use a recording fake.</summary>
public interface IServiceRegistrar
{
    bool IsInstalled { get; }
    /// <summary>The registered command line (binPath), or null if not installed / unreadable.</summary>
    string? RegisteredCommandLine { get; }
    int Create(string name, string commandLine, string displayName);
    void ConfigureRecovery(string name);
    bool Start();
}

/// <summary>Exit codes of <c>PortBridge.exe service install</c>; the desktop app turns them into explanations.</summary>
public static class InstallResult
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int NotElevated = 5;
    public const int UnsafeExecutableLocation = 10;
    public const int UnsafeDataFolder = 11;

    public static string Describe(int? code) => code switch
    {
        null => "Installation was cancelled.",
        Ok => "The service was installed.",
        NotElevated => "Installing the service needs administrator approval.",
        UnsafeExecutableLocation =>
            "PortBridge.exe is in a folder that non-administrators can change, so Windows would run code they control as SYSTEM.\n\n" +
            "Copy PortBridge.exe into a folder only administrators can modify (for example C:\\Program Files\\PortBridge) and install the service from there.",
        UnsafeDataFolder =>
            "The PortBridge data folder (%ProgramData%\\PortBridge) contains links or permissions that could not be secured. " +
            "An administrator must remove the reported junctions/links before the service can use it.",
        _ => $"Installing the service failed (code {code}).",
    };
}

/// <summary>
/// Installs the service only when it can be trusted: the executable, the files loaded next to it and its folders
/// must not be modifiable by non-administrators (no reparse points, canonical local path), and the data folder must
/// be secured first. Nothing is registered when any check fails. Command-bearing settings written while the data
/// folder was open to non-administrators are set aside for review.
/// </summary>
public sealed class ServiceInstaller(IServiceRegistrar registrar, ProtectedPathPolicy policy, DataDirGuard guard, TextWriter output)
{
    public static ServiceInstaller Default(TextWriter output) =>
        new(new ScRegistrar(), ProtectedPathPolicy.Default, DataDirGuard.Default, output);

    public static string CommandLine(string exePath) => $"\"{exePath}\" --service";

    /// <summary>Everything under the application folder (DLLs, runtime config, probe folders).</summary>
    public static IEnumerable<string> Companions(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (dir is null || !Directory.Exists(dir)) return [];
        return Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 });
    }

    public int Install(string exePath, IEnumerable<string> companions, string dataDir, bool start)
    {
        if (registrar.IsInstalled)
        {
            // Already registered: only start it if what is registered is still safe.
            if (CheckRegistered() is var regRc and not InstallResult.Ok) return regRc;
            if (SecureData(dataDir) is var rc and not InstallResult.Ok) return rc;
            return start ? (registrar.Start() ? InstallResult.Ok : InstallResult.Failed) : InstallResult.Ok;
        }

        if (Check(exePath, companions) is var exeRc and not InstallResult.Ok) return exeRc;
        if (SecureData(dataDir) is var dataRc and not InstallResult.Ok) return dataRc;

        var create = registrar.Create(ServiceControl.Name, CommandLine(exePath), ServiceControl.DisplayName);
        if (create != 0) return create;
        registrar.ConfigureRecovery(ServiceControl.Name);
        return start ? (registrar.Start() ? InstallResult.Ok : InstallResult.Failed) : InstallResult.Ok;
    }

    /// <summary>
    /// <c>PortBridge.exe service start</c>: asks the SCM to start the service only if the executable it has registered
    /// (and the files and folders around it) is administrator-protected. The SCM runs whatever the registration points
    /// at as SYSTEM, so a check inside the started service would come too late. This cannot protect an unsafe legacy
    /// registration that Windows starts on its own at boot (start=auto): such a registration must be removed
    /// (<c>service uninstall</c>) and installed again from a protected folder.
    /// </summary>
    public int StartInstalled()
    {
        if (!registrar.IsInstalled)
        {
            output.WriteLine("The PortBridge service is not installed.");
            return InstallResult.Failed;
        }
        if (CheckRegistered() is var rc and not InstallResult.Ok) return rc;
        return registrar.Start() ? InstallResult.Ok : InstallResult.Failed;
    }

    private int CheckRegistered()
    {
        var registered = ExeFrom(registrar.RegisteredCommandLine);
        if (registered is not null && Check(registered, Companions(registered)) == InstallResult.Ok) return InstallResult.Ok;
        output.WriteLine("The registered PortBridge service points at an executable that is not protected; not starting it. " +
                         "Uninstall it ('PortBridge.exe service uninstall') and install again from a protected folder.");
        return InstallResult.UnsafeExecutableLocation;
    }

    private int Check(string exePath, IEnumerable<string> companions)
    {
        var findings = policy.CheckExecutable(exePath, companions);
        if (findings.Count == 0) return InstallResult.Ok;
        output.WriteLine($"Refusing to install the service from {exePath}:");
        foreach (var f in findings.Take(20)) output.WriteLine($"  {f.Path}: {f.Problem}");
        output.WriteLine("Copy PortBridge.exe into a folder only administrators can modify (e.g. C:\\Program Files\\PortBridge) and run 'PortBridge.exe service install' from there.");
        return InstallResult.UnsafeExecutableLocation;
    }

    private int SecureData(string dataDir)
    {
        try
        {
            var result = ServiceTrust.SecureDataFolder(guard, dataDir);
            if (result.Quarantined)
            {
                output.WriteLine("Note: the data folder was not provably administrator-controlled, so these were reset or set aside:");
                foreach (var item in result.Reset) output.WriteLine($"  {item}");
                output.WriteLine(ServiceTrust.BootstrapHint);
            }
            return InstallResult.Ok;
        }
        catch (DataDirSecurityException ex)
        {
            output.WriteLine(ex.Message);
            foreach (var f in ex.Findings.Take(20)) output.WriteLine($"  {f.Path}: {f.Problem}");
            return InstallResult.UnsafeDataFolder;
        }
    }

    /// <summary>Extracts the executable from a binPath like <c>"C:\x\PortBridge.exe" --service</c>.</summary>
    internal static string? ExeFrom(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine[0] != '"') return null;
        var end = commandLine.IndexOf('"', 1);
        return end > 1 ? commandLine[1..end] : null;
    }
}

/// <summary>What a privileged host decided about its configuration.</summary>
public sealed record ServiceTrustResult(bool Untrusted, IReadOnlyList<string> Reset, bool Created)
{
    public bool Quarantined => Reset.Count > 0;
}

public static class ServiceTrust
{
    public const string BootstrapHint =
        "An administrator signs in by opening PortBridge on this PC (it runs as administrator and reads the administrator-only sign-in token), " +
        "then sets a dashboard password, re-enters any command it recognises and adopts existing virtual COM pairs on the Ports page.";

    /// <summary>Secures the data folder for SYSTEM use and migrates content that is not vouched for by the trust
    /// record (<see cref="DataDirMigration"/>). Throws <see cref="DataDirSecurityException"/> (fail closed).</summary>
    public static ServiceTrustResult SecureDataFolder(DataDirGuard guard, string dataDir)
    {
        var result = DataDirMigration.Secure(guard, dataDir);
        return new ServiceTrustResult(result.Untrusted, result.Reset, result.Created);
    }

    /// <summary>
    /// Policy for what the engine may execute in this process:
    ///  - setupc.exe is only run from a protected location (it may run elevated);
    ///  - in an elevated process that did not secure the data folder (e.g. the desktop app run as administrator),
    ///    the on-error command only runs while the folder is administrator-controlled.
    /// </summary>
    public static EngineOptions EngineOptions(bool dataSecured, DataDirGuard guard, ProtectedPathPolicy policy, Func<bool> privileged,
        Func<string, IEnumerable<string>>? companions = null)
    {
        companions ??= ServiceInstaller.Companions;
        string? UntrustedFolder()
        {
            if (dataSecured || !privileged()) return null;
            return guard.Inspect(AppPaths.DataDir).Count > 0
                ? "this process runs with administrator rights but the configuration folder can be edited by non-administrators"
                : null;
        }
        return new EngineOptions
        {
            // setupc.exe and everything it may load from its folder must be administrator-protected.
            RejectExecutable = path => policy.CheckExecutable(path, companions(path)) is { Count: > 0 } f
                ? "it is not in an administrator-protected location (" + string.Join("; ", f.Take(3).Select(x => $"{x.Path}: {x.Problem}")) + ")"
                : null,
            CommandBlocked = UntrustedFolder,
            OwnershipUntrusted = UntrustedFolder,
        };
    }
}

/// <summary>Production registrar: sc.exe from System32 (never resolved through the application folder).</summary>
internal sealed class ScRegistrar : IServiceRegistrar
{
    private static string ScExe => Path.Combine(Environment.SystemDirectory, "sc.exe");

    public bool IsInstalled => ServiceControl.IsInstalled;

    public string? RegisteredCommandLine
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceControl.Name}");
                return k?.GetValue("ImagePath") as string;
            }
            catch
            {
                return null;
            }
        }
    }

    public int Create(string name, string commandLine, string displayName) =>
        Sc("create", name, "binPath=", commandLine, "start=", "auto", "DisplayName=", displayName);

    public void ConfigureRecovery(string name)
    {
        Sc("description", name, "Bridges serial (COM) ports, virtual COM ports and TCP/UDP connections. Manage it from the PortBridge app or its web dashboard.");
        // Restart automatically if it ever crashes.
        Sc("failure", name, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/60000");
        Sc("failureflag", name, "1");
    }

    public bool Start() => ServiceControl.Start();

    private static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo(ScExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) Console.Error.WriteLine(output.Trim());
        return p.ExitCode;
    }
}
