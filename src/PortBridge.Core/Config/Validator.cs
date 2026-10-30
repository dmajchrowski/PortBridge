using System.Text.RegularExpressions;
using PortBridge.Core.Util;

namespace PortBridge.Core.Config;

public static partial class Validator
{
    private static readonly int[] DataBitOptions = [5, 6, 7, 8];

    [GeneratedRegex(@"^COM[1-9][0-9]{0,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex ComName();

    /// <summary>Returns human-readable problems; empty when the bridge can be saved.</summary>
    public static List<string> Validate(BridgeConfig bridge, IEnumerable<BridgeConfig> others)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(bridge.Name)) errors.Add("Give the bridge a name.");
        if (bridge.Primary is null) { errors.Add("The bridge needs a source endpoint."); return errors; }
        if (bridge.Targets.Count == 0) errors.Add("Add at least one target endpoint.");
        if (bridge.Targets.Count > 32) errors.Add("A bridge supports at most 32 targets.");

        var all = bridge.AllEndpoints().ToList();
        for (var i = 0; i < all.Count; i++)
        {
            var label = i == 0 ? "Source" : $"Target {i}";
            ValidateEndpoint(all[i], label, errors);
        }

        // Resources that can only be used once on this machine.
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in others.Where(o => o.Id != bridge.Id))
            foreach (var key in ExclusiveResources(other))
                used.TryAdd(key, other.Name);

        var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ExclusiveResources(bridge))
        {
            if (!mine.Add(key)) errors.Add($"{Pretty(key)} is used twice in this bridge.");
            else if (used.TryGetValue(key, out var owner)) errors.Add($"{Pretty(key)} is already used by bridge \"{owner}\".");
        }
        return errors;
    }

    private static string Pretty(string key) => key.Replace("serial:", "").Replace("tcp:", "TCP port ").Replace("udp:", "UDP port ");

    public static IEnumerable<string> ExclusiveResources(BridgeConfig b)
    {
        foreach (var e in b.AllEndpoints())
        {
            switch (e.Kind)
            {
                case EndpointKind.Serial:
                case EndpointKind.VirtualSerial:
                    yield return "serial:" + e.PortName.ToUpperInvariant();
                    break;
                case EndpointKind.TcpServer:
                    yield return "tcp:" + e.Port;
                    break;
                case EndpointKind.Udp:
                    yield return "udp:" + (e.LocalPort > 0 ? e.LocalPort : e.Port);
                    break;
            }
        }
    }

    private static void ValidateEndpoint(EndpointConfig e, string label, List<string> errors)
    {
        void Err(string msg) => errors.Add($"{label}: {msg}");

        if (e.IsSerial)
        {
            if (!ComName().IsMatch(e.PortName ?? "")) Err("port name must look like COM1 … COM999.");
            if (e.Kind == EndpointKind.Serial && (e.BaudRate < 50 || e.BaudRate > 12_000_000)) Err("baud rate is out of range.");
            if (!DataBitOptions.Contains(e.DataBits)) Err("data bits must be 5, 6, 7 or 8.");
        }

        switch (e.Kind)
        {
            case EndpointKind.TcpServer:
                if (e.Port is < 1 or > 65535) Err("listen port must be 1–65535.");
                if (!string.IsNullOrWhiteSpace(e.Host) && !System.Net.IPAddress.TryParse(e.Host, out _))
                    Err("bind address must be an IP address (or empty for all interfaces).");
                foreach (var rule in e.AllowedClients)
                    if (!IpRule.TryParse(rule, out _)) Err($"\"{rule}\" is not an IP address or CIDR range.");
                if (e.MaxClients < 0) Err("max clients cannot be negative.");
                if (e.Tls.Enabled && !string.IsNullOrWhiteSpace(e.Tls.CertificatePath) && !File.Exists(e.Tls.CertificatePath))
                    Err("TLS certificate file was not found.");
                break;
            case EndpointKind.TcpClient:
                if (string.IsNullOrWhiteSpace(e.Host)) Err("enter the remote host or IP address.");
                if (e.Port is < 1 or > 65535) Err("remote port must be 1–65535.");
                if (e.Tls.Enabled && e.Tls.Validation == TlsValidation.Pinned && string.IsNullOrWhiteSpace(e.Tls.PinnedThumbprint))
                    Err("enter the certificate thumbprint to pin.");
                break;
            case EndpointKind.Udp:
                if (e.Port is < 1 or > 65535) Err("port must be 1–65535.");
                if (e.LocalPort is < 0 or > 65535) Err("local port must be 0–65535.");
                break;
        }

        if (e.PacketMode != PacketMode.Immediate && e.PacketTimeoutMs is < 1 or > 60_000) Err("packet timeout must be 1–60000 ms.");
        if (e.MaxPacketBytes is < 0 or > 1_048_576) Err("max packet size must be 0–1048576 bytes.");
        if (e.PacketMode == PacketMode.Delimiter && DataFormat.Unescape(e.Delimiter).Length == 0) Err("enter a delimiter.");
        if (e.HoldWhileDisconnected && e.HoldBufferKb is < 1 or > 1_048_576) Err("hold buffer must be 1 KB – 1 GB.");
        if (e.ReconnectDelayMs < 100) Err("reconnect delay must be at least 100 ms.");
        if (e.PingWatchdog && (e.PingIntervalSeconds < 1 || e.PingFailures < 1)) Err("ping watchdog settings are invalid.");
    }
}
