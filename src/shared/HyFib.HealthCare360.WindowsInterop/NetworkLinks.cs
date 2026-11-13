using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.WindowsInterop;

/// <summary>How one of this computer's network adapters reaches the hospital network.</summary>
public enum LinkMedium
{
    /// <summary>A network cable (Ethernet) to a switch or an access point.</summary>
    Cable,

    /// <summary>WiFi.</summary>
    Wifi,
}

/// <summary>
/// One of this computer's own network adapters that can carry the hospital network: a cable or WiFi. Never loopback, a
/// tunnel, or a virtual adapter (Hyper-V, VPNs, a phone tethered by USB), which can be up while the cable is out.
/// </summary>
/// <param name="Name">Windows' name for it ("Ethernet", "Wi-Fi 2").</param>
/// <param name="Description">The adapter ("Intel(R) Ethernet Connection (17) I219-V").</param>
/// <param name="Medium">A cable or WiFi.</param>
/// <param name="IsUp">Connected: a cable in with a link to the switch, or WiFi joined to a network.</param>
/// <param name="SpeedMbps">The speed the link runs at, in megabits a second, when Windows gives one.</param>
/// <param name="Addresses">Its IPv4 addresses, without the 169.254.x.x Windows gives itself when no DHCP server answers.</param>
public sealed record NetworkLink(string Name, string Description, LinkMedium Medium, bool IsUp, long? SpeedMbps, IReadOnlyList<string> Addresses)
{
    /// <summary>Up, with an address on the hospital network: workstations can reach this computer over it.</summary>
    public bool CarriesNetwork => IsUp && Addresses.Count > 0;
}

/// <summary>What Windows tells about one network adapter, as <see cref="NetworkLinks.Classify"/> reads it.</summary>
public sealed record AdapterFacts(
    string Name, string Description, NetworkInterfaceType Type, OperationalStatus Status, long SpeedBitsPerSecond, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// This computer's own network links, cable and WiFi (SRS DED-06, DED-07): the Server Node's link monitor watches the cable
/// as it watches the WiFi, and HC360 Desktop's connection indicator says when a workstation is on a cable. Read with .NET's
/// <see cref="NetworkInterface"/>, so it works from a Windows service and on any OS.
/// </summary>
public static partial class NetworkLinks
{
    /// <summary>A speed above this (1 Tbps) is a card's way of saying it does not know.</summary>
    private const long MaxSpeedMbps = 1_000_000;

    /// <summary>This computer's cable and WiFi links, up or not; empty when Windows does not answer.</summary>
    public static IReadOnlyList<NetworkLink> Read()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces().Select(Facts).Select(Classify).OfType<NetworkLink>()];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>
    /// The cable links among <paramref name="links"/> that are up: plugged in, with a link to the switch. Not whether they
    /// have an address: a card shared with a virtual switch (Hyper-V's external switch) keeps its link while its address
    /// moves to the virtual adapter, which is no link, and the cable is still in.
    /// </summary>
    public static IReadOnlyList<NetworkLink> Cables(IEnumerable<NetworkLink> links) =>
        [.. links.Where(l => l.Medium == LinkMedium.Cable && l.IsUp)];

    /// <summary>
    /// The link an adapter is, or null when it is none: a medium that is neither a cable nor WiFi (loopback, a tunnel, a
    /// dial-up line), or a virtual adapter, known by its name or description.
    /// </summary>
    public static NetworkLink? Classify(AdapterFacts adapter)
    {
        LinkMedium? medium = adapter.Type switch
        {
            NetworkInterfaceType.Wireless80211 => LinkMedium.Wifi,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => LinkMedium.Cable,
            _ => null,
        };
        if (medium is null || VirtualAdapter().IsMatch(adapter.Name) || VirtualAdapter().IsMatch(adapter.Description))
        {
            return null;
        }

        var addresses = adapter.Addresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(a))
            .Select(a => a.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // A card that does not know its speed says 0, -1, or 4,294,967,295 Mbps (an unsigned -1): no card runs at a terabit.
        long? speed = adapter.SpeedBitsPerSecond / 1_000_000 is > 0 and <= MaxSpeedMbps and var mbps ? mbps : null;
        return new NetworkLink(adapter.Name, adapter.Description, medium.Value, adapter.Status == OperationalStatus.Up, speed, addresses);
    }

    /// <summary>169.254.0.0/16: what Windows gives an adapter that found no DHCP server, which nobody else can reach.</summary>
    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes is [169, 254, _, _];
    }

    private static AdapterFacts Facts(NetworkInterface adapter)
    {
        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = [.. adapter.GetIPProperties().UnicastAddresses.Select(u => u.Address)];
        }
        catch (NetworkInformationException)
        {
            addresses = [];
        }

        long speed;
        try
        {
            speed = adapter.Speed;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            speed = 0;
        }

        return new AdapterFacts(adapter.Name, adapter.Description, adapter.NetworkInterfaceType, adapter.OperationalStatus, speed, addresses);
    }

    /// <summary>
    /// Virtual adapters, by the words their names and descriptions carry: Hyper-V and WSL (vEthernet), other hypervisors,
    /// VPN and remote-access tunnels, packet capture, Bluetooth, and a phone sharing its internet over USB (Remote NDIS).
    /// A real network card's description names its maker and model, and none of these.
    /// </summary>
    [GeneratedRegex(
        @"virtual|hyper-v|vethernet|vmware|virtualbox|\btap\b|tap-windows|wintun|wireguard|zerotier|tailscale|openvpn|hamachi|radmin|npcap|loopback|bluetooth|wan miniport|kernel debug|remote ndis|\brndis\b|docker|anyconnect|forti|globalprotect|pangp|sonicwall|pulse secure|check point",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VirtualAdapter();
}
