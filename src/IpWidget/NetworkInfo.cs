using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IpWidget;

/// <summary>One widget row. Ip is null when the adapter has no IPv4 address; Speed is null when it's down.</summary>
internal sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    string Kind,
    bool IsUp,
    string? Speed,
    string Mac,
    string? Ip);

internal static class NetworkInfo
{
    /// <summary>
    /// Network cards worth offering in the Interfaces menu: real Wi-Fi and Ethernet adapters, wired first.
    /// VPN, Hyper-V, VMware, Bluetooth, Wi-Fi Direct and other virtual adapters are left out.
    /// </summary>
    public static List<AdapterInfo> Candidates() =>
        Sorted(All()
            .Where(n => KindOf(n) is "LAN" or "WLAN" && NativeMethods.IsHardwareInterface(IfIndex(n)) != false)
            .Select(Describe));

    /// <summary>Row order, the same as the Mac version: wired first, then by name.</summary>
    public static List<AdapterInfo> Sorted(IEnumerable<AdapterInfo> adapters) =>
        adapters
            .OrderBy(a => a.Kind == "LAN" ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>What "Automatic (all active)" shows: every connected candidate with an IPv4 address.</summary>
    public static bool IsActive(AdapterInfo a) => a.IsUp && a.Ip != null;

    /// <summary>Any adapter with a MAC address, virtual ones included (one ticked in an older version may be virtual).</summary>
    public static AdapterInfo? Find(string id) =>
        All().Where(n => n.Id == id).Select(Describe).FirstOrDefault();

    private static List<NetworkInterface> All()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Where(n => n.GetPhysicalAddress().GetAddressBytes().Length == 6)
                .ToList();
        }
        catch
        {
            return new List<NetworkInterface>();
        }
    }

    private static AdapterInfo Describe(NetworkInterface n)
    {
        bool up = n.OperationalStatus == OperationalStatus.Up;
        return new AdapterInfo(
            n.Id,
            n.Name,
            n.Description,
            KindOf(n),
            up,
            up ? FormatSpeed(SafeSpeed(n)) : null,
            FormatMac(n.GetPhysicalAddress()),
            up ? IPv4Of(n) : null);
    }

    private static string KindOf(NetworkInterface n) => n.NetworkInterfaceType switch
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

    /// <summary>Interface index (the same for IPv4 and IPv6); IPv6 covers adapters with IPv4 turned off.</summary>
    private static long IfIndex(NetworkInterface n)
    {
        try
        {
            var p = n.GetIPProperties();
            if (n.Supports(NetworkInterfaceComponent.IPv4)) return p.GetIPv4Properties()?.Index ?? -1;
            if (n.Supports(NetworkInterfaceComponent.IPv6)) return p.GetIPv6Properties()?.Index ?? -1;
        }
        catch { /* unknown */ }
        return -1;
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
