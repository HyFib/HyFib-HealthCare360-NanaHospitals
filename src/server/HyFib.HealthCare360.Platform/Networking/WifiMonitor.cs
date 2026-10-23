using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.WindowsInterop;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>One sample of the Server Node's WiFi link (kept 30 days for the health dashboard).</summary>
[NotAudited]
public sealed class WifiSample
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset At { get; set; }
    public bool Connected { get; set; }
    public string? Ssid { get; set; }
    public string? Bssid { get; set; }
    public int SignalQuality { get; set; }
    public int? RssiDbm { get; set; }
    public double? ReceiveRateMbps { get; set; }
    public double? TransmitRateMbps { get; set; }
    public string? Security { get; set; }
}

internal sealed class WifiSampleConfiguration : IEntityTypeConfiguration<WifiSample>
{
    public void Configure(EntityTypeBuilder<WifiSample> b)
    {
        b.ToTable("wifi_samples", "platform");
        b.HasKey(x => x.Id);
        b.Property(x => x.Ssid).HasMaxLength(64);
        b.Property(x => x.Bssid).HasMaxLength(32);
        b.Property(x => x.Security).HasMaxLength(32);
        b.HasIndex(x => x.At);
    }
}

public enum WifiEventKind
{
    Disconnected = 0,
    Reconnected,
    WeakSignal,
    SignalRecovered,
    InsecureNetwork,
    UnapprovedNetwork,
    AccessPointChanged,
    SharedPasswordNetwork,

    /// <summary>Windows shows no WiFi adapter while the link was in use: see <see cref="WifiAdapterAbsence"/>.</summary>
    AdapterLost,
}

/// <summary>Why Windows shows no WiFi adapter, as far as the server can tell (<see cref="WlanService"/>).</summary>
public enum WifiAdapterAbsence
{
    /// <summary>It cannot tell: the WLAN AutoConfig service is not installed, or Windows did not answer.</summary>
    Unknown = 0,

    /// <summary>The WLAN AutoConfig service (WlanSvc) is installed but not running, so Windows sees no WiFi at all.</summary>
    ServiceStopped,

    /// <summary>The WLAN AutoConfig service runs, but there is no wireless adapter: it was disabled or removed.</summary>
    NoAdapter,
}

public sealed record WifiEvent(DateTimeOffset At, WifiEventKind Kind, string Message);

/// <summary>Latest known state of the Server Node's WiFi link (singleton, read by the health dashboard).</summary>
public sealed class WifiLinkState
{
    private readonly Lock _gate = new();
    private readonly LinkedList<WifiEvent> _events = new();

    public bool Supported { get; private set; } = true;
    public WifiConnectionInfo? Current { get; private set; }
    public DateTimeOffset? LastSampleAt { get; private set; }
    public DateTimeOffset? ConnectedSince { get; private set; }
    public DateTimeOffset? LastDisconnectAt { get; private set; }

    /// <summary>Why the adapter of a WiFi link that was in use is missing, while it is (System health warns); null otherwise.</summary>
    public string? AdapterMissing { get; private set; }

    public IReadOnlyList<WifiEvent> RecentEvents
    {
        get
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }
    }

    internal void Update(WifiConnectionInfo? info, DateTimeOffset at, string? adapterMissing = null)
    {
        lock (_gate)
        {
            Supported = info is not null;
            AdapterMissing = info is null ? adapterMissing : null;
            if (info is { IsConnected: true } && Current is not { IsConnected: true })
            {
                ConnectedSince = at;
            }

            if (info is not { IsConnected: true } && Current is { IsConnected: true })
            {
                LastDisconnectAt = at;
            }

            Current = info;
            LastSampleAt = at;
        }
    }

    internal void Add(WifiEvent wifiEvent)
    {
        lock (_gate)
        {
            _events.AddFirst(wifiEvent);
            while (_events.Count > 50)
            {
                _events.RemoveLast();
            }
        }
    }
}

/// <summary>
/// Monitors the dedicated machine's own WiFi connection (DED-06/12): drops, weak signal, access-point changes, a WiFi
/// adapter that disappears (disabled, removed, or the WLAN AutoConfig service stopped), a link that is down when the
/// server starts though it was on WiFi when it last ran, and insecure (open, WEP), shared-password (WPA-Personal) or
/// unapproved networks. Events are audited, pushed to administrators in-app and, when an on-call number is configured
/// (<c>alerts.oncallPhone</c>), sent by SMS, and by WhatsApp too when <c>alerts.oncallWhatsApp</c> is on, through the
/// outbox (delivered as soon as connectivity returns).
/// </summary>
internal sealed class WifiMonitor(
    WifiLinkState state,
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<WifiMonitor> logger) : BackgroundService
{
    /// <summary>The on-call number that gets the SMS alerts (an administrator sets it in Settings).</summary>
    private const string OnCallPhoneSetting = "alerts.oncallPhone";

    /// <summary>Whether the on-call number also gets the alerts on WhatsApp (off unless set to true).</summary>
    private const string OnCallWhatsAppSetting = "alerts.oncallWhatsApp";

    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    /// <summary>How often the link is sampled: <see cref="NetworkOptions.WifiSampleSeconds"/>, kept between 10 seconds and 10 minutes.</summary>
    internal static TimeSpan SampleInterval(NetworkOptions network) => TimeSpan.FromSeconds(Math.Clamp(network.WifiSampleSeconds, 10, 600));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var network = options.Value.Network;
        if (!network.EnableWifiMonitor || !options.Value.RunsJobs)
        {
            return;
        }

        var interval = SampleInterval(network);
        var detector = await DetectorAsync(stoppingToken);
        WifiConnectionInfo? previous = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var wait = interval;
            try
            {
                var current = WlanClient.GetPrimaryConnection();
                var events = detector.Evaluate(previous, current, now, network, current is null ? WlanService.Diagnose() : WifiAdapterAbsence.Unknown);
                state.Update(current, now, detector.AdapterMissing);
                foreach (var wifiEvent in events)
                {
                    state.Add(wifiEvent);
                }

                await PersistAsync(current, now, events, stoppingToken);
                previous = current;

                // No wireless adapter and no link lost with it: the node may be on Ethernet, so check rarely.
                if (current is null && !detector.LinkLost)
                {
                    wait = TimeSpan.FromMinutes(5);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "WiFi monitoring sample failed");
            }

            await Task.Delay(wait, time, stoppingToken);
        }
    }

    /// <summary>
    /// The detector for this run, told whether the server was on WiFi when it last ran: its last sample, kept 30 days. A
    /// link or an adapter missing now is then a drop, which the first sample reports.
    /// </summary>
    private async Task<WifiEventDetector> DetectorAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var last = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<WifiSample>()
                .OrderByDescending(s => s.At)
                .Select(s => new { s.Connected, s.Ssid })
                .FirstOrDefaultAsync(ct);
            return new WifiEventDetector(last?.Connected == true, last?.Ssid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The WiFi link of the last run could not be read; a link down at start-up is not reported");
            return new WifiEventDetector();
        }
    }

    /// <summary>
    /// Keeps the sample (none when Windows shows no WiFi adapter) and records and alerts the events. When the adapter of a
    /// link in use disappears, a sample of no link is kept in its place, so that a start-up while it is still missing
    /// does not report it again.
    /// </summary>
    private async Task PersistAsync(WifiConnectionInfo? current, DateTimeOffset now, List<WifiEvent> events, CancellationToken ct)
    {
        if (current is null && events.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

        db.Add(current is null
            ? new WifiSample { At = now, Connected = false }
            : new WifiSample
            {
                At = now,
                Connected = current.IsConnected,
                Ssid = current.Ssid,
                Bssid = current.Bssid,
                SignalQuality = current.SignalQuality,
                RssiDbm = current.RssiDbm,
                ReceiveRateMbps = current.ReceiveRateMbps,
                TransmitRateMbps = current.TransmitRateMbps,
                Security = current.Security.ToString(),
            });

        if (events.Count > 0)
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var onCall = await OnCallAsync(scope.ServiceProvider.GetRequiredService<ISettingsService>(), ct);
            await AlertAsync(events, audit, notifications, onCall, Environment.MachineName, ct);
        }

        await db.SaveChangesAsync(ct);

        if (now - _lastCleanup > TimeSpan.FromHours(24))
        {
            _lastCleanup = now;
            var cutoff = now.AddDays(-30);
            await db.Set<WifiSample>().Where(s => s.At < cutoff).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// Where the on-call alerts go, read so that a value of the wrong kind cannot stop the alert itself (the settings are
    /// free JSON): a number typed without quotes is the phone number all the same, and anything but true (or "true")
    /// leaves WhatsApp off. A value that did not read as text used to fail the sample, with its alerts and audit entries.
    /// </summary>
    internal static async Task<OnCallAlerts> OnCallAsync(ISettingsService settings, CancellationToken ct)
    {
        var phone = await settings.GetAsync<JsonElement?>(OnCallPhoneSetting, cancellationToken: ct) switch
        {
            { ValueKind: JsonValueKind.String } text => text.GetString(),
            { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
            _ => null,
        };
        var whatsApp = await settings.GetAsync<JsonElement?>(OnCallWhatsAppSetting, cancellationToken: ct) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.String } text => string.Equals(text.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        return new OnCallAlerts(phone, whatsApp);
    }

    /// <summary>
    /// Logs each event in the audit trail and alerts on it: administrators (those who may see System health) in-app, for
    /// every event but a roam to another access point; and, for a warning or worse (a drop, a missing adapter, a weak
    /// signal, an insecure, shared-password or unapproved network), the IT on-call number by SMS when one is set, and by
    /// WhatsApp too when the hospital turned that on. The messages are queued in the outbox, with the audit entries and the
    /// sample, so they go as soon as the internet is back, for 6 hours.
    /// </summary>
    internal static async Task AlertAsync(
        IReadOnlyList<WifiEvent> events, IAuditLogger audit, INotificationService notifications, OnCallAlerts onCall, string server, CancellationToken ct)
    {
        foreach (var wifiEvent in events)
        {
            var severity = wifiEvent.Kind switch
            {
                WifiEventKind.Disconnected or WifiEventKind.AdapterLost or WifiEventKind.InsecureNetwork or WifiEventKind.UnapprovedNetwork => AuditSeverity.Critical,
                WifiEventKind.WeakSignal or WifiEventKind.SharedPasswordNetwork => AuditSeverity.Warning,
                _ => AuditSeverity.Info,
            };
            var category = wifiEvent.Kind is WifiEventKind.InsecureNetwork or WifiEventKind.SharedPasswordNetwork or WifiEventKind.UnapprovedNetwork
                ? AuditCategories.Security
                : AuditCategories.Admin;
            audit.Add(new AuditEntry(category, $"network.wifi_{wifiEvent.Kind.ToString().ToLowerInvariant()}", wifiEvent.Message) { Severity = severity });

            if (wifiEvent.Kind is not WifiEventKind.AccessPointChanged)
            {
                await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, "Server WiFi: " + wifiEvent.Kind, wifiEvent.Message,
                    "/admin/system", "system", severity == AuditSeverity.Critical ? "critical" : severity == AuditSeverity.Warning ? "warning" : "info", ct);
            }

            if (severity >= AuditSeverity.Warning && !string.IsNullOrWhiteSpace(onCall.Phone))
            {
                notifications.Queue("system.alert", new NotificationRecipient(Phone.Normalize(onCall.Phone), null, null, "IT on-call"),
                    new Dictionary<string, string> { ["server"] = server, ["message"] = wifiEvent.Message },
                    onCall.WhatsApp ? [NotificationChannel.Sms, NotificationChannel.WhatsApp] : [NotificationChannel.Sms], TimeSpan.FromHours(6));
            }
        }
    }
}

/// <summary>
/// Where the IT on-call alerts go: <c>alerts.oncallPhone</c> (none when blank) by SMS, and on WhatsApp too when
/// <c>alerts.oncallWhatsApp</c> is on (the hospital's WhatsApp provider must have the <c>system_alert</c> template approved).
/// </summary>
internal sealed record OnCallAlerts(string? Phone, bool WhatsApp);

/// <summary>
/// Why Windows shows the Server Node no WiFi adapter (<see cref="WifiAdapterAbsence"/>): the state of the WLAN AutoConfig
/// service, read through WMI as System health reads the UPS. Anything it cannot read is <see cref="WifiAdapterAbsence.Unknown"/>.
/// </summary>
internal static class WlanService
{
    public static WifiAdapterAbsence Diagnose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return WifiAdapterAbsence.Unknown;
        }

        try
        {
            return DiagnoseWindows();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or PlatformNotSupportedException)
        {
            return WifiAdapterAbsence.Unknown;
        }
    }

    /// <summary>What the WLAN AutoConfig service's state says: not installed (a server without WiFi) tells nothing.</summary>
    internal static WifiAdapterAbsence FromServiceState(string? state) => state switch
    {
        null => WifiAdapterAbsence.Unknown,
        "Running" => WifiAdapterAbsence.NoAdapter,
        _ => WifiAdapterAbsence.ServiceStopped,
    };

    [SupportedOSPlatform("windows")]
    private static WifiAdapterAbsence DiagnoseWindows()
    {
        using var searcher = new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name = 'WlanSvc'");
        foreach (var item in searcher.Get())
        {
            using var service = (ManagementObject)item;
            return FromServiceState(service["State"]?.ToString());
        }

        return WifiAdapterAbsence.Unknown;
    }
}

/// <summary>
/// Decides which events a new sample of the Server Node's WiFi link raises (DED-06/12), remembering what it has already
/// reported so that one episode is reported once: a drop when the link goes down and a reconnection when it comes back; a
/// WiFi adapter that disappears while the link was in use; at the first sample since the server started, a link or an
/// adapter missing though the server was on WiFi when it last ran; a weak signal once it has stayed below
/// <see cref="NetworkOptions.WifiWeakSignalThreshold"/> for <see cref="WeakStreakThreshold"/> samples in a row, and its
/// recovery once it is back at the threshold or above; a roam to another access point; and an insecure, shared-password
/// or unapproved network.
/// </summary>
/// <param name="onWifiWhenLastRun">The server's last sample before this run (kept 30 days) had a link.</param>
/// <param name="lastSsid">That sample's network, for the message.</param>
internal sealed class WifiEventDetector(bool onWifiWhenLastRun = false, string? lastSsid = null)
{
    /// <summary>Weak samples in a row before the signal is reported weak: a minute below the threshold, sampled every 30 s.</summary>
    internal const int WeakStreakThreshold = 3;

    /// <summary>
    /// The warning lasts until the WiFi is back. A server moved to Ethernet on purpose clears it with a restart: its last
    /// stored sample has no link, so the next run does not count the WiFi as lost.
    /// </summary>
    private const string WiredOnPurpose = " If Server Node now runs on Ethernet on purpose, restart the HC360Server service to clear this warning.";

    private bool _started;
    private int _weakStreak;
    private bool _weakAlerted;
    private string? _lastInsecureAlertSsid;
    private string? _lastSharedPasswordAlertSsid;

    /// <summary>A drop, or the loss of the adapter, has been reported, and the link is not back yet.</summary>
    public bool LinkLost { get; private set; }

    /// <summary>Why the adapter of the link that was lost is missing, while it is; null otherwise.</summary>
    public string? AdapterMissing { get; private set; }

    /// <param name="previous">The sample before (null when it had no adapter), or null for the first since the server started.</param>
    /// <param name="current">This sample, or null when Windows shows no WiFi adapter.</param>
    /// <param name="absence">Why there is no adapter, when <paramref name="current"/> is null.</param>
    public List<WifiEvent> Evaluate(
        WifiConnectionInfo? previous, WifiConnectionInfo? current, DateTimeOffset now, NetworkOptions network,
        WifiAdapterAbsence absence = WifiAdapterAbsence.Unknown)
    {
        var events = new List<WifiEvent>();
        var starting = !_started;
        _started = true;

        // Whether the link was up just before: at the sample before, or, at the first sample, when the server last ran.
        var wasUp = starting ? onWifiWhenLastRun : previous is { IsConnected: true };

        if (current is null)
        {
            if (wasUp)
            {
                LinkLost = true;
                events.Add(new WifiEvent(now, WifiEventKind.AdapterLost, AdapterLostMessage(starting, absence)));
            }

            AdapterMissing = LinkLost ? AdapterLostMessage(false, absence) + WiredOnPurpose : null;
            return events;
        }

        AdapterMissing = null;
        if (!current.IsConnected)
        {
            if (wasUp)
            {
                LinkLost = true;
                events.Add(new WifiEvent(now, WifiEventKind.Disconnected, starting
                    ? $"Server Node started without its WiFi connection{(lastSsid is null ? string.Empty : $" (on '{lastSsid}' when it last ran)")}. Workstations cannot reach HC360 until it connects."
                    : "Server Node lost its WiFi connection. Workstations cannot reach HC360 until it reconnects."));
            }
        }
        else
        {
            // Back from a drop, or with its adapter back after the loss was reported.
            if (!starting && (previous is { IsConnected: false } || (previous is null && LinkLost)))
            {
                events.Add(new WifiEvent(now, WifiEventKind.Reconnected, $"Server Node reconnected to WiFi{(current.Ssid is null ? string.Empty : $" '{current.Ssid}'")}."));
            }

            LinkLost = false;
        }

        if (current is { IsConnected: true, DetailsAvailable: true })
        {
            if (current.SignalQuality < network.WifiWeakSignalThreshold)
            {
                _weakStreak++;
                if (_weakStreak >= WeakStreakThreshold && !_weakAlerted)
                {
                    _weakAlerted = true;
                    events.Add(new WifiEvent(now, WifiEventKind.WeakSignal,
                        $"Server Node WiFi signal is weak ({current.SignalQuality}%). Move the access point closer or connect Ethernet."));
                }
            }
            else
            {
                if (_weakAlerted)
                {
                    events.Add(new WifiEvent(now, WifiEventKind.SignalRecovered, $"Server Node WiFi signal recovered ({current.SignalQuality}%)."));
                }

                _weakStreak = 0;
                _weakAlerted = false;
            }

            if (previous is { IsConnected: true, Bssid: not null } && current.Bssid is not null && previous.Bssid != current.Bssid)
            {
                events.Add(new WifiEvent(now, WifiEventKind.AccessPointChanged, $"Server Node roamed to access point {current.Bssid}."));
            }

            if (current.IsInsecure && _lastInsecureAlertSsid != current.Ssid)
            {
                _lastInsecureAlertSsid = current.Ssid;
                events.Add(new WifiEvent(now, WifiEventKind.InsecureNetwork,
                    $"Server Node is connected to an insecure WiFi network ({current.Security}). Use WPA2/WPA3-Enterprise for clinical data."));
            }
            else if (current.UsesSharedPassword && _lastSharedPasswordAlertSsid != current.Ssid)
            {
                _lastSharedPasswordAlertSsid = current.Ssid;
                events.Add(new WifiEvent(now, WifiEventKind.SharedPasswordNetwork,
                    $"Server Node is on a WiFi network with a shared password ({current.Security}): anyone who has the password can join it or imitate it. Use WPA2/WPA3-Enterprise for clinical data."));
            }

            if (network.ApprovedSsids.Length > 0 && current.Ssid is not null
                && !network.ApprovedSsids.Contains(current.Ssid, StringComparer.Ordinal))
            {
                events.Add(new WifiEvent(now, WifiEventKind.UnapprovedNetwork, $"Server Node joined an unapproved WiFi network '{current.Ssid}'."));
            }
        }

        return events;
    }

    /// <summary>What the on-call engineer is to do: start the service, or enable the adapter.</summary>
    private static string AdapterLostMessage(bool starting, WifiAdapterAbsence absence) => absence switch
    {
        WifiAdapterAbsence.ServiceStopped => $"Server Node {(starting ? "started with" : "has")} no WiFi: the WLAN AutoConfig service is not running. Start it in Services. Workstations cannot reach HC360 over WiFi until then.",
        WifiAdapterAbsence.NoAdapter => $"Server Node {(starting ? "started without" : "lost")} its WiFi adapter: Windows shows none (disabled or removed). Enable it in Network Connections. Workstations cannot reach HC360 over WiFi until then.",
        _ => $"Server Node {(starting ? "started without" : "lost")} its WiFi adapter (disabled or removed, or the WLAN AutoConfig service stopped). Workstations cannot reach HC360 over WiFi until it is back.",
    };
}
