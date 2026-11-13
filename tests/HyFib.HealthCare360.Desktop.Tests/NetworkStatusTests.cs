using HyFib.HealthCare360.Desktop.Services;
using HyFib.HealthCare360.WindowsInterop;

namespace HyFib.HealthCare360.Desktop.Tests;

/// <summary>
/// SRS DED-07 on a cabled LAN, as at Nana Hospitals, where the workstations are cabled: HC360 Desktop tells the staff app
/// that a workstation is on its network cable, so its connection pill says "Cable", and a WiFi card beside the cable,
/// idle or weak, is not taken for its link.
/// </summary>
public sealed class NetworkStatusTests
{
    private static readonly NetworkLink Cable =
        new("Ethernet", "Realtek PCIe GbE Family Controller", LinkMedium.Cable, true, 1000, ["192.168.1.31"]);

    [Fact]
    public void A_cabled_workstation_is_on_its_cable_whatever_its_WiFi_card_is_doing()
    {
        var idle = NetworkStatusService.Describe(WiFi(connected: false), [Cable], networkAvailable: true, TimeSpan.FromMilliseconds(3));

        idle.Wired.ShouldBeTrue();
        idle.CableSpeedMbps.ShouldBe(1000);
        idle.WifiConnected.ShouldBeFalse("the WiFi card is idle; the staff app shows the cable instead");
        idle.ServerReachable.ShouldBeTrue();
        idle.ServerLatencyMs.ShouldBe(3);

        NetworkStatusService.Describe(WiFi(connected: true, quality: 25), [Cable], true, null).Wired.ShouldBeTrue("a weak WiFi beside the cable");
    }

    [Fact]
    public void A_workstation_with_no_WiFi_adapter_says_it_is_on_its_cable()
    {
        var status = NetworkStatusService.Describe(null, [Cable], networkAvailable: true, TimeSpan.FromMilliseconds(3));

        status.Wired.ShouldBeTrue();
        status.Note.ShouldBe("On a network cable");
        status.WifiConnected.ShouldBeTrue("a staff app that does not know Wired still shows it connected, as before");
    }

    [Fact]
    public void A_workstation_on_WiFi_alone_is_not_on_a_cable()
    {
        var status = NetworkStatusService.Describe(WiFi(connected: true, quality: 80), [], networkAvailable: true, TimeSpan.FromMilliseconds(9));

        status.Wired.ShouldBeFalse();
        status.CableSpeedMbps.ShouldBeNull();
        status.Ssid.ShouldBe("NH-Staff");
        status.SignalQuality.ShouldBe(80);

        var nothing = NetworkStatusService.Describe(null, [], networkAvailable: false, null);
        nothing.Wired.ShouldBeFalse();
        nothing.WifiConnected.ShouldBeFalse();
        nothing.ServerReachable.ShouldBeFalse();
    }

    [Fact]
    public void The_fastest_cable_is_given_and_a_speed_Windows_did_not_give_is_left_out()
    {
        NetworkStatusService.Describe(null, [Cable with { SpeedMbps = 100 }, Cable with { Name = "Ethernet 2", SpeedMbps = 1000 }], true, null)
            .CableSpeedMbps.ShouldBe(1000);
        NetworkStatusService.Describe(null, [Cable with { SpeedMbps = null }], true, null).CableSpeedMbps.ShouldBeNull();
    }

    /// <summary>The workstation's WiFi card, on the staff network at <paramref name="quality"/> % signal, or idle.</summary>
    private static WifiConnectionInfo WiFi(bool connected, int quality = 0) =>
        new(Guid.Empty, "Intel(R) Wi-Fi 6 AX201 160MHz", connected, connected ? "NH-Staff" : null, null, quality, null, connected ? 144 : null, null,
            null, null, null, connected ? WifiSecurityLevel.Enterprise : WifiSecurityLevel.Unknown, DetailsAvailable: connected);
}
