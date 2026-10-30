using PortBridge.Core.Serial;

namespace PortBridge.Tests;

/// <summary>Models com0com's setupc.exe: pairs with A/B parameters, list/busynames/install/change/remove.</summary>
internal sealed class FakeSetupc : ISetupcRunner
{
    public readonly SortedDictionary<int, (Dictionary<string, string> A, Dictionary<string, string> B)> Pairs = new();
    public readonly List<string> Calls = [];
    public readonly HashSet<string> OtherBusyNames = new(StringComparer.OrdinalIgnoreCase);
    public Func<string>? InstallFails;
    public Action? DuringInstall;
    public bool InstallCreatesNothing;
    /// <summary>setupc never returns (as when Windows waits on a driver prompt nobody can answer) until it is killed.</summary>
    public bool InstallHangs;
    /// <summary>setupc creates the pair at once but only returns after this long.</summary>
    public TimeSpan InstallDuration;

    public void AddPair(int n, string a, string b) => Pairs[n] = (Parse(a), Parse(b));

    private static Dictionary<string, string> Parse(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);

    private static string Format(Dictionary<string, string> d) => string.Join(",", d.Select(kv => $"{kv.Key}={kv.Value}"));

    public IEnumerable<string> Mutations => Calls.Where(c => !c.StartsWith("list") && !c.StartsWith("busynames"));

    public Task<string> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        lock (this)
        {
            Calls.Add(string.Join(' ', args));
            switch (args[0])
            {
                case "list":
                    return Task.FromResult(string.Join("\n", Pairs.SelectMany(p => new[]
                    {
                        $"       CNCA{p.Key} {Format(p.Value.A)}",
                        $"       CNCB{p.Key} {Format(p.Value.B)}",
                    })));
                case "busynames":
                    var names = Pairs.Values.SelectMany(p => new[] { p.A, p.B })
                        .Select(d => d.TryGetValue("PortName", out var n) ? n : "-").Where(n => n != "-")
                        .Concat(OtherBusyNames);
                    return Task.FromResult(string.Join("\n", names));
                case "install":
                    DuringInstall?.Invoke();
                    if (InstallFails?.Invoke() is { } err) throw new InvalidOperationException(err);
                    if (InstallHangs) return HangAsync(ct);
                    if (InstallCreatesNothing) return Task.FromResult("");
                    var n2 = 0;
                    while (Pairs.ContainsKey(n2)) n2++;
                    AddPair(n2, args[1], args[2]);
                    var echo = $"       CNCA{n2} {args[1]}\n       CNCB{n2} {args[2]}\n";
                    return InstallDuration > TimeSpan.Zero ? ReturnLaterAsync(echo, ct) : Task.FromResult(echo);
                case "change":
                    var id = args[1];
                    var num = int.Parse(id[4..]);
                    var target = id.StartsWith("CNCA") ? Pairs[num].A : Pairs[num].B;
                    foreach (var (k, v) in Parse(args[2])) target[k] = v;
                    return Task.FromResult("");
                case "remove":
                    Pairs.Remove(int.Parse(args[1]));
                    return Task.FromResult("");
            }
            throw new InvalidOperationException("unexpected setupc call " + string.Join(' ', args));
        }
    }

    private static async Task<string> HangAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return "";
    }

    private async Task<string> ReturnLaterAsync(string output, CancellationToken ct)
    {
        await Task.Delay(InstallDuration, ct);
        return output;
    }
}

/// <summary>F3: PortBridge only modifies com0com pairs it can prove it owns; everything else fails closed.</summary>
public class Com0ComOwnershipTests : IDisposable
{
    private const string Ours = "PortName=-,HiddenMode=yes,dcd=ropen";
    private readonly string _dir = Scratch.NewDir("c0c");
    private readonly FakeSetupc _setupc = new();
    private readonly List<string> _waited = [];
    private Func<string, CancellationToken, Task>? _wait;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private Com0ComOwnershipStore Store(string? path = null) => new(path ?? Path.Combine(_dir, "virtual-pairs.json"));

    private Com0ComManager Manager(Com0ComOwnershipStore? store = null, Func<string, string?>? reject = null) => new(null, new Com0ComOptions
    {
        Runner = _setupc,
        Store = store ?? Store(),
        LocateSetupc = () => @"C:\Program Files (x86)\com0com\setupc.exe",
        WaitForDevice = (p, ct) => { _waited.Add(p); return _wait?.Invoke(p, ct) ?? Task.CompletedTask; },
        RejectExecutable = reject,
        DriverInstallLog = DriverLog,
        DriverLogPollInterval = TimeSpan.FromMilliseconds(20),
    });

    private string DriverLog => Path.Combine(_dir, "setupapi.dev.log");

    // Excerpts of setupapi.dev.log on a PC that does not trust com0com's publisher (Windows 10, service install).
    private const string UntrustedSignature =
        "     sig:                FilePath = c:\\program files (x86)\\com0com\\com0com.inf\r\n" +
        "     sig:                Error 0xe0000242: The publisher of an Authenticode(tm) signed catalog has not yet been established as trusted.\r\n";
    private const string Refused =
        "!!!  sto:                Failed to import driver package into Driver Store. Error = 0xE0000242\r\n" +
        "!!!  ndv: Failed to install device instance 'ROOT\\CNCPORTS\\0000'. Error = 0xe0000242\r\n";

    [Fact]
    public async Task Driver_refused_as_untrusted_is_reported_at_once_instead_of_waiting_for_setupc()
    {
        File.WriteAllText(DriverLog, "earlier entries\r\n");
        _setupc.InstallHangs = true;
        _setupc.DuringInstall = () => File.AppendAllText(DriverLog, UntrustedSignature + Refused);

        var ex = await Assert.ThrowsAsync<Com0ComDriverNotTrustedException>(() => Manager().EnsurePairAsync("COM18", false, default))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("0xE0000242", ex.Message);
        Assert.Empty(_setupc.Pairs);
        Assert.Empty(Store().Load());
    }

    [Fact]
    public async Task Setupc_failing_after_the_driver_was_refused_reports_why()
    {
        _setupc.DuringInstall = () => File.AppendAllText(DriverLog, UntrustedSignature + Refused);
        _setupc.InstallFails = () => "setupc: install failed";
        await Assert.ThrowsAsync<Com0ComDriverNotTrustedException>(() => Manager().EnsurePairAsync("COM18", false, default));
    }

    [Fact]
    public async Task A_trust_prompt_that_is_still_open_does_not_stop_the_install()
    {
        // An interactive install logs the untrusted signature, then waits for the user; only "!!!" lines mean refused.
        _setupc.InstallDuration = TimeSpan.FromMilliseconds(300);
        _setupc.DuringInstall = () => File.AppendAllText(DriverLog, UntrustedSignature);
        Assert.Equal(@"\\.\CNCB0", await Manager().EnsurePairAsync("COM18", false, default));
    }

    [Fact]
    public async Task A_refusal_logged_before_this_install_does_not_count()
    {
        File.WriteAllText(DriverLog, UntrustedSignature + Refused); // e.g. before the publisher was trusted
        _setupc.InstallDuration = TimeSpan.FromMilliseconds(300);
        Assert.Equal(@"\\.\CNCB0", await Manager().EnsurePairAsync("COM18", false, default));
    }

    [Fact]
    public async Task Foreign_pair_with_the_requested_name_is_not_touched()
    {
        _setupc.AddPair(0, "PortName=COM20", "PortName=COM21");
        var ex = await Assert.ThrowsAsync<Com0ComOwnershipException>(() => Manager().EnsurePairAsync("COM20", false, default));
        Assert.Contains("did not create", ex.Message);
        Assert.Empty(_setupc.Mutations);
        Assert.Equal("COM21", _setupc.Pairs[0].B["PortName"]);
    }

    [Fact]
    public async Task Look_alike_parameters_do_not_prove_ownership()
    {
        _setupc.AddPair(1, "PortName=COM10,PlugInMode=yes,EmuOverrun=yes", Ours);
        var ex = await Assert.ThrowsAsync<Com0ComOwnershipException>(() => Manager().EnsurePairAsync("COM10", true, default));
        Assert.Contains("adopt", ex.Message);
        Assert.Empty(_setupc.Mutations);
        Assert.False((await Manager().ListPairsAsync()).Single().Owned);
    }

    [Fact]
    public async Task Name_used_by_the_other_side_of_a_foreign_pair_is_refused()
    {
        _setupc.AddPair(2, "PortName=COM30", "PortName=COM31");
        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => Manager().EnsurePairAsync("COM31", false, default));
        Assert.Empty(_setupc.Mutations);
    }

    [Fact]
    public async Task Ambiguous_duplicates_are_refused()
    {
        _setupc.AddPair(3, "PortName=COM40", Ours);
        _setupc.AddPair(4, "PortName=COM40", Ours);
        Store().Upsert(new OwnedPair { Number = 3, ComName = "COM40", BridgeParams = new() { ["PortName"] = "-", ["HiddenMode"] = "yes", ["dcd"] = "ropen" } });
        var ex = await Assert.ThrowsAsync<Com0ComOwnershipException>(() => Manager().EnsurePairAsync("COM40", false, default));
        Assert.Contains("more than one", ex.Message);
        Assert.Empty(_setupc.Mutations);
    }

    [Fact]
    public async Task Created_pair_is_owned_reused_and_only_its_own_switches_change()
    {
        var m = Manager();
        var path = await m.EnsurePairAsync("COM12", false, default);
        Assert.Equal(@"\\.\CNCB0", path);
        Assert.Single(_setupc.Mutations, c => c.StartsWith("install"));
        var rec = Store().Find("COM12");
        Assert.NotNull(rec);
        Assert.Equal("created", rec!.Origin);
        Assert.Equal(0, rec.Number);

        Assert.Equal(path, await m.EnsurePairAsync("COM12", false, default));
        Assert.Single(_setupc.Mutations); // no second install, no change

        Assert.Equal(path, await m.EnsurePairAsync("COM12", true, default)); // baud emulation toggled in config
        Assert.Equal("change CNCA0 PortName=COM12,PlugInMode=yes,EmuOverrun=yes,EmuBR=yes", _setupc.Mutations.Last());
        Assert.True((await m.ListPairsAsync()).Single().Owned);
    }

    [Fact]
    public async Task Stale_record_whose_pair_was_renamed_is_dropped_without_touching_that_pair()
    {
        Store().Upsert(new OwnedPair { Number = 5, ComName = "COM50", BridgeParams = new() { ["PortName"] = "-", ["HiddenMode"] = "yes", ["dcd"] = "ropen" } });
        _setupc.AddPair(5, "PortName=COM51,PlugInMode=yes", Ours); // reused by someone else under another name

        var path = await Manager().EnsurePairAsync("COM50", false, default);
        Assert.Equal(@"\\.\CNCB0", path);
        Assert.Equal("COM51", _setupc.Pairs[5].A["PortName"]);
        Assert.DoesNotContain(_setupc.Mutations, c => c.Contains("CNCA5") || c.Contains("CNCB5") || c == "remove 5");
        Assert.Equal(0, Store().Find("COM50")!.Number);
        Assert.Null(Store().Find(5));
    }

    [Fact]
    public async Task Reused_pair_id_with_changed_bridge_side_fails_closed()
    {
        Store().Upsert(new OwnedPair { Number = 6, ComName = "COM60", BridgeParams = new() { ["PortName"] = "-", ["HiddenMode"] = "yes", ["dcd"] = "ropen" } });
        _setupc.AddPair(6, "PortName=COM60,PlugInMode=yes,EmuOverrun=yes", "PortName=COM61"); // B side now visible elsewhere

        var ex = await Assert.ThrowsAsync<Com0ComOwnershipException>(() => Manager().EnsurePairAsync("COM60", false, default));
        Assert.Contains("no longer matches", ex.Message);
        Assert.Empty(_setupc.Mutations);
        Assert.False((await Manager().ListPairsAsync()).Single().Owned);
    }

    [Fact]
    public async Task Create_failure_leaves_no_ownership_record()
    {
        _setupc.InstallFails = () => "driver refused";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Manager().EnsurePairAsync("COM13", false, default));
        Assert.Null(Store().Find("COM13"));

        _setupc.InstallFails = null;
        _setupc.InstallCreatesNothing = true;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Manager().EnsurePairAsync("COM13", false, default));
        Assert.Contains("could not create", ex.Message);
        Assert.Null(Store().Find("COM13"));
    }

    [Fact]
    public async Task Cancellation_before_install_creates_nothing_and_during_install_still_records_ownership()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manager().EnsurePairAsync("COM14", false, cts.Token));
        Assert.Empty(_setupc.Mutations);

        using var late = new CancellationTokenSource();
        _setupc.DuringInstall = late.Cancel;
        _wait = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manager().EnsurePairAsync("COM14", false, late.Token));
        Assert.Equal(0, Store().Find("COM14")!.Number); // the created pair is not orphaned
        _wait = null;
        _setupc.DuringInstall = null;
        Assert.Equal(@"\\.\CNCB0", await Manager().EnsurePairAsync("COM14", false, default));
        Assert.Single(_setupc.Mutations, c => c.StartsWith("install"));
    }

    [Fact]
    public async Task Ownership_that_cannot_be_recorded_rolls_the_creation_back()
    {
        var blocked = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocked, "x");
        var store = Store(Path.Combine(blocked, "virtual-pairs.json")); // parent is a file: the save fails
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Manager(store).EnsurePairAsync("COM15", false, default));
        Assert.Contains("removed again", ex.Message);
        Assert.Empty(_setupc.Pairs);
        Assert.Equal("remove 0", _setupc.Mutations.Last());
    }

    [Fact]
    public async Task Remove_refuses_foreign_pairs_and_removes_owned_ones()
    {
        _setupc.AddPair(7, "PortName=COM70", "PortName=COM71");
        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => Manager().RemovePairAsync(7));
        Assert.True(_setupc.Pairs.ContainsKey(7));

        var m = Manager();
        await m.EnsurePairAsync("COM16", false, default);
        var n = Store().Find("COM16")!.Number;
        await m.RemovePairAsync(n);
        Assert.False(_setupc.Pairs.ContainsKey(n));
        Assert.Null(Store().Find(n));
    }

    [Fact]
    public async Task Explicit_adoption_records_ownership_without_changing_the_pair()
    {
        _setupc.AddPair(8, "PortName=COM80,PlugInMode=yes,EmuOverrun=yes,EmuBR=no", Ours);
        _setupc.AddPair(9, "PortName=COM90", "PortName=COM91");
        var m = Manager();

        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => m.AdoptPairAsync(8, "COM81")); // wrong expectation
        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => m.AdoptPairAsync(9, "COM90")); // not PortBridge's layout
        Assert.Null(Store().Find(9));

        var rec = await m.AdoptPairAsync(8, "COM80");
        Assert.Equal("adopted", rec.Origin);
        Assert.Equal("COM80", rec.PriorUserParams!["PortName"]);
        Assert.Empty(_setupc.Mutations);
        Assert.Equal(@"\\.\CNCB8", await m.EnsurePairAsync("COM80", false, default));
        Assert.Empty(_setupc.Mutations);

        m.ReleasePair(8);
        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => m.EnsurePairAsync("COM80", false, default));
    }

    /// <summary>S5: the recorded bridge side keeps every recorded key but gains an observable routing parameter.</summary>
    [Fact]
    public async Task Bridge_side_that_gained_a_parameter_is_no_longer_owned()
    {
        Store().Upsert(new OwnedPair { Number = 6, ComName = "COM60", BridgeParams = new() { ["PortName"] = "-", ["HiddenMode"] = "yes", ["dcd"] = "ropen" } });
        _setupc.AddPair(6, "PortName=COM60,PlugInMode=yes,EmuOverrun=yes", "PortName=-,HiddenMode=yes,dcd=ropen,cts=on");

        var ex = await Assert.ThrowsAsync<Com0ComOwnershipException>(() => Manager().EnsurePairAsync("COM60", false, default));
        Assert.Contains("no longer matches", ex.Message);
        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => Manager().RemovePairAsync(6));
        Assert.Empty(_setupc.Mutations);
        Assert.False((await Manager().ListPairsAsync()).Single().Owned);
    }

    /// <summary>S4: malformed records (no recorded bridge side, unknown origin, bad name, duplicates) authorise nothing.</summary>
    [Theory]
    [InlineData("""[{"number":7,"comName":"COM70","bridgeParams":{}}]""")]
    [InlineData("""[{"number":7,"comName":"COM70"}]""")]
    [InlineData("""[{"number":7,"comName":"COM70","origin":"whatever","bridgeParams":{"PortName":"-","HiddenMode":"yes","dcd":"ropen"}}]""")]
    [InlineData("""[{"number":7,"comName":"COM70,x=y","bridgeParams":{"PortName":"-","HiddenMode":"yes","dcd":"ropen"}}]""")]
    [InlineData("""[{"number":7,"comName":"COM70","bridgeParams":{"PortName":"-","HiddenMode":"yes","dcd":"ropen"}},{"number":7,"comName":"COM72","bridgeParams":{"PortName":"-","HiddenMode":"yes","dcd":"ropen"}}]""")]
    public async Task Malformed_ownership_records_authorise_no_setupc_mutation(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "virtual-pairs.json"), json);
        _setupc.AddPair(7, "PortName=COM70", "PortName=-,HiddenMode=yes,dcd=ropen"); // somebody else's pair

        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => Manager().EnsurePairAsync("COM70", false, default));
        await Assert.ThrowsAsync<Com0ComOwnershipException>(() => Manager().RemovePairAsync(7));
        Assert.Empty(_setupc.Mutations);
        Assert.False((await Manager().ListPairsAsync()).Single().Owned);
    }

    [Theory]
    [InlineData("COM5,HiddenMode=yes")]
    [InlineData("COM5 PortName=COM6")]
    [InlineData("CNCB0")]
    [InlineData("COM0")]
    public async Task Port_names_that_could_inject_setupc_parameters_are_rejected(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Manager().EnsurePairAsync(name, false, default));
        Assert.Empty(_setupc.Calls);
    }

    [Fact]
    public async Task Setupc_from_an_untrusted_location_is_not_run()
    {
        var m = Manager(reject: _ => "user-writable");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => m.EnsurePairAsync("COM17", false, default));
        Assert.Contains("Refusing to run", ex.Message);
        Assert.Empty(_setupc.Calls);
    }
}
