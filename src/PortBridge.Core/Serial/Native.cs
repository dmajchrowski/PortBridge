using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PortBridge.Core.Serial;

internal static partial class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint FILE_TYPE_CHAR = 0x0002;

    public const uint PURGE_TXABORT = 0x0001, PURGE_RXABORT = 0x0002, PURGE_TXCLEAR = 0x0004, PURGE_RXCLEAR = 0x0008;

    public const uint SETRTS = 3, CLRRTS = 4, SETDTR = 5, CLRDTR = 6, SETBREAK = 8, CLRBREAK = 9;

    public const uint MS_CTS_ON = 0x0010, MS_DSR_ON = 0x0020, MS_RING_ON = 0x0040, MS_RLSD_ON = 0x0080;

    public const uint CE_RXOVER = 0x0001, CE_OVERRUN = 0x0002, CE_RXPARITY = 0x0004, CE_FRAME = 0x0008, CE_BREAK = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct DCB
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    // DCB bit-field positions
    public const int fBinary = 0, fParity = 1, fOutxCtsFlow = 2, fOutxDsrFlow = 3, fDtrControl = 4,
        fDsrSensitivity = 6, fTXContinueOnXoff = 7, fOutX = 8, fInX = 9, fErrorChar = 10, fNull = 11,
        fRtsControl = 12, fAbortOnError = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct COMMTIMEOUTS
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct COMSTAT
    {
        public uint Flags;
        public uint cbInQue;
        public uint cbOutQue;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetFileType(SafeFileHandle handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCommState(SafeFileHandle handle, ref DCB dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCommState(SafeFileHandle handle, ref DCB dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCommTimeouts(SafeFileHandle handle, ref COMMTIMEOUTS timeouts);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupComm(SafeFileHandle handle, uint inQueue, uint outQueue);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PurgeComm(SafeFileHandle handle, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EscapeCommFunction(SafeFileHandle handle, uint func);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCommModemStatus(SafeFileHandle handle, out uint status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClearCommError(SafeFileHandle handle, out uint errors, out COMSTAT stat);

    // ---- SetupAPI (port enumeration) ----
    public static readonly Guid GUID_DEVCLASS_PORTS = new("4d36e978-e325-11ce-bfc1-08002be10318");
    public const uint DIGCF_PRESENT = 0x2;
    public const uint SPDRP_DEVICEDESC = 0x0, SPDRP_HARDWAREID = 0x1, SPDRP_MFG = 0xB, SPDRP_FRIENDLYNAME = 0xC;
    public const uint DICS_FLAG_GLOBAL = 0x1, DIREG_DEV = 0x1, KEY_READ = 0x20019;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    public static partial IntPtr SetupDiGetClassDevs(in Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property,
        out uint regType, [Out] byte[]? buffer, uint size, out uint required);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    public static partial IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA data, uint scope, uint profile, uint keyType, uint access);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
