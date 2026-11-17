using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IpWidget;

internal sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    string Kind,
    bool IsUp,
    string Speed,
    string Mac,
    string Ip);

internal static class NetworkInfo
{
    /// <summary>Adapters worth offering in the menu: real or virtual NICs with a MAC address.</summary>
    public static List<NetworkInterface> Candidates()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Where(n => n.GetPhysicalAddress().GetAddressBytes().Length == 6)
                .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<NetworkInterface>();
        }
    }

    /// <summary>The adapter Windows uses for the default route (what "Automatic" shows).</summary>
    public static NetworkInterface? Primary(List<NetworkInterface> candidates)
    {
        try
        {
            // 1.1.1.1 in network byte order; nothing is sent, this only asks the routing table.
            uint dest = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
            if (NativeMethods.GetBestInterface(dest, out uint index) == 0)
            {
                var match = candidates.FirstOrDefault(n => IPv4Index(n) == index);
                if (match != null) return match;
            }
        }
        catch { /* fall through to the heuristic */ }

        return candidates
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .OrderByDescending(HasGateway)
            .ThenBy(n => KindOf(n) switch { "LAN" => 0, "WLAN" => 1, _ => 2 })
            .FirstOrDefault()
            ?? candidates.FirstOrDefault();
    }

    public static AdapterInfo Describe(NetworkInterface n)
    {
        bool up = n.OperationalStatus == OperationalStatus.Up;
        return new AdapterInfo(
            n.Id,
            n.Name,
            n.Description,
            KindOf(n),
            up,
            up ? FormatSpeed(SafeSpeed(n)) : "Disconnected",
            FormatMac(n.GetPhysicalAddress()),
            up ? (IPv4Of(n) ?? "No IPv4 address") : "-");
    }

    public static string KindOf(NetworkInterface n) => n.NetworkInterfaceType switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or
        NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or
        NetworkInterfaceType.GigabitEthernet => "LAN",
        NetworkInterfaceType.Wireless80211 => "WLAN",
        var other => other.ToString(),
    };

    private static string? IPv4Of(NetworkInterface n)
    {
        try
        {
            var addrs = n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address)
                .ToList();
            // Prefer a real address over a 169.254.x.x self-assigned one.
            var best = addrs.FirstOrDefault(a => !a.ToString().StartsWith("169.254.")) ?? addrs.FirstOrDefault();
            return best?.ToString();
        }
        catch { return null; }
    }

    private static long IPv4Index(NetworkInterface n)
    {
        try { return n.GetIPProperties().GetIPv4Properties()?.Index ?? -1; }
        catch { return -1; }
    }

    private static bool HasGateway(NetworkInterface n)
    {
        try
        {
            return n.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
        }
        catch { return false; }
    }

    private static long SafeSpeed(NetworkInterface n)
    {
        try { return n.Speed; } catch { return -1; }
    }

    private static string FormatSpeed(long bps)
    {
        if (bps <= 0) return "Unknown";
        if (bps >= 1_000_000_000) return $"{bps / 1e9:0.#} Gbps";
        if (bps >= 1_000_000) return $"{bps / 1e6:0.#} Mbps";
        return $"{bps / 1e3:0.#} Kbps";
    }

    private static string FormatMac(PhysicalAddress mac)
    {
        var b = mac.GetAddressBytes();
        return b.Length == 0 ? "-" : string.Join("-", b.Select(x => x.ToString("X2")));
    }
}
