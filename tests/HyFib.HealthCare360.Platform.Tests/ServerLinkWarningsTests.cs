using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-06 and DED-17 on a cabled LAN, as at Nana Hospitals, where every machine is cabled: System health judges the
/// link the workstations reach the Server Node over. A cabled server's idle WiFi card was a critical "WiFi is disconnected"
/// for as long as it ran, and a server with no link at all said "Wired".
/// </summary>
public sealed class ServerLinkWarningsTests
{
    private const int WeakThreshold = 40;

    private static readonly NetworkLink Cable =
        new("Ethernet", "Intel(R) Ethernet Connection (17) I219-V", LinkMedium.Cable, true, 1000, ["192.168.1.10"]);

    /// <summary>A second card, on an imaging network.</summary>
    private static readonly NetworkLink Imaging =
        new("Ethernet 2", "Realtek PCIe GbE Family Controller", LinkMedium.Cable, true, 1000, ["10.20.0.5"]);

    private static readonly DateTimeOffset LostAt = new(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_cabled_server_with_an_idle_WiFi_card_has_no_link_warning()
    {
        // What it was: the WiFi alone, judged as the server's link.
        SystemHealthService.WifiWarnings(true, Down(), null, WeakThreshold).Select(w => (w.Code, w.Severity)).ShouldBe([("wifi.down", "critical")]);

        Warnings(cables: [Cable], wifi: Down()).ShouldBeEmpty();
        Warnings(cables: [Cable], wifiSupported: false, wifi: null).ShouldBeEmpty();
    }

    [Fact]
    public void On_a_cable_only_the_network_the_WiFi_joins_is_said_not_its_signal()
    {
        var weakShared = Link(20) with { Security = WifiSecurityLevel.Personal };

        Warnings(cables: [Cable], wifi: weakShared).Select(w => w.Code).ShouldBe(["wifi.shared_password"],
            "the server is on that network beside its cable; how strong the WiFi is no longer matters");
    }

    [Fact]
    public void The_cable_lost_is_critical_unless_the_server_is_still_on_WiFi()
    {
        Warnings(cables: [], cableLost: true, wifi: Down()).Select(w => (w.Code, w.Severity, w.Message)).ShouldBe([
            ("network.cable_down", "critical", "The Server Node's network cable is disconnected, and it has no WiFi connection: workstations cannot reach HC360.")]);

        Warnings(cables: [], cableLost: true, wifi: Link(20)).Select(w => (w.Code, w.Severity)).ShouldBe([
            ("network.cable_down", "warning"), ("wifi.weak", "warning")], "on WiFi alone now, how strong it is matters again");
    }

    /// <summary>
    /// A server with no cable up and no WiFi, and no loss the monitor told: as when it started with its cable out, maybe
    /// before its switch after a power cut. No monitor tells this one, so System health's alerts do, once it has lasted.
    /// </summary>
    [Fact]
    public void A_server_with_no_link_at_all_is_critical_and_told_by_System_healths_alerts()
    {
        Warnings(cables: [], wifiSupported: false, wifi: null).Select(w => (w.Code, w.Severity, w.Message)).ShouldBe([
            ("network.down", "critical", "The Server Node has no network connection: no network cable is connected, and it has no WiFi. Workstations cannot reach HC360.")]);
        Warnings(cables: [], wifi: Down()).Select(w => (w.Code, w.Message)).ShouldBe([
            ("network.down", "The Server Node has no network connection: no network cable is connected, and its WiFi is not connected. Workstations cannot reach HC360.")]);

        SystemHealthAlerts.ToldByTheLinkMonitor("network.down").ShouldBeFalse();
        SystemHealthAlerts.ToldByTheLinkMonitor("network.cable_down").ShouldBeTrue("the monitor told it as it happened");
        SystemHealthAlerts.ToldByTheLinkMonitor("wifi.down").ShouldBeTrue();
    }

    [Fact]
    public void A_server_on_WiFi_keeps_its_WiFi_warnings()
    {
        Warnings(cables: [], wifi: Link(20)).Select(w => w.Code).ShouldBe(["wifi.weak"]);
        Warnings(cables: [], wifi: Down(), wifiLinkLost: true).Select(w => w.Code).ShouldBe(["wifi.down"], "the monitor told the drop");
        Warnings(cables: [], wifiSupported: false, wifi: null, adapterMissing: "Server Node lost its WiFi adapter.")
            .Select(w => w.Code).ShouldBe(["wifi.adapter_missing"]);
    }

    [Fact]
    public void Until_the_link_monitor_has_read_the_cables_the_WiFi_alone_is_judged_as_before()
    {
        Warnings(cables: null, wifi: Down()).Select(w => w.Code).ShouldBe(["wifi.down"]);
        Warnings(cables: null, wifiSupported: false, wifi: null).ShouldBeEmpty("a monitor that is off judges no link");
    }

    /// <summary>
    /// A server with two cards: the cable the workstations reached HC360 over, lost while the other is up, is critical and
    /// named with its last address; another cable lost while theirs is up is a warning. Before, nothing was said while any
    /// cable was up.
    /// </summary>
    [Fact]
    public void A_cable_lost_beside_another_is_critical_when_the_workstations_used_it_and_a_warning_otherwise()
    {
        Warnings(cables: [Imaging], wifi: Down(), lost: [new LostCable("Ethernet", ["192.168.1.10"], LostAt, ServedWorkstations: true)])
            .Select(w => (w.Code, w.Severity, w.Message)).ShouldBe([
                ("network.cable_down", "critical", "The Server Node's network cable 'Ethernet' (192.168.1.10) is disconnected. Workstations reached HC360 "
                    + "over it, so they cannot until it is back. It is still on 'Ethernet 2' (10.20.0.5).")]);

        Warnings(cables: [Cable], wifi: Down(), lost: [new LostCable("Ethernet 2", ["10.20.0.5"], LostAt, ServedWorkstations: false)])
            .Select(w => (w.Code, w.Severity, w.Message)).ShouldBe([
                ("network.cable_down", "warning", "The Server Node's network cable 'Ethernet 2' (10.20.0.5) is disconnected. It is still on 'Ethernet' (192.168.1.10).")]);
    }

    private static List<HealthWarning> Warnings(
        IReadOnlyList<NetworkLink>? cables, WifiConnectionInfo? wifi, bool cableLost = false, bool wifiSupported = true, string? adapterMissing = null,
        bool wifiLinkLost = false, IReadOnlyList<LostCable>? lost = null) =>
        [.. SystemHealthService.LinkWarnings(cables, lost ?? (cableLost ? [new LostCable("Ethernet", ["192.168.1.10"], LostAt, true)] : []),
            wifiSupported, wifi, adapterMissing, wifiLinkLost, WeakThreshold)];

    /// <summary>Connected to the staff network (WPA2-Enterprise) at <paramref name="quality"/> % signal.</summary>
    private static WifiConnectionInfo Link(int quality) =>
        new(Guid.Empty, "Intel(R) Wi-Fi 6 AX201 160MHz", true, "Staff", "00:11:22:33:44:55", quality, -55, 300, 300, "ax", null, null, WifiSecurityLevel.Enterprise);

    /// <summary>A WiFi card that is not connected.</summary>
    private static WifiConnectionInfo Down() =>
        new(Guid.Empty, "Intel(R) Wi-Fi 6 AX201 160MHz", false, null, null, 0, null, null, null, null, null, null, WifiSecurityLevel.Unknown);
}
