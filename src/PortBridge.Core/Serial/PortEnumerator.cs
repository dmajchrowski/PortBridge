using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static PortBridge.Core.Serial.Native;

namespace PortBridge.Core.Serial;

public sealed record SystemPort(string Name, string Description, string? Manufacturer, string? HardwareId, bool IsCom0Com);

public static partial class PortEnumerator
{
    [GeneratedRegex(@"^COM(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComNumber();

    public static int SortKey(string name)
    {
        var m = ComNumber().Match(name);
        return m.Success ? int.Parse(m.Groups[1].Value) : int.MaxValue;
    }

    /// <summary>All COM ports Windows currently knows about, with friendly names where available.</summary>
    public static List<SystemPort> List()
    {
        var result = new Dictionary<string, SystemPort>(StringComparer.OrdinalIgnoreCase);
        try { EnumerateSetupApi(result); } catch { /* fall back to the registry list */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is not null)
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is not string name || result.ContainsKey(name)) continue;
                    var isC0c = valueName.Contains("com0com", StringComparison.OrdinalIgnoreCase);
                    result[name] = new SystemPort(name, isC0c ? "com0com virtual port" : "Serial port", null, valueName, isC0c);
                }
            }
        }
        catch { /* ignore */ }

        return result.Values
            .Where(p => p.Name.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => SortKey(p.Name))
            .ToList();
    }

    private static void EnumerateSetupApi(Dictionary<string, SystemPort> result)
    {
        var guid = GUID_DEVCLASS_PORTS;
        var set = SetupDiGetClassDevs(in guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var portName = ReadPortName(set, ref data);
                if (string.IsNullOrEmpty(portName)) continue;
                var friendly = GetProperty(set, ref data, SPDRP_FRIENDLYNAME) ?? GetProperty(set, ref data, SPDRP_DEVICEDESC) ?? "Serial port";
                var mfg = GetProperty(set, ref data, SPDRP_MFG);
                var hwid = GetProperty(set, ref data, SPDRP_HARDWAREID);
                // Friendly names usually end in " (COMx)"; the port name is shown separately in the UI.
                var desc = friendly.Replace($" ({portName})", "", StringComparison.OrdinalIgnoreCase);
                var isC0c = (hwid ?? "").StartsWith("com0com", StringComparison.OrdinalIgnoreCase);
                result[portName] = new SystemPort(portName, desc, mfg, hwid, isC0c);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? ReadPortName(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var hkey = SetupDiOpenDevRegKey(set, ref data, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (hkey == IntPtr.Zero || hkey == new IntPtr(-1)) return null;
        using var safe = new SafeRegistryHandle(hkey, ownsHandle: true);
        using var key = RegistryKey.FromHandle(safe);
        return key.GetValue("PortName") as string;
    }

    private static string? GetProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint prop)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, prop, out _, null, 0, out var needed);
        if (needed == 0) return null;
        var buf = new byte[needed];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, prop, out _, buf, needed, out _)) return null;
        // REG_SZ or REG_MULTI_SZ: take the first string.
        var s = Encoding.Unicode.GetString(buf);
        var nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }
}
