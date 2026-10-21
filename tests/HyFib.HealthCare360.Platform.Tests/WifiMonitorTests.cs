using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.WindowsInterop;
using static HyFib.HealthCare360.Platform.Networking.WifiEventKind;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-06: the Server Node samples its own WiFi link every 30 seconds, logs a drop or a weak signal, and alerts the
/// administrators in-app and the IT on-call number by SMS when the link is lost or the signal stays below the threshold:
/// once for each episode, with a word in-app when it is over.
/// </summary>
public sealed class WifiMonitorTests
{
    private const string OnCallPhone = "98450 12345";
    private const string Server = "HC360-SRV";

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

        await WifiMonitor.AlertAsync([drop], audit, notifications, OnCallPhone, Server, Ct);

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

        await WifiMonitor.AlertAsync([weak], audit, notifications, OnCallPhone, Server, Ct);

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

        await WifiMonitor.AlertAsync([back[0], back[2]], audit, notifications, OnCallPhone, Server, Ct);

        audit.Entries.Select(e => (e.Action, e.Severity)).ShouldBe([("network.wifi_reconnected", AuditSeverity.Info), ("network.wifi_signalrecovered", AuditSeverity.Info)]);
        notifications.InApp.Select(n => (n.Title, n.Severity)).ShouldBe([("Server WiFi: Reconnected", "info"), ("Server WiFi: SignalRecovered", "info")]);
        notifications.Queued.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_an_on_call_number_the_alerts_are_in_app_only()
    {
        var drop = Sample(Link(80), Down()).ShouldHaveSingleItem();
        var (audit, notifications) = (new RecordingAudit(), new RecordingNotifications());

        await WifiMonitor.AlertAsync([drop], audit, notifications, onCallPhone: " ", Server, Ct);

        notifications.InApp.ShouldHaveSingleItem().Severity.ShouldBe("critical");
        notifications.Queued.ShouldBeEmpty();
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

    /// <summary>The SMS as the on-call phone shows it: the built-in template with the message's values.</summary>
    private static string Rendered(RecordingNotifications.QueuedMessage sms) =>
        NotificationTemplateRenderer.Render(
            DefaultNotificationTemplates.All.Single(t => t.Key == sms.TemplateKey && t.Channel == NotificationChannel.Sms).Body, sms.Values, htmlEncode: false);

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
