using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PortBridge.Core.Security;

/// <summary>One access-control entry as it applies to a file or directory.</summary>
public sealed record AceInfo(SecurityIdentifier Sid, FileSystemRights Rights, AccessControlType Type, bool InheritOnly);

/// <summary>What the security checks need to know about one path.</summary>
public sealed record PathSecurityInfo(
    string Path,
    bool Exists,
    bool IsDirectory,
    bool IsReparsePoint,
    SecurityIdentifier? Owner,
    IReadOnlyList<AceInfo> Aces,
    int LinkCount = 1);

/// <summary>Reads ownership/ACLs/attributes. Production: <see cref="WindowsPathSecurityReader"/>; tests: fakes.</summary>
public interface IPathSecurityReader
{
    PathSecurityInfo Read(string path);
    IEnumerable<string> Children(string directory);
}

public sealed class WindowsPathSecurityReader : IPathSecurityReader
{
    public static readonly WindowsPathSecurityReader Instance = new();

    public PathSecurityInfo Read(string path)
    {
        FileSystemInfo fsi = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!fsi.Exists) return new PathSecurityInfo(path, false, false, false, null, []);
        var isDir = fsi is DirectoryInfo;
        var reparse = fsi.Attributes.HasFlag(FileAttributes.ReparsePoint);
        FileSystemSecurity sec = isDir ? ((DirectoryInfo)fsi).GetAccessControl() : ((FileInfo)fsi).GetAccessControl();
        var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var aces = new List<AceInfo>();
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            aces.Add(new AceInfo((SecurityIdentifier)r.IdentityReference, r.FileSystemRights, r.AccessControlType,
                r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));
        return new PathSecurityInfo(path, true, isDir, reparse, owner, aces, isDir || reparse ? 1 : LinkCount(path));
    }

    public IEnumerable<string> Children(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        });

    private static int LinkCount(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(h, out var info) ? (int)info.NumberOfLinks : int.MaxValue;
    }

    // FILETIME is two DWORDs (4-byte aligned); declaring them as long would insert padding and shift every field.
    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);
}

/// <summary>
/// Who may control files that a SYSTEM service loads or trusts: LocalSystem, BUILTIN\Administrators and
/// TrustedInstaller. Anyone else holding write-class rights (or ownership) could make the service run their code.
/// </summary>
public static class TrustedPrincipals
{
    public static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    public static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    public static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    public static bool IsTrusted(SecurityIdentifier sid) => sid == System || sid == Administrators || sid == TrustedInstaller;
}

/// <summary>Direct members of the local Administrators group (read once). Nested/domain groups are not expanded, so
/// they count as untrusted — the safe direction. Lookup failure means "no members".</summary>
public static class LocalAdministrators
{
    private static readonly Lazy<HashSet<SecurityIdentifier>> Members = new(Load);

    public static bool IsMember(SecurityIdentifier sid) => Members.Value.Contains(sid);

    private static HashSet<SecurityIdentifier> Load()
    {
        var set = new HashSet<SecurityIdentifier>();
        try
        {
            var name = TrustedPrincipals.Administrators.Translate(typeof(NTAccount)).Value;
            name = name[(name.IndexOf('\\') + 1)..];
            if (NetLocalGroupGetMembers(null, name, 0, out var buf, -1, out var read, out _, IntPtr.Zero) != 0) return set;
            try
            {
                var size = Marshal.SizeOf<IntPtr>();
                for (var i = 0; i < read; i++)
                {
                    var psid = Marshal.ReadIntPtr(buf, i * size);
                    if (psid != IntPtr.Zero) set.Add(new SecurityIdentifier(psid));
                }
            }
            finally
            {
                NetApiBufferFree(buf);
            }
        }
        catch
        {
            // Unknown membership: trust nobody extra.
        }
        return set;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer, int prefMaxLen,
        out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

/// <summary>Write-class rights, after mapping generic bits, for the three roles a path can play.</summary>
internal static class Rights
{
    private const int GenericAll = 0x10000000, GenericWrite = 0x40000000;

    public const FileSystemRights ModifyContent = FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes;
    public const FileSystemRights ControlObject = FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>The file itself (exe, dll, config): any change to content, attributes, ACL or the object.</summary>
    public const FileSystemRights FileDanger = ModifyContent | ControlObject;
    /// <summary>The directory a program loads from: adding files (DLL planting), deleting/replacing children, ACL.</summary>
    public const FileSystemRights LoadDirDanger = FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories |
        FileSystemRights.DeleteSubdirectoriesAndFiles | ControlObject;
    /// <summary>Higher ancestors: anything that allows renaming/replacing the subtree or changing the ACL.</summary>
    public const FileSystemRights AncestorDanger = FileSystemRights.DeleteSubdirectoriesAndFiles | ControlObject;

    public static FileSystemRights Map(FileSystemRights r)
    {
        var v = (int)r;
        if ((v & GenericAll) != 0) r |= FileSystemRights.FullControl;
        if ((v & GenericWrite) != 0) r |= ModifyContent;
        return r;
    }
}

/// <summary>A problem found while checking a path; <see cref="Fatal"/> problems block privileged use.</summary>
public sealed record PathFinding(string Path, string Problem);

/// <summary>
/// Decides whether a path can be trusted by a privileged (SYSTEM) process: rejects ambiguous syntax (relative, UNC,
/// device, alternate streams, short names, trailing dots/spaces, quotes), reparse points anywhere on the chain, and
/// write-class rights or ownership held by anyone other than SYSTEM, Administrators or TrustedInstaller.
/// A path string (e.g. "starts with C:\Program Files") proves nothing; only the ACLs read here count.
/// </summary>
public sealed class ProtectedPathPolicy(IPathSecurityReader reader, Func<SecurityIdentifier, bool>? isAdministratorAccount = null)
{
    private static readonly char[] Forbidden = ['"', '*', '?', '<', '>', '|'];

    /// <summary>Uses the real ACLs and the machine's local Administrators group (accounts that are direct members are
    /// administrators; files an elevated administrator creates are often owned by that account).</summary>
    public static ProtectedPathPolicy Default => new(WindowsPathSecurityReader.Instance, LocalAdministrators.IsMember);

    /// <summary>Null when the string is a plain, absolute, local path in canonical form; otherwise why not.</summary>
    public static string? SyntaxProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "the path is empty";
        if (path.IndexOfAny(Forbidden) >= 0 || path.Any(char.IsControl)) return "the path contains quotes, wildcards or control characters";
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return "network (UNC) and device paths are not allowed";
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            return "the path must be absolute (e.g. C:\\Program Files\\PortBridge\\PortBridge.exe)";
        if (path.IndexOf(':', 2) >= 0) return "alternate data streams are not allowed";
        if (path.Contains('/')) return "use backslashes only";
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) { return $"the path is invalid ({ex.Message})"; }
        if (!string.Equals(full.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "the path is not in canonical form (relative segments, trailing dots or spaces)";
        foreach (var seg in path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return $"\"{seg}\" ends with a dot or space";
            var tilde = seg.IndexOf('~');
            if (tilde >= 0 && tilde + 1 < seg.Length && char.IsAsciiDigit(seg[tilde + 1])) return $"\"{seg}\" looks like an 8.3 short name";
        }
        return null;
    }

    /// <summary>Checks an executable a SYSTEM service would load, the files it loads next to it, its folder and every
    /// ancestor. Empty result = safe to register.</summary>
    public List<PathFinding> CheckExecutable(string exePath, IEnumerable<string>? companions = null)
    {
        var findings = new List<PathFinding>();
        if (SyntaxProblem(exePath) is { } syntax)
        {
            findings.Add(new PathFinding(exePath, syntax));
            return findings;
        }
        var exe = Read(exePath, findings);
        if (exe is null) return findings;
        if (!exe.Exists || exe.IsDirectory) { findings.Add(new PathFinding(exePath, "the file does not exist")); return findings; }
        CheckObject(exe, Rights.FileDanger, findings);

        // Files the runtime loads from the application folder (DLLs, deps/runtimeconfig) and probe sub-folders.
        foreach (var item in companions ?? [])
        {
            if (string.Equals(item, exePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (Read(item, findings) is { Exists: true } info)
                CheckObject(info, info.IsDirectory ? Rights.LoadDirDanger : Rights.FileDanger, findings);
        }

        var dir = System.IO.Path.GetDirectoryName(exePath);
        var first = true;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Read(dir, findings) is { } d)
                CheckObject(d, first ? Rights.LoadDirDanger : Rights.AncestorDanger, findings);
            first = false;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return findings;
    }

    /// <summary>Checks a directory tree whose content a privileged process trusts (configuration). Reparse points and
    /// multiply-linked files anywhere inside are findings too.</summary>
    public List<PathFinding> CheckTree(string root)
    {
        var findings = new List<PathFinding>();
        if (SyntaxProblem(root) is { } syntax)
        {
            findings.Add(new PathFinding(root, syntax));
            return findings;
        }
        CheckAncestors(root, findings);
        var info = Read(root, findings);
        if (info is null || !info.Exists) return findings;
        Walk(info, findings);
        return findings;
    }

    /// <summary>Ancestors of <paramref name="path"/> must not be reparse points or replaceable by untrusted users.</summary>
    public void CheckAncestors(string path, List<PathFinding> findings)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            if (Read(dir, findings) is { } d) CheckObject(d, Rights.AncestorDanger, findings);
            dir = System.IO.Path.GetDirectoryName(dir);
        }
    }

    private void Walk(PathSecurityInfo node, List<PathFinding> findings)
    {
        CheckObject(node, node.IsDirectory ? Rights.LoadDirDanger | Rights.ModifyContent : Rights.FileDanger, findings);
        if (!node.IsDirectory && node.LinkCount > 1) findings.Add(new PathFinding(node.Path, "the file has additional hard links"));
        if (!node.IsDirectory || node.IsReparsePoint) return;
        IEnumerable<string> children;
        try { children = reader.Children(node.Path).ToList(); }
        catch (Exception ex) { findings.Add(new PathFinding(node.Path, $"cannot list contents: {ex.Message}")); return; }
        foreach (var child in children)
            if (Read(child, findings) is { Exists: true } c) Walk(c, findings);
    }

    private PathSecurityInfo? Read(string path, List<PathFinding> findings)
    {
        try { return reader.Read(path); }
        catch (Exception ex)
        {
            findings.Add(new PathFinding(path, $"cannot read its security settings: {ex.Message}"));
            return null;
        }
    }

    private void CheckObject(PathSecurityInfo info, FileSystemRights danger, List<PathFinding> findings)
    {
        if (!info.Exists) return;
        if (info.IsReparsePoint) findings.Add(new PathFinding(info.Path, "it is a junction/symbolic link (reparse point)"));
        var ownerTrusted = info.Owner is { } o && IsTrusted(o);
        if (!ownerTrusted) findings.Add(new PathFinding(info.Path, $"it is owned by {Name(info.Owner)}, not by Administrators/SYSTEM"));
        foreach (var ace in info.Aces)
        {
            if (ace.Type != AccessControlType.Allow || ace.InheritOnly) continue;
            if (IsTrusted(ace.Sid) || (ace.Sid == TrustedPrincipals.OwnerRights && ownerTrusted)) continue;
            var granted = Rights.Map(ace.Rights) & danger;
            if (granted != 0) findings.Add(new PathFinding(info.Path, $"{Name(ace.Sid)} has {granted} rights"));
        }
    }

    private bool IsTrusted(SecurityIdentifier sid) => TrustedPrincipals.IsTrusted(sid) || (isAdministratorAccount?.Invoke(sid) ?? false);

    public static string Name(SecurityIdentifier? sid)
    {
        if (sid is null) return "an unknown owner";
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch { return sid.Value; }
    }
}
