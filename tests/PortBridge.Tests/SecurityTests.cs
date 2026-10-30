using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using PortBridge.Core.Config;
using PortBridge.Core.Engine;
using PortBridge.Core.Security;
using Xunit.Abstractions;

namespace PortBridge.Tests;

/// <summary>In-memory ACL model. Paths not in the map come from <see cref="Fallback"/> (default: "does not exist").</summary>
internal sealed class FakeAclReader : IPathSecurityReader
{
    public readonly Dictionary<string, PathSecurityInfo> Map = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, PathSecurityInfo>? Fallback;

    public PathSecurityInfo Read(string path) =>
        Map.TryGetValue(path.TrimEnd('\\'), out var i) ? i : Fallback?.Invoke(path) ?? new PathSecurityInfo(path, false, false, false, null, []);

    public IEnumerable<string> Children(string directory) =>
        Map.Keys.Where(k => string.Equals(Path.GetDirectoryName(k), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
}

internal static class Acl
{
    public static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    public static readonly SecurityIdentifier AuthUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    public static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    public static readonly SecurityIdentifier StandardUser = new("S-1-5-21-1000000001-1000000002-1000000003-1001");
    public static readonly SecurityIdentifier AdminUser = new("S-1-5-21-1000000001-1000000002-1000000003-500");

    public static AceInfo Allow(SecurityIdentifier sid, FileSystemRights r, bool inheritOnly = false) => new(sid, r, AccessControlType.Allow, inheritOnly);

    public static readonly AceInfo[] Standard =
    [
        Allow(TrustedPrincipals.System, FileSystemRights.FullControl),
        Allow(TrustedPrincipals.Administrators, FileSystemRights.FullControl),
        Allow(TrustedPrincipals.TrustedInstaller, FileSystemRights.FullControl),
        Allow(Users, FileSystemRights.ReadAndExecute),
        Allow(CreatorOwner, FileSystemRights.FullControl, inheritOnly: true),
    ];

    public static PathSecurityInfo Dir(string p, params AceInfo[] extra) => new(p, true, true, false, TrustedPrincipals.Administrators, [.. Standard, .. extra]);
    public static PathSecurityInfo File(string p, params AceInfo[] extra) => new(p, true, false, false, TrustedPrincipals.Administrators, [.. Standard, .. extra]);
}

/// <summary>F4 (Core): what a SYSTEM process may load or trust.</summary>
public class ProtectedPathPolicyTests
{
    private const string Exe = @"C:\Program Files\PortBridge\PortBridge.exe";

    private static FakeAclReader Install()
    {
        var r = new FakeAclReader();
        r.Map[@"C:"] = Acl.Dir(@"C:\",
            Acl.Allow(Acl.AuthUsers, FileSystemRights.CreateDirectories),          // default C:\: users may create folders
            Acl.Allow(Acl.AuthUsers, FileSystemRights.Modify, inheritOnly: true)); // …and own what they create there
        r.Map[@"C:\Program Files"] = Acl.Dir(@"C:\Program Files");
        r.Map[@"C:\Program Files\PortBridge"] = Acl.Dir(@"C:\Program Files\PortBridge");
        r.Map[Exe] = Acl.File(Exe);
        r.Map[@"C:\Program Files\PortBridge\PortBridge.dll"] = Acl.File(@"C:\Program Files\PortBridge\PortBridge.dll");
        return r;
    }

    [Fact]
    public void Administrator_protected_location_passes()
    {
        var r = Install();
        Assert.Empty(new ProtectedPathPolicy(r).CheckExecutable(Exe, [@"C:\Program Files\PortBridge\PortBridge.dll"]));
    }

    [Fact]
    public void User_writable_binary_is_rejected()
    {
        var r = Install();
        r.Map[Exe] = Acl.File(Exe, Acl.Allow(Acl.Users, FileSystemRights.Modify));
        Assert.Contains(new ProtectedPathPolicy(r).CheckExecutable(Exe), f => f.Path == Exe && f.Problem.Contains("Write"));
    }

    [Fact]
    public void User_writable_application_folder_is_rejected_because_of_dll_planting()
    {
        var r = Install();
        r.Map[@"C:\Program Files\PortBridge"] = Acl.Dir(@"C:\Program Files\PortBridge", Acl.Allow(Acl.AuthUsers, FileSystemRights.CreateFiles));
        Assert.Contains(new ProtectedPathPolicy(r).CheckExecutable(Exe), f => f.Path == @"C:\Program Files\PortBridge");
    }

    [Fact]
    public void Ancestor_that_lets_users_replace_children_is_rejected()
    {
        var r = Install();
        r.Map[@"C:\Program Files"] = Acl.Dir(@"C:\Program Files", Acl.Allow(Acl.Users, FileSystemRights.DeleteSubdirectoriesAndFiles));
        Assert.Contains(new ProtectedPathPolicy(r).CheckExecutable(Exe), f => f.Path == @"C:\Program Files");
    }

    [Fact]
    public void Reparse_point_on_the_path_is_rejected()
    {
        var r = Install();
        r.Map[@"C:\Program Files\PortBridge"] = r.Map[@"C:\Program Files\PortBridge"] with { IsReparsePoint = true };
        Assert.Contains(new ProtectedPathPolicy(r).CheckExecutable(Exe), f => f.Problem.Contains("reparse"));
    }

    [Fact]
    public void Owner_must_be_trusted_or_an_administrator_account()
    {
        var r = Install();
        r.Map[Exe] = r.Map[Exe] with { Owner = Acl.StandardUser };
        Assert.Contains(new ProtectedPathPolicy(r, _ => false).CheckExecutable(Exe), f => f.Problem.Contains("owned by"));

        r.Map[Exe] = r.Map[Exe] with { Owner = Acl.AdminUser };
        Assert.Empty(new ProtectedPathPolicy(r, sid => sid == Acl.AdminUser).CheckExecutable(Exe));
        Assert.NotEmpty(new ProtectedPathPolicy(r, _ => false).CheckExecutable(Exe));
    }

    [Fact]
    public void Generic_write_and_writable_companion_files_are_rejected()
    {
        var r = Install();
        r.Map[Exe] = Acl.File(Exe, Acl.Allow(Acl.Users, (FileSystemRights)0x40000000));
        Assert.NotEmpty(new ProtectedPathPolicy(r).CheckExecutable(Exe));

        r = Install();
        const string dll = @"C:\Program Files\PortBridge\PortBridge.dll";
        r.Map[dll] = Acl.File(dll, Acl.Allow(Acl.StandardUser, FileSystemRights.Write));
        Assert.Contains(new ProtectedPathPolicy(r).CheckExecutable(Exe, [dll]), f => f.Path == dll);
    }

    [Theory]
    [InlineData("PortBridge.exe")]
    [InlineData(@"C:PortBridge.exe")]
    [InlineData(@"\\server\share\PortBridge.exe")]
    [InlineData(@"\\?\C:\Program Files\PortBridge\PortBridge.exe")]
    [InlineData(@"C:\Program Files\PortBridge\PortBridge.exe:payload")]
    [InlineData("C:\\Program Files\\Port\"Bridge\\PortBridge.exe")]
    [InlineData(@"C:\Program Files\Other\..\PortBridge\PortBridge.exe")]
    [InlineData(@"C:\Program Files\PortBridge\PortBridge.exe.")]
    [InlineData(@"C:\Program Files\PortBridge \PortBridge.exe")]
    [InlineData(@"C:\PROGRA~1\PortBridge\PortBridge.exe")]
    [InlineData("C:/Program Files/PortBridge/PortBridge.exe")]
    [InlineData(@"C:\Program Files\PortBridge\Port*.exe")]
    [InlineData("")]
    public void Ambiguous_or_malicious_paths_are_rejected_without_trusting_the_string(string path)
    {
        var r = Install(); // every real location would pass the ACL checks
        Assert.NotNull(ProtectedPathPolicy.SyntaxProblem(path));
        Assert.NotEmpty(new ProtectedPathPolicy(r).CheckExecutable(path));
    }

    [Fact]
    public void Real_acls_system32_cmd_is_protected()
    {
        // Read-only check of the production reader against a known administrator-controlled file.
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.Empty(ProtectedPathPolicy.Default.CheckExecutable(cmd));
    }

    [Fact]
    public void Real_acls_user_writable_fixture_is_rejected()
    {
        var dir = Scratch.NewDir("acl");
        try
        {
            var exe = Path.Combine(dir, "PortBridge.exe");
            System.IO.File.WriteAllBytes(exe, [0x4D, 0x5A]);
            // Run-owned fixture only: grant BUILTIN\Users modify on the folder (inherited by the file).
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(Acl.Users, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);

            var findings = ProtectedPathPolicy.Default.CheckExecutable(exe);
            Assert.Contains(findings, f => f.Path == dir && f.Problem.Contains("Users"));
            Assert.Contains(findings, f => f.Path == exe && f.Problem.Contains("Users"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}

internal sealed class FakeAclWriter(FakeAclReader model) : IAclWriter
{
    public readonly List<string> Calls = [];
    public string? FailOn;
    public bool NoOp;

    private void Maybe(string op, string path, Action change)
    {
        Calls.Add($"{op} {path}");
        if (FailOn is not null && string.Equals(FailOn, path, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Attempted to perform an unauthorized operation.");
        if (!NoOp) change();
    }

    public void CreateProtectedDirectory(string path) => Maybe("create", path, () =>
    {
        Directory.CreateDirectory(path);
        model.Map[path] = new PathSecurityInfo(path, true, true, false, TrustedPrincipals.Administrators,
            [Acl.Allow(TrustedPrincipals.System, FileSystemRights.FullControl), Acl.Allow(TrustedPrincipals.Administrators, FileSystemRights.FullControl)]);
    });

    public void ProtectRoot(string path) => Maybe("protect", path, () => model.Map[path] = model.Map[path] with
    {
        Owner = TrustedPrincipals.Administrators,
        Aces = [Acl.Allow(TrustedPrincipals.System, FileSystemRights.FullControl), Acl.Allow(TrustedPrincipals.Administrators, FileSystemRights.FullControl)],
    });

    public void ResetToInherited(string path, bool isDirectory) => Maybe("reset", path, () => model.Map[path] = model.Map[path] with
    {
        Owner = TrustedPrincipals.Administrators,
        Aces = [Acl.Allow(TrustedPrincipals.System, FileSystemRights.FullControl), Acl.Allow(TrustedPrincipals.Administrators, FileSystemRights.FullControl)],
    });
}

/// <summary>F4 (Core): the data folder a SYSTEM service reads is secured or refused, never trusted as found.</summary>
public class DataDirGuardTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Scratch.NewDir("data");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private (FakeAclReader Model, FakeAclWriter Writer, DataDirGuard Guard, string Data) DesktopOwnedFolder()
    {
        var data = Path.Combine(_root, "PortBridge");
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        System.IO.File.WriteAllText(Path.Combine(data, "config.json"), "{\"settings\":{\"onErrorCommand\":\"calc.exe\"}}");
        System.IO.File.WriteAllText(Path.Combine(data, "logs", "events.log"), "x");
        var model = new FakeAclReader
        {
            // Everything above the data folder is administrator-controlled.
            Fallback = p => Acl.Dir(p),
        };
        var userFull = Acl.Allow(Acl.StandardUser, FileSystemRights.FullControl);
        model.Map[data] = Acl.Dir(data, userFull) with { Owner = Acl.StandardUser };
        model.Map[Path.Combine(data, "config.json")] = Acl.File(Path.Combine(data, "config.json"), userFull) with { Owner = Acl.StandardUser };
        model.Map[Path.Combine(data, "logs")] = Acl.Dir(Path.Combine(data, "logs"), userFull);
        model.Map[Path.Combine(data, "logs", "events.log")] = Acl.File(Path.Combine(data, "logs", "events.log"), userFull);
        var writer = new FakeAclWriter(model);
        return (model, writer, new DataDirGuard(model, writer, _ => false), data);
    }

    [Fact]
    public void Desktop_owned_folder_is_taken_over_flagged_and_its_trusted_files_rewritten()
    {
        var (model, writer, guard, data) = DesktopOwnedFolder();
        var config = Path.Combine(data, "config.json");
        var before = System.IO.File.GetCreationTimeUtc(config);
        using (var stale = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // A handle opened before the takeover keeps pointing at the old file object.
            var result = guard.Harden(data);
            Assert.True(result.WasInsecure);
            Assert.Equal($"protect {data}", writer.Calls[0]);
            Assert.Contains($"reset {config}", writer.Calls);
            Assert.Contains($"reset {Path.Combine(data, "logs", "events.log")}", writer.Calls);
        }
        Assert.Contains("calc.exe", System.IO.File.ReadAllText(config)); // content kept (quarantine is the caller's job)
        Assert.Empty(guard.Inspect(data));
        output.WriteLine($"config.json created {before:o} -> {System.IO.File.GetCreationTimeUtc(config):o}");
    }

    [Fact]
    public void Junction_or_hard_link_inside_is_refused_before_anything_changes()
    {
        var (model, writer, guard, data) = DesktopOwnedFolder();
        var logs = Path.Combine(data, "logs");
        model.Map[logs] = model.Map[logs] with { IsReparsePoint = true };
        var ex = Assert.Throws<DataDirSecurityException>(() => guard.Harden(data));
        Assert.Contains("links", ex.Message);
        Assert.Empty(writer.Calls);

        model.Map[logs] = model.Map[logs] with { IsReparsePoint = false };
        var config = Path.Combine(data, "config.json");
        model.Map[config] = model.Map[config] with { LinkCount = 2 };
        Assert.Throws<DataDirSecurityException>(() => guard.Harden(data));
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public void Acl_failure_fails_closed()
    {
        var (_, writer, guard, data) = DesktopOwnedFolder();
        writer.FailOn = data;
        var ex = Assert.Throws<DataDirSecurityException>(() => guard.Harden(data));
        Assert.Contains("Could not secure", ex.Message);
    }

    [Fact]
    public void Changes_that_do_not_take_effect_fail_closed()
    {
        var (_, writer, guard, data) = DesktopOwnedFolder();
        writer.NoOp = true;
        var ex = Assert.Throws<DataDirSecurityException>(() => guard.Harden(data));
        Assert.Contains("could not be secured", ex.Message);
    }

    [Fact]
    public void Unprotected_parent_folder_is_refused()
    {
        var (model, writer, guard, data) = DesktopOwnedFolder();
        model.Fallback = p => Acl.Dir(p, Acl.Allow(Acl.Users, FileSystemRights.DeleteSubdirectoriesAndFiles));
        Assert.Throws<DataDirSecurityException>(() => guard.Harden(data));
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public void Missing_folder_is_created_protected()
    {
        var model = new FakeAclReader { Fallback = p => p.StartsWith(_root + "\\PortBridge", StringComparison.OrdinalIgnoreCase) ? new PathSecurityInfo(p, false, false, false, null, []) : Acl.Dir(p) };
        var writer = new FakeAclWriter(model);
        var data = Path.Combine(_root, "PortBridge");
        var result = new DataDirGuard(model, writer, _ => false).Harden(data);
        Assert.True(result.Created);
        Assert.False(result.WasInsecure);
        Assert.Equal([$"create {data}"], writer.Calls);
    }

    /// <summary>Production reader and writer on a run-owned fixture (ancestors outside the fixture are modelled as
    /// protected, since the fixture lives under a user profile). Unelevated, taking ownership for Administrators is
    /// denied, which must fail closed; elevated, the fixture must end up administrator-only.</summary>
    [Fact]
    public void Real_acl_writer_on_fixture_hardens_or_fails_closed()
    {
        var data = Path.Combine(_root, "PortBridge");
        Directory.CreateDirectory(data);
        System.IO.File.WriteAllText(Path.Combine(data, "config.json"), "{}");
        var reader = new ScopedReader(data);
        var guard = new DataDirGuard(reader, WindowsAclWriter.Instance, LocalAdministrators.IsMember);
        if (Environment.IsPrivilegedProcess)
        {
            guard.Harden(data);
            var aces = WindowsPathSecurityReader.Instance.Read(data).Aces.Where(a => !a.InheritOnly);
            Assert.All(aces, a => Assert.True(TrustedPrincipals.IsTrusted(a.Sid), a.Sid.Value));
            output.WriteLine("elevated: fixture hardened");
        }
        else
        {
            Assert.Throws<DataDirSecurityException>(() => guard.Harden(data));
            output.WriteLine("not elevated: hardening refused (fail closed)");
        }
    }

    private sealed class ScopedReader(string scope) : IPathSecurityReader
    {
        public PathSecurityInfo Read(string path) => path.StartsWith(scope, StringComparison.OrdinalIgnoreCase)
            ? WindowsPathSecurityReader.Instance.Read(path)
            : Acl.Dir(path);
        public IEnumerable<string> Children(string directory) => WindowsPathSecurityReader.Instance.Children(directory);
    }
}

/// <summary>F4 (Core): command-bearing settings from an untrusted folder are set aside and never run silently.</summary>
public class CommandTrustTests : IDisposable
{
    private readonly string _dir = Scratch.NewDir("trust");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Quarantine_moves_command_and_tool_path_aside()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        var cfg = new AppConfig();
        cfg.Settings.OnErrorCommand = "powershell -File C:\\Users\\Public\\x.ps1";
        cfg.Settings.Com0ComPath = @"C:\Users\Public\setupc.exe";
        cfg.Settings.WebhookUrl = "https://example.invalid/hook";
        store.Save(cfg);

        Assert.True(store.QuarantineCommandSettings("review"));
        var s = store.Load().Settings;
        Assert.Null(s.OnErrorCommand);
        Assert.Null(s.Com0ComPath);
        Assert.Equal("powershell -File C:\\Users\\Public\\x.ps1", s.QuarantinedOnErrorCommand);
        Assert.Equal(@"C:\Users\Public\setupc.exe", s.QuarantinedCom0ComPath);
        Assert.Equal("review", s.QuarantineNote);
        Assert.Equal("https://example.invalid/hook", s.WebhookUrl);
        Assert.False(store.QuarantineCommandSettings("again"));
    }

    [Fact]
    public async Task Blocked_command_is_not_started_and_allowed_one_uses_system_cmd()
    {
        await using var journal = new EventJournal();
        var settings = new AppSettings { OnErrorCommand = "echo %BRIDGE%" };
        var started = new List<ProcessStartInfo>();
        string? blocked = "configuration folder is writable by non-administrators";
        using var notifier = new Notifier(journal, () => settings, psi => { started.Add(psi); return null; }, () => blocked);

        journal.Add(EventLevel.Error, "port lost", "b1", "Bridge 1");
        Assert.Empty(started);
        Assert.Contains(journal.Query(), e => e.Message.StartsWith("Error command not run"));

        blocked = null;
        journal.Add(EventLevel.Error, "port lost", "b2", "Bridge 2");
        var psi = Assert.Single(started);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), psi.FileName);
        Assert.Equal(["/c", "echo Bridge 2"], psi.ArgumentList);
    }
}
