using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PortBridge.Core.Serial;

public sealed record Com0ComPort(string Id, IReadOnlyDictionary<string, string> Parameters)
{
    public bool IsA => Id.StartsWith("CNCA", StringComparison.OrdinalIgnoreCase);
    public int Number => int.Parse(Id.AsSpan(4));
    public string PortName => Parameters.TryGetValue("PortName", out var n) && n != "-" ? n : Id;
}

public sealed record Com0ComPair(int Number, string UserPort, string BridgePort, IReadOnlyDictionary<string, string> UserParams, IReadOnlyDictionary<string, string> BridgeParams)
{
    /// <summary>Has the parameter layout PortBridge uses. This does NOT prove PortBridge created it — see <see cref="Owned"/>.</summary>
    public bool HasPortBridgeLayout =>
        BridgeParams.TryGetValue("dcd", out var dcd) && dcd.Equals("ropen", StringComparison.OrdinalIgnoreCase) &&
        UserParams.TryGetValue("PlugInMode", out var p) && p.Equals("yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>PortBridge has an ownership record for this pair and the pair still matches it.</summary>
    public bool Owned { get; init; }
    /// <summary>"created" / "adopted" when owned.</summary>
    public string? Origin { get; init; }
}

/// <summary>Runs com0com's setupc.exe. Throws when it fails. Replaced by a fake in tests.</summary>
public interface ISetupcRunner
{
    Task<string> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct);
}

public sealed class Com0ComOptions
{
    public ISetupcRunner? Runner { get; init; }
    public Com0ComOwnershipStore? Store { get; init; }
    /// <summary>Waits until the bridge-side device can be opened. Default: polls CreateFile.</summary>
    public Func<string, CancellationToken, Task>? WaitForDevice { get; init; }
    /// <summary>Overrides setupc.exe discovery (tests).</summary>
    public Func<string?>? LocateSetupc { get; init; }
    /// <summary>Returns a reason when setupc.exe at the given path must not be run in this process (e.g. a
    /// user-writable path while running as SYSTEM). Null = allowed.</summary>
    public Func<string, string?>? RejectExecutable { get; init; }
    /// <summary>Returns a reason when the ownership store must not be trusted in this process (e.g. running elevated
    /// while the data folder is editable by non-administrators); recorded pairs are then treated as not owned.</summary>
    public Func<string?>? OwnershipUntrusted { get; init; }
    public Action<string>? Log { get; init; }
    /// <summary>Windows' driver-installation log, watched while setupc installs a pair. Default: %windir%\INF\setupapi.dev.log.</summary>
    public string? DriverInstallLog { get; init; }
    /// <summary>How often that log is checked during an install (tests shorten it).</summary>
    public TimeSpan? DriverLogPollInterval { get; init; }
}

/// <summary>Windows refused to install the com0com driver because its publisher is not trusted on this PC.</summary>
public sealed class Com0ComDriverNotTrustedException(string message) : InvalidOperationException(message);

/// <summary>
/// Drives the com0com null-modem driver through its setupc.exe tool. A virtual COMx is a pair:
/// CNCAn (renamed to COMx, what applications open) and CNCBn (hidden, opened by PortBridge).
///  - CNCAn PlugInMode=yes: COMx only appears while PortBridge has CNCBn open, like TCP-Com's virtual ports.
///  - CNCAn EmuOverrun=yes: an application that stops reading loses data instead of stalling the bridge.
///  - CNCBn dcd=ropen: PortBridge sees DCD on when an application opens COMx.
///  - CNCBn HiddenMode=yes: the bridge side never shows up in port lists.
///
/// Ownership: PortBridge only ever modifies or removes pairs listed in its ownership store — pairs it created, or
/// that an administrator explicitly adopted. Any other pair using the requested name (or ambiguous/changed pairs)
/// makes <see cref="EnsurePairAsync"/> fail without touching it.
/// </summary>
public sealed partial class Com0ComManager : IVirtualPortProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private readonly Func<string?> _configuredPath;
    private readonly ISetupcRunner _runner;
    private readonly Com0ComOwnershipStore? _store;
    private readonly Com0ComOptions _options;

    public Com0ComManager(Func<string?>? configuredPath = null, Com0ComOptions? options = null)
    {
        _configuredPath = configuredPath ?? (() => null);
        _options = options ?? new Com0ComOptions();
        _runner = _options.Runner ?? new ProcessSetupcRunner();
        _store = _options.Store;
    }

    private Com0ComOwnershipStore Store => _store ?? new Com0ComOwnershipStore();

    [GeneratedRegex(@"^\s*(CNC[AB]\d+)\s+(.*)$")]
    private static partial Regex ListLine();

    [GeneratedRegex(@"^COM[1-9][0-9]{0,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex ComName();

    public string? SetupcPath
    {
        get
        {
            if (_options.LocateSetupc is { } locate) return locate();
            var configured = _configuredPath();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var p = Directory.Exists(configured) ? Path.Combine(configured, "setupc.exe") : configured;
                if (File.Exists(p)) return p;
            }
            foreach (var candidate in Candidates())
                if (File.Exists(candidate)) return candidate;
            return null;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        // The installer records its folder in the uninstall key.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            string? loc = null;
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\com0com");
                if (k?.GetValue("UninstallString") is string u) loc = Path.GetDirectoryName(u.Trim('"'));
            }
            catch { /* ignore */ }
            if (loc is not null) yield return Path.Combine(loc, "setupc.exe");
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "com0com", "setupc.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "com0com", "setupc.exe");
    }

    public bool IsInstalled => SetupcPath is not null;

    /// <summary>All pairs, each marked owned/unowned against the ownership store.</summary>
    public async Task<List<Com0ComPair>> ListPairsAsync(CancellationToken ct = default)
    {
        var pairs = await ListRawPairsAsync(ct);
        var owned = OwnershipUntrusted is null ? Store.Load() : [];
        return pairs.Select(p =>
        {
            var rec = owned.FirstOrDefault(o => o.Number == p.Number);
            return rec is not null && IdentityProblem(p, rec) is null ? p with { Owned = true, Origin = rec.Origin } : p;
        }).ToList();
    }

    private async Task<List<Com0ComPair>> ListRawPairsAsync(CancellationToken ct)
    {
        var ports = await ListPortsAsync(ct);
        var pairs = new List<Com0ComPair>();
        foreach (var a in ports.Where(p => p.IsA))
        {
            var b = ports.FirstOrDefault(p => !p.IsA && p.Number == a.Number);
            pairs.Add(new Com0ComPair(a.Number, a.PortName, b?.PortName ?? $"CNCB{a.Number}", a.Parameters,
                b?.Parameters ?? new Dictionary<string, string>()));
        }
        return pairs.OrderBy(p => p.Number).ToList();
    }

    private async Task<List<Com0ComPort>> ListPortsAsync(CancellationToken ct)
    {
        var output = await RunAsync(["list"], ct);
        var ports = new List<Com0ComPort>();
        foreach (var line in output.Split('\n'))
        {
            var m = ListLine().Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            ports.Add(new Com0ComPort(m.Groups[1].Value.ToUpperInvariant(), ParseParams(m.Groups[2].Value)));
        }
        return ports;
    }

    private static Dictionary<string, string> ParseParams(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) d[part[..eq]] = part[(eq + 1)..];
        }
        return d;
    }

    private static string UserParams(string comName, bool emulateBaud) =>
        $"PortName={comName},PlugInMode=yes,EmuOverrun=yes,EmuBR={(emulateBaud ? "yes" : "no")}";

    private const string BridgeParams = "PortName=-,HiddenMode=yes,dcd=ropen";

    private static string PairName(Com0ComPair p) => $"CNCA{p.Number}/CNCB{p.Number}";

    private string? OwnershipUntrusted => _options.OwnershipUntrusted?.Invoke();

    /// <summary>Why <paramref name="pair"/> can no longer be trusted to be the pair described by <paramref name="rec"/> (null = matches).
    /// The bridge side must be identical to what was recorded — no key changed, removed or added — which is stricter
    /// than the subset check used to decide whether PortBridge's own switches need applying.</summary>
    private static string? IdentityProblem(Com0ComPair pair, OwnedPair rec)
    {
        if (pair.Number != rec.Number) return $"it is now pair {pair.Number}, not {rec.Number}";
        if (!pair.UserPort.Equals(rec.ComName, StringComparison.OrdinalIgnoreCase))
            return $"CNCA{pair.Number} is now named {pair.UserPort}, not {rec.ComName}";
        if (!SameIdentity(pair.BridgeParams, rec.BridgeParams))
            return $"its bridge side CNCB{pair.Number} was changed ({Describe(pair.BridgeParams)})";
        return null;
    }

    /// <summary>Normalised parameter set: keys and values compared case-insensitively; "no" (the default of the boolean
    /// switches, which setupc may or may not list) is dropped. Null if a key appears twice with different values.</summary>
    internal static SortedDictionary<string, string>? Normalize(IReadOnlyDictionary<string, string>? p)
    {
        var n = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in p ?? new Dictionary<string, string>())
        {
            if (v.Equals("no", StringComparison.OrdinalIgnoreCase)) continue;
            var key = k.ToLowerInvariant();
            var value = v.ToUpperInvariant();
            if (n.TryGetValue(key, out var existing) && existing != value) return null;
            n[key] = value;
        }
        return n;
    }

    private static bool SameIdentity(IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string>? recorded) =>
        Normalize(actual) is { } a && Normalize(recorded) is { } r && a.Count == r.Count && a.All(kv => r.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>True when a recorded bridge side carries PortBridge's layout (every created or adopted pair has it).</summary>
    internal static bool HasBridgeLayout(IReadOnlyDictionary<string, string>? recorded) =>
        recorded is { Count: > 0 } && Normalize(recorded) is { } n &&
        ParamsMatch(n, new Dictionary<string, string>(ParseParams(BridgeParams).Select(kv => KeyValuePair.Create(kv.Key.ToLowerInvariant(), kv.Value.ToUpperInvariant()))));

    internal static bool IsComName(string? name) => name is not null && ComName().IsMatch(name);

    private static string Describe(IReadOnlyDictionary<string, string> p) =>
        p.Count == 0 ? "defaults" : string.Join(",", p.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>Makes sure a PortBridge-owned pair exposing <paramref name="comName"/> exists and returns the device
    /// path PortBridge opens. Never modifies a pair PortBridge does not own.</summary>
    public async Task<string> EnsurePairAsync(string comName, bool emulateBaud, CancellationToken ct)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("Virtual COM ports need the com0com driver. Install it from the Ports page.");
        comName = comName.Trim().ToUpperInvariant();
        // The name goes into setupc's parameter string: reject anything that could smuggle extra parameters.
        if (!ComName().IsMatch(comName))
            throw new ArgumentException($"\"{comName}\" is not a valid COM port name (COM1 … COM999).");

        await _ensureGate.WaitAsync(ct);
        try
        {
            return await EnsurePairLockedAsync(comName, emulateBaud, ct);
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    private async Task<string> EnsurePairLockedAsync(string comName, bool emulateBaud, CancellationToken ct)
    {
        var store = Store;
        var pairs = await ListRawPairsAsync(ct);
        var untrusted = OwnershipUntrusted;
        var record = untrusted is null ? store.Find(comName) : null;
        var matches = pairs.Where(p => p.UserPort.Equals(comName, StringComparison.OrdinalIgnoreCase) ||
                                       p.BridgePort.Equals(comName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count > 1)
            throw new Com0ComOwnershipException(
                $"{comName} is used by more than one com0com pair ({string.Join(", ", matches.Select(PairName))}). " +
                "PortBridge will not change any of them. Remove the duplicates with the com0com setup tool or pick another name.");

        if (matches.Count == 1)
        {
            var pair = matches[0];
            if (record is null)
                throw new Com0ComOwnershipException(
                    $"{comName} belongs to an existing com0com pair ({PairName(pair)}) that PortBridge did not create. " +
                    "PortBridge will not reconfigure it. Pick another port name, remove that pair with the com0com setup tool, " +
                    (pair.HasPortBridgeLayout ? "or — if an earlier PortBridge version created it — adopt it on the Ports page." : "or reconfigure it yourself.") +
                    (untrusted is null ? "" : $" (PortBridge's ownership records are not trusted here: {untrusted})"));
            if (!pair.UserPort.Equals(comName, StringComparison.OrdinalIgnoreCase))
                throw new Com0ComOwnershipException(
                    $"{comName} is now the bridge side of com0com pair {PairName(pair)}, not a PortBridge virtual port. PortBridge will not change it.");
            if (IdentityProblem(pair, record) is { } problem)
                throw new Com0ComOwnershipException(
                    $"The com0com pair PortBridge recorded for {comName} no longer matches: {problem}. " +
                    "It may have been changed or replaced by another program, so PortBridge will not modify it. " +
                    "Remove it with the com0com setup tool (PortBridge then recreates it) or adopt it again on the Ports page.");

            // Our own pair: only the application-side switches PortBridge controls may need updating.
            var path = $@"\\.\CNCB{pair.Number}";
            if (!ParamsMatch(pair.UserParams, ParseParams(UserParams(comName, emulateBaud))))
            {
                await RunAsync(["change", $"CNCA{pair.Number}", UserParams(comName, emulateBaud)], ct);
                await WaitForDeviceAsync(path, ct);
            }
            return path;
        }

        if (record is not null)
        {
            // The recorded pair is gone or was renamed; whatever now has that number is not ours to touch.
            _options.Log?.Invoke($"The com0com pair PortBridge created for {comName} (CNCA{record.Number}) no longer exists; creating a new one.");
            store.Remove(record.Number);
        }

        var busy = await BusyNamesAsync(ct);
        if (busy.Contains(comName))
            throw new InvalidOperationException($"{comName} is already taken by another device. Pick a different port name.");

        var before = pairs.Select(p => p.Number).ToHashSet();
        ct.ThrowIfCancellationRequested(); // last point where cancelling leaves nothing behind

        // From here on the pair may exist, so finish the bookkeeping even if the caller cancels.
        var output = await InstallAsync(UserParams(comName, emulateBaud), BridgeParams);
        var after = await ListRawPairsAsync(CancellationToken.None);
        var created = after.Where(p => !before.Contains(p.Number) && p.UserPort.Equals(comName, StringComparison.OrdinalIgnoreCase)).ToList();
        // setupc echoes the created ports, e.g. "       CNCA3 PortName=COM12,..."
        var m = Regex.Match(output, @"CNCA(\d+)\s+PortName=" + Regex.Escape(comName), RegexOptions.IgnoreCase);
        if (created.Count != 1 || (m.Success && int.Parse(m.Groups[1].Value) != created[0].Number))
            throw new InvalidOperationException($"com0com could not create {comName} (no single new pair appeared): {output.Trim()}");

        var newPair = created[0];
        try
        {
            store.Upsert(new OwnedPair
            {
                Number = newPair.Number,
                ComName = comName,
                Origin = "created",
                Since = DateTimeOffset.Now,
                BridgeParams = new Dictionary<string, string>(newPair.BridgeParams, StringComparer.OrdinalIgnoreCase),
            });
        }
        catch (Exception ex)
        {
            // Without a durable record PortBridge could never prove ownership later: undo the creation.
            try { await RunAsync(["remove", newPair.Number.ToString()], CancellationToken.None); } catch { /* reported below */ }
            throw new InvalidOperationException($"Created {comName} but could not record ownership ({ex.Message}); the pair was removed again.", ex);
        }

        var device = $@"\\.\CNCB{newPair.Number}";
        await WaitForDeviceAsync(device, ct);
        return device;
    }

    /// <summary>
    /// Runs "setupc install" while watching Windows' driver-installation log. Installing the first pair also installs
    /// the driver; if Windows refuses it because com0com's publisher is not trusted (0xE0000242 — normally the "Would you
    /// like to install this device software?" prompt, which a service cannot show), setupc hangs until its timeout.
    /// Windows' verdict in the log lets PortBridge stop it at once and say why. Only the final "!!!" failure lines count:
    /// the same error code is also logged while an interactive prompt is still waiting for an answer.
    /// </summary>
    private async Task<string> InstallAsync(string userParams, string bridgeParams)
    {
        var log = _options.DriverInstallLog ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "setupapi.dev.log");
        var mark = LogLength(log);
        using var stop = new CancellationTokenSource();
        var install = RunAsync(["install", userParams, bridgeParams], stop.Token);
        var watch = WatchDriverLogAsync(log, mark, stop.Token);
        try
        {
            if (await Task.WhenAny(install, watch) == watch && await watch)
            {
                stop.Cancel();
                try { await install; } catch { /* stopped on purpose */ }
                throw DriverNotTrusted();
            }
            return await install;
        }
        catch (Exception ex) when (ex is not Com0ComDriverNotTrustedException && DriverRefused(log, mark))
        {
            throw DriverNotTrusted(); // setupc failed or timed out for this reason
        }
        finally
        {
            stop.Cancel();
            await watch;
        }
    }

    private Com0ComDriverNotTrustedException DriverNotTrusted() => new(
        "Windows refused to install the com0com driver because its publisher is not trusted on this PC (error 0xE0000242). " +
        "A Windows service cannot show Windows' \"Would you like to install this device software?\" question, so add the driver " +
        "while someone is at this PC: run PortBridge's setup again and keep the com0com task ticked, or run this command as " +
        $"administrator and choose Install: pnputil /add-driver \"{Path.Combine(Path.GetDirectoryName(SetupcPath) ?? "", "com0com.inf")}\". " +
        "PortBridge tries again every minute.");

    private async Task<bool> WatchDriverLogAsync(string log, long mark, CancellationToken ct)
    {
        var interval = _options.DriverLogPollInterval ?? TimeSpan.FromSeconds(1);
        try
        {
            while (true)
            {
                await Task.Delay(interval, ct);
                if (DriverRefused(log, mark)) return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static long LogLength(string log)
    {
        try { return new FileInfo(log).Length; }
        catch { return 0; }
    }

    /// <summary>Windows logged, after <paramref name="mark"/>, that it refused com0com's driver package as untrusted.</summary>
    private static bool DriverRefused(string log, long mark)
    {
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(fs.Length < mark ? 0 : mark, SeekOrigin.Begin); // shorter than before: the log was started anew
            var text = new StreamReader(fs, System.Text.Encoding.Latin1).ReadToEnd();
            return text.Contains("com0com", StringComparison.OrdinalIgnoreCase) &&
                   text.Split('\n').Any(l => l.StartsWith("!!!") && l.Contains("0xE0000242", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ParamsMatch(IReadOnlyDictionary<string, string> actual, Dictionary<string, string> desired)
    {
        foreach (var (k, v) in desired)
        {
            if (k.Equals("PortName", StringComparison.OrdinalIgnoreCase) && v == "-")
            {
                if (actual.TryGetValue(k, out var a) && a != "-") return false;
                continue;
            }
            var hasActual = actual.TryGetValue(k, out var av);
            // setupc only lists non-default values; "no" is the default for the boolean switches.
            if (!hasActual && v.Equals("no", StringComparison.OrdinalIgnoreCase)) continue;
            if (!hasActual || !av!.Equals(v, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Removes a pair PortBridge owns. Pairs of other programs are refused.</summary>
    public async Task RemovePairAsync(int number, CancellationToken ct = default)
    {
        var store = Store;
        if (OwnershipUntrusted is { } why)
            throw new Com0ComOwnershipException($"PortBridge's ownership records are not trusted here ({why}); not removing pair {number}.");
        var record = store.Find(number)
                     ?? throw new Com0ComOwnershipException($"Pair CNCA{number}/CNCB{number} was not created by PortBridge; remove it with the com0com setup tool.");
        var pair = (await ListRawPairsAsync(ct)).FirstOrDefault(p => p.Number == number);
        if (pair is null)
        {
            store.Remove(number);
            return;
        }
        if (IdentityProblem(pair, record) is { } problem)
            throw new Com0ComOwnershipException($"Pair CNCA{number}/CNCB{number} no longer matches what PortBridge recorded ({problem}); not removing it.");
        await RunAsync(["remove", number.ToString()], ct);
        store.Remove(number);
    }

    /// <summary>
    /// Explicit migration for pairs created by older PortBridge versions (no ownership record): records ownership of a
    /// pair that already has exactly PortBridge's layout. Changes no driver parameters, so there is nothing to roll
    /// back; <see cref="ReleasePairAsync"/> drops the record again. The caller names the COM port it expects, so a
    /// pair that changed meanwhile is not adopted by accident.
    /// </summary>
    public async Task<OwnedPair> AdoptPairAsync(int number, string expectedComName, CancellationToken ct = default)
    {
        expectedComName = expectedComName.Trim().ToUpperInvariant();
        if (OwnershipUntrusted is { } why)
            throw new Com0ComOwnershipException($"PortBridge's ownership records are not trusted here ({why}); adopt the pair from the Windows service or a secured data folder.");
        var pairs = await ListRawPairsAsync(ct);
        var pair = pairs.FirstOrDefault(p => p.Number == number)
                   ?? throw new InvalidOperationException($"There is no com0com pair {number}.");
        if (!pair.UserPort.Equals(expectedComName, StringComparison.OrdinalIgnoreCase))
            throw new Com0ComOwnershipException($"Pair {number} is {pair.UserPort}, not {expectedComName}; refresh and try again.");
        if (!ComName().IsMatch(pair.UserPort))
            throw new Com0ComOwnershipException($"Pair {number} is not exposed as a COM port name.");
        if (pairs.Count(p => p.UserPort.Equals(pair.UserPort, StringComparison.OrdinalIgnoreCase) ||
                             p.BridgePort.Equals(pair.UserPort, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new Com0ComOwnershipException($"{pair.UserPort} is used by more than one pair; not adopting.");
        if (!pair.HasPortBridgeLayout || !ParamsMatch(pair.BridgeParams, ParseParams(BridgeParams)))
            throw new Com0ComOwnershipException(
                $"Pair {number} ({pair.UserPort}) is not set up the way PortBridge creates pairs ({Describe(pair.UserParams)} / {Describe(pair.BridgeParams)}). " +
                "PortBridge only adopts pairs it can use without changing them.");
        var existing = Store.Find(number);
        if (existing is not null && IdentityProblem(pair, existing) is null) return existing;

        var rec = new OwnedPair
        {
            Number = number,
            ComName = pair.UserPort.ToUpperInvariant(),
            Origin = "adopted",
            Since = DateTimeOffset.Now,
            BridgeParams = new Dictionary<string, string>(pair.BridgeParams, StringComparer.OrdinalIgnoreCase),
            PriorUserParams = new Dictionary<string, string>(pair.UserParams, StringComparer.OrdinalIgnoreCase),
            PriorBridgeParams = new Dictionary<string, string>(pair.BridgeParams, StringComparer.OrdinalIgnoreCase),
        };
        Store.Upsert(rec);
        return rec;
    }

    /// <summary>Gives up ownership of an adopted pair (the pair itself is left exactly as it is).</summary>
    public void ReleasePair(int number)
    {
        var rec = Store.Find(number) ?? throw new InvalidOperationException($"PortBridge does not own pair {number}.");
        if (rec.Origin != "adopted")
            throw new InvalidOperationException($"Pair {number} was created by PortBridge; remove it instead.");
        Store.Remove(number);
    }

    public async Task<HashSet<string>> BusyNamesAsync(CancellationToken ct = default)
    {
        var output = await RunAsync(["busynames", "COM?*"], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Suggests the lowest COM name nothing is using (starting at COM10 to stay clear of onboard ports).</summary>
    public async Task<string> SuggestFreeNameAsync(IEnumerable<string> reserved, CancellationToken ct = default)
    {
        var busy = IsInstalled ? await BusyNamesAsync(ct) : [];
        foreach (var p in PortEnumerator.List()) busy.Add(p.Name);
        foreach (var r in reserved) busy.Add(r);
        for (var i = 10; i < 256; i++)
            if (!busy.Contains($"COM{i}")) return $"COM{i}";
        return "COM256";
    }

    private async Task<string> RunAsync(string[] args, CancellationToken ct)
    {
        var exe = SetupcPath ?? throw new InvalidOperationException("com0com is not installed.");
        if (_options.RejectExecutable?.Invoke(exe) is { } reason)
            throw new InvalidOperationException($"Refusing to run {exe}: {reason}");
        await _gate.WaitAsync(ct);
        try
        {
            return await _runner.RunAsync(exe, args, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task WaitForDeviceAsync(string path, CancellationToken ct) =>
        _options.WaitForDevice is { } wait ? wait(path, ct) : PollDeviceAsync(path, ct);

    /// <summary>Waits until the bridge-side device exists (Windows needs a moment after install/change).</summary>
    private static async Task PollDeviceAsync(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            using (var h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, 0, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (!h.IsInvalid) return;
                var err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (err == 5) return; // exists, just busy
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The virtual port device {path} did not appear.");
            await Task.Delay(250, ct);
        }
    }

    /// <summary>Runs setupc.exe directly when elevated, otherwise through a UAC prompt.</summary>
    private sealed class ProcessSetupcRunner : ISetupcRunner
    {
        public Task<string> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct) =>
            Environment.IsPrivilegedProcess ? RunDirectAsync(exe, args, ct) : RunElevatedAsync(exe, args, ct);

        // No "--wait": it waits for the application side (COMx) to appear, which PlugInMode deliberately hides
        // until PortBridge opens the bridge side, so it would always run into its timeout. We poll CNCBn instead.
        private static IReadOnlyList<string> CommonArgs(IReadOnlyList<string> args) =>
            args[0] is "list" or "busynames" ? args : ["--silent", .. args];

        private static async Task<string> RunDirectAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in CommonArgs(args)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start setupc.exe");
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { /* ignore */ }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException("com0com setup did not finish in time.");
            }
            var output = await stdout + await stderr;
            if (p.ExitCode != 0) throw new InvalidOperationException($"com0com setupc {string.Join(' ', args)} failed: {output.Trim()}");
            return output;
        }

        /// <summary>Not elevated (desktop app under UAC): run setupc through a UAC prompt, capturing output via --output.</summary>
        private static async Task<string> RunElevatedAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
        {
            var outFile = Path.Combine(Path.GetTempPath(), $"portbridge-setupc-{Guid.NewGuid():N}.txt");
            var all = new List<string> { "--output", outFile };
            all.AddRange(CommonArgs(args));
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(' ', all.Select(Quote)),
            };
            try
            {
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start setupc.exe");
                await p.WaitForExitAsync(ct);
                var output = File.Exists(outFile) ? await File.ReadAllTextAsync(outFile, ct) : "";
                if (p.ExitCode != 0) throw new InvalidOperationException($"com0com setupc failed: {output.Trim()}");
                return output;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException("Administrator approval is needed to create virtual COM ports.");
            }
            finally
            {
                try { File.Delete(outFile); } catch { /* ignore */ }
            }
        }

        private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
    }
}
