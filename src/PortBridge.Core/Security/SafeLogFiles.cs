using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PortBridge.Core.Security;

/// <summary>The log folder (or a file in it) was replaced by a link, so PortBridge did not write there.</summary>
public sealed class UnsafeLogFolderException(string message) : IOException(message);

/// <summary>
/// Writes log files into a folder that every user of the PC may change (by default C:\Users\Public\PortBridgeLogs)
/// without letting them redirect the writes. A SYSTEM service that followed a junction, symbolic link or hard link
/// planted there would create, append to or delete files anywhere on the PC. So the log folder (and its subfolder) is
/// created only inside an already-checked parent, opened without following reparse points, checked to be a real
/// directory at the expected place, and held open without delete sharing only while a file in it is being opened or
/// deleted, so it cannot be swapped in between; the file is opened without following a reparse point and refused if it
/// is one or has other hard links. An open file cannot be redirected any more, so nothing stays locked afterwards: the
/// folders can be renamed and the files read, edited and deleted while PortBridge writes to them.
/// The parent of the log folder (C:\Users\Public, the PortBridge data folder) must be one only administrators control.
/// </summary>
public static class SafeLogFiles
{
    /// <summary>Opens <paramref name="fileName"/> for appending, positioned at its end. Other programs may read, write,
    /// rename and delete it meanwhile; keep it open only briefly, since viewers that share reading only cannot open a
    /// file another program has open for writing.</summary>
    /// <param name="logRoot">The log folder; its parent must exist and be administrator-controlled.</param>
    /// <param name="subfolder">Optional folder inside <paramref name="logRoot"/> (e.g. "traffic").</param>
    public static FileStream OpenAppend(string logRoot, string? subfolder, string fileName)
    {
        var pins = PinFolders(logRoot, subfolder);
        try
        {
            var dir = subfolder is null ? logRoot : Path.Combine(logRoot, subfolder);
            var h = CreateFileW(Path.Combine(dir, fileName), GENERIC_WRITE | FILE_READ_ATTRIBUTES, FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero, OPEN_ALWAYS, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot open {fileName} in {dir}");
            try
            {
                var info = Info(h);
                if ((info.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) != 0 || info.NumberOfLinks != 1)
                    throw new UnsafeLogFolderException($"{Path.Combine(dir, fileName)} is a link to another file");
                var stream = new FileStream(h, FileAccess.Write);
                stream.Seek(0, SeekOrigin.End);
                return stream;
            }
            catch
            {
                h.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var p in pins) p.Dispose();
        }
    }

    /// <summary>Deletes regular files matching <paramref name="pattern"/> last written before <paramref name="cutoff"/>.
    /// Links are left alone; nothing is deleted when the folder itself has been replaced.</summary>
    public static void DeleteOlderThan(string logRoot, string pattern, DateTime cutoff)
    {
        if (!Directory.Exists(logRoot)) return;
        var pins = PinFolders(logRoot, null);
        try
        {
            foreach (var path in Directory.EnumerateFiles(logRoot, pattern))
            {
                using var h = CreateFileW(path, DELETE | FILE_READ_ATTRIBUTES, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
                    OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
                if (h.IsInvalid) continue;
                var info = Info(h);
                if ((info.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) != 0 || info.NumberOfLinks != 1) continue;
                var written = DateTime.FromFileTime(((long)info.WriteHigh << 32) | info.WriteLow);
                if (written >= cutoff) continue;
                var dispose = new FILE_DISPOSITION_INFO { DeleteFile = true };
                SetFileInformationByHandle(h, FileDispositionInfo, ref dispose, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO>());
            }
        }
        finally
        {
            foreach (var p in pins) p.Dispose();
        }
    }

    /// <summary>Creates (if needed) and pins the log folder and the optional subfolder, outermost first.</summary>
    private static List<SafeFileHandle> PinFolders(string logRoot, string? subfolder)
    {
        logRoot = Path.GetFullPath(logRoot);
        var parent = Path.GetDirectoryName(logRoot) ?? throw new ArgumentException("The log folder needs a parent folder.", nameof(logRoot));
        string expected;
        using (var p = CreateFileW(parent, FILE_READ_ATTRIBUTES, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
        {
            if (p.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot open {parent}");
            expected = FinalPath(p); // the parent is administrator-controlled, so wherever it really is counts
        }

        var pins = new List<SafeFileHandle>();
        try
        {
            var path = logRoot;
            expected = Path.Combine(expected, Path.GetFileName(logRoot));
            pins.Add(Pin(path, expected));
            if (subfolder is not null)
            {
                // Created only now that the folder above is pinned, so it lands inside the real log folder.
                path = Path.Combine(path, subfolder);
                pins.Add(Pin(path, Path.Combine(expected, subfolder)));
            }
            return pins;
        }
        catch
        {
            foreach (var p in pins) p.Dispose();
            throw;
        }
    }

    private static SafeFileHandle Pin(string path, string expectedFinal)
    {
        // Fails (already exists) rather than following a link someone created under this name.
        if (!CreateDirectoryW(path, IntPtr.Zero) && Marshal.GetLastPInvokeError() != ERROR_ALREADY_EXISTS)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot create {path}");
        // No delete sharing: while this handle is open nobody can rename, delete or replace the folder.
        var h = CreateFileW(path, FILE_READ_ATTRIBUTES, FileShare.ReadWrite, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot open {path}");
        try
        {
            var info = Info(h);
            if ((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 || (info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
                throw new UnsafeLogFolderException($"{path} is not a real folder (it is a junction, link or file)");
            if (!string.Equals(FinalPath(h), expectedFinal, StringComparison.OrdinalIgnoreCase))
                throw new UnsafeLogFolderException($"{path} leads somewhere else ({FinalPath(h)})");
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    private static string FinalPath(SafeFileHandle h)
    {
        var buf = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Capacity, 0);
        if (n == 0 || n >= buf.Capacity) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var s = buf.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    private static BY_HANDLE_FILE_INFORMATION Info(SafeFileHandle h) =>
        GetFileInformationByHandle(h, out var info) ? info : throw new Win32Exception(Marshal.GetLastPInvokeError());

    private const uint GENERIC_WRITE = 0x40000000, FILE_READ_ATTRIBUTES = 0x80, DELETE = 0x10000;
    private const uint OPEN_EXISTING = 3, OPEN_ALWAYS = 4;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const int ERROR_ALREADY_EXISTS = 183;
    private const int FileDispositionInfo = 4;

    // FILETIME is two DWORDs (4-byte aligned); declaring them as long would insert padding and shift every field.
    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO
    {
        [MarshalAs(UnmanagedType.U1)] public bool DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FILE_DISPOSITION_INFO info, uint size);}
