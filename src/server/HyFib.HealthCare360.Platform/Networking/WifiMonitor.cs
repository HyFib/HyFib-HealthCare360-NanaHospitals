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

    internal void Update(WifiConnectionInfo? info, DateTimeOffset at)
    {
        lock (_gate)
        {
            Supported = info is not null;
            if (info is { IsConnected: true } && Current is not { IsConnected: true })
            {
                ConnectedSince = at;
            }

            if (info is { IsConnected: false } && Current is { IsConnected: true })
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
/// Monitors the dedicated machine's own WiFi connection (DED-06/12): drops, weak signal, access-point changes,
/// and insecure (open, WEP), shared-password (WPA-Personal) or unapproved networks. Events are audited, pushed to administrators in-app and, when an
/// on-call number is configured (<c>alerts.oncallPhone</c>), sent by SMS through the outbox (delivered as soon
/// as connectivity returns).
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

    private readonly WifiEventDetector _detector = new();
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
        WifiConnectionInfo? previous = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            WifiConnectionInfo? current = null;
            try
            {
                current = WlanClient.GetPrimaryConnection();
                state.Update(current, now);

                if (current is null)
                {
                    // No wireless adapter (the node may be on Ethernet) — check rarely.
                    await Task.Delay(TimeSpan.FromMinutes(5), time, stoppingToken);
                    continue;
                }

                var events = _detector.Evaluate(previous, current, now, network);
                foreach (var wifiEvent in events)
                {
                    state.Add(wifiEvent);
                }

                await PersistAsync(current, now, events, stoppingToken);
                previous = current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "WiFi monitoring sample failed");
            }

            await Task.Delay(interval, time, stoppingToken);
        }
    }

    private async Task PersistAsync(WifiConnectionInfo current, DateTimeOffset now, List<WifiEvent> events, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

        db.Add(new WifiSample
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
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var onCallPhone = await settings.GetAsync<string>(OnCallPhoneSetting, cancellationToken: ct);
            await AlertAsync(events, audit, notifications, onCallPhone, Environment.MachineName, ct);
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
    /// Logs each event in the audit trail and alerts on it: administrators (those who may see System health) in-app, for
    /// every event but a roam to another access point; and, for a warning or worse (a drop, a weak signal, an insecure,
    /// shared-password or unapproved network), the IT on-call number by SMS when one is set. The SMS is queued in the
    /// outbox, with the audit entries and the sample, so it goes as soon as the internet is back, for 6 hours.
    /// </summary>
    internal static async Task AlertAsync(
        IReadOnlyList<WifiEvent> events, IAuditLogger audit, INotificationService notifications, string? onCallPhone, string server, CancellationToken ct)
    {
        foreach (var wifiEvent in events)
        {
            var severity = wifiEvent.Kind switch
            {
                WifiEventKind.Disconnected or WifiEventKind.InsecureNetwork or WifiEventKind.UnapprovedNetwork => AuditSeverity.Critical,
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

            if (severity >= AuditSeverity.Warning && !string.IsNullOrWhiteSpace(onCallPhone))
            {
                notifications.Queue("system.alert", new NotificationRecipient(Phone.Normalize(onCallPhone), null, null, "IT on-call"),
                    new Dictionary<string, string> { ["server"] = server, ["message"] = wifiEvent.Message },
                    [NotificationChannel.Sms], TimeSpan.FromHours(6));
            }
        }
    }
}

/// <summary>
/// Decides which events a new sample of the Server Node's WiFi link raises (DED-06/12), remembering what it has already
/// reported so that one episode is reported once: a drop when the link goes down and a reconnection when it comes back; a
/// weak signal once it has stayed below <see cref="NetworkOptions.WifiWeakSignalThreshold"/> for
/// <see cref="WeakStreakThreshold"/> samples in a row, and its recovery once it is back at the threshold or above; a roam
/// to another access point; and an insecure, shared-password or unapproved network.
/// </summary>
internal sealed class WifiEventDetector
{
    /// <summary>Weak samples in a row before the signal is reported weak: a minute below the threshold, sampled every 30 s.</summary>
    internal const int WeakStreakThreshold = 3;

    private int _weakStreak;
    private bool _weakAlerted;
    private string? _lastInsecureAlertSsid;
    private string? _lastSharedPasswordAlertSsid;

    /// <param name="previous">The sample before, or null for the first since the server started.</param>
    public List<WifiEvent> Evaluate(WifiConnectionInfo? previous, WifiConnectionInfo current, DateTimeOffset now, NetworkOptions network)
    {
        var events = new List<WifiEvent>();

        if (previous is { IsConnected: true } && !current.IsConnected)
        {
            events.Add(new WifiEvent(now, WifiEventKind.Disconnected, "Server Node lost its WiFi connection. Workstations cannot reach HC360 until it reconnects."));
        }
        else if (previous is { IsConnected: false } && current.IsConnected)
        {
            events.Add(new WifiEvent(now, WifiEventKind.Reconnected, $"Server Node reconnected to WiFi{(current.Ssid is null ? string.Empty : $" '{current.Ssid}'")}."));
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
}
