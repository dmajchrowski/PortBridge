using System.Security.AccessControl;
using System.Security.Principal;

namespace PortBridge.Core.Security;

/// <summary>Changes ownership/ACLs. Production: <see cref="WindowsAclWriter"/>; tests: fakes or run-owned fixtures.</summary>
public interface IAclWriter
{
    /// <summary>Creates a directory that only SYSTEM and Administrators can modify (owner Administrators).</summary>
    void CreateProtectedDirectory(string path);
    /// <summary>Owner Administrators; protected DACL: SYSTEM + Administrators full control (inherited by children).</summary>
    void ProtectRoot(string path);
    /// <summary>Owner Administrators; explicit entries removed so only the (protected) parent's entries apply.</summary>
    void ResetToInherited(string path, bool isDirectory);
}

public sealed class WindowsAclWriter : IAclWriter
{
    public static readonly WindowsAclWriter Instance = new();
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    private static DirectorySecurity ProtectedDacl()
    {
        var sec = new DirectorySecurity();
        sec.SetOwner(TrustedPrincipals.Administrators);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { TrustedPrincipals.System, TrustedPrincipals.Administrators })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    public void CreateProtectedDirectory(string path) => new DirectoryInfo(path).Create(ProtectedDacl());

    public void ProtectRoot(string path) => new DirectoryInfo(path).SetAccessControl(ProtectedDacl());

    public void ResetToInherited(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            var sec = new DirectorySecurity();
            sec.SetOwner(TrustedPrincipals.Administrators);
            sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            new DirectoryInfo(path).SetAccessControl(sec);
        }
        else
        {
            var sec = new FileSecurity();
            sec.SetOwner(TrustedPrincipals.Administrators);
            sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            new FileInfo(path).SetAccessControl(sec);
        }
    }
}

/// <summary>Result of taking over the data folder for the service. <see cref="WasInsecure"/>: the ACLs allowed
/// non-administrators to change it just now. <see cref="Untrusted"/>: its content is not vouched for by a trust record
/// (also true when the ACLs already look fine, e.g. after an interrupted earlier takeover).</summary>
public sealed record HardenResult(bool WasInsecure, IReadOnlyList<PathFinding> Findings, bool Created)
{
    public bool Untrusted { get; init; }
}

public sealed class DataDirSecurityException(string message, IReadOnlyList<PathFinding> findings) : Exception(message)
{
    public IReadOnlyList<PathFinding> Findings { get; } = findings;
}

/// <summary>
/// Makes the data folder trustworthy for the SYSTEM service, failing closed:
///  1. refuses reparse points / extra hard links anywhere in the tree or on its ancestors (nothing is changed);
///  2. protects the root (owner Administrators, only SYSTEM + Administrators), then resets every descendant top-down
///     to inherit that, re-checking each entry right before touching it;
///  3. re-writes the files whose content the service trusts as fresh files, so handles opened earlier by a
///     non-administrator no longer reach them;
///  4. verifies the whole tree again; any remaining problem throws.
/// <see cref="HardenResult.Untrusted"/> tells the caller that the content is not vouched for by the trust record
/// (<see cref="IDataDirTrustRecord"/>), so it must be migrated (<see cref="DataDirMigration"/>) before it is used.
/// Secure-looking ACLs alone never make content trusted: an earlier takeover may have protected them and then failed.
/// </summary>
public sealed class DataDirGuard(IPathSecurityReader reader, IAclWriter writer, Func<SecurityIdentifier, bool>? isAdministratorAccount = null,
    IDataDirTrustRecord? trust = null)
{
    private readonly IDataDirTrustRecord _trust = trust ?? NoTrustRecord.Instance;

    public static DataDirGuard Default => new(WindowsPathSecurityReader.Instance, WindowsAclWriter.Instance, LocalAdministrators.IsMember,
        RegistryDataDirTrustRecord.Instance);

    /// <summary>Records that the folder's content is now administrator-controlled. Call only after migration succeeded.</summary>
    public void MarkTrusted(string dataDir) => Wrap(() => _trust.MarkTrusted(dataDir), dataDir);

    /// <summary>Files whose content decides what the service does.</summary>
    public static readonly string[] TrustedFiles = ["config.json", "config.json.bak", "virtual-pairs.json"];

    /// <summary>Read-only: problems that make the folder's content untrustworthy for a privileged process (empty = fine).</summary>
    public List<PathFinding> Inspect(string dataDir) => new ProtectedPathPolicy(reader, isAdministratorAccount).CheckTree(dataDir);

    public HardenResult Harden(string dataDir)
    {
        var policy = new ProtectedPathPolicy(reader, isAdministratorAccount);
        if (ProtectedPathPolicy.SyntaxProblem(dataDir) is { } syntax)
            throw new DataDirSecurityException($"The data folder path is not acceptable: {syntax}.", [new PathFinding(dataDir, syntax)]);

        var ancestors = new List<PathFinding>();
        policy.CheckAncestors(dataDir, ancestors);
        if (ancestors.Count > 0)
            throw new DataDirSecurityException($"A parent folder of {dataDir} is not protected; refusing to use it.", ancestors);

        var root = reader.Read(dataDir);
        if (!root.Exists)
        {
            Wrap(() => writer.CreateProtectedDirectory(dataDir), dataDir);
            var created = policy.CheckTree(dataDir);
            if (created.Count > 0) throw new DataDirSecurityException($"Could not create a protected data folder at {dataDir}.", created);
            return new HardenResult(false, [], true);
        }
        if (!root.IsDirectory || root.IsReparsePoint)
            throw new DataDirSecurityException($"{dataDir} is not a plain folder; refusing to use it.", [new PathFinding(dataDir, "not a plain directory")]);

        // Scan first: structural problems are refused before anything is changed.
        var before = policy.CheckTree(dataDir);
        var structural = before.Where(IsStructural).ToList();
        if (structural.Count > 0)
            throw new DataDirSecurityException(
                $"The data folder contains links that could redirect the service's writes ({string.Join("; ", structural.Select(f => $"{f.Path}: {f.Problem}"))}). " +
                "An administrator must remove them before the service can use the folder.", structural);
        var wasInsecure = before.Count > 0;
        var untrusted = wasInsecure || !_trust.IsTrusted(dataDir);
        // Withdraw the trust record before anything changes, so an attempt that fails half-way can never leave secured
        // ACLs plus a stale "trusted" mark behind; the next attempt then migrates the content again.
        if (untrusted) Wrap(() => _trust.Clear(dataDir), dataDir);

        Wrap(() => writer.ProtectRoot(dataDir), dataDir);
        ResetChildren(dataDir);

        if (untrusted)
        {
            foreach (var name in TrustedFiles)
            {
                var path = Path.Combine(dataDir, name);
                if (File.Exists(path)) Wrap(() => Rewrite(path), path);
            }
        }

        var after = policy.CheckTree(dataDir);
        if (after.Count > 0)
            throw new DataDirSecurityException($"The data folder {dataDir} could not be secured.", after);
        return new HardenResult(wasInsecure, before, false) { Untrusted = untrusted };
    }

    private static bool IsStructural(PathFinding f) =>
        f.Problem.Contains("reparse point", StringComparison.Ordinal) || f.Problem.Contains("hard links", StringComparison.Ordinal);

    private void ResetChildren(string dir)
    {
        foreach (var child in reader.Children(dir).ToList())
        {
            // Re-check right before changing anything: a link created after the scan must not redirect the reset.
            var info = reader.Read(child);
            if (!info.Exists) continue;
            if (info.IsReparsePoint || (!info.IsDirectory && info.LinkCount > 1))
                throw new DataDirSecurityException($"{child} became a link while the folder was being secured.", [new PathFinding(child, "link appeared")]);
            Wrap(() => writer.ResetToInherited(child, info.IsDirectory), child);
            if (info.IsDirectory) ResetChildren(child);
        }
    }

    /// <summary>Replaces the file with a fresh copy. The old file object is renamed away first (allowed while others have
    /// it open with delete sharing), so handles opened earlier keep pointing at the orphan, never at the new file.
    /// A handle without delete sharing makes the rename fail, and the takeover fails closed.</summary>
    private static void Rewrite(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var fresh = path + ".secure-tmp";
        var old = path + ".untrusted-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(fresh, bytes); // new file object, inherits the protected ACL
        File.Move(path, old);
        File.Move(fresh, path);
        try { File.Delete(old); } catch { /* still open elsewhere: deleted when the last handle closes */ }
    }

    private static void Wrap(Action action, string path)
    {
        try { action(); }
        catch (Exception ex) when (ex is not DataDirSecurityException)
        {
            throw new DataDirSecurityException($"Could not secure {path}: {ex.Message}", [new PathFinding(path, ex.Message)]);
        }
    }
}
