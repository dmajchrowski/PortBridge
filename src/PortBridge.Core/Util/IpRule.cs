using System.Net;
using System.Net.Sockets;

namespace PortBridge.Core.Util;

/// <summary>A single IP address or CIDR range used for server allow-lists.</summary>
public readonly record struct IpRule(IPAddress Network, int PrefixLength)
{
    public static bool TryParse(string? text, out IpRule rule)
    {
        rule = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var slash = text.IndexOf('/');
        var addrText = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addrText, out var addr)) return false;
        var max = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var prefix = max;
        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > max)) return false;
        rule = new IpRule(addr, prefix);
        return true;
    }

    public bool Matches(IPAddress candidate)
    {
        var net = Network;
        if (candidate.IsIPv4MappedToIPv6) candidate = candidate.MapToIPv4();
        if (net.IsIPv4MappedToIPv6) net = net.MapToIPv4();
        if (candidate.AddressFamily != net.AddressFamily) return false;
        var a = net.GetAddressBytes();
        var b = candidate.GetAddressBytes();
        var bits = PrefixLength;
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? (byte)0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }

    public static bool IsAllowed(IPAddress remote, IReadOnlyList<IpRule> rules) =>
        rules.Count == 0 || rules.Any(r => r.Matches(remote));
}
