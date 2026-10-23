using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.WindowsInterop;
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
        (detector.LinkLost, detector.AdapterMissing).ShouldBe((true, lost.Message), "System health says so while it lasts");

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
            Queued.Add(new QueuedMessage(templateKey, recipient, values, channels, expiresIn));
            return [Guid.CreateVersion7()];
        }

        public Task NotifyUsersAsync(IEnumerable<Guid> userIds, string title, string? body, string? link, string category, string severity = "info", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task NotifyPermissionHoldersAsync(string permission, Guid? facilityId, string title, string? body, string? link, string category, string severity = "info", CancellationToken cancellationToken = default)
        {
            InApp.Add((permission, facilityId, title, body, link, category, severity));
            return Task.CompletedTask;
        }

        public sealed record QueuedMessage(
            string TemplateKey, NotificationRecipient Recipient, IReadOnlyDictionary<string, string> Values, IReadOnlyList<NotificationChannel> Channels, TimeSpan? ExpiresIn);
    }
}
