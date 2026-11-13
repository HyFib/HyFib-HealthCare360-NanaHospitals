using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.WindowsInterop;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using static HyFib.HealthCare360.Platform.Networking.WifiEventKind;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-06: the Server Node samples its own WiFi link every 30 seconds, logs a drop or a weak signal, and alerts the
/// administrators in-app and the IT on-call number by SMS, and by WhatsApp when the hospital turns that on, when the link
/// is lost or the signal stays below the threshold: once for each episode, with a word in-app when it is over. The link
/// is lost too when its WiFi adapter disappears, and when the server starts without the link it had when it last ran.
/// </summary>
public sealed class WifiMonitorTests
{
    private const string OnCallPhone = "98450 12345";
    private const string Server = "HC360-SRV";

    private static readonly OnCallAlerts BySms = new(OnCallPhone, WhatsApp: false);

    private static readonly DateTimeOffset Start = new(2026, 10, 20, 4, 0, 0, TimeSpan.Zero);
    private static readonly Guid Adapter = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_link_is_sampled_every_30_seconds_unless_the_hospital_sets_another_interval()
    {
        WifiMonitor.SampleInterval(new NetworkOptions()).ShouldBe(TimeSpan.FromSeconds(30));
        WifiMonitor.SampleInterval(new NetworkOptions { WifiSampleSeconds = 120 }).ShouldBe(TimeSpan.FromMinutes(2));
        WifiMonitor.SampleInterval(new NetworkOptions { WifiSampleSeconds = 1 }).ShouldBe(TimeSpan.FromSeconds(10), "no busier than every 10 seconds");
        WifiMonitor.SampleInterval(new NetworkOptions { WifiSampleSeconds = 86_400 }).ShouldBe(TimeSpan.FromMinutes(10), "never quieter than every 10 minutes");
    }

    [Fact]
    public void A_drop_is_reported_once_however_long_the_link_stays_down_and_its_return_once()
    {
        var events = Sample(Link(80), Down(), Down(), Down(), Link(80), Link(80));

        events.Select(e => (e.Kind, e.At)).ShouldBe([(Disconnected, At(1)), (Reconnected, At(4))]);
        events[0].Message.ShouldBe("Server Node lost its WiFi connection. Workstations cannot reach HC360 until it reconnects.");
        events[1].Message.ShouldBe("Server Node reconnected to WiFi 'Staff'.");
    }

    [Fact]
    public void A_weak_signal_is_reported_once_it_has_stayed_below_the_threshold_for_a_minute_and_its_recovery_once()
    {
        // The default threshold is 40 %: 39 % is weak, 40 % is not.
        var events = Sample(Link(80), Link(39), Link(39), Link(39), Link(35), Link(30), Link(40), Link(80));

        events.Select(e => (e.Kind, e.At)).ShouldBe([(WeakSignal, At(3)), (SignalRecovered, At(6))]);
        (events[0].At - At(1)).ShouldBe(TimeSpan.FromMinutes(1), "three samples 30 seconds apart: weak at the first, alerted at the third");
        events[0].Message.ShouldBe("Server Node WiFi signal is weak (39%). Move the access point closer or connect Ethernet.");
        events[1].Message.ShouldBe("Server Node WiFi signal recovered (40%).");
    }

    [Fact]
    public void A_dip_shorter_than_three_samples_raises_nothing()
    {
        Sample(Link(39), Link(39), Link(41), Link(39), Link(39), Link(40), Link(10), Link(10)).ShouldBeEmpty();
    }

    [Fact]
    public void Each_weak_episode_is_reported_once_and_a_new_one_again()
    {
        var events = Sample(Link(30), Link(30), Link(30), Link(30), Link(30), Link(30), Link(70), Link(30), Link(30), Link(30));

        events.Select(e => (e.Kind, e.At)).ShouldBe([(WeakSignal, At(2)), (SignalRecovered, At(6)), (WeakSignal, At(9))]);
    }

    [Fact]
    public void The_threshold_is_the_one_the_hospital_sets()
    {
        Sample(new NetworkOptions { WifiWeakSignalThreshold = 60 }, Link(55), Link(55), Link(55)).ShouldHaveSingleItem().Kind.ShouldBe(WeakSignal);
        Sample(new NetworkOptions(), Link(55), Link(55), Link(55)).ShouldBeEmpty();
    }

    [Fact]
    public void A_link_that_is_down_or_whose_signal_Windows_withholds_is_not_reported_weak()
    {
        // Both report 0 %: no signal, or one Windows does not show without Location access (System health says so).
        Sample(Link(80), Down(), Down(), Down(), Down()).Select(e => e.Kind).ShouldBe([Disconnected]);
        Sample(Withheld(), Withheld(), Withheld(), Withheld()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_drop_is_logged_and_alerts_the_administrators_in_app_and_the_on_call_number_by_sms()
    {
        var drop = Sample(Link(80), Down()).ShouldHaveSingleItem();
        var (audit, notifications) = (new RecordingAudit(), new RecordingNotifications());

        await WifiMonitor.AlertAsync([drop], audit, notifications, BySms, Server, Ct);

        var logged = audit.Entries.ShouldHaveSingleItem();
        (logged.Category, logged.Action, logged.Summary, logged.Severity)
            .ShouldBe((AuditCategories.Admin, "network.wifi_disconnected", drop.Message, AuditSeverity.Critical));
        notifications.InApp.ShouldBe([(PlatformPermissions.SystemRead, (Guid?)null, "Server WiFi: Disconnected", drop.Message, "/admin/system", "system", "critical")]);
        var sms = notifications.Queued.ShouldHaveSingleItem();
        sms.TemplateKey.ShouldBe("system.alert");
        sms.Recipient.ShouldBe(new NotificationRecipient("+919845012345", null, null, "IT on-call"));
        sms.Channels.ShouldBe([NotificationChannel.Sms]);
        sms.ExpiresIn.ShouldBe(TimeSpan.FromHours(6), "an outbox message, sent once the internet is back, while it still matters");
        Rendered(sms).ShouldBe("HC360 server alert (HC360-SRV): Server Node lost its WiFi connection. Workstations cannot reach HC360 until it reconnects.");
    }

    [Fact]
    public async Task A_weak_signal_is_logged_and_alerts_the_administrators_in_app_and_the_on_call_number_by_sms_as_a_warning()
    {
        var weak = Sample(Link(30), Link(30), Link(30)).ShouldHaveSingleItem();
        var (audit, notifications) = (new RecordingAudit(), new RecordingNotifications());

        await WifiMonitor.AlertAsync([weak], audit, notifications, BySms, Server, Ct);

        audit.Entries.ShouldHaveSingleItem().Severity.ShouldBe(AuditSeverity.Warning);
        notifications.InApp.ShouldHaveSingleItem().Severity.ShouldBe("warning");
        Rendered(notifications.Queued.ShouldHaveSingleItem())
            .ShouldBe("HC360 server alert (HC360-SRV): Server Node WiFi signal is weak (30%). Move the access point closer or connect Ethernet.");
    }

    [Fact]
    public async Task The_return_of_the_link_or_of_the_signal_is_told_in_app_without_an_sms()
    {
        var back = Sample(Down(), Link(30), Link(30), Link(30), Link(90));
        back.Select(e => e.Kind).ShouldBe([Reconnected, WeakSignal, SignalRecovered]);
        var (audit, notifications) = (new RecordingAudit(), new RecordingNotifications());

        await WifiMonitor.AlertAsync([back[0], back[2]], audit, notifications, BySms, Server, Ct);

        audit.Entries.Select(e => (e.Action, e.Severity)).ShouldBe([("network.wifi_reconnected", AuditSeverity.Info), ("network.wifi_signalrecovered", AuditSeverity.Info)]);
        notifications.InApp.Select(n => (n.Title, n.Severity)).ShouldBe([("Server WiFi: Reconnected", "info"), ("Server WiFi: SignalRecovered", "info")]);
        notifications.Queued.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_an_on_call_number_the_alerts_are_in_app_only()
    {
        var drop = Sample(Link(80), Down()).ShouldHaveSingleItem();
        var (audit, notifications) = (new RecordingAudit(), new RecordingNotifications());

        await WifiMonitor.AlertAsync([drop], audit, notifications, new OnCallAlerts(" ", WhatsApp: true), Server, Ct);

        notifications.InApp.ShouldHaveSingleItem().Severity.ShouldBe("critical");
        notifications.Queued.ShouldBeEmpty();
    }

    /// <summary>
    /// The adapter of a link in use disappears: Windows shows no WiFi adapter, because it was disabled or removed, or the
    /// WLAN AutoConfig service stopped. That is a lost link, reported once with what to do, and its return once. It was
    /// taken for a server on Ethernet: nothing was said, and System health showed no warning.
    /// </summary>
    [Fact]
    public void An_adapter_that_disappears_while_the_link_is_in_use_is_reported_once_with_what_to_do()
    {
        var detector = new WifiEventDetector();
        var events = Sample(detector, WifiAdapterAbsence.ServiceStopped, Link(80), null, null, null);

        var lost = events.ShouldHaveSingleItem();
        (lost.Kind, lost.At).ShouldBe((AdapterLost, At(1)));
        lost.Message.ShouldBe(
            "Server Node has no WiFi: the WLAN AutoConfig service is not running. Start it in Services. Workstations cannot reach HC360 over WiFi until then.");
        (detector.LinkLost, detector.AdapterMissing).ShouldBe(
            (true, lost.Message + " If Server Node now runs on a network cable on purpose, this warning clears once the cable is connected."),
            "System health says so while it lasts, and how it clears on a server cabled on purpose");

        // Back, and connected.
        detector.Evaluate(null, Link(80), At(4), new NetworkOptions()).Select(e => e.Kind).ShouldBe([Reconnected]);
        (detector.LinkLost, detector.AdapterMissing).ShouldBe((false, (string?)null));

        // Disabled or removed, with the service running; and Windows not saying which.
        Sample(new WifiEventDetector(), WifiAdapterAbsence.NoAdapter, Link(80), null).ShouldHaveSingleItem().Message.ShouldBe(
            "Server Node lost its WiFi adapter: Windows shows none (disabled or removed). Enable it in Network Connections. Workstations cannot reach HC360 over WiFi until then.");
        Sample(new WifiEventDetector(), WifiAdapterAbsence.Unknown, Link(80), null).ShouldHaveSingleItem().Message.ShouldBe(
            "Server Node lost its WiFi adapter (disabled or removed, or the WLAN AutoConfig service stopped). Workstations cannot reach HC360 over WiFi until it is back.");
    }

    [Fact]
    public void An_adapter_that_was_not_in_use_raises_nothing_when_it_is_missing()
    {
        // A server on Ethernet: with no WiFi adapter at all, or with one that is not connected and is then disabled.
        Sample(new WifiEventDetector(), WifiAdapterAbsence.NoAdapter, null, null).ShouldBeEmpty();
        var idle = new WifiEventDetector();
        Sample(idle, WifiAdapterAbsence.NoAdapter, Down(), null, null).ShouldBeEmpty();
        idle.AdapterMissing.ShouldBeNull();

        // A drop already reported is not reported again when the adapter goes too, but System health says why.
        var dropped = new WifiEventDetector();
        Sample(dropped, WifiAdapterAbsence.NoAdapter, Link(80), Down(), null).Select(e => e.Kind).ShouldBe([Disconnected]);
        dropped.AdapterMissing.ShouldNotBeNull().ShouldContain("Enable it in Network Connections");
    }

    /// <summary>
    /// A server that was on WiFi when it last ran (its last sample before the start had a link) and starts without the link,
    /// or without the adapter, reports it at its first sample. The first sample was never compared with anything, so a
    /// server that started with its WiFi down said nothing until the link had come up once and dropped.
    /// </summary>
    [Fact]
    public void A_server_that_starts_without_the_link_it_had_when_it_last_ran_reports_it_at_its_first_sample()
    {
        var down = Sample(new WifiEventDetector(onWifiWhenLastRun: true, lastSsid: "Staff"), WifiAdapterAbsence.Unknown, Down(), Down(), Link(80));
        down.Select(e => (e.Kind, e.At)).ShouldBe([(Disconnected, At(0)), (Reconnected, At(2))]);
        down[0].Message.ShouldBe("Server Node started without its WiFi connection (on 'Staff' when it last ran). Workstations cannot reach HC360 until it connects.");

        var adapter = Sample(new WifiEventDetector(onWifiWhenLastRun: true, lastSsid: "Staff"), WifiAdapterAbsence.ServiceStopped, null, null, Link(80));
        adapter.Select(e => (e.Kind, e.At)).ShouldBe([(AdapterLost, At(0)), (Reconnected, At(2))]);
        adapter[0].Message.ShouldBe(
            "Server Node started with no WiFi: the WLAN AutoConfig service is not running. Start it in Services. Workstations cannot reach HC360 over WiFi until then.");

        // Not on WiFi when it last ran, or connected now: nothing to report.
        Sample(new WifiEventDetector(onWifiWhenLastRun: false), WifiAdapterAbsence.NoAdapter, Down(), null).ShouldBeEmpty();
        Sample(new WifiEventDetector(onWifiWhenLastRun: true, lastSsid: "Staff"), WifiAdapterAbsence.Unknown, Link(80), Link(80)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_adapter_alerts_the_on_call_number_by_sms_and_on_whatsapp_when_the_hospital_turned_that_on()
    {
        var lost = Sample(new WifiEventDetector(), WifiAdapterAbsence.NoAdapter, Link(80), null).ShouldHaveSingleItem();
        var (audit, notifications) = (new RecordingAudit(), new RecordingNotifications());

        await WifiMonitor.AlertAsync([lost], audit, notifications, BySms with { WhatsApp = true }, Server, Ct);

        var logged = audit.Entries.ShouldHaveSingleItem();
        (logged.Category, logged.Action, logged.Severity).ShouldBe((AuditCategories.Admin, "network.wifi_adapterlost", AuditSeverity.Critical));
        notifications.InApp.ShouldHaveSingleItem().Severity.ShouldBe("critical");
        var alert = notifications.Queued.ShouldHaveSingleItem();
        alert.Channels.ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp]);
        Rendered(alert, NotificationChannel.WhatsApp).ShouldBe($"HC360 server alert (HC360-SRV): {lost.Message}");

        // Off unless the hospital turns it on: SMS only.
        var sms = new RecordingNotifications();
        await WifiMonitor.AlertAsync([lost], new RecordingAudit(), sms, BySms, Server, Ct);
        sms.Queued.ShouldHaveSingleItem().Channels.ShouldBe([NotificationChannel.Sms]);
    }

    /// <summary>
    /// Settings are free JSON, and the on-call number typed without quotes is a number: it is the number all the same. It
    /// did not read as text, which failed the whole sample: no alert, no SMS and no audit entry.
    /// </summary>
    [Theory]
    [InlineData("\"98450 12345\"", "true", "98450 12345", true)]
    [InlineData("9845012345", "\"TRUE\"", "9845012345", true)]
    [InlineData("\"98450 12345\"", "\"yes\"", "98450 12345", false)]
    [InlineData("null", null, null, false)]
    [InlineData(null, "1", null, false)]
    public async Task The_on_call_settings_are_read_whatever_kind_of_value_they_hold(string? phone, string? whatsApp, string? expectedPhone, bool expectedWhatsApp)
    {
        var settings = new JsonSettings(new Dictionary<string, string?> { ["alerts.oncallPhone"] = phone, ["alerts.oncallWhatsApp"] = whatsApp });

        (await WifiMonitor.OnCallAsync(settings, Ct)).ShouldBe(new OnCallAlerts(expectedPhone, expectedWhatsApp));
    }

    [Fact]
    public void The_state_of_the_WLAN_AutoConfig_service_says_why_Windows_shows_no_adapter()
    {
        WlanService.FromServiceState("Running").ShouldBe(WifiAdapterAbsence.NoAdapter);
        WlanService.FromServiceState("Stopped").ShouldBe(WifiAdapterAbsence.ServiceStopped);
        WlanService.FromServiceState("Start Pending").ShouldBe(WifiAdapterAbsence.ServiceStopped);
        WlanService.FromServiceState(null).ShouldBe(WifiAdapterAbsence.Unknown);
    }

    /// <summary>System health warns of a missing adapter as of a link that is down; a server on Ethernet gets no warning.</summary>
    [Fact]
    public void System_health_warns_while_the_adapter_of_the_link_is_missing()
    {
        const string missing = "Server Node lost its WiFi adapter: Windows shows none (disabled or removed).";
        SystemHealthService.WifiWarnings(supported: false, null, missing, 40).Select(w => (w.Code, w.Severity, w.Message))
            .ShouldBe([("wifi.adapter_missing", "critical", missing)]);
        SystemHealthService.WifiWarnings(supported: true, Down(), null, 40).Select(w => w.Code).ShouldBe(["wifi.down"]);
        SystemHealthService.WifiWarnings(supported: false, null, null, 40).ShouldBeEmpty();
    }

    /// <summary>
    /// A server cabled to the hospital LAN with a WiFi adapter beside the cable: workstations reach it over the cable, so
    /// the WiFi coming and going is not a lost link. It was: a WiFi joined for a while and left raised a critical alert,
    /// and an SMS to IT on-call, saying workstations could not reach HC360, while they reached it over the cable.
    /// </summary>
    [Fact]
    public void While_a_cable_is_up_a_WiFi_drop_or_a_lost_WiFi_adapter_raises_nothing()
    {
        var detector = new WifiEventDetector(onWifiWhenLastRun: true, lastSsid: "Staff");

        OnCable(detector, WifiAdapterAbsence.NoAdapter, Link(80), Down(), Down(), null, null, Link(80)).ShouldBeEmpty();
        (detector.LinkLost, detector.AdapterMissing).ShouldBe((false, (string?)null), "System health has no WiFi loss to warn of");
    }

    [Fact]
    public void While_a_cable_is_up_a_weak_WiFi_signal_raises_nothing_but_the_network_the_WiFi_joins_still_does()
    {
        OnCable(new WifiEventDetector(), WifiAdapterAbsence.Unknown, Link(30), Link(30), Link(30), Link(30)).ShouldBeEmpty();

        var open = Link(80) with { Ssid = "Free WiFi", Security = WifiSecurityLevel.Open };
        OnCable(new WifiEventDetector(), WifiAdapterAbsence.Unknown, open).ShouldHaveSingleItem().Kind.ShouldBe(InsecureNetwork,
            "the server is on that network all the same, beside the cable");
    }

    [Fact]
    public void A_WiFi_loss_already_told_is_over_once_a_cable_is_up()
    {
        var detector = new WifiEventDetector();
        Sample(detector, WifiAdapterAbsence.NoAdapter, Link(80), null).Select(e => e.Kind).ShouldBe([AdapterLost]);
        detector.AdapterMissing.ShouldNotBeNull();

        // The cable is connected: the warning clears, and the WiFi coming back later is no reconnection to tell.
        detector.Evaluate(null, null, At(2), new NetworkOptions(), WifiAdapterAbsence.NoAdapter, onCable: true).ShouldBeEmpty();
        (detector.LinkLost, detector.AdapterMissing).ShouldBe((false, (string?)null));
        detector.Evaluate(null, Link(80), At(3), new NetworkOptions(), onCable: true).ShouldBeEmpty();
    }

    /// <summary>
    /// A WiFi drop told to the administrators and the on-call number ("workstations cannot reach HC360"), then ended with a
    /// network cable: its end is told too, once. Before, it ended without a word, since a cable takes the WiFi's place
    /// silently and the cable had not been lost: whoever was told HC360 could not be reached was never told it can.
    /// </summary>
    [Fact]
    public void A_WiFi_loss_ended_by_a_cable_is_told_over()
    {
        var detector = new WifiEventDetector();
        var cables = new CableEventDetector();
        List<WifiEvent> Step(WifiConnectionInfo? previous, WifiConnectionInfo? current, int sample, params NetworkLink[] up) =>
            WifiMonitor.SampleLinks(detector, cables, previous, current, up, At(sample), new NetworkOptions(), () => WifiAdapterAbsence.Unknown);

        Step(null, Link(80), 1).ShouldBeEmpty();
        Step(Link(80), Down(), 2).Select(e => e.Kind).ShouldBe([Disconnected]);
        Step(Down(), Down(), 3).ShouldBeEmpty("told once");

        Step(Down(), Down(), 4, Cable(speedMbps: 1000)).Select(e => (e.Kind, e.Message)).ShouldBe([(CableReconnected,
            "Server Node is back on the network, on a network cable ('Ethernet', 1 Gbps): workstations can reach HC360 again. Back at 09:32.")]);
        Step(Down(), Down(), 5, Cable(speedMbps: 1000)).ShouldBeEmpty("its end is told once");
        detector.LinkLost.ShouldBeFalse();
    }

    [Fact]
    public void A_cable_connected_beside_a_WiFi_that_was_never_lost_is_not_news()
    {
        var detector = new WifiEventDetector();
        var cables = new CableEventDetector();

        WifiMonitor.SampleLinks(detector, cables, null, Link(80), [], At(1), new NetworkOptions(), () => WifiAdapterAbsence.Unknown).ShouldBeEmpty();
        WifiMonitor.SampleLinks(detector, cables, Link(80), Link(80), [Cable()], At(2), new NetworkOptions(), () => WifiAdapterAbsence.Unknown)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// DED-06 on a cabled LAN: the cable the server is on, lost because it is out or its switch is off, is told once with
    /// what it means, and its return once. Before, nothing watched the cable: a server on a cable that lost it said nothing,
    /// and System health said "Wired".
    /// </summary>
    [Fact]
    public void The_cable_lost_is_reported_once_however_long_it_stays_out_and_its_return_once()
    {
        var detector = new CableEventDetector();
        var events = CableSamples(detector, null, [Cable()], [Cable()], [], [], [], [Cable(speedMbps: 1000)]);

        events.Select(e => (e.Kind, e.At)).ShouldBe([(CableDisconnected, At(2)), (CableReconnected, At(5))]);
        events[0].Message.ShouldBe("Server Node lost its network cable ('Ethernet'): the cable is out or its switch is off. Workstations cannot reach HC360 until it is back.");
        events[1].Message.ShouldBe("Server Node's network cable is connected again ('Ethernet', 1 Gbps): workstations can reach HC360 again. Lost at 09:31, back at 09:32.");
        detector.CableLost.ShouldBeFalse();
    }

    [Fact]
    public void The_cable_lost_says_when_the_server_is_still_on_WiFi()
    {
        var events = CableSamples(new CableEventDetector(), Link(80), [Cable()], []);

        events.ShouldHaveSingleItem().Message.ShouldBe("Server Node lost its network cable ('Ethernet'): the cable is out or its switch is off. It is on WiFi 'Staff' only.");
    }

    /// <summary>The server stayed on WiFi while its cable was out: its return does not say the workstations were cut off.</summary>
    [Fact]
    public void The_cable_back_while_the_server_was_on_WiFi_says_only_that_it_is_back()
    {
        var events = CableSamples(new CableEventDetector(), Link(80), [Cable()], [], [Cable()]);

        events[1].Message.ShouldBe("Server Node's network cable is connected again ('Ethernet'). Lost at 09:30, back at 09:31.");
    }

    /// <summary>A cable out past midnight: the return names both days, so a late SMS is not read as today's.</summary>
    [Fact]
    public void A_cable_back_on_another_day_names_both_days()
    {
        var detector = new CableEventDetector();
        var evening = new DateTimeOffset(2026, 11, 13, 18, 25, 0, TimeSpan.Zero); // 23:55 in India

        detector.Evaluate([Cable()], null, evening);
        detector.Evaluate([], null, evening.AddMinutes(1));

        detector.Evaluate([Cable()], null, evening.AddMinutes(10)).ShouldHaveSingleItem().Message.ShouldBe(
            "Server Node's network cable is connected again ('Ethernet'): workstations can reach HC360 again. Lost at 13 Nov 23:56, back at 14 Nov 00:05.");
    }

    /// <summary>
    /// A server that starts with no cable up may have started before its switch, after a power cut: a minute later the cable
    /// is up. Nothing is told for it; System health tells a server with no link at all once that has lasted.
    /// </summary>
    [Fact]
    public void A_server_that_starts_without_a_cable_up_raises_nothing_for_the_cable()
    {
        var detector = new CableEventDetector();

        CableSamples(detector, null, [], [], [Cable()]).ShouldBeEmpty();
        detector.CableLost.ShouldBeFalse();
    }

    /// <summary>The cable lost and back: both are logged, told in the app, and sent to IT's on-call phone.</summary>
    [Fact]
    public async Task The_cable_lost_and_back_are_logged_and_alert_the_administrators_in_app_and_the_on_call_number_by_sms()
    {
        var events = CableSamples(new CableEventDetector(), null, [Cable()], [], [Cable()]);
        var (audit, notifications) = (new RecordingAudit(), new RecordingNotifications());

        await WifiMonitor.AlertAsync(events, audit, notifications, BySms, Server, Ct);

        audit.Entries.Select(e => (e.Category, e.Action, e.Severity)).ShouldBe([
            (AuditCategories.Admin, "network.cable_disconnected", AuditSeverity.Critical),
            (AuditCategories.Admin, "network.cable_reconnected", AuditSeverity.Info)]);
        notifications.InApp.Select(n => (n.Title, n.Severity)).ShouldBe([
            ("Server network cable: Disconnected", "critical"), ("Server network cable: Connected", "info")]);
        notifications.Queued.Select(m => Rendered(m)).ShouldBe([
            "HC360 server alert (HC360-SRV): Server Node lost its network cable ('Ethernet'): the cable is out or its switch is off. Workstations cannot reach HC360 until it is back.",
            "HC360 server alert (HC360-SRV): Server Node's network cable is connected again ('Ethernet'): workstations can reach HC360 again. Lost at 09:30, back at 09:31."]);
    }

    /// <summary>
    /// Theni's server reaches the SMS gateway over its one cable, so the alert of the cable's loss waits in the outbox while
    /// the cable is out, tried again at 10, 20, 40 seconds and on. The return, queued once the cable is back, would go
    /// first and leave "lost" as the last word on IT's phone. So the loss not sent yet is withdrawn, and the return says
    /// when the cable was lost and when it was back.
    /// </summary>
    [Fact]
    public async Task A_loss_not_sent_when_the_cable_is_back_is_withdrawn_and_the_return_says_both()
    {
        var detector = new CableEventDetector();
        var told = new CableLossMessages();
        var notifications = new RecordingNotifications();
        var onCall = BySms with { WhatsApp = true };
        var withdrawn = new List<Guid>();
        Task Withdraw(IReadOnlyList<Guid> ids, CancellationToken _)
        {
            withdrawn.AddRange(ids);
            return Task.CompletedTask;
        }

        Task Step(int sample, params NetworkLink[] up) =>
            WifiMonitor.AlertAsync(detector.Evaluate(up, null, At(sample)), new RecordingAudit(), notifications, onCall, Server, severalServers: false, told, Withdraw, Ct);

        await Step(0, Cable());
        await Step(1);
        await Step(2);
        var loss = notifications.Queued.ShouldHaveSingleItem();
        withdrawn.ShouldBeEmpty("the cable is still out");

        await Step(3, Cable());

        withdrawn.ShouldBe(loss.Ids, "its SMS and its WhatsApp message, those not gone yet");
        notifications.Queued.Count.ShouldBe(2);
        Rendered(notifications.Queued[1]).ShouldBe(
            "HC360 server alert (HC360-SRV): Server Node's network cable is connected again ('Ethernet'): workstations can reach HC360 again. Lost at 09:30, back at 09:31.");

        await Step(4);
        await Step(5, Cable());
        withdrawn.ShouldBe([.. loss.Ids, .. notifications.Queued[2].Ids], "a later loss is followed afresh, and its return withdraws its own");
    }

    /// <summary>
    /// Every server watches its own links (DED-06), whatever its role and whether or not it holds the job lease. Before, the
    /// monitor was registered and ran only where the background jobs run, and with the lease only on its holder, so an API
    /// node could lose the cable its requests come in on unseen.
    /// </summary>
    [Theory]
    [InlineData("api")]
    [InlineData("worker")]
    [InlineData("all")]
    public void Every_server_watches_its_own_links_whatever_its_role(string role)
    {
        var data = Directory.CreateTempSubdirectory("hc360-links-");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HealthCare360:Role"] = role,
                ["HealthCare360:DataDirectory"] = data.FullName,
                ["ConnectionStrings:Hospital"] = "Host=localhost;Database=hc360;Username=hc360", // registered, never opened
            }).Build();
            var services = new ServiceCollection();
            services.AddHospitalPlatform(configuration, new DevelopmentHost(), [], "migrations");

            services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WifiMonitor)).ShouldBe(1);
            WifiMonitor.Watches(configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>()!).ShouldBeTrue();
        }
        finally
        {
            data.Delete(recursive: true);
        }

        JobLeadership.IsJob(typeof(WifiMonitor)).ShouldBeFalse("it runs on every server at once, not only on the one holding the job lease");
        WifiMonitor.Watches(new PlatformOptions { Role = role, Network = new NetworkOptions { EnableWifiMonitor = false } })
            .ShouldBeFalse("the hospital may still turn the monitor off");
    }

    /// <summary>
    /// One server of several (SCAL-01) takes its requests through the reverse proxy, which sends them to the other servers
    /// while it is out: its alerts say so, rather than that the workstations cannot reach HC360, which would be untrue.
    /// </summary>
    [Fact]
    public void One_server_of_several_says_the_reverse_proxy_sends_its_requests_to_the_others()
    {
        var events = TwoCards(new CableEventDetector(severalServers: true), ["192.168.1.10"], [Lan, Imaging], [Imaging], [Lan, Imaging]);

        events[0].Message.ShouldBe("Server Node lost its network cable 'Ethernet' (192.168.1.10), which requests reach it over: the reverse proxy sends "
            + "them to the other servers until it is back. It is still on 'Ethernet 2' (10.20.0.5).");
        events[1].Message.ShouldBe("Server Node's network cable is connected again ('Ethernet', 1 Gbps): it takes requests again. Lost at 09:30, back at 09:31.");
        CableSamples(new CableEventDetector(severalServers: true), null, [Cable()], []).ShouldHaveSingleItem().Message.ShouldBe(
            "Server Node lost its network cable ('Ethernet'): the cable is out or its switch is off. The reverse proxy sends requests to the other servers until it is back.");
        TwoCards(new CableEventDetector(severalServers: true), ["192.168.1.10"], [Lan, Imaging], [Lan]).ShouldHaveSingleItem().Message.ShouldBe(
            "Server Node lost its network cable 'Ethernet 2' (10.20.0.5). Requests reach it over 'Ethernet', which is up; what used 'Ethernet 2' cannot reach it until it is back.");
    }

    /// <summary>
    /// With several servers the administrators get every one's alerts: the in-app title names the server, as the SMS does, and
    /// the audit trail records it.
    /// </summary>
    [Fact]
    public async Task One_server_of_several_names_itself_in_the_app_the_audit_trail_and_the_sms()
    {
        var events = CableSamples(new CableEventDetector(severalServers: true), null, [Cable()], []);
        var (audit, notifications) = (new RecordingAudit(), new RecordingNotifications());

        await WifiMonitor.AlertAsync(events, audit, notifications, BySms, "HC360-API2", severalServers: true, new CableLossMessages(),
            (_, _) => Task.CompletedTask, Ct);

        notifications.InApp.ShouldHaveSingleItem().Title.ShouldBe("Server network cable: Disconnected (HC360-API2)");
        var logged = audit.Entries.ShouldHaveSingleItem();
        (logged.EntityType, logged.EntityId).ShouldBe(("Server", "HC360-API2"));
        Rendered(notifications.Queued.ShouldHaveSingleItem()).ShouldStartWith("HC360 server alert (HC360-API2): ");
    }

    /// <summary>
    /// Each server's WiFi samples are its own, for its WiFi history and its check at start-up; those from before servers were
    /// told apart count as a single server's, which they are.
    /// </summary>
    [Fact]
    public void A_servers_wifi_samples_are_its_own_and_those_from_before_servers_were_told_apart()
    {
        WifiSample[] samples = [new() { Node = "api-1" }, new() { Node = "api-2" }, new() { Node = null }];

        samples.AsQueryable().Where(WifiSample.Of("api-1")).ShouldBe([samples[0], samples[2]]);
    }

    /// <summary>
    /// The switch both cards are on goes off: one alert tells both cables lost. It is withdrawn only once both are back; while
    /// one is still out it still goes, as the only word of that one.
    /// </summary>
    [Fact]
    public void A_loss_told_for_two_cables_is_withdrawn_only_once_both_are_back()
    {
        var told = new CableLossMessages();
        var message = Guid.CreateVersion7();
        told.Lost(["Ethernet", "Ethernet 2"], [message]);

        told.Back(["ethernet"]).ShouldBeEmpty("it also told 'Ethernet 2', still out");
        told.Back(["Ethernet 2"]).ShouldBe([message]);
        told.Back(["Ethernet", "Ethernet 2"]).ShouldBeEmpty("it is followed no longer");
    }

    /// <summary>
    /// A Server Node with two network cards, the hospital LAN on one and an imaging network on the other, has each cable told
    /// on its own. The one the workstations' requests came over, lost while the other is up, is critical and named with its
    /// address: they cannot reach HC360. Before, nothing was told while any cable was up.
    /// </summary>
    [Fact]
    public void The_cable_the_workstations_use_lost_while_another_is_up_is_critical_and_named()
    {
        var detector = new CableEventDetector();
        var events = TwoCards(detector, ["192.168.1.10"], [Lan, Imaging], [Imaging], [Imaging], [Lan, Imaging]);

        events.Select(e => (e.Kind, e.At)).ShouldBe([(CableDisconnected, At(1)), (CableReconnected, At(3))]);
        events[0].Message.ShouldBe("Server Node lost its network cable 'Ethernet' (192.168.1.10), which workstations reach it over: "
            + "they cannot reach HC360 until it is back. It is still on 'Ethernet 2' (10.20.0.5).");
        events[1].Message.ShouldBe("Server Node's network cable is connected again ('Ethernet', 1 Gbps): workstations can reach HC360 again. Lost at 09:30, back at 09:31.");
        events[1].Cables.ShouldBe(["Ethernet"]);
        detector.CableLost.ShouldBeFalse();
    }

    [Fact]
    public void Another_cable_lost_while_the_workstations_one_is_up_is_a_warning_and_named()
    {
        var events = TwoCards(new CableEventDetector(), ["192.168.1.10"], [Lan, Imaging], [Lan], [Lan], [Lan, Imaging]);

        events.Select(e => (e.Kind, e.At)).ShouldBe([(OtherCableDisconnected, At(1)), (CableReconnected, At(3))]);
        events[0].Message.ShouldBe("Server Node lost its network cable 'Ethernet 2' (10.20.0.5). Workstations reach it over 'Ethernet', "
            + "which is up; what used 'Ethernet 2' cannot reach it until it is back.");
        events[1].Message.ShouldBe("Server Node's network cable is connected again ('Ethernet 2', 1 Gbps). Lost at 09:30, back at 09:31.");
    }

    [Fact]
    public void With_no_requests_to_go_by_a_cable_lost_while_another_is_up_is_a_warning()
    {
        var events = TwoCards(new CableEventDetector(), null, [Lan, Imaging], [Imaging]);

        events.ShouldHaveSingleItem().Kind.ShouldBe(OtherCableDisconnected);
        events[0].Message.ShouldBe("Server Node lost its network cable 'Ethernet' (192.168.1.10). It is still on 'Ethernet 2' (10.20.0.5).");
    }

    /// <summary>The switch both cards are on goes off: one event for both, and each is told back on its own.</summary>
    [Fact]
    public void Both_cables_lost_at_once_are_one_critical_event_and_each_is_told_back()
    {
        var events = TwoCards(new CableEventDetector(), ["192.168.1.10"], [Lan, Imaging], [], [], [Lan], [Lan, Imaging]);

        events.Select(e => (e.Kind, e.At)).ShouldBe([(CableDisconnected, At(1)), (CableReconnected, At(3)), (CableReconnected, At(4))]);
        events[0].Message.ShouldBe("Server Node lost its network cables ('Ethernet', 'Ethernet 2'): the cables are out or their switch is off. "
            + "No cable is up now: workstations cannot reach HC360 until one is back.");
        events[0].Cables.ShouldBe(["Ethernet", "Ethernet 2"]);
        events[1].Message.ShouldBe("Server Node's network cable is connected again ('Ethernet', 1 Gbps): workstations can reach HC360 again. Lost at 09:30, back at 09:31.");
        events[2].Message.ShouldBe("Server Node's network cable is connected again ('Ethernet 2', 1 Gbps). Lost at 09:30, back at 09:32.");
    }

    [Fact]
    public void Each_lost_cable_is_kept_with_its_last_address_when_it_was_lost_and_whether_workstations_used_it()
    {
        var detector = new CableEventDetector();

        TwoCards(detector, ["192.168.1.10"], [Lan, Imaging], [Imaging], []);

        detector.Lost.Select(l => (l.Name, string.Join(",", l.Addresses), l.LostAt, l.ServedWorkstations)).ShouldBe([
            ("Ethernet", "192.168.1.10", At(1), true), ("Ethernet 2", "10.20.0.5", At(2), false)],
            "a cable that is down has no address any more: the one it had is kept");
    }

    /// <summary>
    /// The cable moved to the card's other port, one not up before: the server is back on the network, and the port it left
    /// is not waited for.
    /// </summary>
    [Fact]
    public void A_cable_moved_to_another_port_is_told_back_and_the_port_it_left_is_not_waited_for()
    {
        var detector = new CableEventDetector();
        var other = Lan with { Name = "Ethernet 3" };

        var events = TwoCards(detector, ["192.168.1.10"], [Lan], [], [other], [other]);

        events.Select(e => (e.Kind, e.At)).ShouldBe([(CableDisconnected, At(1)), (CableReconnected, At(2))]);
        events[1].Message.ShouldBe("Server Node is back on the network, on a network cable ('Ethernet 3', 1 Gbps): workstations can reach HC360 again. Lost at 09:30, back at 09:31.");
        events[1].Cables.ShouldBe(["Ethernet"], "the port it left: the alert of its loss is settled");
        detector.Lost.ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_cable_lost_is_a_warning_in_app_and_still_goes_to_the_on_call_number()
    {
        var events = TwoCards(new CableEventDetector(), ["192.168.1.10"], [Lan, Imaging], [Lan]);
        var (audit, notifications) = (new RecordingAudit(), new RecordingNotifications());

        await WifiMonitor.AlertAsync(events, audit, notifications, BySms, Server, Ct);

        audit.Entries.Select(e => (e.Action, e.Severity)).ShouldBe([("network.cable_disconnected", AuditSeverity.Warning)]);
        notifications.InApp.Select(n => (n.Title, n.Severity)).ShouldBe([("Server network cable: Disconnected", "warning")]);
        notifications.Queued.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(10, "10 Mbps")]
    [InlineData(100, "100 Mbps")]
    [InlineData(1000, "1 Gbps")]
    [InlineData(2500, "2.5 Gbps")]
    [InlineData(10000, "10 Gbps")]
    public void A_link_speed_reads_as_people_say_it(long mbps, string said) => CableEventDetector.Speed(mbps).ShouldBe(said);

    /// <summary>Feeds the samples, 30 seconds apart, to one detector, as the monitor does, and returns the events raised.</summary>
    private static List<WifiEvent> Sample(params WifiConnectionInfo[] samples) => Sample(new NetworkOptions(), samples);

    private static List<WifiEvent> Sample(NetworkOptions network, params WifiConnectionInfo[] samples)
    {
        var detector = new WifiEventDetector();
        var events = new List<WifiEvent>();
        WifiConnectionInfo? previous = null;
        for (var i = 0; i < samples.Length; i++)
        {
            events.AddRange(detector.Evaluate(previous, samples[i], At(i), network));
            previous = samples[i];
        }

        return events;
    }

    /// <summary>The same with <paramref name="detector"/>, where a null sample is one with no WiFi adapter, missing for <paramref name="absence"/>.</summary>
    private static List<WifiEvent> Sample(WifiEventDetector detector, WifiAdapterAbsence absence, params WifiConnectionInfo?[] samples)
    {
        var events = new List<WifiEvent>();
        WifiConnectionInfo? previous = null;
        for (var i = 0; i < samples.Length; i++)
        {
            events.AddRange(detector.Evaluate(previous, samples[i], At(i), new NetworkOptions(), samples[i] is null ? absence : WifiAdapterAbsence.Unknown));
            previous = samples[i];
        }

        return events;
    }

    /// <summary>The same with a network cable up at every sample, as on a cabled server with WiFi beside it.</summary>
    private static List<WifiEvent> OnCable(WifiEventDetector detector, WifiAdapterAbsence absence, params WifiConnectionInfo?[] samples)
    {
        var events = new List<WifiEvent>();
        WifiConnectionInfo? previous = null;
        for (var i = 0; i < samples.Length; i++)
        {
            events.AddRange(detector.Evaluate(previous, samples[i], At(i), new NetworkOptions(), samples[i] is null ? absence : WifiAdapterAbsence.Unknown, onCable: true));
            previous = samples[i];
        }

        return events;
    }

    /// <summary>Feeds the cables up at each sample, 30 seconds apart, to <paramref name="detector"/>, with <paramref name="wifi"/> beside them.</summary>
    private static List<WifiEvent> CableSamples(CableEventDetector detector, WifiConnectionInfo? wifi, params NetworkLink[][] samples)
    {
        var events = new List<WifiEvent>();
        for (var i = 0; i < samples.Length; i++)
        {
            events.AddRange(detector.Evaluate(samples[i], wifi, At(i)));
        }

        return events;
    }

    /// <summary>A Server Node's first card, on the hospital LAN.</summary>
    private static readonly NetworkLink Lan = new("Ethernet", "Intel(R) Ethernet Connection (17) I219-V", LinkMedium.Cable, true, 1000, ["192.168.1.10"]);

    /// <summary>Its second card, on an imaging network.</summary>
    private static readonly NetworkLink Imaging = new("Ethernet 2", "Realtek PCIe GbE Family Controller", LinkMedium.Cable, true, 1000, ["10.20.0.5"]);

    /// <summary>
    /// Feeds the samples to one detector, 30 seconds apart, with the workstations' requests lately arriving on
    /// <paramref name="served"/> (null: none to go by), and returns the events raised.
    /// </summary>
    private static List<WifiEvent> TwoCards(CableEventDetector detector, string[]? served, params NetworkLink[][] samples)
    {
        Func<IReadOnlyList<string>, bool>? recently = served is null ? null : addresses => addresses.Any(served.Contains);
        var events = new List<WifiEvent>();
        for (var i = 0; i < samples.Length; i++)
        {
            events.AddRange(detector.Evaluate(samples[i], null, At(i), servedRecently: recently));
        }

        return events;
    }

    /// <summary>The server's network card with its cable in, as the hospital's i5-12400 has it.</summary>
    private static NetworkLink Cable(string name = "Ethernet", long? speedMbps = null) =>
        new(name, "Intel(R) Ethernet Connection (17) I219-V", LinkMedium.Cable, true, speedMbps, ["192.168.1.10"]);

    /// <summary>When the <paramref name="sample"/>th sample (from 0) is taken, at the default interval.</summary>
    private static DateTimeOffset At(int sample) => Start + (sample * WifiMonitor.SampleInterval(new NetworkOptions()));

    /// <summary>Connected to the approved staff network (WPA2-Enterprise) at <paramref name="quality"/> % signal.</summary>
    private static WifiConnectionInfo Link(int quality) =>
        new(Adapter, "Wireless adapter", true, "Staff", "00:11:22:33:44:55", quality, -55, 300, 300, "ax", null, null, WifiSecurityLevel.Enterprise);

    /// <summary>Not connected, as Windows reports it: no network and no signal.</summary>
    private static WifiConnectionInfo Down() =>
        new(Adapter, "Wireless adapter", false, null, null, 0, null, null, null, null, null, null, WifiSecurityLevel.Unknown);

    /// <summary>Connected, with the network and signal withheld (Windows 11 without Location access).</summary>
    private static WifiConnectionInfo Withheld() =>
        new(Adapter, "Wireless adapter", true, null, null, 0, null, 300, 300, null, null, null, WifiSecurityLevel.Unknown,
            DetailsAvailable: false, Note: WifiConnectionInfo.LocationPermissionNote);

    /// <summary>The message as the on-call phone shows it: the built-in template of the channel with the message's values.</summary>
    private static string Rendered(RecordingNotifications.QueuedMessage message, NotificationChannel channel = NotificationChannel.Sms) =>
        NotificationTemplateRenderer.Render(
            DefaultNotificationTemplates.All.Single(t => t.Key == message.TemplateKey && t.Channel == channel).Body, message.Values, htmlEncode: false);

    /// <summary>Settings as the server keeps them: each a JSON value, read into the type asked for.</summary>
    private sealed class JsonSettings(IReadOnlyDictionary<string, string?> values) : ISettingsService
    {
        public Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.GetValueOrDefault(key) is { } json ? JsonSerializer.Deserialize<T>(json) : default);

        public Task SetAsync<T>(string key, T value, Guid? facilityId = null, bool secret = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetAsync<T>(string key, T value, Guid? facilityId, bool secret, uint? expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(string key, Guid? facilityId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A development host: the platform's settings are checked when the host starts, not when it registers.</summary>
    private sealed class DevelopmentHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "HC360";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Add(AuditEntry entry) => Entries.Add(entry);

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task PatientRecordAccessedAsync(Guid patientId, string what, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingNotifications : INotificationService
    {
        public List<(string Permission, Guid? FacilityId, string Title, string? Body, string? Link, string Category, string Severity)> InApp { get; } = [];

        public List<QueuedMessage> Queued { get; } = [];

        public IReadOnlyList<Guid> Queue(
            string templateKey,
            NotificationRecipient recipient,
            IReadOnlyDictionary<string, string> values,
            IReadOnlyList<NotificationChannel> channels,
            TimeSpan? expiresIn = null,
            Guid? facilityId = null,
            NotificationDocument? document = null)
        {
            var ids = channels.Select(_ => Guid.CreateVersion7()).ToList();
            Queued.Add(new QueuedMessage(templateKey, recipient, values, channels, expiresIn, ids));
            return ids;
        }

        public Task NotifyUsersAsync(IEnumerable<Guid> userIds, string title, string? body, string? link, string category, string severity = "info", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task NotifyPermissionHoldersAsync(string permission, Guid? facilityId, string title, string? body, string? link, string category, string severity = "info", CancellationToken cancellationToken = default)
        {
            InApp.Add((permission, facilityId, title, body, link, category, severity));
            return Task.CompletedTask;
        }

        public sealed record QueuedMessage(
            string TemplateKey, NotificationRecipient Recipient, IReadOnlyDictionary<string, string> Values, IReadOnlyList<NotificationChannel> Channels, TimeSpan? ExpiresIn,
            IReadOnlyList<Guid> Ids);
    }
}
