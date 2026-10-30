using System.Text;
using PortBridge.Core.Config;
using PortBridge.Core.Security;
using PortBridge.Core.Serial;
using PortBridge.Host;

namespace PortBridge.Host.Tests;

/// <summary>Tests that change the process-wide <see cref="AppPaths.DataDir"/> run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppPathsCollection
{
    public const string Name = "AppPaths";
}

/// <summary>Minimal setupc model: pairs with A/B parameters. Nothing real is run.</summary>
internal sealed class FakeSetupcRunner : ISetupcRunner
{
    public readonly SortedDictionary<int, (Dictionary<string, string> A, Dictionary<string, string> B)> Pairs = new();
    public readonly List<string> Calls = [];

    public void AddPair(int n, string a, string b) => Pairs[n] = (Parse(a), Parse(b));

    private static Dictionary<string, string> Parse(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Mutations => Calls.Where(c => !c.StartsWith("list") && !c.StartsWith("busynames"));

    public Task<string> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        Calls.Add(string.Join(' ', args));
        switch (args[0])
        {
            case "list":
                return Task.FromResult(string.Join("\n", Pairs.SelectMany(p => new[]
                {
                    $"       CNCA{p.Key} {string.Join(",", p.Value.A.Select(kv => $"{kv.Key}={kv.Value}"))}",
                    $"       CNCB{p.Key} {string.Join(",", p.Value.B.Select(kv => $"{kv.Key}={kv.Value}"))}",
                })));
            case "busynames":
                return Task.FromResult("");
            case "change":
                var target = args[1].StartsWith("CNCA") ? Pairs[int.Parse(args[1][4..])].A : Pairs[int.Parse(args[1][4..])].B;
                foreach (var (k, v) in Parse(args[2])) target[k] = v;
                return Task.FromResult("");
            case "remove":
                Pairs.Remove(int.Parse(args[1]));
                return Task.FromResult("");
        }
        throw new InvalidOperationException("unexpected setupc call " + string.Join(' ', args));
    }
}

/// <summary>S1/S3/S4: what a privileged takeover does with content non-administrators could have written. Production
/// installer, guard, config store and com0com manager; ACLs, SCM and setupc are injected fakes.</summary>
[Collection(AppPathsCollection.Name)]
public class MigrationTrustTests : IDisposable
{
    private const string Exe = @"C:\Program Files\PortBridge\PortBridge.exe";
    private const string AttackerPassword = "synthetic-attacker-pw"; // made up for this test
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pb-mig-" + Guid.NewGuid().ToString("N")[..8], "PortBridge");
    private readonly Model _model = new();
    private readonly FakeRegistrar _scm = new();
    private readonly StringWriter _out = new();

    public MigrationTrustTests()
    {
        Directory.CreateDirectory(_data);
        _model.Map[_data] = Model.Protected(_data, true);
        _model.Map[Exe] = Model.Protected(Exe, false);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_data)!, true); } catch { /* ignore */ }
    }

    private string ConfigPath => Path.Combine(_data, "config.json");

    private ServiceInstaller Installer() => new(_scm, new ProtectedPathPolicy(_model, _ => false), new DataDirGuard(_model, _model, _ => false), _out);

    /// <summary>The data folder and the given files were writable by a standard user (desktop-owned folder).</summary>
    private void UserWritable(params string[] files)
    {
        _model.Map[_data] = Model.UserWritable(_data, true);
        foreach (var f in files) _model.Map[f] = Model.UserWritable(f, false);
    }

    private void PlantHostileConfig()
    {
        var cfg = new AppConfig();
        cfg.Settings.PasswordHash = AuthService.HashPassword(AttackerPassword);
        cfg.Settings.AllowRemote = true;
        cfg.Settings.RequirePasswordLocally = true;
        cfg.Settings.OnErrorCommand = @"C:\Users\Public\payload.exe";
        new ConfigStore(ConfigPath).Save(cfg);
    }

    [Fact]
    public void Untrusted_password_hash_and_remote_access_do_not_survive_takeover()
    {
        PlantHostileConfig();
        UserWritable(ConfigPath);

        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: false));

        var s = new ConfigStore(ConfigPath).Load().Settings;
        Assert.Null(s.OnErrorCommand);
        Assert.False(AuthService.VerifyPassword(AttackerPassword, s.PasswordHash), "the planted password still signs in");
        Assert.False(new AuthService(() => s).PasswordSet); // /api/auth/login refuses: no password until an administrator sets one
        Assert.False(s.AllowRemote);
        Assert.False(s.RequirePasswordLocally);
    }

    /// <summary>S3: the first takeover protects every ACL and then fails (a retained handle without delete sharing
    /// blocks the fresh-file replacement). The retry finds secure ACLs; the content is still untrusted.</summary>
    [Fact]
    public void Retry_after_a_failed_takeover_still_treats_the_content_as_untrusted()
    {
        PlantHostileConfig();
        UserWritable(ConfigPath);

        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.Equal(InstallResult.UnsafeDataFolder, Installer().Install(Exe, [], _data, start: false));
        Assert.Empty(_scm.Calls);
        Assert.Equal(Model.Protected(_data, true).Aces, _model.Map[_data].Aces); // attempt 1 did protect the folder

        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: false));
        var s = new ConfigStore(ConfigPath).Load().Settings;
        Assert.Null(s.OnErrorCommand);
        Assert.Equal(@"C:\Users\Public\payload.exe", s.QuarantinedOnErrorCommand);
        Assert.False(AuthService.VerifyPassword(AttackerPassword, s.PasswordHash), "the planted password survived the retry");
        Assert.False(s.AllowRemote);
    }

    /// <summary>S4: a forged ownership record for somebody else's pair, planted while the folder was writable, must
    /// not authorise changing or removing that pair after takeover. Explicit adoption still works afterwards.</summary>
    [Fact]
    public async Task Forged_ownership_records_are_not_promoted_by_takeover()
    {
        var pairsPath = Path.Combine(_data, "virtual-pairs.json");
        File.WriteAllText(pairsPath, """[{"number":7,"comName":"COM70","bridgeParams":{"PortName":"-","HiddenMode":"yes","dcd":"ropen"}}]""");
        UserWritable(pairsPath);

        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: false));

        var setupc = new FakeSetupcRunner();
        setupc.AddPair(7, "PortName=COM70", "PortName=-,HiddenMode=yes,dcd=ropen");                       // somebody else's
        setupc.AddPair(1, "PortName=COM10,PlugInMode=yes,EmuOverrun=yes,EmuBR=no", "PortName=-,HiddenMode=yes,dcd=ropen"); // older PortBridge
        var mgr = new Com0ComManager(null, new Com0ComOptions
        {
            Runner = setupc,
            Store = new Com0ComOwnershipStore(pairsPath),
            LocateSetupc = () => @"C:\Program Files (x86)\com0com\setupc.exe",
            WaitForDevice = (_, _) => Task.CompletedTask,
        });

        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => mgr.EnsurePairAsync("COM70", false, default));
        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => mgr.RemovePairAsync(7));
        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => mgr.EnsurePairAsync("COM10", false, default)); // needs adoption
        Assert.Empty(setupc.Mutations);

        // Migration path for COM10: an authenticated administrator adopts it explicitly; nothing in the driver changes.
        await mgr.AdoptPairAsync(1, "COM10");
        Assert.Equal(@"\\.\CNCB1", await mgr.EnsurePairAsync("COM10", false, default));
        Assert.Empty(setupc.Mutations);
    }
}

/// <summary>In-memory stand-in for the HKLM trust record. Nothing touches the real registry.</summary>
internal sealed class FakeTrustRecord : IDataDirTrustRecord
{
    public readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Calls = [];
    public bool FailMark;
    public bool IsTrusted(string dataDir) => Trusted.Contains(dataDir);
    public void MarkTrusted(string dataDir)
    {
        Calls.Add("mark");
        if (FailMark) throw new UnauthorizedAccessException("denied");
        Trusted.Add(dataDir);
    }
    public void Clear(string dataDir)
    {
        Calls.Add("clear");
        Trusted.Remove(dataDir);
    }
}

/// <summary>Trust record semantics: only a completed migration vouches for content; secure ACLs alone never do.</summary>
[Collection(AppPathsCollection.Name)]
public class TrustRecordTests : IDisposable
{
    private const string Exe = @"C:\Program Files\PortBridge\PortBridge.exe";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pb-trust-" + Guid.NewGuid().ToString("N")[..8], "PortBridge");
    private readonly Model _model = new();
    private readonly FakeRegistrar _scm = new();
    private readonly FakeTrustRecord _trust = new();
    private readonly StringWriter _out = new();

    public TrustRecordTests()
    {
        Directory.CreateDirectory(_data);
        _model.Map[_data] = Model.Protected(_data, true);
        _model.Map[Exe] = Model.Protected(Exe, false);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_data)!, true); } catch { /* ignore */ }
    }

    private ServiceInstaller Installer() =>
        new(_scm, new ProtectedPathPolicy(_model, _ => false), new DataDirGuard(_model, _model, _ => false, _trust), _out);

    private string ConfigPath => Path.Combine(_data, "config.json");

    private void SaveAdminConfig()
    {
        var cfg = new AppConfig();
        cfg.Settings.PasswordHash = AuthService.HashPassword("synthetic-admin-pw");
        cfg.Settings.OnErrorCommand = "echo admin";
        new ConfigStore(ConfigPath).Save(cfg);
        _model.Map[ConfigPath] = Model.Protected(ConfigPath, false);
    }

    [Fact]
    public void Content_of_a_folder_with_a_trust_record_is_kept()
    {
        SaveAdminConfig();
        _trust.Trusted.Add(_data);
        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: false));
        var s = new ConfigStore(ConfigPath).Load().Settings;
        Assert.True(AuthService.VerifyPassword("synthetic-admin-pw", s.PasswordHash));
        Assert.Equal("echo admin", s.OnErrorCommand);
        Assert.DoesNotContain("clear", _trust.Calls);
    }

    [Fact]
    public void Protected_looking_folder_without_a_record_is_migrated_once_then_trusted()
    {
        SaveAdminConfig(); // ACLs look fine, but nothing vouches for who wrote the content
        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: false));
        Assert.Null(new ConfigStore(ConfigPath).Load().Settings.PasswordHash);
        Assert.Equal(["clear", "mark"], _trust.Calls);
        Assert.Contains(ServiceTrust.BootstrapHint, _out.ToString());

        // The administrator bootstraps a password afterwards; the next start keeps it.
        var store = new ConfigStore(ConfigPath);
        var cfg = store.Load();
        cfg.Settings.PasswordHash = AuthService.HashPassword("synthetic-admin-pw");
        store.Save(cfg);
        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: false));
        Assert.True(AuthService.VerifyPassword("synthetic-admin-pw", new ConfigStore(ConfigPath).Load().Settings.PasswordHash));
    }

    [Fact]
    public void Folder_that_became_writable_again_loses_its_trust_record_before_anything_changes()
    {
        SaveAdminConfig();
        _trust.Trusted.Add(_data);
        _model.Map[_data] = Model.UserWritable(_data, true);
        _model.FailOn = _data; // the takeover then fails at the first ACL change
        Assert.Equal(InstallResult.UnsafeDataFolder, Installer().Install(Exe, [], _data, start: false));
        Assert.Empty(_trust.Trusted);
        Assert.Equal("clear", _trust.Calls[0]);
    }

    [Fact]
    public void A_migration_whose_trust_record_cannot_be_written_fails_closed_and_is_repeated()
    {
        File.WriteAllText(ConfigPath, "{\"settings\":{\"onErrorCommand\":\"calc.exe\",\"allowRemote\":true}}");
        _model.Map[ConfigPath] = Model.UserWritable(ConfigPath, false);
        _trust.FailMark = true;
        Assert.Equal(InstallResult.UnsafeDataFolder, Installer().Install(Exe, [], _data, start: false));
        Assert.Empty(_scm.Calls);
        _trust.FailMark = false;
        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: false));
        var s = new ConfigStore(ConfigPath).Load().Settings;
        Assert.Null(s.OnErrorCommand);
        Assert.Equal("calc.exe", s.QuarantinedOnErrorCommand);
        Assert.False(s.AllowRemote);
    }

    [Fact]
    public void Unreadable_untrusted_config_is_set_aside_not_loaded()
    {
        File.WriteAllText(ConfigPath, "{ not json");
        Assert.Equal(InstallResult.Ok, Installer().Install(Exe, [], _data, start: false));
        Assert.False(File.Exists(ConfigPath));
        Assert.Single(Directory.GetFiles(_data, "config.json.untrusted-*"));
    }

    /// <summary>S6: the ordinary 'service start' route validates the registered image like install does.</summary>
    [Fact]
    public void Service_start_refuses_an_unprotected_registered_image_and_starts_a_protected_one()
    {
        _scm.IsInstalled = true;
        const string unsafeExe = @"C:\Users\Public\pb-nonexistent-fixture\PortBridge.exe"; // modelled only; the folder does not exist
        _scm.RegisteredCommandLine = $"\"{unsafeExe}\" --service";
        _model.Map[unsafeExe] = Model.UserWritable(unsafeExe, false);
        Assert.Equal(InstallResult.UnsafeExecutableLocation, Installer().StartInstalled());
        Assert.Empty(_scm.Calls);

        _scm.RegisteredCommandLine = @"C:\Program Files\PortBridge\PortBridge.exe --service"; // unquoted: cannot be checked unambiguously
        Assert.Equal(InstallResult.UnsafeExecutableLocation, Installer().StartInstalled());
        Assert.Empty(_scm.Calls);

        _scm.RegisteredCommandLine = ServiceInstaller.CommandLine(Exe);
        Assert.Equal(InstallResult.Ok, Installer().StartInstalled());
        Assert.Equal(["start"], _scm.Calls);

        _scm.IsInstalled = false;
        Assert.Equal(InstallResult.Failed, Installer().StartInstalled());
    }

    [Fact]
    public void Setupc_with_a_writable_companion_is_not_run()
    {
        const string setupc = @"C:\Program Files (x86)\com0com\setupc.exe";
        const string dll = @"C:\Program Files (x86)\com0com\helper.dll";
        _model.Map[setupc] = Model.Protected(setupc, false);
        _model.Map[dll] = Model.UserWritable(dll, false);
        var guard = new DataDirGuard(_model, _model, _ => false);
        var policy = new ProtectedPathPolicy(_model, _ => false);
        var options = ServiceTrust.EngineOptions(true, guard, policy, () => true, _ => [dll]);
        Assert.NotNull(options.RejectExecutable!(setupc));
        Assert.Null(ServiceTrust.EngineOptions(true, guard, policy, () => true, _ => []).RejectExecutable!(setupc));
    }

    /// <summary>S4 (elevated desktop variant): an elevated process whose data folder non-administrators can edit does
    /// not let ownership records authorise setupc changes.</summary>
    [Fact]
    public async Task Elevated_process_with_an_editable_folder_does_not_trust_ownership_records()
    {
        var previous = AppPaths.DataDir;
        AppPaths.DataDir = _data;
        try
        {
            _model.Map[_data] = Model.UserWritable(_data, true);
            var options = ServiceTrust.EngineOptions(false, new DataDirGuard(_model, _model, _ => false), new ProtectedPathPolicy(_model, _ => false), () => true);
            var pairsPath = Path.Combine(_data, "virtual-pairs.json");
            new Com0ComOwnershipStore(pairsPath).Upsert(new OwnedPair
            {
                Number = 7, ComName = "COM70", BridgeParams = new() { ["PortName"] = "-", ["HiddenMode"] = "yes", ["dcd"] = "ropen" },
            });
            var setupc = new FakeSetupcRunner();
            setupc.AddPair(7, "PortName=COM70", "PortName=-,HiddenMode=yes,dcd=ropen");
            var mgr = new Com0ComManager(null, new Com0ComOptions
            {
                Runner = setupc,
                Store = new Com0ComOwnershipStore(pairsPath),
                LocateSetupc = () => @"C:\Program Files (x86)\com0com\setupc.exe",
                WaitForDevice = (_, _) => Task.CompletedTask,
                OwnershipUntrusted = options.OwnershipUntrusted,
            });
            var ex = await Assert.ThrowsAsync<Com0ComOwnershipException>(() => mgr.EnsurePairAsync("COM70", false, default));
            Assert.Contains("not trusted", ex.Message);
            await Assert.ThrowsAsync<Com0ComOwnershipException>(() => mgr.RemovePairAsync(7));
            Assert.Empty(setupc.Mutations);
            Assert.False((await mgr.ListPairsAsync()).Single().Owned);
        }
        finally
        {
            AppPaths.DataDir = previous;
        }
    }
}

/// <summary>S2: the local sign-in token is always written into a new file object, so a handle opened earlier (e.g.
/// while a standard user could still read the desktop-owned folder) never observes the new credential. Tokens are
/// generated by the test's own AuthService instances and never printed.</summary>
[Collection(AppPathsCollection.Name)]
public class LocalTokenFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-token-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _previous = AppPaths.DataDir;

    public LocalTokenFileTests()
    {
        Directory.CreateDirectory(_dir);
        AppPaths.DataDir = _dir;
    }

    public void Dispose()
    {
        AppPaths.DataDir = _previous;
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private static string ReadAll(FileStream s)
    {
        s.Position = 0;
        var buf = new byte[4096];
        var n = s.Read(buf, 0, buf.Length);
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    [Fact]
    public void A_handle_opened_before_the_write_cannot_read_the_new_token()
    {
        File.WriteAllText(AppPaths.LocalTokenFile, "stale-synthetic-value");
        using var stale = new FileStream(AppPaths.LocalTokenFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var auth = new AuthService(() => new AppSettings());

        auth.WriteLocalTokenFile();

        Assert.False(ReadAll(stale).Contains(auth.LocalToken, StringComparison.Ordinal), "a handle opened earlier observed the new credential");
        Assert.True(string.Equals(AuthService.ReadLocalTokenFile(), auth.LocalToken, StringComparison.Ordinal), "the token file does not hold the new credential");
    }

    [Fact]
    public void A_handle_that_blocks_replacement_gets_no_token_written_into_it()
    {
        File.WriteAllText(AppPaths.LocalTokenFile, "stale-synthetic-value");
        using var stale = new FileStream(AppPaths.LocalTokenFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); // no delete sharing
        var auth = new AuthService(() => new AppSettings());

        auth.WriteLocalTokenFile();

        Assert.False(ReadAll(stale).Contains(auth.LocalToken, StringComparison.Ordinal), "the credential was written into a file object another handle can read");
    }
}
