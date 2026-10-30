using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using PortBridge.Core.Config;

namespace PortBridge.Core.Security;

/// <summary>
/// Durable record that a data folder completed a trusted migration (or was created protected) by an administrator or
/// SYSTEM. It must live where non-administrators can never write, so it cannot be planted together with the content it
/// vouches for. The folder's current ACLs prove nothing about who wrote its content earlier.
/// </summary>
public interface IDataDirTrustRecord
{
    bool IsTrusted(string dataDir);
    void MarkTrusted(string dataDir);
    void Clear(string dataDir);
}

/// <summary>Production record: <c>HKLM\SOFTWARE\PortBridge</c>, value <c>TrustedDataDir</c>. HKLM\SOFTWARE is writable only
/// by administrators and SYSTEM, i.e. by the same principals that run the installer and the service.</summary>
public sealed class RegistryDataDirTrustRecord : IDataDirTrustRecord
{
    public static readonly RegistryDataDirTrustRecord Instance = new();
    private const string Key = @"SOFTWARE\PortBridge";
    private const string Value = "TrustedDataDir";

    private static string Normalize(string dir) => Path.GetFullPath(dir).TrimEnd('\\');

    public bool IsTrusted(string dataDir)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(Key);
            return k?.GetValue(Value) is string s && string.Equals(Normalize(s), Normalize(dataDir), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // unknown = untrusted
        }
    }

    public void MarkTrusted(string dataDir)
    {
        using var k = Registry.LocalMachine.CreateSubKey(Key, writable: true);
        k.SetValue(Value, Normalize(dataDir), RegistryValueKind.String);
    }

    public void Clear(string dataDir)
    {
        using var k = Registry.LocalMachine.OpenSubKey(Key, writable: true);
        k?.DeleteValue(Value, throwOnMissingValue: false);
    }
}

/// <summary>Default when no record is supplied (tests, tools): nothing is ever vouched for, so content is always migrated.</summary>
public sealed class NoTrustRecord : IDataDirTrustRecord
{
    public static readonly NoTrustRecord Instance = new();
    public bool IsTrusted(string dataDir) => false;
    public void MarkTrusted(string dataDir) { }
    public void Clear(string dataDir) { }
}

/// <summary>What a privileged takeover did with the data folder's content.</summary>
public sealed record MigrationResult(bool Untrusted, bool Created, IReadOnlyList<string> Reset);

/// <summary>
/// Takes over the data folder for a privileged process. Content without a trust record is untrusted no matter what the
/// ACLs look like now (an earlier, interrupted takeover may already have protected them):
///  - the trust record is cleared before anything changes, and written again only after every step below succeeded,
///    so a failed or interrupted attempt is simply repeated in full;
///  - config.json: commands are set aside for review, and the dashboard password, remote access and "password in the
///    desktop window" are reset — an administrator signs in through the administrator-only local token and sets them again;
///  - virtual-pairs.json and config.json.bak are set aside unread: com0com pairs must be adopted again explicitly.
/// </summary>
public static class DataDirMigration
{
    public const string Reason =
        "The data folder was not provably administrator-controlled when PortBridge took it over for a privileged process, so settings " +
        "that grant access or run programs were reset or set aside.";

    public static MigrationResult Secure(DataDirGuard guard, string dataDir)
    {
        var hardened = guard.Harden(dataDir); // throws: fail closed
        var reset = new List<string>();
        if (hardened.Untrusted)
        {
            Step(Path.Combine(dataDir, "config.json"), p => reset.AddRange(new ConfigStore(p).SanitizeUntrusted(Reason)));
            Step(Path.Combine(dataDir, "config.json.bak"), p => { if (SetAside(p)) reset.Add("the configuration backup (set aside)"); });
            Step(Path.Combine(dataDir, "virtual-pairs.json"), p =>
            {
                if (SetAside(p)) reset.Add("com0com ownership records (set aside; adopt the pairs again on the Ports page)");
            });
        }
        guard.MarkTrusted(dataDir);
        return new MigrationResult(hardened.Untrusted, hardened.Created, reset);
    }

    /// <summary>Renames an untrusted file so nothing reads it; it stays in the (now protected) folder for review.</summary>
    internal static bool SetAside(string path)
    {
        if (!File.Exists(path)) return false;
        File.Move(path, $"{path}.untrusted-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        return true;
    }

    private static void Step(string path, Action<string> action)
    {
        try { action(path); }
        catch (Exception ex) when (ex is not DataDirSecurityException)
        {
            throw new DataDirSecurityException($"Could not migrate {path}: {ex.Message}", [new PathFinding(path, ex.Message)]);
        }
    }
}

/// <summary>Writes secrets (e.g. the local sign-in token) into a brand-new file object.</summary>
public static class SecretFile
{
    /// <summary>
    /// Replaces <paramref name="path"/> with a new file that only <paramref name="readers"/> can open. The existing file
    /// object is renamed away first, so a handle anyone opened earlier keeps pointing at the old object and never sees
    /// the new secret. If the old object cannot be moved (open without delete sharing), nothing is written and an
    /// exception is thrown: the secret is never written into a file object others may already hold open.
    /// </summary>
    public static void WriteNew(string path, string content, IEnumerable<SecurityIdentifier> readers)
    {
        if (File.Exists(path))
        {
            var old = $"{path}.old-{Guid.NewGuid().ToString("N")[..8]}";
            File.Move(path, old);
            try { File.Delete(old); } catch { /* still open elsewhere: removed when the last handle closes */ }
        }
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in readers.Distinct())
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        // CreateNew: the ACL applies from the moment the object exists, and a file that reappeared meanwhile is not reused.
        using var fs = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.WriteData, FileShare.None, 4096, FileOptions.None, sec);
        fs.Write(Encoding.ASCII.GetBytes(content));
    }
}
