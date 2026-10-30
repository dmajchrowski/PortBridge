using System.Text.Json;
using PortBridge.Core.Config;

namespace PortBridge.Core.Serial;

/// <summary>One com0com pair PortBridge is allowed to manage, with what it looked like when ownership began.</summary>
public sealed record OwnedPair
{
    public int Number { get; init; }
    public string ComName { get; init; } = "";
    /// <summary>"created" (PortBridge installed it) or "adopted" (an administrator explicitly handed it over).</summary>
    public string Origin { get; init; } = "created";
    public DateTimeOffset Since { get; init; }
    /// <summary>The bridge-side (CNCB) parameters PortBridge applied; drift means someone else changed the pair.</summary>
    public Dictionary<string, string> BridgeParams { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Adopted pairs only: parameters before adoption, restored by <see cref="Com0ComManager.ReleasePairAsync"/>.</summary>
    public Dictionary<string, string>? PriorUserParams { get; init; }
    public Dictionary<string, string>? PriorBridgeParams { get; init; }
}

/// <summary>
/// Durable list of the com0com pairs PortBridge owns (in the protected data folder). Parameter appearance never
/// proves ownership: a pair that is not in this list is somebody else's and is left alone.
/// </summary>
public sealed class Com0ComOwnershipStore(string? path = null)
{
    private readonly Lock _gate = new();

    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.DataDir, "virtual-pairs.json");

    /// <summary>Records that may authorise changes. Malformed records (bad COM name, unknown origin, no recorded
    /// PortBridge bridge side) and ambiguous ones (a number or name recorded twice) authorise nothing.</summary>
    public List<OwnedPair> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return [];
            var all = (JsonSerializer.Deserialize<List<OwnedPair?>>(File.ReadAllText(Path), Json.Options) ?? []).OfType<OwnedPair>().ToList();
            return all.Where(p => IsValid(p) &&
                                  all.Count(q => q.Number == p.Number) == 1 &&
                                  all.Count(q => string.Equals(q.ComName, p.ComName, StringComparison.OrdinalIgnoreCase)) == 1)
                .ToList();
        }
    }

    private static bool IsValid(OwnedPair p) =>
        p.Number >= 0 &&
        Com0ComManager.IsComName(p.ComName) &&
        p.Origin is "created" or "adopted" &&
        Com0ComManager.HasBridgeLayout(p.BridgeParams);

    public OwnedPair? Find(string comName) =>
        Load().FirstOrDefault(p => p.ComName.Equals(comName, StringComparison.OrdinalIgnoreCase));

    public OwnedPair? Find(int number) => Load().FirstOrDefault(p => p.Number == number);

    public void Upsert(OwnedPair pair) => Mutate(list =>
    {
        list.RemoveAll(p => p.Number == pair.Number || p.ComName.Equals(pair.ComName, StringComparison.OrdinalIgnoreCase));
        list.Add(pair);
    });

    public void Remove(int number) => Mutate(list => list.RemoveAll(p => p.Number == number));

    private void Mutate(Action<List<OwnedPair>> change)
    {
        lock (_gate)
        {
            var list = File.Exists(Path) ? JsonSerializer.Deserialize<List<OwnedPair>>(File.ReadAllText(Path), Json.Options) ?? [] : [];
            change(list);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list.OrderBy(p => p.Number), Json.Indented));
            File.Move(tmp, Path, overwrite: true);
        }
    }
}

/// <summary>An existing pair PortBridge does not own (or can no longer prove it owns) blocks the requested port.</summary>
public sealed class Com0ComOwnershipException(string message) : InvalidOperationException(message);
