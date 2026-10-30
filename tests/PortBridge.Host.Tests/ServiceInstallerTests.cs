using System.Security.AccessControl;
using System.Security.Principal;
using PortBridge.Core.Config;
using PortBridge.Core.Security;
using PortBridge.Host;

namespace PortBridge.Host.Tests;

/// <summary>Records what would have been sent to the Service Control Manager. Nothing real is registered.</summary>
internal sealed class FakeRegistrar : IServiceRegistrar
{
    public readonly List<string> Calls = [];
    public bool IsInstalled { get; set; }
    public string? RegisteredCommandLine { get; set; }

    public int Create(string name, string commandLine, string displayName)
    {
        Calls.Add($"create {name} {commandLine}");
        IsInstalled = true;
        RegisteredCommandLine = commandLine;
        return 0;
    }

    public void ConfigureRecovery(string name) => Calls.Add("recovery");

    public bool Start()
    {
        Calls.Add("start");
        return true;
    }
}

internal sealed class Model : IPathSecurityReader, IAclWriter
{
    public static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    public static readonly SecurityIdentifier User = new("S-1-5-21-1000000001-1000000002-1000000003-1001");
    private static readonly AceInfo[] Admin =
    [
        new(TrustedPrincipals.System, FileSystemRights.FullControl, AccessControlType.Allow, false),
        new(TrustedPrincipals.Administrators, FileSystemRights.FullControl, AccessControlType.Allow, false),
        new(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow, false),
    ];

    public readonly Dictionary<string, PathSecurityInfo> Map = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> AclCalls = [];
    public string? FailOn;

    public static PathSecurityInfo Protected(string p, bool dir) => new(p, true, dir, false, TrustedPrincipals.Administrators, Admin);

    public static PathSecurityInfo UserWritable(string p, bool dir) => new(p, true, dir, false, User,
        [.. Admin, new AceInfo(User, FileSystemRights.FullControl, AccessControlType.Allow, false)]);

    public PathSecurityInfo Read(string path) =>
        Map.TryGetValue(path.TrimEnd('\\'), out var i) ? i : Protected(path, dir: true); // everything else: protected folders

    public IEnumerable<string> Children(string directory) =>
        Map.Keys.Where(k => string.Equals(Path.GetDirectoryName(k), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();

    private void Op(string op, string path, bool dir)
    {
        AclCalls.Add($"{op} {path}");
        if (string.Equals(FailOn, path, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("denied");
        Map[path] = Protected(path, dir);
    }

    public void CreateProtectedDirectory(string path) { Directory.CreateDirectory(path); Op("create", path, true); }
    public void ProtectRoot(string path) => Op("protect", path, true);
    public void ResetToInherited(string path, bool isDirectory) => Op("reset", path, isDirectory);
}

/// <summary>F4 (host): service registration only from protected locations, after the data folder is secured.
/// These are fixtures with injected boundaries: they do not prove anything about a real SCM installation.</summary>
[Collection(AppPathsCollection.Name)]
public class ServiceInstallerTests : IDisposable
{
    private const string Exe = @"C:\Program Files\PortBridge\PortBridge.exe";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pb-host-" + Guid.NewGuid().ToString("N")[..8], "PortBridge");
    private readonly Model _model = new();
    private readonly FakeRegistrar _scm = new();
    private readonly StringWriter _out = new();

    public ServiceInstallerTests()
    {
        Directory.CreateDirectory(_data);
        _model.Map[_data] = Model.Protected(_data, true);
        _model.Map[Exe] = Model.Protected(Exe, false);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_data)!, true); } catch { /* ignore */ }
    }

    private ServiceInstaller Installer() => new(_scm, new ProtectedPathPolicy(_model, _ => false), new DataDirGuard(_model, _model, _ => false), _out);

    [Fact]
    public void Protected_location_registers_a_quoted_command_line()
    {
        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: true));
        Assert.Equal(["create PortBridge \"C:\\Program Files\\PortBridge\\PortBridge.exe\" --service", "recovery", "start"], _scm.Calls);
    }

    [Fact]
    public void Standard_user_writable_binary_registers_nothing_and_leaves_data_alone()
    {
        _model.Map[Exe] = Model.UserWritable(Exe, false);
        Assert.Equal(InstallResult.UnsafeExecutableLocation, Installer().Install(Exe, [], _data, start: true));
        Assert.Empty(_scm.Calls);
        Assert.Empty(_model.AclCalls);
        Assert.Contains("Refusing to install", _out.ToString());
    }

    [Fact]
    public void Standard_user_writable_parent_registers_nothing()
    {
        _model.Map[@"C:\Program Files\PortBridge"] = Model.UserWritable(@"C:\Program Files\PortBridge", true);
        Assert.Equal(InstallResult.UnsafeExecutableLocation, Installer().Install(Exe, [], _data, start: false));
        Assert.Empty(_scm.Calls);
    }

    [Theory]
    [InlineData("C:\\Program Files\\PortBridge\\PortBridge.exe\" --evil \"x")]
    [InlineData(@"\\attacker\share\PortBridge.exe")]
    [InlineData(@"C:\Users\Public\..\..\Program Files\PortBridge\PortBridge.exe")]
    public void Malicious_or_ambiguous_paths_register_nothing(string exe)
    {
        Assert.Equal(InstallResult.UnsafeExecutableLocation, Installer().Install(exe, [], _data, start: true));
        Assert.Empty(_scm.Calls);
    }

    [Fact]
    public void Junction_in_data_folder_registers_nothing()
    {
        var logs = Path.Combine(_data, "logs");
        _model.Map[logs] = Model.Protected(logs, true) with { IsReparsePoint = true };
        Assert.Equal(InstallResult.UnsafeDataFolder, Installer().Install(Exe, [], _data, start: true));
        Assert.Empty(_scm.Calls);
    }

    [Fact]
    public void Acl_failure_on_data_folder_registers_nothing()
    {
        _model.Map[_data] = Model.UserWritable(_data, true);
        _model.FailOn = _data;
        Assert.Equal(InstallResult.UnsafeDataFolder, Installer().Install(Exe, [], _data, start: true));
        Assert.Empty(_scm.Calls);
    }

    [Fact]
    public void Insecure_pre_existing_config_has_its_commands_set_aside_before_registration()
    {
        var configPath = Path.Combine(_data, "config.json");
        var store = new ConfigStore(configPath);
        var cfg = new AppConfig();
        cfg.Settings.OnErrorCommand = "C:\\Users\\Public\\payload.exe";
        store.Save(cfg);
        _model.Map[_data] = Model.UserWritable(_data, true);
        _model.Map[configPath] = Model.UserWritable(configPath, false);

        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: false));
        var s = store.Load().Settings;
        Assert.Null(s.OnErrorCommand);
        Assert.Equal("C:\\Users\\Public\\payload.exe", s.QuarantinedOnErrorCommand);
        Assert.Contains("set aside", _out.ToString());
        Assert.Contains($"protect {_data}", _model.AclCalls);
    }

    [Fact]
    public void Existing_registration_pointing_at_an_unprotected_binary_is_not_started()
    {
        _scm.IsInstalled = true;
        const string unsafeExe = @"C:\Users\Public\pb-nonexistent-fixture\PortBridge.exe"; // modelled only; the folder does not exist
        _scm.RegisteredCommandLine = $"\"{unsafeExe}\" --service";
        _model.Map[unsafeExe] = Model.UserWritable(unsafeExe, false);
        Assert.Equal(InstallResult.UnsafeExecutableLocation, Installer().Install(Exe, [], _data, start: true));
        Assert.Empty(_scm.Calls);

        _scm.RegisteredCommandLine = ServiceInstaller.CommandLine(Exe);
        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: true));
        Assert.Equal(["start"], _scm.Calls);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\PortBridge\\PortBridge.exe\" --service", @"C:\Program Files\PortBridge\PortBridge.exe")]
    [InlineData(@"C:\Program Files\PortBridge\PortBridge.exe --service", null)]
    [InlineData("", null)]
    public void Registered_command_line_parsing(string commandLine, string? exe) =>
        Assert.Equal(exe, ServiceInstaller.ExeFrom(commandLine));

    [Fact]
    public void Desktop_explains_refusals()
    {
        Assert.Contains("Program Files", InstallResult.Describe(InstallResult.UnsafeExecutableLocation));
        Assert.Contains("cancelled", InstallResult.Describe(null));
        Assert.Contains("links", InstallResult.Describe(InstallResult.UnsafeDataFolder));
    }

    [Fact]
    public void Engine_policy_refuses_untrusted_tools_and_unverified_commands_when_privileged()
    {
        _model.Map[@"C:\Users\Public\setupc.exe"] = Model.UserWritable(@"C:\Users\Public\setupc.exe", false);
        _model.Map[@"C:\Program Files (x86)\com0com\setupc.exe"] = Model.Protected(@"C:\Program Files (x86)\com0com\setupc.exe", false);
        var guard = new DataDirGuard(_model, _model, _ => false);
        var policy = new ProtectedPathPolicy(_model, _ => false);
        var previous = AppPaths.DataDir;
        AppPaths.DataDir = _data;
        try
        {
            _model.Map[_data] = Model.UserWritable(_data, true);
            var privileged = ServiceTrust.EngineOptions(false, guard, policy, () => true, _ => []); // companions modelled as none
            Assert.NotNull(privileged.RejectExecutable!(@"C:\Users\Public\setupc.exe"));
            Assert.Null(privileged.RejectExecutable!(@"C:\Program Files (x86)\com0com\setupc.exe"));
            Assert.NotNull(privileged.CommandBlocked!());
            Assert.Null(ServiceTrust.EngineOptions(false, guard, policy, () => false).CommandBlocked!()); // runs as the user anyway
            Assert.Null(ServiceTrust.EngineOptions(true, guard, policy, () => true).CommandBlocked!());  // folder was secured
        }
        finally
        {
            AppPaths.DataDir = previous;
        }
    }
}
