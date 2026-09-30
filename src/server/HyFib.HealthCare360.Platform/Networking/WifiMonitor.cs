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
/// Monitors the dedicated machine's own WiFi connection (DED-06/12): drops, weak signal, access-point changes
/// and insecure or unapproved networks. Events are audited, pushed to administrators in-app and, when an
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
    private const int WeakStreakThreshold = 3;
    private int _weakStreak;
    private bool _weakAlerted;
    private string? _lastInsecureAlertSsid;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var network = options.Value.Network;
        if (!network.EnableWifiMonitor || !options.Value.RunsJobs)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(network.WifiSampleSeconds, 10, 600));
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

                var events = Evaluate(previous, current, now, network);
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

    private List<WifiEvent> Evaluate(WifiConnectionInfo? previous, WifiConnectionInfo current, DateTimeOffset now, NetworkOptions network)
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

            if (network.ApprovedSsids.Length > 0 && current.Ssid is not null
                && !network.ApprovedSsids.Contains(current.Ssid, StringComparer.Ordinal))
            {
                events.Add(new WifiEvent(now, WifiEventKind.UnapprovedNetwork, $"Server Node joined an unapproved WiFi network '{current.Ssid}'."));
            }
        }

        foreach (var wifiEvent in events)
        {
            state.Add(wifiEvent);
        }

        return events;
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
            var onCallPhone = await settings.GetAsync<string>("alerts.oncallPhone", cancellationToken: ct);

            foreach (var wifiEvent in events)
            {
                var severity = wifiEvent.Kind switch
                {
                    WifiEventKind.Disconnected or WifiEventKind.InsecureNetwork or WifiEventKind.UnapprovedNetwork => AuditSeverity.Critical,
                    WifiEventKind.WeakSignal => AuditSeverity.Warning,
                    _ => AuditSeverity.Info,
                };
                var category = wifiEvent.Kind is WifiEventKind.InsecureNetwork or WifiEventKind.UnapprovedNetwork ? AuditCategories.Security : AuditCategories.Admin;
                audit.Add(new AuditEntry(category, $"network.wifi_{wifiEvent.Kind.ToString().ToLowerInvariant()}", wifiEvent.Message) { Severity = severity });

                if (wifiEvent.Kind is not WifiEventKind.AccessPointChanged)
                {
                    await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, "Server WiFi: " + wifiEvent.Kind, wifiEvent.Message,
                        "/admin/system", "system", severity == AuditSeverity.Critical ? "critical" : severity == AuditSeverity.Warning ? "warning" : "info", ct);
                }

                if (severity >= AuditSeverity.Warning && !string.IsNullOrWhiteSpace(onCallPhone))
                {
                    notifications.Queue("system.alert", new NotificationRecipient(Phone.Normalize(onCallPhone), null, null, "IT on-call"),
                        new Dictionary<string, string> { ["server"] = Environment.MachineName, ["message"] = wifiEvent.Message },
                        [NotificationChannel.Sms], TimeSpan.FromHours(6));
                }
            }
        }

        await db.SaveChangesAsync(ct);

        if (now - _lastCleanup > TimeSpan.FromHours(24))
        {
            _lastCleanup = now;
            var cutoff = now.AddDays(-30);
            await db.Set<WifiSample>().Where(s => s.At < cutoff).ExecuteDeleteAsync(ct);
        }
    }
}
