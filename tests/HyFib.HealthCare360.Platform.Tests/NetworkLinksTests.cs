using System.Net;
using System.Net.NetworkInformation;
using HyFib.HealthCare360.WindowsInterop;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The network links the Server Node's link monitor and HC360 Desktop read (SRS DED-06, DED-07): a computer's own cable and
/// WiFi adapters, and never a virtual one, which stays up while the cable is out. The adapters are those of the hospital's
/// machines and the tools found on desk PCs.
/// </summary>
public sealed class NetworkLinksTests
{
    private const long Gigabit = 1_000_000_000;

    [Fact]
    public void A_network_card_with_a_cable_in_and_an_address_carries_the_network()
    {
        var link = NetworkLinks.Classify(Adapter("Ethernet", "Intel(R) Ethernet Connection (17) I219-V", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, Gigabit, "192.168.1.10", "fe80::1c2d:3e4f:5a6b:7c8d"));

        link.ShouldNotBeNull();
        link.Medium.ShouldBe(LinkMedium.Cable);
        link.IsUp.ShouldBeTrue();
        link.SpeedMbps.ShouldBe(1000);
        link.Addresses.ShouldBe(["192.168.1.10"], "IPv4 only: the hospital network is reached on its IPv4 address");
        link.CarriesNetwork.ShouldBeTrue();
    }

    [Fact]
    public void A_card_whose_cable_is_out_is_down_and_carries_nothing()
    {
        var link = NetworkLinks.Classify(Adapter("Ethernet", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet,
            OperationalStatus.Down, -1));

        link.ShouldNotBeNull();
        link.Medium.ShouldBe(LinkMedium.Cable);
        link.IsUp.ShouldBeFalse();
        link.SpeedMbps.ShouldBeNull();
        link.CarriesNetwork.ShouldBeFalse();
    }

    [Fact]
    public void A_card_that_got_no_address_from_DHCP_carries_nothing_though_its_cable_is_in()
    {
        var link = NetworkLinks.Classify(Adapter("Ethernet", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, 100_000_000, "169.254.12.34"));

        link.ShouldNotBeNull();
        link.IsUp.ShouldBeTrue();
        link.Addresses.ShouldBeEmpty("169.254.x.x is what Windows gives itself when no DHCP server answers");
        link.CarriesNetwork.ShouldBeFalse();
        link.SpeedMbps.ShouldBe(100);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(4_294_967_295L * 1_000_000)]
    public void A_card_that_does_not_know_its_speed_gives_none(long bitsPerSecond)
    {
        // 4,294,967,295 Mbps is an unsigned -1, which virtual and some USB cards report.
        NetworkLinks.Classify(Adapter("Ethernet", "Realtek USB GbE Family Controller", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, bitsPerSecond, "192.168.1.10"))!.SpeedMbps.ShouldBeNull();
    }

    [Fact]
    public void A_WiFi_card_is_WiFi()
    {
        var link = NetworkLinks.Classify(Adapter("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211,
            OperationalStatus.Up, 866_700_000, "192.168.1.44"));

        link.ShouldNotBeNull();
        link.Medium.ShouldBe(LinkMedium.Wifi);
        link.SpeedMbps.ShouldBe(866);
    }

    [Theory]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet)]
    [InlineData("vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter #2", NetworkInterfaceType.Ethernet)]
    [InlineData("Ethernet 2", "VirtualBox Host-Only Ethernet Adapter", NetworkInterfaceType.Ethernet)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", NetworkInterfaceType.Ethernet)]
    [InlineData("Ethernet 3", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet)]
    [InlineData("ZeroTier One [8056c2e21c000001]", "ZeroTier Virtual Port", NetworkInterfaceType.Ethernet)]
    [InlineData("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter", NetworkInterfaceType.Ethernet)]
    [InlineData("Ethernet 4", "Remote NDIS based Internet Sharing Device", NetworkInterfaceType.Ethernet)]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", NetworkInterfaceType.Ethernet)]
    [InlineData("Local Area Connection* 2", "Microsoft Wi-Fi Direct Virtual Adapter #2", NetworkInterfaceType.Wireless80211)]
    [InlineData("Ethernet 5", "Npcap Loopback Adapter", NetworkInterfaceType.Ethernet)]
    [InlineData("OpenVPN Wintun", "Wintun Userspace Tunnel", NetworkInterfaceType.Ethernet)]
    public void A_virtual_adapter_is_no_link_however_up_it_is(string name, string description, NetworkInterfaceType type)
    {
        NetworkLinks.Classify(Adapter(name, description, type, OperationalStatus.Up, 10 * Gigabit, "172.20.0.1")).ShouldBeNull(description);
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Loopback)]
    [InlineData(NetworkInterfaceType.Tunnel)]
    [InlineData(NetworkInterfaceType.Ppp)]
    public void Loopback_tunnels_and_dial_up_are_no_links(NetworkInterfaceType type)
    {
        NetworkLinks.Classify(Adapter("Other", "Some interface", type, OperationalStatus.Up, Gigabit, "10.0.0.2")).ShouldBeNull();
    }

    [Fact]
    public void The_cables_are_the_cable_links_that_are_up()
    {
        var plugged = NetworkLinks.Classify(Adapter("Ethernet", "Intel(R) Ethernet Connection (17) I219-V", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, Gigabit, "192.168.1.10"))!;
        var spare = NetworkLinks.Classify(Adapter("Ethernet 2", "Intel(R) Ethernet Controller I225-V", NetworkInterfaceType.Ethernet,
            OperationalStatus.Down, 0))!;
        var wifi = NetworkLinks.Classify(Adapter("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211,
            OperationalStatus.Up, 866_700_000, "192.168.1.44"))!;

        NetworkLinks.Cables([plugged, spare, wifi]).ShouldBe([plugged]);
        NetworkLinks.Cables([spare, wifi]).ShouldBeEmpty();
    }

    [Fact]
    public void A_card_shared_with_a_virtual_switch_is_still_a_cable_plugged_in_though_its_address_is_on_the_virtual_adapter()
    {
        // Hyper-V's external switch: the card keeps its link to the switch, and its address moves to vEthernet, no link.
        var card = NetworkLinks.Classify(Adapter("Ethernet", "Intel(R) Ethernet Connection (17) I219-V", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, Gigabit))!;
        var virtualSwitch = NetworkLinks.Classify(Adapter("vEthernet (External)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, 10 * Gigabit, "192.168.1.10"));

        virtualSwitch.ShouldBeNull();
        card.CarriesNetwork.ShouldBeFalse("it has no address of its own");
        NetworkLinks.Cables([card]).ShouldBe([card], "the cable is in, with a link to the switch");
    }

    private static AdapterFacts Adapter(string name, string description, NetworkInterfaceType type, OperationalStatus status, long speed,
        params string[] addresses) =>
        new(name, description, type, status, speed, [.. addresses.Select(IPAddress.Parse)]);
}
